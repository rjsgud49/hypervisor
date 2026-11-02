using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
            Application.Run(new CleanForm());
        }
    }

    sealed class CleanForm : Form
    {
        readonly TextBox _log;
        readonly Button _go;
        const string Thumb = "BBB32E6A294E510615B8BCA36C0A598F7ADD047B";

        public CleanForm()
        {
            Text = "HvShare 제거";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new System.Drawing.Size(560, 420);
            Font = new System.Drawing.Font("Segoe UI", 10f);

            Label lead = new Label();
            lead.Text = "서비스, 예약 작업, 인증서, 테스트 서명을 지웁니다. 소스 파일은 그대로 둡니다.";
            lead.Bounds = new System.Drawing.Rectangle(16, 12, 528, 40);
            Controls.Add(lead);

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Bounds = new System.Drawing.Rectangle(16, 56, 528, 300);
            Controls.Add(_log);

            _go = new Button();
            _go.Text = "지금 지우기";
            _go.Bounds = new System.Drawing.Rectangle(16, 372, 160, 32);
            _go.Click += delegate { BeginClean(); };
            Controls.Add(_go);
        }

        void BeginClean()
        {
            _go.Enabled = false;
            _log.Clear();
            Thread worker = new Thread(Clean);
            worker.IsBackground = true;
            worker.Start();
        }

        void Clean()
        {
            Line("예약 작업 HvShareSend 삭제");
            Line(Run("schtasks.exe", "/Delete /F /TN HvShareSend"));
            Kill("HvShare");
            Kill("HvShareView");
            Thread.Sleep(500);
            Line(Service.Remove());
            string cer = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HvShare.cer");
            Line(Certs.Remove(cer, Thumb));
            Line("테스트 서명 끄기");
            Line(Run("bcdedit.exe", "/set testsigning off"));
            Line("끝났습니다. 드라이버가 메모리에 남아 있으면 재부팅해야 장치와 테스트 서명이 빠집니다.");
        }

        static void Kill(string name)
        {
            Process[] found = Process.GetProcessesByName(name);
            if (found.Length == 0)
            {
                Line(name + ".exe 없음");
                return;
            }
            for (int i = 0; i < found.Length; i++)
            {
                try
                {
                    found[i].Kill();
                    Line(name + ".exe 종료 " + found[i].Id.ToString());
                }
                catch (Exception ex)
                {
                    Line(name + ".exe 종료 실패 " + ex.Message);
                }
            }
        }

        static string Run(string file, string args)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = file;
                info.Arguments = args;
                info.CreateNoWindow = true;
                info.UseShellExecute = false;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = Encoding.Default;
                info.StandardErrorEncoding = Encoding.Default;
                Process proc = Process.Start(info);
                if (proc == null)
                    return file + " 시작 실패";
                string text = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                proc.WaitForExit(8000);
                text = text.Trim();
                if (text.Length == 0)
                    text = "코드 " + proc.ExitCode.ToString();
                return text;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        static void Line(string text)
        {
            CleanForm form = Application.OpenForms.Count > 0 ? Application.OpenForms[0] as CleanForm : null;
            if (form == null)
                return;
            form.BeginInvoke((MethodInvoker)delegate
            {
                form._log.AppendText(text + Environment.NewLine);
            });
        }
    }

    static class Service
    {
        const uint ScManagerAllAccess = 0xF003F;
        const uint ServiceAllAccess = 0xF01FF;
        const uint ServiceControlStop = 1;
        const uint ServiceStopped = 1;

        public static string Remove()
        {
            IntPtr scm = OpenSCManager(null, null, ScManagerAllAccess);
            if (scm == IntPtr.Zero)
                return "서비스 관리자를 열지 못했습니다. " + Win32();
            IntPtr service = OpenService(scm, "HvShare", ServiceAllAccess);
            if (service == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                CloseServiceHandle(scm);
                if (err == 1060)
                    return "HvShare 서비스 없음";
                return "HvShare 서비스를 열지 못했습니다. " + err.ToString();
            }

            ServiceStatus status = new ServiceStatus();
            if (!ControlService(service, ServiceControlStop, ref status))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1062)
                {
                    CloseServiceHandle(service);
                    CloseServiceHandle(scm);
                    return "HvShare 중지에 실패했습니다. " + err.ToString();
                }
            }
            for (int i = 0; i < 20; i++)
            {
                if (QueryServiceStatus(service, ref status) && status.CurrentState == ServiceStopped)
                    break;
                Thread.Sleep(250);
            }
            bool deleted = DeleteService(service);
            int deleteErr = Marshal.GetLastWin32Error();
            CloseServiceHandle(service);
            CloseServiceHandle(scm);
            if (!deleted && deleteErr != 1072)
                return "HvShare 삭제에 실패했습니다. " + deleteErr.ToString();
            if (status.CurrentState != ServiceStopped)
                return "HvShare 는 삭제 예약됐습니다. 재부팅하면 빠집니다.";
            return "HvShare 서비스를 지웠습니다.";
        }

        static string Win32()
        {
            return Marshal.GetLastWin32Error().ToString();
        }

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

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenSCManager(string machine, string database, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenService(IntPtr scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool QueryServiceStatus(IntPtr service, ref ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DeleteService(IntPtr service);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool CloseServiceHandle(IntPtr handle);
    }

    static class Certs
    {
        public static string Remove(string cerPath, string knownThumb)
        {
            string thumb = knownThumb;
            if (File.Exists(cerPath))
            {
                try
                {
                    X509Certificate2 file = new X509Certificate2(cerPath);
                    thumb = file.Thumbprint;
                }
                catch (Exception ex)
                {
                    return "인증서 파일을 읽지 못했습니다. " + ex.Message;
                }
            }
            int removed = 0;
            removed += Drop(StoreName.Root, thumb);
            removed += Drop(StoreName.TrustedPublisher, thumb);
            if (!string.Equals(thumb, knownThumb, StringComparison.OrdinalIgnoreCase))
            {
                removed += Drop(StoreName.Root, knownThumb);
                removed += Drop(StoreName.TrustedPublisher, knownThumb);
            }
            return "인증서 " + removed.ToString() + "개 제거";
        }

        static int Drop(StoreName name, string thumb)
        {
            int count = 0;
            X509Store store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            try
            {
                X509Certificate2Collection found = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, false);
                for (int i = 0; i < found.Count; i++)
                {
                    store.Remove(found[i]);
                    count++;
                }
            }
            finally
            {
                store.Close();
            }
            return count;
        }
    }
}
