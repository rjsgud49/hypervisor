#include <ntddk.h>

/* Captures the composed desktop by resolving exports in the loaded
   dxgkrnl.sys, win32kbase.sys, and win32kfull.sys images and calling them
   on the GUI thread that issued the capture IOCTL.

   dxgkrnl enumerates the adapter and its display-mode list.
   win32k copies the screen DC into a compatible bitmap and returns a
   top-down 32-bit DIB. The shared primary is not mapped. */

#define HV_MODINFO 11
#define HV_SCRATCH (128u * 1024u)
#define HV_HEAD 384u
#define HV_ADAPTER_BYTES 20u
#define HV_MODE_BYTES 44u
#define HV_SRCCOPY 0x00CC0020u
#define HV_HORZRES 8
#define HV_VERTRES 10
#define HV_FRAME_TAG 'fSvH'
#define HV_MOD_TAG 'mSvH'

typedef struct _HV_MODULE {
    HANDLE Section;
    PVOID MappedBase;
    PVOID ImageBase;
    ULONG ImageSize;
    ULONG Flags;
    USHORT LoadOrderIndex;
    USHORT InitOrderIndex;
    USHORT LoadCount;
    USHORT OffsetToFileName;
    UCHAR FullPathName[256];
} HV_MODULE;

typedef struct _HV_MODULES {
    ULONG Count;
    HV_MODULE Modules[1];
} HV_MODULES;

typedef NTSTATUS (NTAPI *PFN_DXGK)(PVOID args);
typedef PVOID (NTAPI *PFN_GETDC)(PVOID hwnd);
typedef int (NTAPI *PFN_RELDC)(PVOID hwnd, PVOID hdc);
typedef PVOID (NTAPI *PFN_CREATEDC)(PVOID hdc);
typedef PVOID (NTAPI *PFN_CREATEBMP)(PVOID hdc, int width, int height);
typedef PVOID (NTAPI *PFN_SELECTBMP)(PVOID hdc, PVOID bitmap);
typedef int (NTAPI *PFN_BITBLT)(
    PVOID dst, int x, int y, int cx, int cy,
    PVOID src, int xSrc, int ySrc, ULONG rop, ULONG backColor, ULONG flags);
typedef int (NTAPI *PFN_STRETCH)(
    PVOID dst, int x, int y, int cx, int cy,
    PVOID src, int xSrc, int ySrc, int cxSrc, int cySrc,
    ULONG rop, ULONG backColor);
typedef int (NTAPI *PFN_CAPS)(PVOID hdc, int index);
typedef int (NTAPI *PFN_DIBITS)(
    PVOID hdc, PVOID bitmap, unsigned int start, unsigned int lines,
    PVOID bits, PVOID info, unsigned int usage, unsigned int maxBits, unsigned int maxInfo);
typedef int (NTAPI *PFN_DELETE)(PVOID object);

typedef struct _HV_BMI {
    ULONG biSize;
    LONG biWidth;
    LONG biHeight;
    USHORT biPlanes;
    USHORT biBitCount;
    ULONG biCompression;
    ULONG biSizeImage;
    LONG biXPelsPerMeter;
    LONG biYPelsPerMeter;
    ULONG biClrUsed;
    ULONG biClrImportant;
} HV_BMI;

C_ASSERT(sizeof(HV_MODULE) == 296);
C_ASSERT(sizeof(HV_BMI) == 40);

NTKERNELAPI PVOID PsGetThreadWin32Thread(_In_ PETHREAD Thread);
NTKERNELAPI PVOID PsGetProcessWow64Process(_In_ PEPROCESS Process);

NTSYSAPI
NTSTATUS
NTAPI
ZwQuerySystemInformation(
    _In_ ULONG SystemInformationClass,
    _Inout_ PVOID SystemInformation,
    _In_ ULONG SystemInformationLength,
    _Out_opt_ PULONG ReturnLength);

