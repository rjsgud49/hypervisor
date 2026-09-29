using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HvShareView
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ViewForm());
        }
    }

    sealed class HubPeer
    {
        public string Id;
        public string Name;
        public int Age;
    }

    sealed class ViewForm : Form
    {
        readonly Label _status;
        readonly object _gate = new object();
        Bitmap _frame;
        int _frames;
        volatile bool _running = true;
        const string HubUrl = "http://monitor.rjsgud.com:19723";

        static readonly Color Bg = Color.FromArgb(14, 17, 22);
        static readonly Color Panel = Color.FromArgb(23, 27, 34);
        static readonly Color Fg = Color.FromArgb(231, 237, 245);
        static readonly Color Muted = Color.FromArgb(147, 160, 180);

        public ViewForm()
        {
            Text = "HvShareView 신버전";
            BackColor = Bg;
            ForeColor = Fg;
            ClientSize = new Size(1100, 700);
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 10f);

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 64;
            bar.BackColor = Panel;
            Controls.Add(bar);

            _status = new Label();
            _status.Text = "중계 서버에서 화면을 기다리는 중";
            _status.ForeColor = Muted;
            _status.Bounds = new Rectangle(16, 8, 1040, 48);
            bar.Controls.Add(_status);

            Shown += delegate
            {
                Thread worker = new Thread(ReceiveLoop);
                worker.IsBackground = true;
                worker.Start();
            };
            FormClosing += delegate { _running = false; };
        }

        void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    string listText = HttpText(HubUrl + "/list");
                    HubPeer peer = Pick(ParseList(listText));
                    if (peer == null)
                    {
                        SetStatus("서버에 화면이 없습니다. 신버전 HvShare가 켜져 있어야 합니다.");
                        Thread.Sleep(400);
                        continue;
                    }
                    byte[] jpeg = HttpBytes(HubUrl + "/frame/" + peer.Id);
                    if (!ShowJpeg(jpeg, peer.Name))
                    {
                        SetStatus(peer.Name + " 화면을 아직 받지 못했습니다.");
                        Thread.Sleep(200);
                        continue;
                    }
                }
                catch (Exception)
                {
                    SetStatus("중계 서버에 연결하지 못했습니다. " + HubUrl);
                }
                Thread.Sleep(100);
            }
        }

        static HubPeer Pick(List<HubPeer> peers)
        {
            HubPeer best = null;
            for (int i = 0; i < peers.Count; i++)
            {
                if (best == null || peers[i].Age < best.Age)
                    best = peers[i];
            }
            return best;
        }

        static List<HubPeer> ParseList(string json)
        {
            List<HubPeer> peers = new List<HubPeer>();
            if (json == null)
                return peers;
            int at = 0;
            while (at < json.Length)
            {
                int idAt = json.IndexOf("\"id\":\"", at);
                if (idAt < 0)
                    break;
                idAt += 6;
                int idEnd = json.IndexOf('"', idAt);
                int nameAt = idEnd < 0 ? -1 : json.IndexOf("\"name\":\"", idEnd);
                if (nameAt < 0)
                    break;
                nameAt += 8;
                int nameEnd = json.IndexOf('"', nameAt);
                int ageAt = nameEnd < 0 ? -1 : json.IndexOf("\"age_ms\":", nameEnd);
                if (ageAt < 0)
                    break;
                ageAt += 9;
                int ageEnd = ageAt;
                while (ageEnd < json.Length && json[ageEnd] >= '0' && json[ageEnd] <= '9')
                    ageEnd++;
                int age;
                if (!int.TryParse(json.Substring(ageAt, ageEnd - ageAt), out age))
                    age = 9999;
                HubPeer peer = new HubPeer();
                peer.Id = json.Substring(idAt, idEnd - idAt);
                peer.Name = json.Substring(nameAt, nameEnd - nameAt);
                peer.Age = age;
                peers.Add(peer);
                at = ageEnd;
            }
            return peers;
        }

        bool ShowJpeg(byte[] jpeg, string name)
        {
            if (jpeg == null || jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
                return false;
            Bitmap shown;
            using (MemoryStream ms = new MemoryStream(jpeg))
            using (Bitmap tmp = new Bitmap(ms))
                shown = new Bitmap(tmp);
            lock (_gate)
            {
                Bitmap old = _frame;
                _frame = shown;
                if (old != null)
                    old.Dispose();
            }
            _frames++;
            SetStatus("서버 수신   " + name + "   " + shown.Width.ToString() + "×" + shown.Height.ToString()
                + "   프레임 " + _frames.ToString());
            try
            {
                BeginInvoke((MethodInvoker)delegate { Invalidate(); });
            }
            catch (Exception)
            {
            }
            return true;
        }

        static string HttpText(string url)
        {
            byte[] body = HttpBytes(url);
            if (body == null)
                return "";
            return Encoding.UTF8.GetString(body);
        }

        static byte[] HttpBytes(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 2500;
            req.ReadWriteTimeout = 2500;
            using (WebResponse response = req.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[8192];
                int n;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                    ms.Write(buf, 0, n);
                return ms.ToArray();
            }
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

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle client = ClientRectangle;
            client.Y += 64;
            client.Height -= 64;
            e.Graphics.Clear(Bg);
            Rectangle screen = new Rectangle(24, client.Y + 16, client.Width - 48, client.Height - 32);
            if (screen.Width < 80 || screen.Height < 80)
                return;
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
                e.Graphics.DrawImage(_frame, new Rectangle(dx, dy, dw, dh));
            }
        }
    }
}
