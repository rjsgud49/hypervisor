#include <ntddk.h>
#include <wsk.h>

/* Posts the frame the driver already holds to the HvShare relay.
   Winsock Kernel TCP. No display-stack read. */

void HvNetSignal(void);
PVOID HvShareSnapshot(ULONG *outBytes);
void HvShareSnapshotFree(PVOID buffer);

NTSYSAPI
NTSTATUS
NTAPI
ZwWaitForSingleObject(
    _In_ HANDLE Handle,
    _In_ BOOLEAN Alertable,
    _In_opt_ PLARGE_INTEGER Timeout);

static VOID ReleaseWsk(void);

#define HV_NET_PORT 19723

static KEVENT g_Wake;
static volatile LONG g_WakeReady;
static volatile LONG g_Stop;
static volatile LONG g_ThreadOn;
static HANDLE g_Thread;
static KEVENT g_Done;
static BOOLEAN g_ReuseIrp;
static WSK_REGISTRATION g_Reg;
static WSK_PROVIDER_NPI g_Npi;
static WSK_CLIENT_DISPATCH g_ClientDispatch;
static BOOLEAN g_WskRegistered;
static BOOLEAN g_WskCaptured;
static BOOLEAN g_WskDns;
static BOOLEAN g_HavePeer;
static SOCKADDR_IN g_Peer;
static CHAR g_Id[49] = "hvshare-k";

void HvNetSignal(void)
{
    if (InterlockedCompareExchange(&g_WakeReady, 1, 1) == 1)
        KeSetEvent(&g_Wake, IO_NO_INCREMENT, FALSE);
}

static NTSTATUS NetComplete(_In_ PDEVICE_OBJECT DeviceObject, _In_ PIRP Irp, _In_opt_ PVOID Context)
{
    UNREFERENCED_PARAMETER(DeviceObject);
    UNREFERENCED_PARAMETER(Irp);
    KeSetEvent((PKEVENT)Context, IO_NO_INCREMENT, FALSE);
    return STATUS_MORE_PROCESSING_REQUIRED;
}

static VOID NetPrepare(_Inout_ PIRP Irp)
{
    KeClearEvent(&g_Done);
    if (g_ReuseIrp)
        IoReuseIrp(Irp, STATUS_UNSUCCESSFUL);
    g_ReuseIrp = TRUE;
    IoSetCompletionRoutine(Irp, NetComplete, &g_Done, TRUE, TRUE, TRUE);
}

static NTSTATUS NetInvoke(_In_ PIRP Irp, _In_ NTSTATUS status)
{
    LARGE_INTEGER timeout;
    NTSTATUS wait;

    if (status != STATUS_PENDING)
        return status;
    timeout.QuadPart = -150000000LL;
    wait = KeWaitForSingleObject(&g_Done, Executive, KernelMode, FALSE, &timeout);
    if (wait == STATUS_TIMEOUT) {
        IoCancelIrp(Irp);
        KeWaitForSingleObject(&g_Done, Executive, KernelMode, FALSE, NULL);
        return STATUS_IO_TIMEOUT;
    }
    return Irp->IoStatus.Status;
}

static BOOLEAN ParseV4(_In_ PCWSTR text, _Out_ ULONG *addr)
{
    ULONG part[4];
    ULONG i;
    ULONG n;
    ULONG value;

    if (text == NULL)
        return FALSE;
    for (i = 0; i < 4; i++) {
        if (text[0] < L'0' || text[0] > L'9')
            return FALSE;
        n = 0;
        value = 0;
        while (text[0] >= L'0' && text[0] <= L'9') {
            value = value * 10u + (ULONG)(text[0] - L'0');
            if (value > 255u)
                return FALSE;
            text++;
            n++;
        }
        if (n == 0)
            return FALSE;
        part[i] = value;
        if (i < 3) {
            if (text[0] != L'.')
                return FALSE;
            text++;
        }
    }
    if (text[0] != 0)
        return FALSE;
    *addr = part[0] | (part[1] << 8) | (part[2] << 16) | (part[3] << 24);
    return TRUE;
}

