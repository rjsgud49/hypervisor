using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HvScreen
{
    static class Program
    {
        public const int Port = 19721;
        public const int DiscoverPort = 19722;
        public const int HubPort = 19723;
        public const string MonitorUrl = "http://monitor.rjsgud.com:19723";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try
            {
                SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch (Exception)
            {
                SetProcessDPIAware();
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            }
            catch (Exception)
            {
            }
            RelaySettings.Load();

#if RELAY
            Application.Run(new HubForm());
#elif VIEW
            Application.Run(new RelayViewForm());
#elif SHARE
            Application.Run(new ShareForm());
#else
            throw new InvalidOperationException("role");
#endif
        }
    }

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(14, 17, 22);
        public static readonly Color Panel = Color.FromArgb(23, 27, 34);
        public static readonly Color Text = Color.FromArgb(231, 237, 245);
        public static readonly Color Muted = Color.FromArgb(147, 160, 180);
        public static readonly Color Accent = Color.FromArgb(61, 126, 238);
    }

    static class Wire
    {
        public const int MaxFrame = 8 * 1024 * 1024;

        public static string[] LocalIpv4()
        {
            List<string> list = new List<string>();
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < nics.Length; i++)
            {
                NetworkInterface ni = nics[i];
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (UnicastIPAddressInformation addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        list.Add(addr.Address.ToString());
                }
            }
            return list.ToArray();
        }

        public static byte[] ReadExact(NetworkStream stream, int length)
        {
            byte[] buf = new byte[length];
            int off = 0;
            while (off < length)
            {
                int n = stream.Read(buf, off, length - off);
                if (n <= 0)
                    throw new EndOfStreamException();
                off += n;
            }
            return buf;
        }

        public static void WriteAll(NetworkStream stream, byte[] buf)
        {
            stream.Write(buf, 0, buf.Length);
        }

        public static byte[] CaptureJpeg(long quality, int maxWidth)
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            using (Bitmap raw = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(raw))
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

                Bitmap frame = raw;
                Bitmap scaled = null;
                if (raw.Width > maxWidth)
                {
                    int height = Math.Max(1, raw.Height * maxWidth / raw.Width);
                    scaled = new Bitmap(maxWidth, height, PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(scaled))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(raw, 0, 0, maxWidth, height);
                    }
                    frame = scaled;
                }

                try
                {
                    return EncodeJpeg(frame, quality);
                }
                finally
                {
                    if (scaled != null)
                        scaled.Dispose();
                }
            }
        }

        static byte[] EncodeJpeg(Image image, long quality)
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
                throw new InvalidOperationException("JPEG encoder missing");

            using (EncoderParameters ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                using (MemoryStream ms = new MemoryStream())
                {
                    image.Save(ms, codec, ep);
                    return ms.ToArray();
                }
            }
        }

        public static Bitmap DecodeJpeg(byte[] jpeg)
        {
            using (MemoryStream ms = new MemoryStream(jpeg))
            using (Bitmap tmp = new Bitmap(ms))
                return new Bitmap(tmp);
        }
    }

    static class RelaySettings
    {
        public static string Url = "";
        public static string Room = "";

        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hv-relay.txt"); }
        }

        public static void Load()
        {
            Url = "";
            Room = "";
            try
            {
                if (File.Exists(FilePath))
                {
                    string[] lines = File.ReadAllLines(FilePath);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.StartsWith("url="))
                            Url = line.Substring(4).Trim();
                        else if (line.StartsWith("room="))
                            Room = line.Substring(5).Trim();
                    }
                }
            }
            catch (Exception)
            {
            }
            Room = HubServer.CleanToken(Room);
            if (Room.Length < 4)
                Room = NewRoom();
            Url = Program.MonitorUrl;
        }

        public static void Save()
        {
            Room = HubServer.CleanToken(Room);
            if (Room.Length < 4)
                Room = NewRoom();
            Url = Program.MonitorUrl;
            File.WriteAllLines(FilePath, new string[] { "url=" + Url, "room=" + Room });
        }

        public static string TrimUrl(string url)
        {
            if (url == null)
                return "";
            url = url.Trim();
            while (url.EndsWith("/"))
                url = url.Substring(0, url.Length - 1);
            return url;
        }

        public static string RoomPage(string url, string room)
        {
            return TrimUrl(url) + "/r/" + HubServer.CleanToken(room) + "/";
        }

        static string NewRoom()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 10);
        }
    }

    sealed class HubSlot
    {
        public string Name;
        public byte[] Jpeg;
        public DateTime SeenUtc;
    }

    sealed class HubServer
    {
        readonly object _gate = new object();
        readonly Dictionary<string, Dictionary<string, HubSlot>> _rooms = new Dictionary<string, Dictionary<string, HubSlot>>();
        TcpListener _listener;
        volatile bool _running;

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, Program.HubPort);
            _listener.Start();
            _running = true;
            Thread accept = new Thread(AcceptLoop);
            accept.IsBackground = true;
            accept.Start();
            Thread beacon = new Thread(BeaconLoop);
            beacon.IsBackground = true;
            beacon.Start();
        }

        public void Stop()
        {
            _running = false;
            try
            {
                if (_listener != null)
                    _listener.Stop();
            }
            catch (Exception)
            {
            }
        }

        void BeaconLoop()
        {
            UdpClient udp = new UdpClient();
            try
            {
                udp.EnableBroadcast = true;
                byte[] msg = System.Text.Encoding.ASCII.GetBytes("HVHUB1 " + Program.HubPort);
                while (_running)
                {
                    try
                    {
                        udp.Send(msg, msg.Length, new IPEndPoint(IPAddress.Broadcast, Program.DiscoverPort));
                        udp.Send(msg, msg.Length, new IPEndPoint(IPAddress.Loopback, Program.DiscoverPort));
                    }
                    catch (Exception)
                    {
                    }
                    Thread.Sleep(500);
                }
            }
            finally
            {
                udp.Close();
            }
        }

        void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    break;
                }
                Thread worker = new Thread(delegate () { Handle(client); });
                worker.IsBackground = true;
                worker.Start();
            }
        }

        void Handle(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 8000;
                NetworkStream stream = client.GetStream();
                string header = ReadHeader(stream);
                string[] lines = header.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0)
                    return;
                string[] req = lines[0].Split(' ');
                if (req.Length < 2)
                    return;
                string method = req[0];
                string path = req[1];
                int query = path.IndexOf('?');
                if (query >= 0)
                    path = path.Substring(0, query);

                int length = HeaderInt(lines, "Content-Length");
                byte[] body = length > 0 ? Wire.ReadExact(stream, length) : new byte[0];

                string room;
                string tail;
                if (!SplitRoom(path, out room, out tail))
                {
                    byte[] hint = System.Text.Encoding.UTF8.GetBytes("missing");
                    WriteResponse(stream, 404, "Not Found", "text/plain", hint);
                    return;
                }
                if (method == "GET" && tail.Length == 0)
                {
                    string loc = "/r/" + room + "/";
                    string head = "HTTP/1.1 302 Found\r\nLocation: " + loc
                        + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    byte[] hb = System.Text.Encoding.ASCII.GetBytes(head);
                    stream.Write(hb, 0, hb.Length);
                }
                else if (method == "GET" && tail == "/")
                {
                    byte[] page = System.Text.Encoding.UTF8.GetBytes(Page());
                    WriteResponse(stream, 200, "OK", "text/html; charset=utf-8", page);
                }
                else if (method == "GET" && tail == "/list")
                {
                    byte[] json = System.Text.Encoding.UTF8.GetBytes(ListJson(room));
                    WriteResponse(stream, 200, "OK", "application/json; charset=utf-8", json);
                }
                else if (method == "GET" && tail.StartsWith("/frame/"))
                {
                    string id = CleanToken(tail.Substring("/frame/".Length));
                    byte[] jpeg = Frame(room, id);
                    if (jpeg == null)
                        WriteResponse(stream, 404, "Not Found", "text/plain", System.Text.Encoding.ASCII.GetBytes("missing"));
                    else
                        WriteResponse(stream, 200, "OK", "image/jpeg", jpeg);
                }
                else if (method == "POST" && tail == "/frame")
                {
                    string id = CleanToken(HeaderValue(lines, "X-Hv-Id"));
                    string name = HeaderValue(lines, "X-Hv-Name");
                    if (id.Length == 0 || body.Length == 0 || body.Length > Wire.MaxFrame)
                        WriteResponse(stream, 400, "Bad Request", "text/plain", System.Text.Encoding.ASCII.GetBytes("bad"));
                    else
                    {
                        if (name == null || name.Length == 0)
                            name = id;
                        Put(room, id, name, body);
                        WriteResponse(stream, 204, "No Content", "text/plain", new byte[0]);
                    }
                }
                else
                {
                    WriteResponse(stream, 404, "Not Found", "text/plain", System.Text.Encoding.ASCII.GetBytes("missing"));
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                client.Close();
            }
        }

        static bool SplitRoom(string path, out string room, out string tail)
        {
            room = "live";
            tail = "";
            if (path == null)
                return false;
            if (path == "/" || path == "/list" || path == "/frame" || path.StartsWith("/frame/"))
            {
                tail = path;
                return true;
            }
            if (!path.StartsWith("/r/"))
                return false;
            string rest = path.Substring(3);
            int slash = rest.IndexOf('/');
            string raw = slash < 0 ? rest : rest.Substring(0, slash);
            room = CleanToken(raw);
            if (room.Length < 4 || room != raw)
                return false;
            tail = slash < 0 ? "" : rest.Substring(slash);
            return true;
        }

        void Put(string room, string id, string name, byte[] jpeg)
        {
            lock (_gate)
            {
                Dictionary<string, HubSlot> slots;
                if (!_rooms.TryGetValue(room, out slots))
                {
                    slots = new Dictionary<string, HubSlot>();
                    _rooms[room] = slots;
                }
                HubSlot slot;
                if (!slots.TryGetValue(id, out slot))
                {
                    slot = new HubSlot();
                    slots[id] = slot;
                }
                slot.Name = name;
                slot.Jpeg = jpeg;
                slot.SeenUtc = DateTime.UtcNow;
            }
        }

        byte[] Frame(string room, string id)
        {
            lock (_gate)
            {
                Dictionary<string, HubSlot> slots;
                if (!_rooms.TryGetValue(room, out slots))
                    return null;
                HubSlot slot;
                if (!slots.TryGetValue(id, out slot))
                    return null;
                if ((DateTime.UtcNow - slot.SeenUtc).TotalSeconds > 3)
                    return null;
                return slot.Jpeg;
            }
        }

        string ListJson(string room)
        {
            StringBuilder json = new StringBuilder();
            json.Append("[");
            bool first = true;
            DateTime now = DateTime.UtcNow;
            lock (_gate)
            {
                Dictionary<string, HubSlot> slots;
                if (_rooms.TryGetValue(room, out slots))
                {
                    List<string> drop = new List<string>();
                    foreach (KeyValuePair<string, HubSlot> pair in slots)
                    {
                        double ageSec = (now - pair.Value.SeenUtc).TotalSeconds;
                        if (ageSec > 3 || pair.Value.Jpeg == null)
                        {
                            drop.Add(pair.Key);
                            continue;
                        }
                        if (!first)
                            json.Append(",");
                        first = false;
                        int age = (int)(ageSec * 1000);
                        json.Append("{\"id\":\"");
                        json.Append(Escape(pair.Key));
                        json.Append("\",\"name\":\"");
                        json.Append(Escape(pair.Value.Name));
                        json.Append("\",\"age_ms\":");
                        json.Append(age.ToString());
                        json.Append("}");
                    }
                    for (int i = 0; i < drop.Count; i++)
                        slots.Remove(drop[i]);
                    if (slots.Count == 0)
                        _rooms.Remove(room);
                }
            }
            json.Append("]");
            return json.ToString();
        }

        static string Escape(string value)
        {
            if (value == null)
                return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static string Page()
        {
            return "<!DOCTYPE html><html lang=\"ko\"><head><meta charset=\"utf-8\"/>"
                + "<title>HV 화면 모음</title><style>"
                + "body{margin:0;background:#0e1116;color:#e7edf5;font:15px/1.4 Segoe UI,sans-serif}"
                + "header{padding:22px 24px 8px}h1{margin:0 0 6px;font-size:22px}"
                + "p{margin:0;color:#93a0b4}"
                + "#wall{display:grid;grid-template-columns:repeat(auto-fit,minmax(420px,1fr));gap:16px;padding:16px 24px 32px}"
                + ".card{background:#171b22;border:1px solid #2a3140;border-radius:14px;overflow:hidden}"
                + ".card header{padding:10px 12px 0}img{width:100%;background:#000;display:block}"
                + "#empty{color:#93a0b4;padding:48px 24px}"
                + "</style></head><body><header><h1>실행 중인 화면</h1>"
                + "<p id=\"lead\">화면 보내기가 켜진 PC가 여기 모두 표시됩니다.</p></header>"
                + "<div id=\"empty\">아직 실행 중인 화면이 없습니다.</div><div id=\"wall\"></div>"
                + "<script>"
                + "const wall=document.getElementById('wall');"
                + "const empty=document.getElementById('empty');"
                + "const cards={};"
                + "async function tick(){"
                + "let list=[];"
                + "try{list=await (await fetch('list',{cache:'no-store'})).json();}catch(e){return;}"
                + "const seen={};"
                + "empty.hidden=list.length>0;"
                + "for(const item of list){"
                + "seen[item.id]=true;"
                + "let card=cards[item.id];"
                + "if(!card){"
                + "card=document.createElement('section');"
                + "card.className='card';"
                + "card.innerHTML='<header></header><img alt=\"\">';"
                + "wall.appendChild(card);"
                + "cards[item.id]=card;"
                + "}"
                + "card.querySelector('header').textContent=item.name;"
                + "card.querySelector('img').src='frame/'+encodeURIComponent(item.id)+'?t='+Date.now();"
                + "}"
                + "Object.keys(cards).forEach(function(id){"
                + "if(!seen[id]){cards[id].remove();delete cards[id];}"
                + "});"
                + "}"
                + "tick();setInterval(tick,400);"
                + "</script></body></html>";
        }

        static string ReadHeader(NetworkStream stream)
        {
            MemoryStream ms = new MemoryStream();
            byte[] one = new byte[1];
            while (ms.Length < 65536)
            {
                int n = stream.Read(one, 0, 1);
                if (n <= 0)
                    throw new EndOfStreamException();
                ms.WriteByte(one[0]);
                if (ms.Length >= 4)
                {
                    byte[] buf = ms.ToArray();
                    int len = buf.Length;
                    if (buf[len - 4] == 13 && buf[len - 3] == 10 && buf[len - 2] == 13 && buf[len - 1] == 10)
                        break;
                }
            }
            return System.Text.Encoding.ASCII.GetString(ms.ToArray());
        }

        static int HeaderInt(string[] lines, string name)
        {
            string value = HeaderValue(lines, name);
            if (value == null)
                return 0;
            int parsed;
            if (!int.TryParse(value.Trim(), out parsed) || parsed < 0)
                return 0;
            return parsed;
        }

        static string HeaderValue(string[] lines, string name)
        {
            string prefix = name + ":";
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return lines[i].Substring(prefix.Length).Trim();
            }
            return null;
        }

        public static string CleanToken(string raw)
        {
            if (raw == null)
                return "";
            string acc = "";
            for (int i = 0; i < raw.Length && acc.Length < 48; i++)
            {
                char c = raw[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (ok)
                    acc += c;
            }
            return acc;
        }

        static void WriteResponse(NetworkStream stream, int code, string reason, string contentType, byte[] body)
        {
            string head = "HTTP/1.1 " + code + " " + reason
                + "\r\nContent-Type: " + contentType
                + "\r\nContent-Length: " + body.Length
                + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            byte[] hb = System.Text.Encoding.ASCII.GetBytes(head);
            stream.Write(hb, 0, hb.Length);
            if (body.Length > 0)
                stream.Write(body, 0, body.Length);
        }
    }

    static class Ui
    {
        public static TextBox Box(string text, int x, int y, int width)
        {
            TextBox box = new TextBox();
            box.Text = text;
            box.Bounds = new Rectangle(x, y, width, 28);
            box.BackColor = Theme.Panel;
            box.ForeColor = Theme.Text;
            box.BorderStyle = BorderStyle.FixedSingle;
            return box;
        }

        public static Label Mute(string text, int x, int y, int width, int height)
        {
            Label label = new Label();
            label.Text = text;
            label.ForeColor = Theme.Muted;
            label.Bounds = new Rectangle(x, y, width, height);
            return label;
        }
    }

    sealed class HubForm : Form
    {
        readonly HubServer _hub = new HubServer();
        readonly Label _status;

        public HubForm()
        {
            Text = "HV 중계 서버";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            ClientSize = new Size(520, 220);
            Font = new Font("Segoe UI", 10f);

            Controls.Add(Ui.Mute("이 PC에서 중계를 엽니다. 도메인이 이 PC를 가리키고 19723 포트가 열려 있어야 합니다.", 24, 16, 472, 44));

            _status = new Label();
            _status.ForeColor = Theme.Text;
            _status.Bounds = new Rectangle(24, 72, 472, 120);
            _status.Text = "여는 중";
            Controls.Add(_status);

            Shown += delegate { OpenHub(); };
            FormClosing += delegate { _hub.Stop(); };
        }

        void OpenHub()
        {
            try
            {
                _hub.Start();
            }
            catch (Exception ex)
            {
                _status.Text = "중계 서버를 열지 못했습니다. " + ex.Message;
                return;
            }
            _status.Text = "감시 주소\r\n" + Program.MonitorUrl + "/";
        }
    }

    sealed class RelayViewForm : Form
    {
        readonly Label _status;

        public RelayViewForm()
        {
            Text = "HV 화면 보기";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            ClientSize = new Size(520, 160);
            Font = new Font("Segoe UI", 10f);

            _status = new Label();
            _status.ForeColor = Theme.Text;
            _status.Bounds = new Rectangle(24, 24, 472, 110);
            _status.Text = "여는 중";
            Controls.Add(_status);
            Shown += delegate { OpenPage(); };
        }

        void OpenPage()
        {
            string page = Program.MonitorUrl + "/";
            _status.Text = "브라우저에서 보는 중입니다.\r\n" + page;
            try
            {
                System.Diagnostics.Process.Start(page);
            }
            catch (Exception ex)
            {
                _status.Text = "브라우저를 열지 못했습니다. " + ex.Message;
            }
        }
    }

    sealed class ShareForm : Form
    {
        readonly Label _status;
        volatile bool _running = true;
        Thread _captureThread;
        readonly string _senderId;
        readonly string _senderName;
        bool _hubPosted;

        public ShareForm()
        {
            _senderName = Environment.MachineName;
            _senderId = HubServer.CleanToken(_senderName) + "-" + new Random().Next(0x1000, 0xFFFF).ToString("X");
            if (_senderId.StartsWith("-"))
                _senderId = "pc" + _senderId;
            Text = "HV 화면 보내기";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            ClientSize = new Size(520, 180);
            Font = new Font("Segoe UI", 10f);

            Controls.Add(Ui.Mute("이 PC 화면을 감시 주소로 보냅니다. 창을 켜 두면 바로 보입니다.", 24, 16, 472, 36));
            Label addr = new Label();
            addr.Text = Program.MonitorUrl + "/";
            addr.ForeColor = Theme.Text;
            addr.Bounds = new Rectangle(24, 60, 472, 28);
            Controls.Add(addr);

            _status = new Label();
            _status.Text = "연결하는 중";
            _status.ForeColor = Theme.Muted;
            _status.Bounds = new Rectangle(24, 100, 472, 56);
            Controls.Add(_status);

            Shown += delegate { StartShare(); };
            FormClosing += delegate { StopShare(); };
        }

        void SetStatus(string text)
        {
            if (IsDisposed)
                return;
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)delegate { SetStatus(text); });
                return;
            }
            _status.Text = text;
        }

        void StartShare()
        {
            if (_captureThread == null)
            {
                _captureThread = new Thread(CaptureLoop);
                _captureThread.IsBackground = true;
                _captureThread.Start();
            }
            SetStatus("감시 주소로 보내는 중");
        }

        void StopShare()
        {
            _running = false;
        }

        void PostHub(byte[] jpeg)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Program.MonitorUrl + "/frame");
                req.Method = "POST";
                req.ContentType = "image/jpeg";
                req.Headers.Add("X-Hv-Id", _senderId);
                req.Headers.Add("X-Hv-Name", _senderName);
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                req.ServicePoint.Expect100Continue = false;
                req.ContentLength = jpeg.Length;
                using (Stream stream = req.GetRequestStream())
                    stream.Write(jpeg, 0, jpeg.Length);
                using (WebResponse response = req.GetResponse())
                {
                }
                if (!_hubPosted)
                {
                    _hubPosted = true;
                    SetStatus("중계 서버로 보내는 중");
                }
            }
            catch (Exception)
            {
                _hubPosted = false;
                SetStatus("감시 주소에 연결하지 못했습니다. 중계 서버가 켜져 있는지 확인하세요.");
            }
        }

        void CaptureLoop()
        {
            while (_running)
            {
                byte[] jpeg = null;
                try
                {
                    jpeg = Wire.CaptureJpeg(80L, 1920);
                }
                catch (Exception ex)
                {
                    SetStatus("캡처 실패. " + ex.Message);
                }

                if (jpeg != null)
                    PostHub(jpeg);

                Thread.Sleep(100);
            }
        }
    }

    sealed class ViewForm : Form
    {
        readonly TextBox _host;
        readonly TextBox _code;
        readonly Button _connect;
        readonly Label _status;
        readonly object _gate = new object();
        readonly string _fixedHost;
        readonly string _fixedCode;
        string _manualHost;
        string _manualCode;
        Bitmap _frame;
        int _frames;
        volatile bool _running = true;
        Thread _thread;
        TcpClient _client;
        UdpClient _discover;

        public ViewForm(string host, string code, bool autoConnect)
        {
            Text = "HV 화면";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
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
            bar.Height = 52;
            bar.BackColor = Theme.Panel;
            Controls.Add(bar);

            Label hostLabel = new Label();
            hostLabel.Text = "주소";
            hostLabel.ForeColor = Theme.Muted;
            hostLabel.AutoSize = true;
            hostLabel.Location = new Point(12, 16);
            bar.Controls.Add(hostLabel);

            _host = new TextBox();
            _host.Text = host;
            _host.Bounds = new Rectangle(52, 12, 220, 26);
            bar.Controls.Add(_host);

            Label codeLabel = new Label();
            codeLabel.Text = "코드";
            codeLabel.ForeColor = Theme.Muted;
            codeLabel.AutoSize = true;
            codeLabel.Location = new Point(284, 16);
            bar.Controls.Add(codeLabel);

            _code = new TextBox();
            _code.Text = code;
            _code.MaxLength = 6;
            _code.Bounds = new Rectangle(324, 12, 90, 26);
            bar.Controls.Add(_code);

            _connect = new Button();
            _connect.Text = "직접 연결";
            _connect.Bounds = new Rectangle(426, 10, 72, 28);
            _connect.FlatStyle = FlatStyle.Flat;
            _connect.BackColor = Theme.Accent;
            _connect.ForeColor = Color.White;
            _connect.FlatAppearance.BorderSize = 0;
            _connect.Click += delegate { Connect(); };
            bar.Controls.Add(_connect);

            _status = new Label();
            _status.Text = "보내는 PC를 찾는 중";
            _status.ForeColor = Theme.Muted;
            _status.AutoSize = false;
            _status.Bounds = new Rectangle(510, 14, 560, 24);
            bar.Controls.Add(_status);

            _fixedHost = autoConnect ? host : null;
            _fixedCode = autoConnect ? code : null;
            FormClosing += delegate { StopView(); };
            Shown += delegate { StartWatch(); };
        }

        void StopView()
        {
            _running = false;
            try
            {
                if (_client != null)
                    _client.Close();
            }
            catch (Exception)
            {
            }
            try
            {
                if (_discover != null)
                    _discover.Close();
            }
            catch (Exception)
            {
            }
        }

        void StartWatch()
        {
            if (_thread != null && _thread.IsAlive)
                return;
            _running = true;
            _thread = new Thread(WatchLoop);
            _thread.IsBackground = true;
            _thread.Start();
        }

        void WatchLoop()
        {
            while (_running)
            {
                string host = _manualHost != null ? _manualHost : _fixedHost;
                string code = _manualHost != null ? _manualCode : _fixedCode;
                int port = Program.Port;
                if (host == null)
                {
                    if (!WaitBeacon(out host, out port, out code))
                        continue;
                    string foundHost = host;
                    string foundCode = code;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            _host.Text = foundHost;
                            _code.Text = foundCode;
                        });
                    }
                    catch (Exception)
                    {
                    }
                }

                SetStatus("연결하는 중   " + host);
                string error = RunSession(host, port, code);
                if (!_running)
                    break;
                if (error == null || error.Length == 0)
                    SetStatus("연결이 끊겼습니다. 다시 찾는 중...");
                else
                    SetStatus(error + "  다시 찾는 중...");
                Thread.Sleep(400);
            }
        }

        bool WaitBeacon(out string host, out int port, out string code)
        {
            host = null;
            port = Program.Port;
            code = null;
            try
            {
                if (_discover == null)
                {
                    UdpClient udp = new UdpClient();
                    udp.Client.ExclusiveAddressUse = false;
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, Program.DiscoverPort));
                    udp.Client.ReceiveTimeout = 1000;
                    _discover = udp;
                }

                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = _discover.Receive(ref remote);
                string text = System.Text.Encoding.ASCII.GetString(data).Trim();
                string[] parts = text.Split(' ');
                if (parts.Length != 3 || parts[0] != "HVSCREEN1" || parts[2].Length != 6)
                    return false;
                int parsed;
                if (!int.TryParse(parts[1], out parsed))
                    return false;
                host = remote.Address.ToString();
                port = parsed;
                code = parts[2];
                return host != "0.0.0.0";
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        void SetStatus(string text)
        {
            if (IsDisposed)
                return;
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)delegate { SetStatus(text); });
                return;
            }
            _status.Text = text;
        }

        void Connect()
        {
            string host = _host.Text.Trim();
            string code = _code.Text.Trim();
            if (host.Length == 0 || code.Length != 6)
            {
                SetStatus("주소와 6자리 코드가 필요합니다.");
                return;
            }

            _manualHost = host;
            _manualCode = code;
            _connect.Enabled = false;
            try
            {
                if (_client != null)
                    _client.Close();
            }
            catch (Exception)
            {
            }
            try
            {
                if (_discover != null)
                {
                    _discover.Close();
                    _discover = null;
                }
            }
            catch (Exception)
            {
            }
        }

        string RunSession(string host, int port, string code)
        {
            TcpClient client = new TcpClient();
            _client = client;
            try
            {
                client.Connect(host, port);
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                byte[] codeBytes = System.Text.Encoding.ASCII.GetBytes(code);
                if (codeBytes.Length != 6)
                    return "코드 형식 오류";
                Wire.WriteAll(stream, codeBytes);
                int ok = stream.ReadByte();
                if (ok != 1)
                    return "코드가 맞지 않습니다.";

                BeginInvoke((MethodInvoker)delegate { _connect.Enabled = false; });
                SetStatus("원격 화면 수신 중");
                while (_running)
                {
                    byte[] header = Wire.ReadExact(stream, 4);
                    if (!BitConverter.IsLittleEndian)
                        Array.Reverse(header);
                    int length = BitConverter.ToInt32(header, 0);
                    if (length <= 0 || length > Wire.MaxFrame)
                        return "화면 데이터 오류";
                    byte[] jpeg = Wire.ReadExact(stream, length);
                    Bitmap bmp = Wire.DecodeJpeg(jpeg);
                    SwapFrame(bmp);
                    _frames++;
                    if ((_frames % 10) == 0)
                        SetStatus("원격 화면 수신 중   " + bmp.Width + "×" + bmp.Height);
                    BeginInvoke((MethodInvoker)delegate { Invalidate(); });
                }
                return "";
            }
            catch (Exception ex)
            {
                if (!_running)
                    return "";
                return "연결 실패. " + ex.Message;
            }
            finally
            {
                client.Close();
                if (_client == client)
                    _client = null;
                EnableConnect();
            }
        }

        void EnableConnect()
        {
            try
            {
                if (IsDisposed)
                    return;
                BeginInvoke((MethodInvoker)delegate { _connect.Enabled = true; });
            }
            catch (Exception)
            {
            }
        }

        void SwapFrame(Bitmap bmp)
        {
            lock (_gate)
            {
                Bitmap old = _frame;
                _frame = bmp;
                if (old != null)
                    old.Dispose();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle client = ClientRectangle;
            client.Y += 52;
            client.Height -= 52;
            e.Graphics.Clear(Theme.Bg);

            Rectangle outer = new Rectangle(24, client.Y + 16, client.Width - 48, client.Height - 32);
            if (outer.Width < 80 || outer.Height < 80)
                return;

            using (SolidBrush bezel = new SolidBrush(Color.FromArgb(10, 12, 16)))
                e.Graphics.FillRectangle(bezel, outer);

            int pad = 14;
            Rectangle screen = new Rectangle(
                outer.X + pad,
                outer.Y + pad,
                outer.Width - pad * 2,
                outer.Height - pad * 2);

            lock (_gate)
            {
                if (_frame == null)
                {
                    TextRenderer.DrawText(
                        e.Graphics,
                        "원격 화면 대기",
                        Font,
                        screen,
                        Theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }

                float scale = Math.Min(
                    (float)screen.Width / _frame.Width,
                    (float)screen.Height / _frame.Height);
                int dw = Math.Max(1, (int)(_frame.Width * scale));
                int dh = Math.Max(1, (int)(_frame.Height * scale));
                int dx = screen.X + (screen.Width - dw) / 2;
                int dy = screen.Y + (screen.Height - dh) / 2;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                e.Graphics.DrawImage(_frame, new Rectangle(dx, dy, dw, dh));
            }
        }
    }
}
