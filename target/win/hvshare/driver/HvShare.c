#include <ntddk.h>
#include "..\include\HvShareIoctl.h"

/* Read-only VGA window for a local screen view.
   Legacy aperture 0xA0000 and, when the QEMU stdvga device is present, its
   linear framebuffer. No arbitrary physical read and no process memory. */

C_ASSERT(sizeof(HVSHARE_INFO) == 808);
C_ASSERT(sizeof(HVSHARE_PUBLISH) == 16);
C_ASSERT(IOCTL_HVSHARE_QUERY == 0x00226000);
C_ASSERT(IOCTL_HVSHARE_READ == 0x00226006);
C_ASSERT(IOCTL_HVSHARE_PUBLISH == 0x0022A009);

#define HV_VGA_PHYS 0xA0000ULL
#define HV_VGA_BYTES 0x20000u
#define HV_TEXT_OFFSET 0x18000u
#define HV_MAX_W 1920u
#define HV_MAX_H 1080u
#define HV_MAX_MAP (8u * 1024u * 1024u)

#define VBE_DISPI_INDEX 0x01CE
#define VBE_DISPI_DATA 0x01CF
#define VBE_INDEX_ID 0
#define VBE_INDEX_XRES 1
#define VBE_INDEX_YRES 2
#define VBE_INDEX_BPP 3
#define VBE_INDEX_ENABLE 4
#define VBE_INDEX_VIRT_WIDTH 6
#define VBE_INDEX_X_OFFSET 8
#define VBE_INDEX_Y_OFFSET 9
#define VBE_LFB_ENABLED 0x40
#define VBE_8BIT_DAC 0x20
#define QEMU_VGA_VENDOR 0x1234
#define QEMU_VGA_DEVICE 0x1111

typedef struct _HV_PCI {
    USHORT VendorID;
    USHORT DeviceID;
    USHORT Command;
    USHORT Status;
    UCHAR RevisionID;
    UCHAR ProgIf;
    UCHAR SubClass;
    UCHAR BaseClass;
    UCHAR CacheLine;
    UCHAR Latency;
    UCHAR HeaderType;
    UCHAR Bist;
    ULONG Bar[6];
} HV_PCI;

static PDEVICE_OBJECT g_Device;
static ERESOURCE g_Lock;
static BOOLEAN g_LockReady;
static PVOID g_VgaWindow;
static PVOID g_Lfb;
static ULONG g_LfbMapBytes;
static ULONGLONG g_LfbPhys;
static BOOLEAN g_VgaReady;
static BOOLEAN g_HaveQemu;
static ULONG g_QemuBar;
static ULONG g_SrcPitch;
static ULONG g_BppBytes;
static ULONG g_XOff;
static ULONG g_YOff;
static HVSHARE_INFO g_Info;
static PVOID g_Desktop;
static ULONG g_DesktopBytes;
static BOOLEAN g_HaveDesktop;
#define HVSHARE_DESKTOP_TAG 'fSvH'

NTSYSAPI
ULONG
HalGetBusDataByOffset(
    _In_ BUS_DATA_TYPE BusDataType,
    _In_ ULONG BusNumber,
    _In_ ULONG SlotNumber,
    _Out_writes_bytes_(Length) PVOID Buffer,
    _In_ ULONG Offset,
    _In_ ULONG Length);

static VOID FillLegacyPalette(UCHAR pal[768])
{
    static const UCHAR ega[16][3] = {
        { 0, 0, 0 }, { 0, 0, 42 }, { 0, 42, 0 }, { 0, 42, 42 },
        { 42, 0, 0 }, { 42, 0, 42 }, { 42, 21, 0 }, { 42, 42, 42 },
        { 21, 21, 21 }, { 21, 21, 63 }, { 21, 63, 21 }, { 21, 63, 63 },
        { 63, 21, 21 }, { 63, 21, 63 }, { 63, 63, 21 }, { 63, 63, 63 }
    };
    ULONG i;

    RtlZeroMemory(pal, 768);
    for (i = 0; i < 16; i++) {
        pal[i * 3] = ega[i][0];
        pal[i * 3 + 1] = ega[i][1];
        pal[i * 3 + 2] = ega[i][2];
    }
    for (i = 16; i < 232; i++) {
        ULONG n = i - 16;
        ULONG r = n / 36;
        ULONG g = (n / 6) % 6;
        ULONG b = n % 6;
        pal[i * 3] = (UCHAR)(r * 63 / 5);
        pal[i * 3 + 1] = (UCHAR)(g * 63 / 5);
        pal[i * 3 + 2] = (UCHAR)(b * 63 / 5);
    }
    for (i = 232; i < 256; i++) {
        UCHAR g = (UCHAR)((i - 232) * 63 / 23);
        pal[i * 3] = g;
        pal[i * 3 + 1] = g;
        pal[i * 3 + 2] = g;
    }
}