static BOOLEAN ReadServerIp(_Out_ SOCKADDR_IN *peer)
{
    UNICODE_STRING path = RTL_CONSTANT_STRING(
        L"\\Registry\\Machine\\SYSTEM\\CurrentControlSet\\Services\\HvShare\\Parameters");
    UNICODE_STRING name = RTL_CONSTANT_STRING(L"ServerIp");
    OBJECT_ATTRIBUTES attrs;
    HANDLE key = NULL;
    UCHAR raw[256];
    ULONG got = 0;
    PKEY_VALUE_PARTIAL_INFORMATION info;
    WCHAR text[64];
    ULONG chars;
    ULONG i;
    ULONG addr;
    NTSTATUS status;

    RtlZeroMemory(peer, sizeof(*peer));
    InitializeObjectAttributes(&attrs, &path, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);
    status = ZwOpenKey(&key, KEY_READ, &attrs);
    if (!NT_SUCCESS(status))
        return FALSE;
    status = ZwQueryValueKey(key, &name, KeyValuePartialInformation, raw, sizeof(raw), &got);
    ZwClose(key);
    if (!NT_SUCCESS(status))
        return FALSE;
    info = (PKEY_VALUE_PARTIAL_INFORMATION)raw;
    if (info->Type != REG_SZ || info->DataLength < sizeof(WCHAR))
        return FALSE;
    chars = info->DataLength / sizeof(WCHAR);
    if (chars >= RTL_NUMBER_OF(text))
        chars = RTL_NUMBER_OF(text) - 1;
    RtlCopyMemory(text, info->Data, chars * sizeof(WCHAR));
    text[chars] = 0;
    for (i = 0; i < chars; i++) {
        if (text[i] == 0)
            break;
    }
    text[i] = 0;
    if (!ParseV4(text, &addr))
        return FALSE;
    peer->sin_family = AF_INET;
    {
        USHORT port = (USHORT)HV_NET_PORT;
        peer->sin_port = (USHORT)((port << 8) | (port >> 8));
    }
    peer->sin_addr.s_addr = addr;
    return TRUE;
}

static VOID LoadId(void)
{
    UNICODE_STRING path = RTL_CONSTANT_STRING(
        L"\\Registry\\Machine\\SYSTEM\\CurrentControlSet\\Control\\ComputerName\\ActiveComputerName");
    UNICODE_STRING name = RTL_CONSTANT_STRING(L"ComputerName");
    OBJECT_ATTRIBUTES attrs;
    HANDLE key = NULL;
    UCHAR raw[256];
    ULONG got = 0;
    PKEY_VALUE_PARTIAL_INFORMATION info;
    ULONG chars;
    ULONG i;
    ULONG o = 0;
    WCHAR ch;
    CHAR out[49];

    InitializeObjectAttributes(&attrs, &path, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);
    if (!NT_SUCCESS(ZwOpenKey(&key, KEY_READ, &attrs)))
        return;
    if (!NT_SUCCESS(ZwQueryValueKey(key, &name, KeyValuePartialInformation, raw, sizeof(raw), &got))) {
        ZwClose(key);
        return;
    }
    ZwClose(key);
    info = (PKEY_VALUE_PARTIAL_INFORMATION)raw;
    if (info->Type != REG_SZ)
        return;
    chars = info->DataLength / sizeof(WCHAR);
    RtlZeroMemory(out, sizeof(out));
    for (i = 0; i < chars && o < 46; i++) {
        ch = ((PWCH)info->Data)[i];
        if (ch == 0)
            break;
        if ((ch >= L'A' && ch <= L'Z') || (ch >= L'a' && ch <= L'z') || (ch >= L'0' && ch <= L'9') || ch == L'_' || ch == L'-')
            out[o++] = (CHAR)ch;
    }
    if (o == 0)
        return;
    out[o++] = '-';
    out[o++] = 'k';
    out[o] = 0;
    RtlCopyMemory(g_Id, out, o + 1);
}

static ULONG AppendText(_Out_writes_(cap) CHAR *dst, _In_ ULONG cap, _In_ ULONG at, _In_z_ const CHAR *text)
{
    while (text[0] != 0 && at + 1 < cap) {
        dst[at++] = text[0];
        text++;
    }
    if (at < cap)
        dst[at] = 0;
    return at;
}

static ULONG AppendNum(_Out_writes_(cap) CHAR *dst, _In_ ULONG cap, _In_ ULONG at, _In_ ULONG value)
{
    CHAR tmp[16];
    ULONG n = 0;

    if (value == 0)
        tmp[n++] = '0';
    while (value != 0 && n < sizeof(tmp)) {
        tmp[n++] = (CHAR)('0' + (value % 10u));
        value /= 10u;
    }
    while (n > 0 && at + 1 < cap)
        dst[at++] = tmp[--n];
    if (at < cap)
        dst[at] = 0;
    return at;
}