NTSYSAPI
NTSTATUS
NTAPI
ZwAllocateVirtualMemory(
    _In_ HANDLE ProcessHandle,
    _Inout_ PVOID *BaseAddress,
    _In_ ULONG_PTR ZeroBits,
    _Inout_ PSIZE_T RegionSize,
    _In_ ULONG AllocationType,
    _In_ ULONG Protect);

NTSYSAPI
NTSTATUS
NTAPI
ZwFreeVirtualMemory(
    _In_ HANDLE ProcessHandle,
    _Inout_ PVOID *BaseAddress,
    _Inout_ PSIZE_T RegionSize,
    _In_ ULONG FreeType);

static volatile LONG g_ResolveState;
static PFN_DXGK g_Enum2;
static PFN_DXGK g_OpenLuid;
static PFN_DXGK g_ModeList;
static PFN_DXGK g_CloseAdapter;
static PFN_GETDC g_GetDc;
static PFN_RELDC g_ReleaseDc;
static PFN_CREATEDC g_CreateDc;
static PFN_CREATEBMP g_CreateBmp;
static PFN_SELECTBMP g_SelectBmp;
static PFN_BITBLT g_BitBlt;
static PFN_STRETCH g_Stretch;
static PFN_CAPS g_Caps;
static PFN_DIBITS g_DiBits;
static PFN_DELETE g_Delete;

static BOOLEAN LeafIs(_In_ const HV_MODULE *mod, _In_z_ const char *leaf)
{
    const char *name;
    SIZE_T i;

    if (mod->OffsetToFileName >= sizeof(mod->FullPathName))
        return FALSE;
    name = (const char *)mod->FullPathName + mod->OffsetToFileName;
    for (i = 0; leaf[i] != 0; i++) {
        char a = name[i];
        char b = leaf[i];
        if (a >= 'A' && a <= 'Z')
            a = (char)(a - 'A' + 'a');
        if (b >= 'A' && b <= 'Z')
            b = (char)(b - 'A' + 'a');
        if (a != b)
            return FALSE;
    }
    return name[i] == 0;
}

static PVOID Rva(_In_ PVOID base, _In_ ULONG imageBytes, _In_ ULONG rva, _In_ ULONG need)
{
    if (rva >= imageBytes || need > imageBytes - rva)
        return NULL;
    return (PUCHAR)base + rva;
}

