using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HvShare
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool send = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "/send", StringComparison.OrdinalIgnoreCase))
                    send = true;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SenderForm(send));
        }
    }

    static class Ioctl
    {
        public const uint FileDeviceUnknown = 0x22;
        public const uint MethodBuffered = 0;
        public const uint MethodInDirect = 1;
        public const uint MethodOutDirect = 2;
        public const uint FileReadData = 1;
        public const uint FileWriteData = 2;
        public const int InfoSize = 808;
        public const int PublishHeader = 16;
        public const int MaxPayload = 32 * 1024 * 1024;
        public const int MaxWidth = 4096;
        public const int MaxHeight = 2400;

        public const uint SourceLfb = 1;
        public const uint SourceText = 2;
        public const uint SourceLegacy = 3;
        public const uint SourceDesktop = 4;
        public const uint SourceGfx = 5;
        public const uint FormatIndex8 = 1;
        public const uint FormatText = 2;
        public const uint FormatBgra32 = 3;

        public static uint Code(uint function, uint method, uint access)
        {
            return (FileDeviceUnknown << 16) | (access << 14) | (function << 2) | method;
        }

        public static readonly uint Query = Code(0x800, MethodBuffered, FileReadData);
        public static readonly uint Read = Code(0x801, MethodOutDirect, FileReadData);
        public static readonly uint Publish = Code(0x802, MethodInDirect, FileWriteData);
        public static readonly uint Capture = Code(0x803, MethodBuffered, FileWriteData);
    }

    struct VgaInfo
    {
        public uint Version;
        public uint Source;
        public uint Format;
        public uint Width;
        public uint Height;
        public uint Pitch;
        public uint Payload;
        public uint PaletteScale;
        public ulong PhysBase;
        public byte[] Palette;

        public static bool TryParse(byte[] raw, out VgaInfo info)
        {
            info = new VgaInfo();
            if (raw == null || raw.Length < Ioctl.InfoSize)
                return false;
            info.Version = BitConverter.ToUInt32(raw, 0);
            info.Source = BitConverter.ToUInt32(raw, 4);
            info.Format = BitConverter.ToUInt32(raw, 8);
            info.Width = BitConverter.ToUInt32(raw, 12);
            info.Height = BitConverter.ToUInt32(raw, 16);
            info.Pitch = BitConverter.ToUInt32(raw, 20);
            info.Payload = BitConverter.ToUInt32(raw, 24);
            info.PaletteScale = BitConverter.ToUInt32(raw, 28);
            info.PhysBase = BitConverter.ToUInt64(raw, 32);
            info.Palette = new byte[768];
            Buffer.BlockCopy(raw, 40, info.Palette, 0, 768);
            if (info.Version != 1 || info.Width == 0 || info.Height == 0)
                return false;
            if (info.Width > Ioctl.MaxWidth || info.Height > Ioctl.MaxHeight)
                return false;
            if (info.Payload == 0 || info.Payload > Ioctl.MaxPayload)
                return false;
            return true;
        }
    }

    static class Driver
    {
        const uint ScManagerAllAccess = 0xF003F;
        const uint ServiceAllAccess = 0xF01FF;
        const uint ServiceKernelDriver = 1;
        const uint ServiceAutoStart = 2;
        const uint ServiceErrorNormal = 1;
        const uint ServiceNoChange = 0xFFFFFFFF;
        const uint ServiceControlStop = 1;
        const uint GenericRead = 0x80000000;
        const uint GenericWrite = 0x40000000;
        const uint OpenExisting = 3;
        const uint FileShareRead = 1;
        const uint FileShareWrite = 2;

        public static IntPtr Handle = Invalid();
        public static bool Created;
        public static bool Started;
        static IntPtr _scm;
        static IntPtr _service;

        public static IntPtr Invalid()
        {
            return new IntPtr(-1);
        }

        static void ConfigAuto(string bin)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo();
                info.FileName = "sc.exe";
                info.Arguments = "config HvShare start= auto binPath= " + bin;
                info.CreateNoWindow = true;
                info.UseShellExecute = false;
                System.Diagnostics.Process proc = System.Diagnostics.Process.Start(info);
                if (proc != null)
                    proc.WaitForExit(4000);
            }
            catch (Exception)
            {
            }
        }

        public static string SysPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HvShare.sys");
        }

        public static string Open(out string error)
        {
            error = "";
            if (Handle != Invalid())
                return "";
            if (!File.Exists(SysPath()))
            {
                error = "HvShare.sys 가 이 폴더에 없습니다. WDK로 드라이버를 빌드한 뒤 뷰어 옆에 두세요.";
                return error;
            }

            _scm = OpenSCManager(null, null, ScManagerAllAccess);
            if (_scm == IntPtr.Zero)
            {
                error = "서비스 관리자를 열지 못했습니다. 관리자 권한으로 실행하세요. " + Win32();
                return error;
            }

            string bin = SysPath();
            _service = CreateService(_scm, "HvShare", "HvShare", ServiceAllAccess,
                ServiceKernelDriver, ServiceAutoStart, ServiceErrorNormal,
                bin, null, IntPtr.Zero, null, null, null);
            if (_service == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 1073)
                {
                    _service = OpenService(_scm, "HvShare", ServiceAllAccess);
                    if (_service != IntPtr.Zero)
                        ConfigAuto(bin);
                }
                else
                {
                    error = "드라이버 서비스를 만들지 못했습니다. " + Win32();
                    return error;
                }
            }
            else
            {
                Created = true;
            }

            if (_service == IntPtr.Zero)
            {
                error = "드라이버 서비스를 열지 못했습니다. " + Win32();
                return error;
            }

            ServiceStatus current = new ServiceStatus();
            bool already = QueryServiceStatus(_service, ref current) && current.CurrentState == 4;
            if (!already && !StartService(_service, 0, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 123)
                {
                    ConfigAuto(bin);
                    if (StartService(_service, 0, IntPtr.Zero))
                        err = 0;
                    else
                        err = Marshal.GetLastWin32Error();
                }
                if (err != 0 && err != 1056)
                {
                    if (err == 577)
                        error = "드라이버 서명이 거부되었습니다. 테스트 서명이 켜진 머신에서만 로드됩니다.";
                    else
                        error = "드라이버를 시작하지 못했습니다. 코드 " + err.ToString() + "  " + bin;
                    return error;
                }
                if (err == 0)
                    Started = true;
            }
            else
            {
                Started = true;
            }

            for (int i = 0; i < 10; i++)
            {
                IntPtr handle = CreateFile(@"\\.\HvShare", GenericRead | GenericWrite,
                    FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (handle != Invalid())
                {
                    Handle = handle;
                    return "";
                }
                System.Threading.Thread.Sleep(100);
            }
            error = "\\\\.\\HvShare 장치를 열지 못했습니다. " + Win32();
            return error;
        }

        public static void Close()
        {
            if (Handle != Invalid())
            {
                CloseHandle(Handle);
                Handle = Invalid();
            }
            if (_service != IntPtr.Zero)
            {
                CloseServiceHandle(_service);
                _service = IntPtr.Zero;
            }
            if (_scm != IntPtr.Zero)
            {
                CloseServiceHandle(_scm);
                _scm = IntPtr.Zero;
            }
        }

        public static bool Query(out VgaInfo info, out string error)
        {
            info = new VgaInfo();
            error = "";
            byte[] raw = new byte[Ioctl.InfoSize];
            uint got;
            if (!DeviceIoControl(Handle, Ioctl.Query, IntPtr.Zero, 0, raw, (uint)raw.Length, out got, IntPtr.Zero))
            {
                error = "드라이버 프레임 정보를 읽지 못했습니다. " + Win32();
                return false;
            }
            if (!VgaInfo.TryParse(raw, out info))
            {
                error = "VGA 정보 형식이 맞지 않습니다.";
                return false;
            }
            return true;
        }

        public static byte[] Read(VgaInfo info, out string error)
        {
            error = "";
            byte[] payload = new byte[info.Payload];
            uint got;
            if (!DeviceIoControl(Handle, Ioctl.Read, IntPtr.Zero, 0, payload, info.Payload, out got, IntPtr.Zero))
            {
                error = "드라이버 프레임을 읽지 못했습니다. " + Win32();
                return null;
            }
            if (got < info.Payload)
            {
                error = "VGA 프레임이 잘렸습니다.";
                return null;
            }
            return payload;
        }

        public static bool Publish(Bitmap bmp, out string error)
        {
            error = "";
            int width = bmp.Width;
            int height = bmp.Height;
            int payload = width * height * 4;
            if (width <= 0 || height <= 0 || width > Ioctl.MaxWidth || height > Ioctl.MaxHeight || payload > Ioctl.MaxPayload)
            {
                error = "화면 크기가 드라이버 한도를 넘습니다.";
                return false;
            }
            byte[] packet = new byte[Ioctl.PublishHeader + payload];
            BitConverter.GetBytes(width).CopyTo(packet, 0);
            BitConverter.GetBytes(height).CopyTo(packet, 4);
            BitConverter.GetBytes(width * 4).CopyTo(packet, 8);
            BitConverter.GetBytes((int)Ioctl.FormatBgra32).CopyTo(packet, 12);
            BitmapData bits = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), packet, Ioctl.PublishHeader + y * width * 4, width * 4);
            }
            finally
            {
                bmp.UnlockBits(bits);
            }
            uint got;
            if (!DeviceIoControl(Handle, Ioctl.Publish, packet, (uint)packet.Length, IntPtr.Zero, 0, out got, IntPtr.Zero))
            {
                error = "드라이버에 프레임을 넣지 못했습니다. " + Win32();
                return false;
            }
            return true;
        }

        public static bool Capture(out string error)
        {
            error = "";
            byte[] unused = new byte[8];
            uint got;
            if (!DeviceIoControl(Handle, Ioctl.Capture, IntPtr.Zero, 0, unused, 0, out got, IntPtr.Zero))
            {
                error = "dxgkrnl/win32k 화면을 읽지 못했습니다. " + Win32();
                return false;
            }
            return true;
        }

        static string Win32()
        {
            return "코드 " + Marshal.GetLastWin32Error().ToString();
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr device, uint code, IntPtr inBuffer, uint inSize, byte[] outBuffer, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr device, uint code, byte[] inBuffer, uint inSize, IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenSCManager(string machine, string database, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateService(IntPtr scm, string name, string display, uint access, uint type, uint start, uint error, string binary, string group, IntPtr tag, string deps, string account, string password);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenService(IntPtr scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint errorControl, string binary, string group, IntPtr tag, string deps, string account, string password, string display);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool StartService(IntPtr service, uint argc, IntPtr argv);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool QueryServiceStatus(IntPtr service, ref ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DeleteService(IntPtr service);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool CloseServiceHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        struct ServiceStatus
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }
    }

    sealed class SenderForm : Form
    {
        readonly Label _status;
        readonly string _senderName;
        readonly string _senderId;
        readonly bool _send;
        volatile bool _running;
        Thread _capture;
        bool _hubOk;
        static System.Threading.Mutex _sendLock;
        const string HubUrl = "http://monitor.rjsgud.com:19723";

        public SenderForm(bool send)
        {
            _send = send;
            _senderName = Environment.MachineName;
            _senderId = CleanToken(_senderName);
            if (_senderId.Length == 0)
                _senderId = "pc";
            _senderId = _senderId + "-" + new Random().Next(0x1000, 0xFFFF).ToString("X");

            Text = "HvShare 신버전";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Color.FromArgb(14, 17, 22);
            ForeColor = Color.FromArgb(231, 237, 245);
            ClientSize = new Size(560, 200);
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = !send;

            Label lead = new Label();
            lead.Text = "실패";
            lead.ForeColor = Color.FromArgb(147, 160, 180);
            lead.Bounds = new Rectangle(24, 16, 512, 48);
            Controls.Add(lead);

            _status = new Label();
            _status.Text = "실패";
            _status.ForeColor = Color.FromArgb(231, 237, 245);
            _status.Bounds = new Rectangle(24, 76, 512, 100);
            Controls.Add(_status);

            FormClosing += delegate
            {
                _running = false;
                if (_send)
                    Driver.Close();
            };
            if (_send)
                StartSend();
            else
            {
                Shown += delegate
                {
                    Thread worker = new Thread(Install);
                    worker.IsBackground = true;
                    worker.Start();
                };
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(_send ? false : value);
        }

        void Install()
        {
            PrepareDriverTrust();
            string bin = Driver.SysPath();
            RunQuiet("sc.exe", "create HvShare type= kernel start= auto binPath= " + bin);
            RunQuiet("sc.exe", "config HvShare start= auto binPath= " + bin);
            RunQuiet("sc.exe", "start HvShare");
            RunQuiet("schtasks.exe", "/Delete /F /TN HvShareSend");
            SetStatus("실패");
            Thread.Sleep(200);
            try
            {
                BeginInvoke((MethodInvoker)delegate { Close(); });
            }
            catch (Exception)
            {
            }
        }

        void StartSend()
        {
            bool created;
            _sendLock = new System.Threading.Mutex(true, @"Local\HvShareSend", out created);
            if (!created)
            {
                Environment.Exit(0);
                return;
            }
            string error;
            Driver.Open(out error);
            if (error.Length != 0)
            {
                PrepareDriverTrust();
                Driver.Close();
                Driver.Open(out error);
            }
            _running = true;
            if (error.Length != 0)
                _capture = new Thread(DirectSendLoop);
            else
                _capture = new Thread(SendLoop);
            _capture.IsBackground = true;
            _capture.Start();
        }

        static void PrepareDriverTrust()
        {
            string cer = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HvShare.cer");
            if (File.Exists(cer))
            {
                RunQuiet("certutil.exe", "-addstore -f Root \"" + cer + "\"");
                RunQuiet("certutil.exe", "-addstore -f TrustedPublisher \"" + cer + "\"");
            }
            RunQuiet("bcdedit.exe", "/set testsigning on");
        }

        static bool ServiceRunning()
        {
            try
            {
                System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo();
                info.FileName = "sc.exe";
                info.Arguments = "query HvShare";
                info.CreateNoWindow = true;
                info.UseShellExecute = false;
                info.RedirectStandardOutput = true;
                System.Diagnostics.Process proc = System.Diagnostics.Process.Start(info);
                if (proc == null)
                    return false;
                string text = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(2000);
                return text.IndexOf("RUNNING") >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static int RunQuiet(string file, string args)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo();
                info.FileName = file;
                info.Arguments = args;
                info.CreateNoWindow = true;
                info.UseShellExecute = false;
                System.Diagnostics.Process proc = System.Diagnostics.Process.Start(info);
                if (proc == null)
                    return -1;
                DateTime start = DateTime.UtcNow;
                while (!proc.HasExited && (DateTime.UtcNow - start).TotalMilliseconds < 4000)
                    Thread.Sleep(100);
                if (!proc.HasExited)
                {
                    try
                    {
                        proc.Kill();
                    }
                    catch (Exception)
                    {
                    }
                    return -1;
                }
                return proc.ExitCode;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        void DirectSendLoop()
        {
            int frames = 0;
            while (_running)
            {
                string error;
                Bitmap shot = CaptureDesktop(out error);
                if (!_running)
                {
                    if (shot != null)
                        shot.Dispose();
                    break;
                }
                if (shot == null)
                {
                    SetStatus(error);
                    Thread.Sleep(200);
                    continue;
                }
                byte[] jpeg = EncodeBitmap(shot);
                shot.Dispose();
                if (jpeg != null)
                    PostHub(jpeg);
                frames++;
                string link = _hubOk ? "서버로 보내는 중" : "서버 연결 대기";
                SetStatus("드라이버는 재부팅 뒤에 올라갑니다.\r\n" + link + "   프레임 " + frames.ToString() + "\r\n" + HubUrl);
                Thread.Sleep(100);
            }
        }

        void SendLoop()
        {
            int frames = 0;
            EnsureGuiThread();
            while (_running && Driver.Handle != Driver.Invalid())
            {
                string error;
                Bitmap shot = CaptureDesktop(out error);
                if (!_running)
                {
                    if (shot != null)
                        shot.Dispose();
                    break;
                }
                if (shot == null)
                {
                    SetStatus(error);
                    Thread.Sleep(200);
                    continue;
                }
                bool published = Driver.Publish(shot, out error);
                shot.Dispose();
                if (!published)
                {
                    SetStatus(error);
                    Thread.Sleep(200);
                    continue;
                }
                VgaInfo info;
                if (!Driver.Query(out info, out error) || (info.Source != Ioctl.SourceDesktop && info.Source != Ioctl.SourceGfx))
                {
                    SetStatus(error.Length != 0 ? error : "드라이버가 화면 프레임을 들고 있지 않습니다.");
                    Thread.Sleep(200);
                    continue;
                }
                byte[] payload = Driver.Read(info, out error);
                if (payload == null)
                {
                    SetStatus(error);
                    Thread.Sleep(200);
                    continue;
                }
                byte[] jpeg = EncodeJpeg(info, payload);
                if (jpeg != null)
                    PostHub(jpeg);
                frames++;
                string link = _hubOk ? "서버로 보내는 중" : "서버 연결 대기";
                SetStatus("합성 화면 " + info.Width.ToString() + "×" + info.Height.ToString()
                    + "\r\n" + link + "   프레임 " + frames.ToString()
                    + "\r\n" + HubUrl);
                Thread.Sleep(100);
            }
        }

        static void EnsureGuiThread()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, dc);
        }

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        void PostHub(byte[] jpeg)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(HubUrl + "/frame");
                req.Method = "POST";
                req.ContentType = "image/jpeg";
                req.Headers.Add("X-Hv-Id", _senderId);
                req.Headers.Add("X-Hv-Name", _senderName);
                req.Timeout = 2500;
                req.ReadWriteTimeout = 2500;
                req.ServicePoint.Expect100Continue = false;
                req.ContentLength = jpeg.Length;
                using (Stream stream = req.GetRequestStream())
                    stream.Write(jpeg, 0, jpeg.Length);
                using (WebResponse response = req.GetResponse())
                {
                }
                _hubOk = true;
            }
            catch (Exception)
            {
                _hubOk = false;
            }
        }

        static byte[] EncodeJpeg(VgaInfo info, byte[] payload)
        {
            int width = (int)info.Width;
            int height = (int)info.Height;
            int pitch = (int)info.Pitch;
            if (width <= 0 || height <= 0 || pitch < width * 4 || payload.Length < pitch * height)
                return null;
            int outW = width > 1920 ? 1920 : width;
            int outH = width > 1920 ? Math.Max(1, height * 1920 / width) : height;
            Bitmap bmp = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
            try
            {
                BitmapData bits = bmp.LockBits(new Rectangle(0, 0, outW, outH), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    for (int y = 0; y < outH; y++)
                    {
                        int srcY = width > 1920 ? y * height / outH : y;
                        int row = srcY * pitch;
                        byte[] line = new byte[outW * 3];
                        for (int x = 0; x < outW; x++)
                        {
                            int srcX = width > 1920 ? x * width / outW : x;
                            int p = row + srcX * 4;
                            line[x * 3 + 0] = payload[p + 0];
                            line[x * 3 + 1] = payload[p + 1];
                            line[x * 3 + 2] = payload[p + 2];
                        }
                        Marshal.Copy(line, 0, IntPtr.Add(bits.Scan0, y * bits.Stride), line.Length);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bits);
                }
                ImageCodecInfo codec = null;
                ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
                for (int i = 0; i < codecs.Length; i++)
                {
                    if (codecs[i].MimeType == "image/jpeg")
                    {
                        codec = codecs[i];
                        break;
                    }
                }
                if (codec == null)
                    return null;
                using (EncoderParameters ep = new EncoderParameters(1))
                {
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
                    using (MemoryStream ms = new MemoryStream())
                    {
                        bmp.Save(ms, codec, ep);
                        if (ms.Length <= 0 || ms.Length > 8 * 1024 * 1024)
                            return null;
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                bmp.Dispose();
            }
        }

        static byte[] EncodeBitmap(Bitmap source)
        {
            Bitmap frame = source;
            Bitmap scaled = null;
            try
            {
                if (source.Width > 1920)
                {
                    int height = Math.Max(1, source.Height * 1920 / source.Width);
                    scaled = new Bitmap(1920, height, PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(scaled))
                        g.DrawImage(source, 0, 0, 1920, height);
                    frame = scaled;
                }
                return SaveJpeg(frame);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (scaled != null)
                    scaled.Dispose();
            }
        }

        static byte[] SaveJpeg(Image frame)
        {
            ImageCodecInfo codec = null;
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < codecs.Length; i++)
            {
                if (codecs[i].MimeType == "image/jpeg")
                {
                    codec = codecs[i];
                    break;
                }
            }
            if (codec == null)
                return null;
            using (EncoderParameters ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
                using (MemoryStream ms = new MemoryStream())
                {
                    frame.Save(ms, codec, ep);
                    if (ms.Length <= 0 || ms.Length > 8 * 1024 * 1024)
                        return null;
                    return ms.ToArray();
                }
            }
        }

        static Bitmap CaptureDesktop(out string error)
        {
            error = "";
            try
            {
                Rectangle bounds = Screen.PrimaryScreen.Bounds;
                Bitmap bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                return bmp;
            }
            catch (Exception ex)
            {
                error = "화면을 읽지 못했습니다. " + ex.Message;
                return null;
            }
        }

        static string CleanToken(string raw)
        {
            if (raw == null)
                return "";
            StringBuilder acc = new StringBuilder();
            for (int i = 0; i < raw.Length && acc.Length < 40; i++)
            {
                char c = raw[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (ok)
                    acc.Append(c);
            }
            return acc.ToString();
        }

        void SetStatus(string text)
        {
            if (IsDisposed)
                return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate { SetStatus(text); });
                }
                catch (Exception)
                {
                }
                return;
            }
            _status.Text = text;
        }
    }
}