static USHORT ReadDispi(USHORT index)
{
    WRITE_PORT_USHORT((PUSHORT)VBE_DISPI_INDEX, index);
    return READ_PORT_USHORT((PUSHORT)VBE_DISPI_DATA);
}

static VOID UnmapLfb(VOID)
{
    if (g_Lfb != NULL) {
        MmUnmapIoSpace(g_Lfb, g_LfbMapBytes);
        g_Lfb = NULL;
        g_LfbMapBytes = 0;
        g_LfbPhys = 0;
    }
}

static BOOLEAN MapLfb(ULONGLONG phys, ULONG bytes)
{
    PHYSICAL_ADDRESS pa;

    if (bytes == 0 || bytes > HV_MAX_MAP || phys < 0x100000ULL)
        return FALSE;
    if (g_Lfb != NULL && g_LfbPhys == phys && g_LfbMapBytes == bytes)
        return TRUE;
    UnmapLfb();
    pa.QuadPart = (LONGLONG)phys;
    g_Lfb = MmMapIoSpace(pa, bytes, MmNonCached);
    if (g_Lfb == NULL)
        return FALSE;
    g_LfbPhys = phys;
    g_LfbMapBytes = bytes;
    return TRUE;
}

static VOID FindQemuVga(VOID)
{
    ULONG bus;
    ULONG device;
    ULONG function;

    g_HaveQemu = FALSE;
    g_QemuBar = 0;
    for (bus = 0; bus < 8; bus++) {
        for (device = 0; device < 32; device++) {
            for (function = 0; function < 8; function++) {
                HV_PCI pci;
                ULONG slot = device | (function << 5);
                ULONG bar;
                ULONG n;

                RtlZeroMemory(&pci, sizeof(pci));
                n = HalGetBusDataByOffset(PCIConfiguration, bus, slot, &pci, 0, sizeof(pci));
                if (n < sizeof(pci) || pci.VendorID == 0xFFFF || pci.VendorID == 0)
                    continue;
                if (pci.VendorID != QEMU_VGA_VENDOR || pci.DeviceID != QEMU_VGA_DEVICE)
                    continue;
                if (pci.BaseClass != 0x03)
                    continue;
                bar = pci.Bar[0];
                if ((bar & 1u) != 0)
                    return;
                if (((bar >> 1) & 3u) == 2u && pci.Bar[1] != 0)
                    return;
                g_QemuBar = bar & 0xFFFFFFF0u;
                g_HaveQemu = g_QemuBar >= 0x100000u;
                return;
            }
        }
    }
}

static BOOLEAN LooksLikeText(const UCHAR *cells)
{
    ULONG printable = 0;
    ULONG gray = 0;
    ULONG i;

    for (i = 0; i < 80u * 25u; i++) {
        UCHAR ch = cells[i * 2];
        UCHAR attr = cells[i * 2 + 1];
        if (ch >= 32 && ch < 127)
            printable++;
        if (attr == 0x07)
            gray++;
    }
    return printable > (80u * 25u * 7u) / 10u && gray > (80u * 25u) / 2u;
}

static VOID SetLegacy(VOID)
{
    RtlZeroMemory(&g_Info, sizeof(g_Info));
    g_Info.Version = HVSHARE_VERSION;
    g_Info.Source = HVSHARE_SOURCE_LEGACY;
    g_Info.Format = HVSHARE_FORMAT_INDEX8;
    g_Info.Width = 320;
    g_Info.Height = 200;
    g_Info.Pitch = 320;
    g_Info.Payload = 320u * 200u;
    g_Info.PaletteScale = 2;
    g_Info.PhysBase = HV_VGA_PHYS;
    FillLegacyPalette(g_Info.Palette);
}

static VOID SetText(VOID)
{
    RtlZeroMemory(&g_Info, sizeof(g_Info));
    g_Info.Version = HVSHARE_VERSION;
    g_Info.Source = HVSHARE_SOURCE_TEXT;
    g_Info.Format = HVSHARE_FORMAT_TEXT;
    g_Info.Width = 80;
    g_Info.Height = 25;
    g_Info.Pitch = 160;
    g_Info.Payload = 80u * 25u * 2u;
    g_Info.PhysBase = 0xB8000ULL;
}