static PVOID FindExport(_In_ PVOID base, _In_ ULONG imageBytes, _In_z_ const char *want)
{
    USHORT magic;
    ULONG lfanew;
    ULONG exportRva;
    ULONG exportBytes;
    ULONG names;
    ULONG addrRva;
    ULONG nameRva;
    ULONG ordRva;
    ULONG i;
    PUCHAR exp;
    PUCHAR image;

    if (base == NULL || imageBytes < 0x200)
        return NULL;
    image = (PUCHAR)base;
    __try {
        if (image[0] != 'M' || image[1] != 'Z')
            return NULL;
        RtlCopyMemory(&lfanew, image + 0x3C, sizeof(lfanew));
        if (lfanew >= imageBytes || imageBytes - lfanew < 0x90)
            return NULL;
        if (image[lfanew] != 'P' || image[lfanew + 1] != 'E')
            return NULL;
        RtlCopyMemory(&magic, image + lfanew + 24, sizeof(magic));
        if (magic != 0x20B)
            return NULL;
        RtlCopyMemory(&exportRva, image + lfanew + 24 + 112, sizeof(exportRva));
        RtlCopyMemory(&exportBytes, image + lfanew + 24 + 116, sizeof(exportBytes));
        exp = Rva(base, imageBytes, exportRva, 40);
        if (exp == NULL || exportBytes < 40)
            return NULL;
        RtlCopyMemory(&names, exp + 24, sizeof(names));
        RtlCopyMemory(&addrRva, exp + 28, sizeof(addrRva));
        RtlCopyMemory(&nameRva, exp + 32, sizeof(nameRva));
        RtlCopyMemory(&ordRva, exp + 36, sizeof(ordRva));
        if (names > 65536)
            return NULL;
        for (i = 0; i < names; i++) {
            ULONG entry;
            USHORT ordinal;
            ULONG funcRva;
            const char *got;
            SIZE_T n;

            if (i > (MAXULONG / 4u))
                return NULL;
            entry = i * 4u;
            if (nameRva > MAXULONG - entry)
                return NULL;
            got = Rva(base, imageBytes, nameRva + entry, 4);
            if (got == NULL)
                return NULL;
            RtlCopyMemory(&entry, got, sizeof(entry));
            got = Rva(base, imageBytes, entry, 1);
            if (got == NULL)
                continue;
            n = 0;
            while (n < 128 && got[n] != 0 && want[n] != 0 && got[n] == want[n])
                n++;
            if (want[n] != 0 || got[n] != 0)
                continue;
            got = Rva(base, imageBytes, ordRva + i * 2u, 2);
            if (got == NULL)
                return NULL;
            RtlCopyMemory(&ordinal, got, sizeof(ordinal));
            got = Rva(base, imageBytes, addrRva + (ULONG)ordinal * 4u, 4);
            if (got == NULL)
                return NULL;
            RtlCopyMemory(&funcRva, got, sizeof(funcRva));
            if (funcRva >= exportRva && funcRva < exportRva + exportBytes)
                return NULL;
            return Rva(base, imageBytes, funcRva, 1);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return NULL;
    }
    return NULL;
}

static PVOID FindModule(_In_ const HV_MODULES *mods, _In_z_ const char *leaf, _Out_ ULONG *bytes)
{
    ULONG i;
    ULONG count;

    *bytes = 0;
    count = mods->Count;
    if (count > 1024)
        count = 1024;
    for (i = 0; i < count; i++) {
        if (!LeafIs(&mods->Modules[i], leaf))
            continue;
        if (mods->Modules[i].ImageBase == NULL || mods->Modules[i].ImageSize < 0x1000)
            return NULL;
        *bytes = mods->Modules[i].ImageSize;
        return mods->Modules[i].ImageBase;
    }
    return NULL;
}

static NTSTATUS ResolveAll(void)
{
    ULONG bytes = 1024u * 1024u;
    ULONG got = 0;
    HV_MODULES *mods;
    PVOID dxgk;
    PVOID base;
    PVOID full;
    ULONG dxgkBytes = 0;
    ULONG baseBytes = 0;
    ULONG fullBytes = 0;
    NTSTATUS status;

    mods = ExAllocatePool2(POOL_FLAG_NON_PAGED, bytes, HV_MOD_TAG);
    if (mods == NULL)
        return STATUS_INSUFFICIENT_RESOURCES;
    status = ZwQuerySystemInformation(HV_MODINFO, mods, bytes, &got);
    if (status == STATUS_INFO_LENGTH_MISMATCH && got > bytes && got < 4u * 1024u * 1024u) {
        ExFreePoolWithTag(mods, HV_MOD_TAG);
        bytes = got + 4096u;
        mods = ExAllocatePool2(POOL_FLAG_NON_PAGED, bytes, HV_MOD_TAG);
        if (mods == NULL)
            return STATUS_INSUFFICIENT_RESOURCES;
        status = ZwQuerySystemInformation(HV_MODINFO, mods, bytes, &got);
    }
    if (!NT_SUCCESS(status)) {
        ExFreePoolWithTag(mods, HV_MOD_TAG);
        return status;
    }

    dxgk = FindModule(mods, "dxgkrnl.sys", &dxgkBytes);
    base = FindModule(mods, "win32kbase.sys", &baseBytes);
    full = FindModule(mods, "win32kfull.sys", &fullBytes);
    if (dxgk == NULL || base == NULL || full == NULL) {
        ExFreePoolWithTag(mods, HV_MOD_TAG);
        return STATUS_NOT_FOUND;
    }

    g_Enum2 = (PFN_DXGK)FindExport(dxgk, dxgkBytes, "NtGdiDdDDIEnumAdapters2");
    g_OpenLuid = (PFN_DXGK)FindExport(dxgk, dxgkBytes, "NtGdiDdDDIOpenAdapterFromLuid");
    g_ModeList = (PFN_DXGK)FindExport(dxgk, dxgkBytes, "NtGdiDdDDIGetDisplayModeList");
    g_CloseAdapter = (PFN_DXGK)FindExport(dxgk, dxgkBytes, "NtGdiDdDDICloseAdapter");
    g_GetDc = (PFN_GETDC)FindExport(base, baseBytes, "NtUserGetDC");
    g_ReleaseDc = (PFN_RELDC)FindExport(base, baseBytes, "NtUserReleaseDC");
    g_CreateDc = (PFN_CREATEDC)FindExport(base, baseBytes, "NtGdiCreateCompatibleDC");
    g_Delete = (PFN_DELETE)FindExport(base, baseBytes, "NtGdiDeleteObjectApp");
    g_Caps = (PFN_CAPS)FindExport(base, baseBytes, "GreGetDeviceCaps");
    g_CreateBmp = (PFN_CREATEBMP)FindExport(full, fullBytes, "NtGdiCreateCompatibleBitmap");
    g_SelectBmp = (PFN_SELECTBMP)FindExport(full, fullBytes, "NtGdiSelectBitmap");
    g_BitBlt = (PFN_BITBLT)FindExport(full, fullBytes, "NtGdiBitBlt");
    g_Stretch = (PFN_STRETCH)FindExport(full, fullBytes, "NtGdiStretchBlt");
    g_DiBits = (PFN_DIBITS)FindExport(full, fullBytes, "NtGdiGetDIBitsInternal");
    ExFreePoolWithTag(mods, HV_MOD_TAG);

    if (g_Enum2 == NULL || g_OpenLuid == NULL || g_ModeList == NULL || g_CloseAdapter == NULL)
        return STATUS_PROCEDURE_NOT_FOUND;
    if (g_GetDc == NULL || g_ReleaseDc == NULL || g_CreateDc == NULL || g_Delete == NULL || g_Caps == NULL)
        return STATUS_PROCEDURE_NOT_FOUND;
    if (g_CreateBmp == NULL || g_SelectBmp == NULL || g_BitBlt == NULL || g_Stretch == NULL || g_DiBits == NULL)
        return STATUS_PROCEDURE_NOT_FOUND;
    return STATUS_SUCCESS;
}

static NTSTATUS EnsureResolved(void)
{
    LONG state;
    NTSTATUS status;
    LARGE_INTEGER pause;

    for (;;) {
        state = InterlockedCompareExchange(&g_ResolveState, 1, 0);
        if (state == 2)
            return STATUS_SUCCESS;
        if (state == 0)
            break;
        pause.QuadPart = -10 * 1000 * 10;
        KeDelayExecutionThread(KernelMode, FALSE, &pause);
    }

    status = ResolveAll();
    if (NT_SUCCESS(status))
        InterlockedExchange(&g_ResolveState, 2);
    else
        InterlockedExchange(&g_ResolveState, 0);
    return status;
}

static NTSTATUS UserAlloc(_In_ SIZE_T bytes, _Out_ PVOID *address)
{
    PVOID base = NULL;
    SIZE_T size = bytes;
    NTSTATUS status;

    *address = NULL;
    status = ZwAllocateVirtualMemory(ZwCurrentProcess(), &base, 0, &size, 0x3000, PAGE_READWRITE);
    if (!NT_SUCCESS(status))
        return status;
    __try {
        RtlZeroMemory(base, bytes);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        size = 0;
        ZwFreeVirtualMemory(ZwCurrentProcess(), &base, &size, 0x8000);
        return STATUS_ACCESS_VIOLATION;
    }
    *address = base;
    return STATUS_SUCCESS;
}

static VOID UserFree(_In_opt_ PVOID address)
{
    PVOID base = address;
    SIZE_T size = 0;

    if (address != NULL)
        ZwFreeVirtualMemory(ZwCurrentProcess(), &base, &size, 0x8000);
}

static NTSTATUS ReadU32(_In_ PVOID address, _Out_ ULONG *value)
{
    __try {
        RtlCopyMemory(value, address, sizeof(*value));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return STATUS_ACCESS_VIOLATION;
    }
    return STATUS_SUCCESS;
}

static NTSTATUS WriteU32(_In_ PVOID address, _In_ ULONG value)
{
    __try {
        RtlCopyMemory(address, &value, sizeof(value));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return STATUS_ACCESS_VIOLATION;
    }
    return STATUS_SUCCESS;
}

static NTSTATUS WritePtr(_In_ PVOID address, _In_opt_ PVOID value)
{
    __try {
        RtlCopyMemory(address, &value, sizeof(value));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return STATUS_ACCESS_VIOLATION;
    }
    return STATUS_SUCCESS;
}

static VOID QueryDxgk(_In_ PVOID scratch, _Out_ int *modeW, _Out_ int *modeH)
{
    PUCHAR adapters;
    PUCHAR open;
    PUCHAR close;
    PUCHAR req;
    PUCHAR modes;
    ULONG count = 0;
    ULONG i;
    ULONG chosen = 0;
    ULONG chosenSources = 0;
    ULONG handle = 0;
    ULONG listed = 0;
    ULONG maxModes;
    int bestW = 0;
    int bestH = 0;
    NTSTATUS status;

    *modeW = 0;
    *modeH = 0;
    adapters = (PUCHAR)scratch + 16;
    open = (PUCHAR)scratch + 336;
    close = (PUCHAR)scratch + 352;
    req = (PUCHAR)scratch + 360;
    modes = (PUCHAR)scratch + HV_HEAD;
    maxModes = (HV_SCRATCH - HV_HEAD) / HV_MODE_BYTES;

    if (!NT_SUCCESS(WriteU32(scratch, 16)) || !NT_SUCCESS(WritePtr((PUCHAR)scratch + 8, adapters)))
        return;
    __try {
        status = g_Enum2(scratch);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return;
    }
    if (!NT_SUCCESS(status) || !NT_SUCCESS(ReadU32(scratch, &count)) || count == 0)
        return;
    if (count > 16)
        count = 16;
    for (i = 0; i < count; i++) {
        ULONG sources = 0;
        if (!NT_SUCCESS(ReadU32(adapters + i * HV_ADAPTER_BYTES + 12, &sources)))
            return;
        if (sources > chosenSources) {
            chosen = i;
            chosenSources = sources;
        }
    }

    __try {
        RtlCopyMemory(open, adapters + chosen * HV_ADAPTER_BYTES + 4, 8);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return;
    }
    if (!NT_SUCCESS(WriteU32(open + 8, 0)))
        return;
    __try {
        status = g_OpenLuid(open);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return;
    }
    if (!NT_SUCCESS(status) || !NT_SUCCESS(ReadU32(open + 8, &handle)) || handle == 0)
        return;

    if (!NT_SUCCESS(WriteU32(req, handle)) || !NT_SUCCESS(WriteU32(req + 4, 0)) ||
        !NT_SUCCESS(WritePtr(req + 8, NULL)) || !NT_SUCCESS(WriteU32(req + 16, 0))) {
        WriteU32(close, handle);
        g_CloseAdapter(close);
        return;
    }
    __try {
        status = g_ModeList(req);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        WriteU32(close, handle);
        g_CloseAdapter(close);
        return;
    }
    if (NT_SUCCESS(status))
        ReadU32(req + 16, &listed);
    if (listed > 0 && listed <= maxModes) {
        if (NT_SUCCESS(WritePtr(req + 8, modes)) && NT_SUCCESS(WriteU32(req + 16, listed))) {
            __try {
                status = g_ModeList(req);
            } __except (EXCEPTION_EXECUTE_HANDLER) {
                status = STATUS_UNSUCCESSFUL;
            }
        } else {
            status = STATUS_UNSUCCESSFUL;
        }
        if (NT_SUCCESS(status)) {
            for (i = 0; i < listed; i++) {
                ULONG w = 0;
                ULONG h = 0;
                if (!NT_SUCCESS(ReadU32(modes + i * HV_MODE_BYTES, &w)) ||
                    !NT_SUCCESS(ReadU32(modes + i * HV_MODE_BYTES + 4, &h)))
                    break;
                if (w == 0 || h == 0 || w > 8192 || h > 8192)
                    continue;
                if ((ULONG64)w * h > (ULONG64)bestW * (ULONG64)bestH) {
                    bestW = (int)w;
                    bestH = (int)h;
                }
            }
        }
    }

    WriteU32(close, handle);
    __try {
        g_CloseAdapter(close);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *modeW = bestW;
        *modeH = bestH;
        return;
    }
    *modeW = bestW;
    *modeH = bestH;
}

static VOID Fit(_In_ int srcW, _In_ int srcH, _Out_ int *dstW, _Out_ int *dstH)
{
    int dw = srcW;
    int dh = srcH;

    if (dw > 1920 && dw > 0) {
        dh = (int)(((LONGLONG)srcH * 1920) / srcW);
        dw = 1920;
        if (dh < 1)
            dh = 1;
    }
    if (dh > 1080 && dh > 0) {
        dw = (int)(((LONGLONG)dw * 1080) / dh);
        dh = 1080;
        if (dw < 1)
            dw = 1;
    }
    *dstW = dw;
    *dstH = dh;
}

NTSTATUS HvGfxCapture(_Out_ PVOID *outBits, _Out_ ULONG *outWidth, _Out_ ULONG *outHeight)
{
    PVOID scratch = NULL;
    PVOID dib = NULL;
    PVOID screen = NULL;
    PVOID mem = NULL;
    PVOID bmp = NULL;
    PVOID previous = NULL;
    PVOID neu = NULL;
    PUCHAR userBits;
    HV_BMI bmi;
    int modeW = 0;
    int modeH = 0;
    int srcW = 0;
    int srcH = 0;
    int dw = 0;
    int dh = 0;
    ULONG payload;
    int copied = 0;
    int blitOk = 0;
    NTSTATUS status;

    *outBits = NULL;
    *outWidth = 0;
    *outHeight = 0;

    if (KeGetCurrentIrql() != PASSIVE_LEVEL)
        return STATUS_UNSUCCESSFUL;
    if (PsGetProcessWow64Process(PsGetCurrentProcess()) != NULL)
        return STATUS_NOT_SUPPORTED;
    if (PsGetThreadWin32Thread(PsGetCurrentThread()) == NULL)
        return STATUS_INVALID_THREAD;

    status = EnsureResolved();
    if (!NT_SUCCESS(status))
        return status;

    status = UserAlloc(HV_SCRATCH, &scratch);
    if (!NT_SUCCESS(status))
        return status;
    QueryDxgk(scratch, &modeW, &modeH);

    __try {
        screen = g_GetDc(NULL);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        screen = NULL;
        status = STATUS_UNSUCCESSFUL;
        goto cleanup;
    }
    if (screen == NULL) {
        status = STATUS_DEVICE_NOT_READY;
        goto cleanup;
    }
    __try {
        srcW = g_Caps(screen, HV_HORZRES);
        srcH = g_Caps(screen, HV_VERTRES);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        status = STATUS_UNSUCCESSFUL;
        goto cleanup;
    }
    if (srcW <= 0 || srcH <= 0 || srcW > 8192 || srcH > 8192) {
        srcW = modeW;
        srcH = modeH;
    }
    if (srcW <= 0 || srcH <= 0 || srcW > 8192 || srcH > 8192) {
        status = STATUS_DEVICE_NOT_READY;
        goto cleanup;
    }
    Fit(srcW, srcH, &dw, &dh);
    if (dw <= 0 || dh <= 0 || (ULONG64)dw * (ULONG64)dh * 4ull > 16ull * 1024ull * 1024ull) {
        status = STATUS_BUFFER_OVERFLOW;
        goto cleanup;
    }

    __try {
        mem = g_CreateDc(screen);
        bmp = g_CreateBmp(screen, dw, dh);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        status = STATUS_UNSUCCESSFUL;
        goto cleanup;
    }
    if (mem == NULL || bmp == NULL) {
        status = STATUS_INSUFFICIENT_RESOURCES;
        goto cleanup;
    }
    __try {
        previous = g_SelectBmp(mem, bmp);
        if (dw == srcW && dh == srcH)
            blitOk = g_BitBlt(mem, 0, 0, dw, dh, screen, 0, 0, HV_SRCCOPY, 0, 0);
        else
            blitOk = g_Stretch(mem, 0, 0, dw, dh, screen, 0, 0, srcW, srcH, HV_SRCCOPY, 0);
        g_SelectBmp(mem, previous);
        previous = NULL;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        status = STATUS_UNSUCCESSFUL;
        goto cleanup;
    }
    if (!blitOk) {
        status = STATUS_UNSUCCESSFUL;
        goto cleanup;
    }

    payload = (ULONG)dw * (ULONG)dh * 4u;
    status = UserAlloc((SIZE_T)payload + 256u, &dib);
    if (!NT_SUCCESS(status))
        goto cleanup;
    RtlZeroMemory(&bmi, sizeof(bmi));
    bmi.biSize = sizeof(bmi);
    bmi.biWidth = dw;
    bmi.biHeight = -dh;
    bmi.biPlanes = 1;
    bmi.biBitCount = 32;
    bmi.biSizeImage = payload;
    userBits = (PUCHAR)dib + 256;
    __try {
        RtlCopyMemory(dib, &bmi, sizeof(bmi));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        status = STATUS_ACCESS_VIOLATION;
        goto cleanup;
    }
    __try {
        copied = g_DiBits(screen, bmp, 0, (unsigned int)dh, userBits, dib, 0, payload, sizeof(bmi));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        status = STATUS_UNSUCCESSFUL;
        goto cleanup;
    }
    if (copied != dh) {
        status = STATUS_UNSUCCESSFUL;
        goto cleanup;
    }

    neu = ExAllocatePool2(POOL_FLAG_NON_PAGED, payload, HV_FRAME_TAG);
    if (neu == NULL) {
        status = STATUS_INSUFFICIENT_RESOURCES;
        goto cleanup;
    }
    __try {
        RtlCopyMemory(neu, userBits, payload);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ExFreePoolWithTag(neu, HV_FRAME_TAG);
        neu = NULL;
        status = STATUS_ACCESS_VIOLATION;
        goto cleanup;
    }

    *outBits = neu;
    *outWidth = (ULONG)dw;
    *outHeight = (ULONG)dh;
    neu = NULL;
    status = STATUS_SUCCESS;

cleanup:
    __try {
        if (previous != NULL && mem != NULL)
            g_SelectBmp(mem, previous);
        if (bmp != NULL)
            g_Delete(bmp);
        if (mem != NULL)
            g_Delete(mem);
        if (screen != NULL)
            g_ReleaseDc(NULL, screen);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
    UserFree(dib);
    UserFree(scratch);
    return status;
}
