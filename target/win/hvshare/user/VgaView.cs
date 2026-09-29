using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HvShare
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new VgaForm());
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
        const uint ServiceDemandStart = 3;
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
                ServiceKernelDriver, ServiceDemandStart, ServiceErrorNormal,
                bin, null, IntPtr.Zero, null, null, null);
            if (_service == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 1073)
                {
                    _service = OpenService(_scm, "HvShare", ServiceAllAccess);
                    if (_service != IntPtr.Zero)
                        ChangeServiceConfig(_service, ServiceNoChange, ServiceNoChange, ServiceNoChange,
                            bin, null, IntPtr.Zero, null, null, null, null);
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

            if (!StartService(_service, 0, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 123)
                {
                    ChangeServiceConfig(_service, ServiceNoChange, ServiceNoChange, ServiceNoChange,
                        bin, null, IntPtr.Zero, null, null, null, null);
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
            if (_service != IntPtr.Zero && Started)
            {
                ServiceStatus status = new ServiceStatus();
                ControlService(_service, ServiceControlStop, ref status);
                Started = false;
            }
            if (_service != IntPtr.Zero && Created)
            {
                DeleteService(_service);
                Created = false;
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

    sealed class VgaForm : Form
    {
        readonly Label _status;
        readonly Button _retry;
        readonly object _gate = new object();
        Bitmap _frame;
        int _frames;
        volatile bool _running;
        Thread _capture;

        static readonly Color Bg = Color.FromArgb(14, 17, 22);
        static readonly Color Panel = Color.FromArgb(23, 27, 34);
        static readonly Color Fg = Color.FromArgb(231, 237, 245);
        static readonly Color Muted = Color.FromArgb(147, 160, 180);
        static readonly Color Accent = Color.FromArgb(61, 126, 238);
        static readonly Color[] Ega = new Color[]
        {
            Color.FromArgb(0, 0, 0), Color.FromArgb(0, 0, 170), Color.FromArgb(0, 170, 0), Color.FromArgb(0, 170, 170),
            Color.FromArgb(170, 0, 0), Color.FromArgb(170, 0, 170), Color.FromArgb(170, 85, 0), Color.FromArgb(170, 170, 170),
            Color.FromArgb(85, 85, 85), Color.FromArgb(85, 85, 255), Color.FromArgb(85, 255, 85), Color.FromArgb(85, 255, 255),
            Color.FromArgb(255, 85, 85), Color.FromArgb(255, 85, 255), Color.FromArgb(255, 255, 85), Color.FromArgb(255, 255, 255)
        };

        public VgaForm()
        {
            Text = "HV 화면";
            BackColor = Bg;
            ForeColor = Fg;
            ClientSize = new Size(1100, 700);
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 10f);
            KeyPreview = true;
            KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                    Close();
            };

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 64;
            bar.BackColor = Panel;
            Controls.Add(bar);

            _status = new Label();
            _status.Text = "드라이버를 여는 중";
            _status.ForeColor = Muted;
            _status.Bounds = new Rectangle(16, 8, 860, 48);
            bar.Controls.Add(_status);

            _retry = new Button();
            _retry.Text = "다시 연결";
            _retry.Bounds = new Rectangle(980, 16, 100, 32);
            _retry.FlatStyle = FlatStyle.Flat;
            _retry.BackColor = Accent;
            _retry.ForeColor = Color.White;
            _retry.FlatAppearance.BorderSize = 0;
            _retry.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _retry.Click += delegate { Connect(); };
            bar.Controls.Add(_retry);

            Shown += delegate { Connect(); };
            FormClosing += delegate
            {
                StopCapture();
                Driver.Close();
            };
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

        void Connect()
        {
            StopCapture();
            Driver.Close();
            string error;
            Driver.Open(out error);
            if (error.Length != 0)
            {
                SetStatus(error);
                Invalidate();
                return;
            }
            _running = true;
            _capture = new Thread(CaptureLoop);
            _capture.IsBackground = true;
            _capture.Start();
        }

        void StopCapture()
        {
            _running = false;
        }

        void CaptureLoop()
        {
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
                    Thread.Sleep(100);
                    continue;
                }
                bool published = Driver.Publish(shot, out error);
                shot.Dispose();
                if (!published)
                {
                    SetStatus(error);
                    Thread.Sleep(100);
                    continue;
                }
                VgaInfo info;
                if (!Driver.Query(out info, out error) || info.Source != Ioctl.SourceDesktop)
                {
                    SetStatus(error.Length != 0 ? error : "드라이버가 바탕화면 프레임을 들고 있지 않습니다.");
                    Thread.Sleep(100);
                    continue;
                }
                byte[] payload = Driver.Read(info, out error);
                if (payload == null)
                {
                    SetStatus(error);
                    Thread.Sleep(100);
                    continue;
                }
                Bitmap shown = BuildBgra(info, payload);
                if (shown == null)
                {
                    SetStatus("드라이버 프레임을 그릴 수 없습니다.");
                    Thread.Sleep(100);
                    continue;
                }
                lock (_gate)
                {
                    Bitmap old = _frame;
                    _frame = shown;
                    if (old != null)
                        old.Dispose();
                }
                _frames++;
                SetStatus("드라이버 프레임   " + info.Width.ToString() + "×" + info.Height.ToString() + "   프레임 " + _frames.ToString());
                try
                {
                    BeginInvoke((MethodInvoker)delegate { Invalidate(); });
                }
                catch (Exception)
                {
                }
                Thread.Sleep(100);
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

        static string Describe(VgaInfo info, int frames)
        {
            string kind;
            if (info.Source == Ioctl.SourceDesktop)
                kind = "드라이버 프레임";
            else if (info.Source == Ioctl.SourceLfb)
                kind = "QEMU VGA 선형 프레임버퍼";
            else if (info.Source == Ioctl.SourceText)
                kind = "VGA 텍스트 메모리 0xB8000";
            else
                kind = "레거시 VGA 0xA0000. 최신 Windows 바탕화면은 이 창에 없습니다";
            return kind + "   " + info.Width.ToString() + "×" + info.Height.ToString()
                + "   0x" + info.PhysBase.ToString("X")
                + "   프레임 " + frames.ToString();
        }

        static Bitmap Build(VgaInfo info, byte[] payload)
        {
            if (info.Format == Ioctl.FormatIndex8)
                return BuildIndex(info, payload);
            if (info.Format == Ioctl.FormatBgra32)
                return BuildBgra(info, payload);
            if (info.Format == Ioctl.FormatText)
                return BuildText(info, payload);
            return null;
        }

        static Bitmap BuildIndex(VgaInfo info, byte[] payload)
        {
            int width = (int)info.Width;
            int height = (int)info.Height;
            int shift = info.PaletteScale == 2 ? 2 : 0;
            if (payload.Length < width * height)
                return null;
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            BitmapData bits = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            byte[] row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int p = payload[y * width + x];
                    int o = p * 3;
                    row[x * 4 + 0] = Scale(info.Palette[o + 2], shift);
                    row[x * 4 + 1] = Scale(info.Palette[o + 1], shift);
                    row[x * 4 + 2] = Scale(info.Palette[o + 0], shift);
                    row[x * 4 + 3] = 255;
                }
                Marshal.Copy(row, 0, IntPtr.Add(bits.Scan0, y * bits.Stride), width * 4);
            }
            bmp.UnlockBits(bits);
            return bmp;
        }

        static byte Scale(byte value, int shift)
        {
            int n = value << shift;
            if (n > 255)
                n = 255;
            return (byte)n;
        }

        static Bitmap BuildBgra(VgaInfo info, byte[] payload)
        {
            int width = (int)info.Width;
            int height = (int)info.Height;
            int pitch = (int)info.Pitch;
            if (pitch < width * 4 || payload.Length < pitch * height)
                return null;
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            BitmapData bits = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            for (int y = 0; y < height; y++)
                Marshal.Copy(payload, y * pitch, IntPtr.Add(bits.Scan0, y * bits.Stride), width * 4);
            bmp.UnlockBits(bits);
            return bmp;
        }

        static Bitmap BuildText(VgaInfo info, byte[] payload)
        {
            int cols = (int)info.Width;
            int rows = (int)info.Height;
            if (payload.Length < cols * rows * 2)
                return null;
            int cw = 9;
            int ch = 16;
            Bitmap bmp = new Bitmap(Math.Max(1, cols * cw), Math.Max(1, rows * ch), PixelFormat.Format24bppRgb);
            SolidBrush[] ink = new SolidBrush[16];
            SolidBrush[] back = new SolidBrush[8];
            for (int i = 0; i < 16; i++)
                ink[i] = new SolidBrush(Ega[i]);
            for (int i = 0; i < 8; i++)
                back[i] = new SolidBrush(Ega[i]);
            using (Graphics g = Graphics.FromImage(bmp))
            using (Font font = new Font("Consolas", 11f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.Clear(Color.Black);
                for (int y = 0; y < rows; y++)
                {
                    for (int x = 0; x < cols; x++)
                    {
                        int i = (y * cols + x) * 2;
                        char glyph = (char)payload[i];
                        if (glyph < 32 || glyph > 126)
                            glyph = ' ';
                        int attr = payload[i + 1];
                        Rectangle cell = new Rectangle(x * cw, y * ch, cw, ch);
                        g.FillRectangle(back[(attr >> 4) & 7], cell);
                        g.DrawString(glyph.ToString(), font, ink[attr & 0x0F], cell.X, cell.Y - 1);
                    }
                }
            }
            for (int i = 0; i < 16; i++)
                ink[i].Dispose();
            for (int i = 0; i < 8; i++)
                back[i].Dispose();
            return bmp;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle client = ClientRectangle;
            client.Y += 64;
            client.Height -= 64;
            e.Graphics.Clear(Bg);
            Rectangle outer = new Rectangle(24, client.Y + 16, client.Width - 48, client.Height - 32);
            if (outer.Width < 80 || outer.Height < 80)
                return;
            using (SolidBrush bezel = new SolidBrush(Color.FromArgb(10, 12, 16)))
                e.Graphics.FillRectangle(bezel, outer);
            int pad = 14;
            Rectangle screen = new Rectangle(outer.X + pad, outer.Y + pad, outer.Width - pad * 2, outer.Height - pad * 2);
            lock (_gate)
            {
                if (_frame == null)
                {
                    TextRenderer.DrawText(e.Graphics, "화면 대기", Font, screen, Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }
                float scale = Math.Min((float)screen.Width / _frame.Width, (float)screen.Height / _frame.Height);
                int dw = Math.Max(1, (int)(_frame.Width * scale));
                int dh = Math.Max(1, (int)(_frame.Height * scale));
                int dx = screen.X + (screen.Width - dw) / 2;
                int dy = screen.Y + (screen.Height - dh) / 2;
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                e.Graphics.DrawImage(_frame, new Rectangle(dx, dy, dw, dh));
            }
        }
    }
}