static BOOLEAN SetLfb(USHORT xres, USHORT yres, USHORT bpp, USHORT enable, USHORT virtWidth, USHORT xoff, USHORT yoff)
{
    ULONG bppBytes;
    ULONG srcPitch;
    ULONG copyW;
    ULONG copyH;
    ULONG mapBytes;
    ULONG scale;

    if ((enable & 0x01) == 0 || (enable & VBE_LFB_ENABLED) == 0)
        return FALSE;
    if (bpp == 8)
        bppBytes = 1;
    else if (bpp == 16)
        bppBytes = 2;
    else if (bpp == 24)
        bppBytes = 3;
    else if (bpp == 32)
        bppBytes = 4;
    else
        return FALSE;
    if (xres == 0 || yres == 0)
        return FALSE;
    if (virtWidth < xres)
        virtWidth = xres;
    copyW = xres;
    copyH = yres;
    if (copyW > HV_MAX_W)
        copyW = HV_MAX_W;
    if (copyH > HV_MAX_H)
        copyH = HV_MAX_H;
    srcPitch = (ULONG)virtWidth * bppBytes;
    if ((ULONG)xoff + copyW > virtWidth || yoff > 4096)
        return FALSE;
    mapBytes = srcPitch * ((ULONG)yoff + copyH);
    if (mapBytes > HV_MAX_MAP || mapBytes < srcPitch)
        return FALSE;
    if (!MapLfb(g_QemuBar, mapBytes))
        return FALSE;

    g_SrcPitch = srcPitch;
    g_BppBytes = bppBytes;
    g_XOff = xoff;
    g_YOff = yoff;

    RtlZeroMemory(&g_Info, sizeof(g_Info));
    g_Info.Version = HVSHARE_VERSION;
    g_Info.Source = HVSHARE_SOURCE_LFB;
    g_Info.Width = copyW;
    g_Info.Height = copyH;
    g_Info.PhysBase = g_QemuBar;
    scale = (enable & VBE_8BIT_DAC) ? 0u : 2u;
    if (bpp == 8) {
        g_Info.Format = HVSHARE_FORMAT_INDEX8;
        g_Info.Pitch = copyW;
        g_Info.Payload = copyW * copyH;
        g_Info.PaletteScale = scale;
    } else {
        g_Info.Format = HVSHARE_FORMAT_BGRA32;
        g_Info.Pitch = copyW * 4u;
        g_Info.Payload = g_Info.Pitch * copyH;
    }
    return g_Info.Payload <= HVSHARE_MAX_PAYLOAD;
}

static VOID EnsureVga(VOID)
{
    PHYSICAL_ADDRESS pa;

    if (g_VgaReady)
        return;
    pa.QuadPart = HV_VGA_PHYS;
    g_VgaWindow = MmMapIoSpace(pa, HV_VGA_BYTES, MmNonCached);
    FindQemuVga();
    g_VgaReady = TRUE;
}

static VOID RefreshMode(VOID)
{
    USHORT id;
    USHORT enable;
    USHORT xres;
    USHORT yres;
    USHORT bpp;
    USHORT virtWidth;
    USHORT xoff;
    USHORT yoff;

    EnsureVga();
    if (g_HaveQemu) {
        id = ReadDispi(VBE_INDEX_ID);
        if (id >= 0xB0C0 && id <= 0xB0C5) {
            enable = ReadDispi(VBE_INDEX_ENABLE);
            xres = ReadDispi(VBE_INDEX_XRES);
            yres = ReadDispi(VBE_INDEX_YRES);
            bpp = ReadDispi(VBE_INDEX_BPP);
            virtWidth = ReadDispi(VBE_INDEX_VIRT_WIDTH);
            xoff = ReadDispi(VBE_INDEX_X_OFFSET);
            yoff = ReadDispi(VBE_INDEX_Y_OFFSET);
            if (SetLfb(xres, yres, bpp, enable, virtWidth, xoff, yoff)) {
                if (g_Info.Format == HVSHARE_FORMAT_INDEX8) {
                    ULONG i;
                    WRITE_PORT_UCHAR((PUCHAR)0x3C7, 0);
                    for (i = 0; i < 256; i++) {
                        g_Info.Palette[i * 3] = READ_PORT_UCHAR((PUCHAR)0x3C9);
                        g_Info.Palette[i * 3 + 1] = READ_PORT_UCHAR((PUCHAR)0x3C9);
                        g_Info.Palette[i * 3 + 2] = READ_PORT_UCHAR((PUCHAR)0x3C9);
                    }
                }
                return;
            }
        }
    }

    UnmapLfb();
    if (g_VgaWindow != NULL && LooksLikeText((const UCHAR *)g_VgaWindow + HV_TEXT_OFFSET))
        SetText();
    else
        SetLegacy();
}