static NTSTATUS SendAll(_In_ PWSK_SOCKET socket, _In_ PIRP irp, _In_ PVOID data, _In_ SIZE_T length)
{
    PWSK_PROVIDER_CONNECTION_DISPATCH dispatch;
    PMDL mdl;
    WSK_BUF buf;
    NTSTATUS status;

    if (length == 0 || length > 0xFFFFFFFFu)
        return STATUS_INVALID_PARAMETER;
    dispatch = (PWSK_PROVIDER_CONNECTION_DISPATCH)socket->Dispatch;
    mdl = IoAllocateMdl(data, (ULONG)length, FALSE, FALSE, NULL);
    if (mdl == NULL)
        return STATUS_INSUFFICIENT_RESOURCES;
    MmBuildMdlForNonPagedPool(mdl);
    buf.Mdl = mdl;
    buf.Offset = 0;
    buf.Length = length;
    NetPrepare(irp);
    status = dispatch->WskSend(socket, &buf, 0, irp);
    status = NetInvoke(irp, status);
    if (NT_SUCCESS(status) && irp->IoStatus.Information != length)
        status = STATUS_CONNECTION_ABORTED;
    IoFreeMdl(mdl);
    return status;
}

static VOID CloseSocket(_In_ PWSK_SOCKET socket, _In_ PIRP irp)
{
    PWSK_PROVIDER_CONNECTION_DISPATCH dispatch;
    NTSTATUS status;

    dispatch = (PWSK_PROVIDER_CONNECTION_DISPATCH)socket->Dispatch;
    NetPrepare(irp);
    status = dispatch->WskCloseSocket(socket, irp);
    NetInvoke(irp, status);
}

static NTSTATUS PostFrame(_In_ PIRP irp, _In_ PVOID body, _In_ ULONG bodyBytes)
{
    SOCKADDR_IN local;
    PWSK_SOCKET socket = NULL;
    PUCHAR header;
    ULONG at = 0;
    NTSTATUS status;

    if (!g_HavePeer || g_Npi.Dispatch == NULL)
        return STATUS_HOST_UNREACHABLE;
    header = ExAllocatePool2(POOL_FLAG_NON_PAGED, 384, 'nSvH');
    if (header == NULL)
        return STATUS_INSUFFICIENT_RESOURCES;

    RtlZeroMemory(&local, sizeof(local));
    local.sin_family = AF_INET;
    NetPrepare(irp);
    status = g_Npi.Dispatch->WskSocketConnect(
        g_Npi.Client,
        SOCK_STREAM,
        IPPROTO_TCP,
        (PSOCKADDR)&local,
        (PSOCKADDR)&g_Peer,
        0,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        irp);
    status = NetInvoke(irp, status);
    if (!NT_SUCCESS(status)) {
        ExFreePoolWithTag(header, 'nSvH');
        return status;
    }
    socket = (PWSK_SOCKET)irp->IoStatus.Information;
    if (socket == NULL) {
        ExFreePoolWithTag(header, 'nSvH');
        return STATUS_CONNECTION_REFUSED;
    }

    at = AppendText((PCHAR)header, 384, at, "POST /frame HTTP/1.0\r\nHost: monitor.rjsgud.com\r\nX-Hv-Id: ");
    at = AppendText((PCHAR)header, 384, at, g_Id);
    at = AppendText((PCHAR)header, 384, at, "\r\nX-Hv-Name: ");
    at = AppendText((PCHAR)header, 384, at, g_Id);
    at = AppendText((PCHAR)header, 384, at, "\r\nContent-Length: ");
    at = AppendNum((PCHAR)header, 384, at, bodyBytes);
    at = AppendText((PCHAR)header, 384, at, "\r\nConnection: close\r\n\r\n");
    status = SendAll(socket, irp, header, at);
    if (NT_SUCCESS(status))
        status = SendAll(socket, irp, body, bodyBytes);
    ExFreePoolWithTag(header, 'nSvH');
    CloseSocket(socket, irp);
    return status;
}

static BOOLEAN ResolvePeer(_In_ PIRP irp)
{
    UNICODE_STRING node = RTL_CONSTANT_STRING(L"monitor.rjsgud.com");
    UNICODE_STRING service = RTL_CONSTANT_STRING(L"19723");
    ADDRINFOEXW hints;
    PADDRINFOEXW result = NULL;
    PADDRINFOEXW cursor;
    NTSTATUS status;

    g_HavePeer = FALSE;
    if (g_WskDns && g_Npi.Dispatch != NULL && g_Npi.Dispatch->WskGetAddressInfo != NULL) {
        RtlZeroMemory(&hints, sizeof(hints));
        hints.ai_family = AF_INET;
        hints.ai_socktype = SOCK_STREAM;
        hints.ai_protocol = IPPROTO_TCP;
        NetPrepare(irp);
        status = g_Npi.Dispatch->WskGetAddressInfo(
            g_Npi.Client,
            &node,
            &service,
            0,
            NULL,
            &hints,
            &result,
            NULL,
            NULL,
            irp);
        status = NetInvoke(irp, status);
        if (NT_SUCCESS(status) && result != NULL) {
            for (cursor = result; cursor != NULL; cursor = cursor->ai_next) {
                if (cursor->ai_family == AF_INET && cursor->ai_addr != NULL && cursor->ai_addrlen >= sizeof(SOCKADDR_IN)) {
                    RtlCopyMemory(&g_Peer, cursor->ai_addr, sizeof(SOCKADDR_IN));
                    g_HavePeer = TRUE;
                    break;
                }
            }
            if (g_Npi.Dispatch->WskFreeAddressInfo != NULL)
                g_Npi.Dispatch->WskFreeAddressInfo(g_Npi.Client, result);
        }
    }
    if (!g_HavePeer)
        g_HavePeer = ReadServerIp(&g_Peer);
    return g_HavePeer;
}

static NTSTATUS CaptureWsk(void)
{
    WSK_CLIENT_NPI client;
    NTSTATUS status;

    if (g_WskCaptured)
        return STATUS_SUCCESS;
    if (g_WskRegistered)
        ReleaseWsk();
    RtlZeroMemory(&g_ClientDispatch, sizeof(g_ClientDispatch));
    g_ClientDispatch.Version = MAKE_WSK_VERSION(1, 1);
    client.ClientContext = NULL;
    client.Dispatch = &g_ClientDispatch;
    RtlZeroMemory(&g_Reg, sizeof(g_Reg));
    status = WskRegister(&client, &g_Reg);
    if (!NT_SUCCESS(status)) {
        g_ClientDispatch.Version = MAKE_WSK_VERSION(1, 0);
        RtlZeroMemory(&g_Reg, sizeof(g_Reg));
        status = WskRegister(&client, &g_Reg);
        g_WskDns = FALSE;
    } else {
        g_WskDns = TRUE;
    }
    if (!NT_SUCCESS(status))
        return status;
    g_WskRegistered = TRUE;
    status = WskCaptureProviderNPI(&g_Reg, 3000, &g_Npi);
    if (!NT_SUCCESS(status))
        return status;
    g_WskCaptured = TRUE;
    return STATUS_SUCCESS;
}

static VOID ReleaseWsk(void)
{
    if (g_WskCaptured) {
        WskReleaseProviderNPI(&g_Reg);
        g_WskCaptured = FALSE;
    }
    if (g_WskRegistered) {
        WskDeregister(&g_Reg);
        g_WskRegistered = FALSE;
    }
}

static VOID NetThread(_In_ PVOID Context)
{
    PIRP irp;
    LARGE_INTEGER timeout;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(Context);
    KeInitializeEvent(&g_Done, NotificationEvent, FALSE);
    LoadId();
    irp = IoAllocateIrp(1, FALSE);
    if (irp == NULL) {
        PsTerminateSystemThread(STATUS_INSUFFICIENT_RESOURCES);
        return;
    }
    status = CaptureWsk();
    while (InterlockedCompareExchange(&g_Stop, 0, 0) == 0) {
        if (!NT_SUCCESS(status)) {
            ReleaseWsk();
            status = CaptureWsk();
        } else if (!g_HavePeer) {
            ResolvePeer(irp);
        }
        /* Desktop JPEG is posted by HvShare.exe. This thread used to upload
           the raw HVBG snapshot (about 2MB). Remote viewers then selected
           that peer and could not decode it, so the window stayed blank.
           After HvShare.exe exits there is no new JPEG until it starts again. */
        if (InterlockedCompareExchange(&g_Stop, 0, 0) != 0)
            break;
        KeClearEvent(&g_Wake);
        timeout.QuadPart = -10000000LL;
        KeWaitForSingleObject(&g_Wake, Executive, KernelMode, FALSE, &timeout);
    }
    IoFreeIrp(irp);
    ReleaseWsk();
    PsTerminateSystemThread(STATUS_SUCCESS);
}

NTSTATUS HvNetStart(void)
{
    NTSTATUS status;

    KeInitializeEvent(&g_Wake, SynchronizationEvent, FALSE);
    InterlockedExchange(&g_WakeReady, 1);
    status = PsCreateSystemThread(&g_Thread, THREAD_ALL_ACCESS, NULL, NULL, NULL, NetThread, NULL);
    if (!NT_SUCCESS(status))
        return status;
    InterlockedExchange(&g_ThreadOn, 1);
    return STATUS_SUCCESS;
}

void HvNetStop(void)
{
    HANDLE thread;

    if (InterlockedCompareExchange(&g_ThreadOn, 0, 1) != 1)
        return;
    InterlockedExchange(&g_Stop, 1);
    KeSetEvent(&g_Wake, IO_NO_INCREMENT, FALSE);
    thread = g_Thread;
    g_Thread = NULL;
    if (thread != NULL) {
        ZwWaitForSingleObject(thread, FALSE, NULL);
        ZwClose(thread);
    }
}