static NTSTATUS CopyOut(PVOID dest, ULONG destBytes, const VOID *src, ULONG srcBytes)
{
    if (dest == NULL || src == NULL || destBytes < srcBytes)
        return STATUS_BUFFER_TOO_SMALL;
    __try {
        RtlCopyMemory(dest, src, srcBytes);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return STATUS_ACCESS_VIOLATION;
    }
    return STATUS_SUCCESS;
}

static NTSTATUS ReadFrame(PVOID dest, ULONG destBytes, PULONG written)
{
    ULONG x;
    ULONG y;
    ULONG copyW;
    ULONG copyH;
    UCHAR *out;
    const UCHAR *srcBase;
    ULONG bppBytes;
    ULONG srcPitch;

    *written = 0;
    RefreshMode();
    if (g_Info.Payload == 0 || g_Info.Payload > destBytes)
        return STATUS_BUFFER_TOO_SMALL;

    if (g_Info.Source == HVSHARE_SOURCE_LEGACY) {
        if (g_VgaWindow == NULL)
            return STATUS_DEVICE_NOT_READY;
        if (!NT_SUCCESS(CopyOut(dest, destBytes, g_VgaWindow, g_Info.Payload)))
            return STATUS_ACCESS_VIOLATION;
        *written = g_Info.Payload;
        return STATUS_SUCCESS;
    }

    if (g_Info.Source == HVSHARE_SOURCE_TEXT) {
        if (g_VgaWindow == NULL)
            return STATUS_DEVICE_NOT_READY;
        if (!NT_SUCCESS(CopyOut(dest, destBytes, (const UCHAR *)g_VgaWindow + HV_TEXT_OFFSET, g_Info.Payload)))
            return STATUS_ACCESS_VIOLATION;
        *written = g_Info.Payload;
        return STATUS_SUCCESS;
    }

    if (g_Lfb == NULL)
        return STATUS_DEVICE_NOT_READY;

    copyW = g_Info.Width;
    copyH = g_Info.Height;
    out = (UCHAR *)dest;
    bppBytes = g_BppBytes;
    srcPitch = g_SrcPitch;
    if (bppBytes == 0 || srcPitch == 0)
        return STATUS_DEVICE_NOT_READY;
    if ((g_YOff + copyH) * srcPitch > g_LfbMapBytes)
        return STATUS_DEVICE_NOT_READY;
    srcBase = (const UCHAR *)g_Lfb + g_YOff * srcPitch + g_XOff * bppBytes;

    __try {
        if (g_Info.Format == HVSHARE_FORMAT_INDEX8) {
            for (y = 0; y < copyH; y++)
                RtlCopyMemory(out + y * copyW, srcBase + y * srcPitch, copyW);
        } else {
            for (y = 0; y < copyH; y++) {
                const UCHAR *row = srcBase + y * srcPitch;
                UCHAR *dst = out + y * copyW * 4u;
                for (x = 0; x < copyW; x++) {
                    const UCHAR *p = row + x * bppBytes;
                    if (bppBytes == 2) {
                        USHORT pix = (USHORT)(p[0] | (p[1] << 8));
                        dst[x * 4 + 0] = (UCHAR)((pix & 0x1F) * 255 / 31);
                        dst[x * 4 + 1] = (UCHAR)(((pix >> 5) & 0x3F) * 255 / 63);
                        dst[x * 4 + 2] = (UCHAR)(((pix >> 11) & 0x1F) * 255 / 31);
                    } else if (bppBytes == 3) {
                        dst[x * 4 + 0] = p[0];
                        dst[x * 4 + 1] = p[1];
                        dst[x * 4 + 2] = p[2];
                    } else {
                        dst[x * 4 + 0] = p[0];
                        dst[x * 4 + 1] = p[1];
                        dst[x * 4 + 2] = p[2];
                    }
                    dst[x * 4 + 3] = 255;
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return STATUS_ACCESS_VIOLATION;
    }

    *written = g_Info.Payload;
    return STATUS_SUCCESS;
}

static NTSTATUS StoreDesktop(PVOID input, ULONG inputBytes)
{
    HVSHARE_PUBLISH hdr;
    ULONG payload;
    ULONG y;
    PUCHAR src;
    PUCHAR dst;
    PVOID neu;

    if (input == NULL || inputBytes < sizeof(HVSHARE_PUBLISH))
        return STATUS_BUFFER_TOO_SMALL;
    __try {
        RtlCopyMemory(&hdr, input, sizeof(hdr));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return STATUS_ACCESS_VIOLATION;
    }
    if (hdr.Format != HVSHARE_FORMAT_BGRA32)
        return STATUS_INVALID_PARAMETER;
    if (hdr.Width == 0 || hdr.Height == 0 || hdr.Width > HVSHARE_MAX_W || hdr.Height > HVSHARE_MAX_H)
        return STATUS_INVALID_PARAMETER;
    if (hdr.Pitch < hdr.Width * 4u || hdr.Pitch > HVSHARE_MAX_PAYLOAD)
        return STATUS_INVALID_PARAMETER;
    if (hdr.Height > HVSHARE_MAX_PAYLOAD / hdr.Pitch)
        return STATUS_INVALID_PARAMETER;
    if (hdr.Width > (HVSHARE_MAX_PAYLOAD / 4u) / hdr.Height)
        return STATUS_INVALID_PARAMETER;
    payload = hdr.Width * hdr.Height * 4u;
    if (inputBytes < sizeof(HVSHARE_PUBLISH) + hdr.Height * hdr.Pitch)
        return STATUS_BUFFER_TOO_SMALL;

    neu = ExAllocatePool2(POOL_FLAG_NON_PAGED, payload, HVSHARE_DESKTOP_TAG);
    if (neu == NULL)
        return STATUS_INSUFFICIENT_RESOURCES;

    src = (PUCHAR)input + sizeof(HVSHARE_PUBLISH);
    dst = (PUCHAR)neu;
    __try {
        for (y = 0; y < hdr.Height; y++)
            RtlCopyMemory(dst + (y * hdr.Width * 4u), src + (y * hdr.Pitch), hdr.Width * 4u);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ExFreePoolWithTag(neu, HVSHARE_DESKTOP_TAG);
        return STATUS_ACCESS_VIOLATION;
    }

    if (g_Desktop != NULL)
        ExFreePoolWithTag(g_Desktop, HVSHARE_DESKTOP_TAG);
    g_Desktop = neu;
    g_DesktopBytes = payload;
    g_HaveDesktop = TRUE;

    RtlZeroMemory(&g_Info, sizeof(g_Info));
    g_Info.Version = HVSHARE_VERSION;
    g_Info.Source = HVSHARE_SOURCE_DESKTOP;
    g_Info.Format = HVSHARE_FORMAT_BGRA32;
    g_Info.Width = hdr.Width;
    g_Info.Height = hdr.Height;
    g_Info.Pitch = hdr.Width * 4u;
    g_Info.Payload = payload;
    return STATUS_SUCCESS;
}

static NTSTATUS HvUnsupported(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    UNREFERENCED_PARAMETER(DeviceObject);
    Irp->IoStatus.Status = STATUS_INVALID_DEVICE_REQUEST;
    Irp->IoStatus.Information = 0;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return STATUS_INVALID_DEVICE_REQUEST;
}

static NTSTATUS HvCreateClose(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    UNREFERENCED_PARAMETER(DeviceObject);
    Irp->IoStatus.Status = STATUS_SUCCESS;
    Irp->IoStatus.Information = 0;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}

static NTSTATUS HvDeviceControl(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    PIO_STACK_LOCATION stack;
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR information = 0;
    PVOID buffer;
    ULONG outLen;
    ULONG inLen;

    UNREFERENCED_PARAMETER(DeviceObject);
    stack = IoGetCurrentIrpStackLocation(Irp);
    outLen = stack->Parameters.DeviceIoControl.OutputBufferLength;
    inLen = stack->Parameters.DeviceIoControl.InputBufferLength;

    ExAcquireResourceExclusiveLite(&g_Lock, TRUE);

    if (stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_HVSHARE_QUERY) {
        buffer = Irp->AssociatedIrp.SystemBuffer;
        if (!g_HaveDesktop)
            RefreshMode();
        if (buffer == NULL || outLen < sizeof(HVSHARE_INFO)) {
            status = STATUS_BUFFER_TOO_SMALL;
        } else {
            RtlCopyMemory(buffer, &g_Info, sizeof(HVSHARE_INFO));
            information = sizeof(HVSHARE_INFO);
            status = STATUS_SUCCESS;
        }
    } else if (stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_HVSHARE_PUBLISH) {
        buffer = Irp->AssociatedIrp.SystemBuffer;
        if (buffer == NULL || inLen < sizeof(HVSHARE_PUBLISH))
            status = STATUS_INVALID_PARAMETER;
        else
            status = StoreDesktop(buffer, inLen);
    } else if (stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_HVSHARE_READ) {
        if (Irp->MdlAddress == NULL) {
            status = STATUS_INVALID_PARAMETER;
        } else {
            buffer = MmGetSystemAddressForMdlSafe(Irp->MdlAddress, NormalPagePriority | MdlMappingNoExecute);
            if (buffer == NULL) {
                status = STATUS_INSUFFICIENT_RESOURCES;
            } else if (g_HaveDesktop) {
                status = CopyOut(buffer, outLen, g_Desktop, g_DesktopBytes);
                information = NT_SUCCESS(status) ? g_DesktopBytes : 0;
            } else {
                ULONG written = 0;
                status = ReadFrame(buffer, outLen, &written);
                information = written;
            }
        }
    }

    ExReleaseResourceLite(&g_Lock);

    Irp->IoStatus.Status = status;
    Irp->IoStatus.Information = information;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return status;
}

static VOID HvUnload(_In_ PDRIVER_OBJECT DriverObject)
{
    UNICODE_STRING link;

    UNREFERENCED_PARAMETER(DriverObject);
    if (g_LockReady)
        ExAcquireResourceExclusiveLite(&g_Lock, TRUE);
    UnmapLfb();
    if (g_VgaWindow != NULL) {
        MmUnmapIoSpace(g_VgaWindow, HV_VGA_BYTES);
        g_VgaWindow = NULL;
    }
    if (g_Desktop != NULL) {
        ExFreePoolWithTag(g_Desktop, HVSHARE_DESKTOP_TAG);
        g_Desktop = NULL;
        g_DesktopBytes = 0;
        g_HaveDesktop = FALSE;
    }
    if (g_LockReady) {
        ExReleaseResourceLite(&g_Lock);
        ExDeleteResourceLite(&g_Lock);
        g_LockReady = FALSE;
    }
    RtlInitUnicodeString(&link, L"\\??\\HvShare");
    IoDeleteSymbolicLink(&link);
    if (g_Device != NULL) {
        IoDeleteDevice(g_Device);
        g_Device = NULL;
    }
}

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    UNICODE_STRING devName;
    UNICODE_STRING link;
    NTSTATUS status;
    ULONG i;

    UNREFERENCED_PARAMETER(RegistryPath);

    status = ExInitializeResourceLite(&g_Lock);
    if (!NT_SUCCESS(status))
        return status;
    g_LockReady = TRUE;

    for (i = 0; i <= IRP_MJ_MAXIMUM_FUNCTION; i++)
        DriverObject->MajorFunction[i] = HvUnsupported;
    DriverObject->MajorFunction[IRP_MJ_CREATE] = HvCreateClose;
    DriverObject->MajorFunction[IRP_MJ_CLOSE] = HvCreateClose;
    DriverObject->MajorFunction[IRP_MJ_CLEANUP] = HvCreateClose;
    DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = HvDeviceControl;
    DriverObject->DriverUnload = HvUnload;

    RtlInitUnicodeString(&devName, L"\\Device\\HvShare");
    RtlInitUnicodeString(&link, L"\\??\\HvShare");
    status = IoCreateDevice(
        DriverObject,
        0,
        &devName,
        FILE_DEVICE_UNKNOWN,
        FILE_DEVICE_SECURE_OPEN,
        FALSE,
        &g_Device);
    if (!NT_SUCCESS(status)) {
        HvUnload(DriverObject);
        return status;
    }
    status = IoCreateSymbolicLink(&link, &devName);
    if (!NT_SUCCESS(status)) {
        HvUnload(DriverObject);
        return status;
    }
    g_Device->Flags |= DO_DIRECT_IO;
    g_Device->Flags &= ~DO_DEVICE_INITIALIZING;
    return STATUS_SUCCESS;
}
