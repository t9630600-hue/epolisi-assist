// EPOLISI assist — программа агента (Windows).
// Показывает ID и пароль, держит соединение с сервером EPOLISI assist,
// во время сеанса передаёт экран оператору и выполняет его мышь/клавиатуру.
// Собирается встроенным в Windows компилятором .NET Framework 4.x (C# 5), см. build.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("EPOLISI assist")]
[assembly: AssemblyProduct("EPOLISI assist")]
[assembly: AssemblyCompany("EPOLISI")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace EpolisiAssist
{
    static class Program
    {
        public const string ServerUrl = "wss://assist.avto-sugurta.uz/ws/agent";

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "EPOLISI_assist_agent", out created))
            {
                if (!created)
                {
                    MessageBox.Show("EPOLISI assist уже запущен (значок возле часов).", "EPOLISI assist");
                    return;
                }
                Native.SetProcessDPIAware();
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool startHidden = Array.IndexOf(args, "/min") >= 0;
                Application.Run(new MainForm(startHidden));
            }
        }
    }

    // ---------- настройки: %APPDATA%\EPOLISI assist\agent.cfg ----------
    class Config
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EPOLISI assist");
        static readonly string FilePath = Path.Combine(Dir, "agent.cfg");
        public string Id = "", Key = "", FixedPw = "", Akey = "";

        public static Config Load()
        {
            var c = new Config();
            try
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i), v = line.Substring(i + 1);
                    if (k == "id") c.Id = v; else if (k == "key") c.Key = v;
                    else if (k == "fixedpw") c.FixedPw = v; else if (k == "akey") c.Akey = v;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (c.Id.Length != 9 || c.Key.Length < 16) c.NewIdentity();
            if (c.Akey.Length < 16) { c.Akey = NewSecret(); c.Save(); }
            return c;
        }

        static string NewSecret()
        {
            var b = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return Convert.ToBase64String(b);
        }

        public void NewIdentity()
        {
            Id = (RandomInt(900000000) + 100000000).ToString();
            Key = NewSecret();
            if (Akey.Length < 16) Akey = NewSecret();
            Save();
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, new[] { "id=" + Id, "key=" + Key, "fixedpw=" + FixedPw, "akey=" + Akey });
        }

        public static int RandomInt(int max)
        {
            var b = new byte[4];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return (int)(BitConverter.ToUInt32(b, 0) % (uint)max);
        }

        public static string FormatId(string id)
        {
            return id.Length == 9 ? id.Substring(0, 3) + " " + id.Substring(3, 3) + " " + id.Substring(6) : id;
        }
    }

    // ---------- главное окно ----------
    class MainForm : Form
    {
        readonly Config cfg = Config.Load();
        readonly Link link;
        string tempPw;
        bool reallyExit, hiddenStart, trayTipShown;
        readonly Label idLabel, pwLabel, statusLabel, sessionLabel;
        readonly Button endButton;
        readonly CheckBox autoStart;
        readonly NotifyIcon tray;
        SessionBanner banner;

        static readonly Color Blue = Color.FromArgb(31, 111, 235);
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public MainForm(bool startHidden)
        {
            hiddenStart = startHidden;
            Text = "EPOLISI assist";
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(400, 372);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            var head = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Blue };
            head.Controls.Add(new Label { Text = "EPOLISI assist", ForeColor = Color.White, Font = new Font("Segoe UI", 16f, FontStyle.Bold), AutoSize = true, Location = new Point(18, 12) });
            Controls.Add(head);

            Controls.Add(Caption("Ваш ID", 72));
            idLabel = new Label { Font = new Font("Consolas", 24f, FontStyle.Bold), AutoSize = true, Location = new Point(16, 92) };
            Controls.Add(idLabel);
            var copyId = SmallButton("Копировать", 285, 100);
            copyId.Click += delegate { Clipboard.SetText(cfg.Id); };
            Controls.Add(copyId);

            Controls.Add(Caption("Пароль", 142));
            pwLabel = new Label { Font = new Font("Consolas", 20f, FontStyle.Bold), AutoSize = true, Location = new Point(16, 162) };
            Controls.Add(pwLabel);
            var newPw = SmallButton("Новый", 285, 166);
            newPw.Click += delegate { tempPw = NewTempPw(); cfg.FixedPw = ""; cfg.Save(); UpdatePw(); };
            Controls.Add(newPw);

            statusLabel = new Label { AutoSize = true, Location = new Point(18, 212), ForeColor = Color.Gray, Text = "● Подключение к серверу…" };
            Controls.Add(statusLabel);

            sessionLabel = new Label { AutoSize = false, Size = new Size(250, 44), Location = new Point(18, 238), ForeColor = Color.FromArgb(209, 36, 47), Font = new Font("Segoe UI", 10f, FontStyle.Bold), Visible = false };
            Controls.Add(sessionLabel);
            endButton = SmallButton("Отключить", 285, 242);
            endButton.Visible = false;
            endButton.Click += delegate { link.EndSessionByUser(); };
            Controls.Add(endButton);

            autoStart = new CheckBox { Text = "Запускать вместе с Windows", AutoSize = true, Location = new Point(18, 292) };
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) autoStart.Checked = k != null && k.GetValue("EPOLISI assist") != null;
            autoStart.CheckedChanged += delegate { SetAutoStart(autoStart.Checked); };
            Controls.Add(autoStart);

            var fixedLink = new LinkLabel { Text = "Постоянный пароль…", AutoSize = true, Location = new Point(18, 324) };
            fixedLink.LinkClicked += delegate { AskFixedPw(); };
            Controls.Add(fixedLink);
            var ver = new Label { Text = "v1.0", AutoSize = true, ForeColor = Color.Silver, Location = new Point(355, 326) };
            Controls.Add(ver);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть", null, delegate { ShowWindow(); });
            menu.Items.Add("Выход", null, delegate { reallyExit = true; Close(); });
            tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "EPOLISI assist", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += delegate { ShowWindow(); };

            tempPw = NewTempPw();
            idLabel.Text = Config.FormatId(cfg.Id);
            UpdatePw();
            link = new Link(this, cfg);

            var clipTimer = new System.Windows.Forms.Timer { Interval = 800 };
            clipTimer.Tick += delegate { PollClipboard(); };
            clipTimer.Start();
        }

        // ---------- буфер обмена: синхронизация с оператором ----------
        string lastClip = "";
        volatile bool sessionActive;

        void PollClipboard()
        {
            if (!sessionActive) return; // только во время сеанса, чтобы не слать буфер зря
            try
            {
                if (!Clipboard.ContainsText()) return;
                string t = Clipboard.GetText();
                if (t.Length > 0 && t.Length < 100000 && t != lastClip) { lastClip = t; link.SendClip(t); }
            }
            catch (Exception) { } // буфер временно занят другим процессом — пропускаем попытку
        }

        public void SetClipboard(string text)
        {
            Ui(delegate { try { lastClip = text; Clipboard.SetText(text); } catch (Exception) { } });
        }

        static Label Caption(string text, int y)
        {
            return new Label { Text = text, ForeColor = Color.Gray, AutoSize = true, Location = new Point(18, y) };
        }

        static Button SmallButton(string text, int x, int y)
        {
            return new Button { Text = text, Location = new Point(x, y), Size = new Size(98, 30), FlatStyle = FlatStyle.System };
        }

        static string NewTempPw() { return (Config.RandomInt(900000) + 100000).ToString(); }

        public string Password { get { return cfg.FixedPw.Length > 0 ? cfg.FixedPw : tempPw; } }

        void UpdatePw()
        {
            pwLabel.Text = cfg.FixedPw.Length > 0 ? "••••••  (постоянный)" : tempPw;
            pwLabel.Font = new Font("Consolas", cfg.FixedPw.Length > 0 ? 14f : 20f, FontStyle.Bold);
            if (link != null) link.SendPassword();
        }

        void AskFixedPw()
        {
            string pw = Prompt("Постоянный пароль (не меньше 6 символов).\nОставьте пустым, чтобы вернуть одноразовый пароль.");
            if (pw == null) return;
            if (pw.Length > 0 && pw.Length < 6) { MessageBox.Show("Пароль слишком короткий.", "EPOLISI assist"); return; }
            cfg.FixedPw = pw; cfg.Save(); UpdatePw();
        }

        string Prompt(string text)
        {
            using (var f = new Form { Text = "EPOLISI assist", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(360, 150), MaximizeBox = false, MinimizeBox = false, Font = Font, AutoScaleMode = AutoScaleMode.Dpi })
            {
                var l = new Label { Text = text, Location = new Point(12, 10), Size = new Size(336, 44) };
                var t = new TextBox { Location = new Point(12, 60), Width = 336, UseSystemPasswordChar = true };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(176, 104), Size = new Size(84, 30) };
                var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(264, 104), Size = new Size(84, 30) };
                f.Controls.AddRange(new Control[] { l, t, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK ? t.Text : null;
            }
        }

        void SetAutoStart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("EPOLISI assist", "\"" + Application.ExecutablePath + "\" /min");
                else k.DeleteValue("EPOLISI assist", false);
            }
        }

        void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        protected override void SetVisibleCore(bool value)
        {
            if (hiddenStart) { hiddenStart = false; value = false; if (!IsHandleCreated) CreateHandle(); }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            link.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!trayTipShown)
                {
                    trayTipShown = true;
                    tray.ShowBalloonTip(3000, "EPOLISI assist", "Программа работает в фоне. Значок — возле часов.", ToolTipIcon.Info);
                }
                return;
            }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        // вызовы из потока связи
        public void SetOnline(bool online)
        {
            Ui(delegate
            {
                statusLabel.Text = online ? "● Готов к подключению" : "● Нет связи с сервером, повтор…";
                statusLabel.ForeColor = online ? Color.FromArgb(26, 127, 55) : Color.Gray;
            });
        }

        public void SetIdentity() { Ui(delegate { idLabel.Text = Config.FormatId(cfg.Id); }); }

        public void SetSession(bool active)
        {
            sessionActive = active;
            Ui(delegate
            {
                sessionLabel.Text = "Оператор EPOLISI подключён\nи управляет компьютером";
                sessionLabel.Visible = endButton.Visible = active;
                if (active)
                {
                    if (banner == null) banner = new SessionBanner(delegate { link.EndSessionByUser(); });
                    banner.Show();
                    tray.ShowBalloonTip(3000, "EPOLISI assist", "Оператор подключился к вашему компьютеру", ToolTipIcon.Info);
                }
                else if (banner != null) banner.Hide();
            });
        }

        void Ui(MethodInvoker a)
        {
            if (IsDisposed) return;
            try { if (InvokeRequired) BeginInvoke(a); else a(); } catch (InvalidOperationException) { }
        }
    }

    // красная плашка вверху экрана на время сеанса (оператор её не видит)
    class SessionBanner : Form
    {
        public SessionBanner(Action end)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(209, 36, 47);
            Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(470, 38);
            var b = Screen.PrimaryScreen.Bounds;
            Location = new Point(b.Left + (b.Width - Width) / 2, b.Top);
            Controls.Add(new Label { Text = "Идёт сеанс удалённого доступа EPOLISI", ForeColor = Color.White, AutoSize = true, Location = new Point(12, 9) });
            var btn = new Button { Text = "Отключить", Location = new Point(360, 4), Size = new Size(100, 30), BackColor = Color.White, FlatStyle = FlatStyle.Flat };
            btn.Click += delegate { end(); };
            Controls.Add(btn);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { Native.SetWindowDisplayAffinity(Handle, 0x11); } catch (Exception) { } // WDA_EXCLUDEFROMCAPTURE
        }
    }

    // ---------- связь с сервером и сеанс ----------
    class Link
    {
        readonly MainForm ui;
        readonly Config cfg;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        ClientWebSocket ws;
        CancellationTokenSource session;
        int inflight;
        volatile bool forceFrame;
        volatile int maxWidth = 1600;
        volatile int quality = 55;

        public Link(MainForm ui, Config cfg) { this.ui = ui; this.cfg = cfg; }

        int started;

        public void Start()
        {
            if (Interlocked.Exchange(ref started, 1) == 0) Task.Run(() => Loop());
        }

        async Task Loop()
        {
            while (true)
            {
                try { await RunOnce(); }
                catch (Exception) { }
                StopSession();
                ui.SetOnline(false);
                await Task.Delay(3000);
            }
        }

        async Task RunOnce()
        {
            var sock = new ClientWebSocket();
            sock.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            ws = sock;
            using (sock)
            {
                await sock.ConnectAsync(new Uri(Program.ServerUrl), CancellationToken.None);
                await SendJson(new Dictionary<string, object> {
                    { "type", "hello" }, { "id", cfg.Id }, { "key", cfg.Key }, { "pw", ui.Password }, { "akey", cfg.Akey },
                    { "name", Environment.MachineName + " / " + Environment.UserName }, { "ver", "1.1" } });
                var buf = new byte[65536];
                var ms = new MemoryStream();
                while (sock.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await sock.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Text)
                        Handle(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                }
            }
        }

        void Handle(string text)
        {
            var m = json.DeserializeObject(text) as Dictionary<string, object>;
            if (m == null || !m.ContainsKey("type")) return;
            switch ((string)m["type"])
            {
                case "ok": ui.SetOnline(true); break;
                case "error":
                    if ((string)m["code"] == "id_taken") { cfg.NewIdentity(); ui.SetIdentity(); }
                    throw new IOException("server error");
                case "start": StartSession(); break;
                case "stop": StopSession(); break;
                case "ack": if (Interlocked.Decrement(ref inflight) < 0) Interlocked.Exchange(ref inflight, 0); break;
                case "clip": ui.SetClipboard(m.ContainsKey("text") ? (string)m["text"] : ""); break;
                case "q":
                    string mode = (string)m["mode"];
                    if (mode == "fast") { maxWidth = 1280; quality = 40; }
                    else if (mode == "hq") { maxWidth = 100000; quality = 75; }
                    else { maxWidth = 1600; quality = 55; }
                    forceFrame = true;
                    break;
                default:
                    string tp = (string)m["type"];
                    if (tp != null && tp.Length > 0 && tp[0] == 'f') Files(m);
                    else if (session != null) Input.Apply(m);
                    break;
            }
        }

        public void SendPassword()
        {
            var _ = SendJson(new Dictionary<string, object> { { "type", "pw" }, { "pw", ui.Password } });
        }

        public void SendClip(string text)
        {
            var _ = SendJson(new Dictionary<string, object> { { "type", "clip" }, { "text", text } });
        }

        // ---------- передача файлов (оператор видит диск агента, шлёт и забирает файлы) ----------
        FileStream upFile;
        string upName = "";

        void Files(Dictionary<string, object> m)
        {
            string t = (string)m["type"];
            if (t == "fls") Task.Run(() => ListDir(m));
            else if (t == "fget") Task.Run(() => SendFile(m.ContainsKey("path") ? (string)m["path"] : ""));
            else if (t == "fput") StartUpload(m);
            else if (t == "fdata") WriteUpload(m.ContainsKey("data") ? (string)m["data"] : "");
            else if (t == "fend") EndUpload();
        }

        void FileErr(string msg) { try { SendJson(new Dictionary<string, object> { { "type", "ferr" }, { "msg", msg } }).Wait(); } catch (Exception) { } }

        void ListDir(Dictionary<string, object> m)
        {
            try
            {
                string path = m.ContainsKey("path") ? (string)m["path"] : "";
                bool up = m.ContainsKey("up") && m["up"] != null && Convert.ToDouble(m["up"]) > 0;
                if (string.IsNullOrEmpty(path))
                    path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                else if (up)
                {
                    var parent = Directory.GetParent(path.TrimEnd('\\'));
                    path = parent != null ? parent.FullName : path;
                }
                var entries = new List<object>();
                foreach (var d in Directory.GetDirectories(path))
                {
                    string nm = Path.GetFileName(d);
                    entries.Add(new Dictionary<string, object> { { "n", nm.Length > 0 ? nm : d }, { "d", 1 }, { "p", d } });
                }
                foreach (var f in Directory.GetFiles(path))
                {
                    long sz = 0;
                    try { sz = new FileInfo(f).Length; } catch (Exception) { }
                    entries.Add(new Dictionary<string, object> { { "n", Path.GetFileName(f) }, { "d", 0 }, { "s", sz }, { "p", f } });
                }
                SendJson(new Dictionary<string, object> { { "type", "flist" }, { "path", path }, { "entries", entries } }).Wait();
            }
            catch (Exception) { FileErr("Нет доступа к этой папке"); }
        }

        void SendFile(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                SendJson(new Dictionary<string, object> { { "type", "fgetmeta" }, { "name", fi.Name }, { "size", fi.Length } }).Wait();
                using (var fs = File.OpenRead(path))
                {
                    var buf = new byte[48000];
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                        SendJson(new Dictionary<string, object> { { "type", "fdata" }, { "data", Convert.ToBase64String(buf, 0, n) } }).Wait();
                }
                SendJson(new Dictionary<string, object> { { "type", "fend" } }).Wait();
            }
            catch (Exception) { FileErr("Не удалось прочитать файл"); }
        }

        void StartUpload(Dictionary<string, object> m)
        {
            EndUpload();
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "EPOLISI assist");
                Directory.CreateDirectory(dir);
                string name = Path.GetFileName(m.ContainsKey("name") ? (string)m["name"] : "");
                if (string.IsNullOrEmpty(name)) name = "file";
                string dest = Path.Combine(dir, name);
                int k = 1;
                while (File.Exists(dest))
                    dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + " (" + (k++) + ")" + Path.GetExtension(name));
                upFile = File.Create(dest);
                upName = Path.GetFileName(dest);
            }
            catch (Exception) { upFile = null; FileErr("Не удалось сохранить файл у агента"); }
        }

        void WriteUpload(string b64)
        {
            try { if (upFile != null && b64.Length > 0) { var b = Convert.FromBase64String(b64); upFile.Write(b, 0, b.Length); } }
            catch (Exception) { }
        }

        void EndUpload()
        {
            if (upFile == null) return;
            try { upFile.Dispose(); } catch (Exception) { }
            upFile = null;
            try { SendJson(new Dictionary<string, object> { { "type", "fok" }, { "msg", "Файл получен: " + upName } }).Wait(); } catch (Exception) { }
        }

        public void EndSessionByUser()
        {
            var _ = SendJson(new Dictionary<string, object> { { "type", "end" } });
            StopSession();
        }

        void StartSession()
        {
            StopSession();
            Interlocked.Exchange(ref inflight, 0);
            forceFrame = true;
            var cts = new CancellationTokenSource();
            session = cts;
            ui.SetSession(true);
            Task.Run(() => CaptureLoop(cts.Token));
        }

        void StopSession()
        {
            var s = Interlocked.Exchange(ref session, null);
            if (s == null) return;
            s.Cancel();
            ui.SetSession(false);
        }

        async Task SendJson(Dictionary<string, object> obj)
        {
            await Send(Encoding.UTF8.GetBytes(json.Serialize(obj)), WebSocketMessageType.Text);
        }

        async Task Send(byte[] data, WebSocketMessageType type)
        {
            var sock = ws;
            if (sock == null || sock.State != WebSocketState.Open) return;
            await sendLock.WaitAsync();
            try { await sock.SendAsync(new ArraySegment<byte>(data), type, true, CancellationToken.None); }
            catch (Exception) { }
            finally { sendLock.Release(); }
        }

        async Task CaptureLoop(CancellationToken ct)
        {
            byte[] prev = null, cur = null;
            Rectangle last = Rectangle.Empty;
            var jpeg = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.MimeType == "image/jpeg");
            while (!ct.IsCancellationRequested)
            {
                if (Volatile.Read(ref inflight) >= 2) { await Task.Delay(10); continue; }
                bool changed = false, failed = false;
                try
                {
                    var b = Screen.PrimaryScreen.Bounds;
                    if (b != last)
                    {
                        last = b; forceFrame = true;
                        await SendJson(new Dictionary<string, object> { { "type", "info" }, { "w", b.Width }, { "h", b.Height } });
                    }
                    using (var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppRgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                            DrawCursor(g, b);
                        }
                        cur = Snapshot(bmp, cur);
                        changed = forceFrame || prev == null || prev.Length != cur.Length || Native.memcmp(prev, cur, (UIntPtr)cur.Length) != 0;
                        if (changed)
                        {
                            forceFrame = false;
                            var t = prev; prev = cur; cur = t;
                            byte[] data = Encode(bmp, jpeg);
                            Interlocked.Increment(ref inflight);
                            await Send(data, WebSocketMessageType.Binary);
                        }
                    }
                }
                catch (Exception)
                {
                    failed = true; // экран блокировки / UAC: захват недоступен, ждём
                }
                await Task.Delay(failed ? 500 : changed ? 30 : 70);
            }
        }

        static byte[] Snapshot(Bitmap bmp, byte[] buf)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
            try
            {
                int n = Math.Abs(data.Stride) * bmp.Height;
                if (buf == null || buf.Length != n) buf = new byte[n];
                Marshal.Copy(data.Scan0, buf, 0, n);
                return buf;
            }
            finally { bmp.UnlockBits(data); }
        }

        byte[] Encode(Bitmap bmp, ImageCodecInfo jpeg)
        {
            var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            using (var ms = new MemoryStream())
            {
                int w = maxWidth;
                if (bmp.Width > w)
                {
                    int h = (int)((long)bmp.Height * w / bmp.Width);
                    using (var small = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                    {
                        using (var g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.Bilinear;
                            g.DrawImage(bmp, 0, 0, w, h);
                        }
                        small.Save(ms, jpeg, p);
                    }
                }
                else bmp.Save(ms, jpeg, p);
                return ms.ToArray();
            }
        }

        static void DrawCursor(Graphics g, Rectangle screen)
        {
            var ci = new Native.CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO));
            if (!Native.GetCursorInfo(ref ci) || ci.flags != 1) return;
            int hx = 0, hy = 0;
            Native.ICONINFO ii;
            if (Native.GetIconInfo(ci.hCursor, out ii))
            {
                hx = ii.xHotspot; hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
            }
            IntPtr hdc = g.GetHdc();
            try { Native.DrawIconEx(hdc, ci.x - screen.Left - hx, ci.y - screen.Top - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
            finally { g.ReleaseHdc(hdc); }
        }
    }

    // ---------- мышь и клавиатура оператора ----------
    static class Input
    {
        const uint MOVE = 0x0001, LDOWN = 0x0002, LUP = 0x0004, RDOWN = 0x0008, RUP = 0x0010,
                   MDOWN = 0x0020, MUP = 0x0040, WHEEL = 0x0800, ABSOLUTE = 0x8000;
        const uint KEY_EXT = 0x0001, KEY_UP = 0x0002, KEY_UNICODE = 0x0004;

        static double D(Dictionary<string, object> m, string k)
        {
            object v; return m.TryGetValue(k, out v) && v != null ? Convert.ToDouble(v) : 0;
        }

        public static void Apply(Dictionary<string, object> m)
        {
            switch ((string)m["type"])
            {
                case "m": Move(D(m, "x"), D(m, "y")); break;
                case "b":
                    Move(D(m, "x"), D(m, "y"));
                    bool down = D(m, "d") > 0;
                    string b = m.ContainsKey("b") ? (string)m["b"] : "l";
                    uint f = b == "r" ? (down ? RDOWN : RUP) : b == "m" ? (down ? MDOWN : MUP) : (down ? LDOWN : LUP);
                    Mouse(0, 0, 0, f);
                    break;
                case "w": Mouse(0, 0, (int)D(m, "d"), WHEEL); break;
                case "k":
                    int vk = (int)D(m, "vk");
                    if (vk <= 0 || vk > 254) return;
                    uint kf = (D(m, "d") > 0 ? 0 : KEY_UP) | (D(m, "x") > 0 ? KEY_EXT : 0);
                    Key((ushort)vk, (ushort)Native.MapVirtualKey((uint)vk, 0), kf);
                    break;
                case "t":
                    string text = m.ContainsKey("text") ? (string)m["text"] : "";
                    foreach (char c in text.Replace("\r\n", "\n"))
                    {
                        if (c == '\n') { Key(13, 0, 0); Key(13, 0, KEY_UP); continue; }
                        Key(0, c, KEY_UNICODE); Key(0, c, KEY_UNICODE | KEY_UP);
                    }
                    break;
            }
        }

        static void Move(double x, double y)
        {
            x = Math.Max(0, Math.Min(1, x)); y = Math.Max(0, Math.Min(1, y));
            Mouse((int)(x * 65535), (int)(y * 65535), 0, MOVE | ABSOLUTE);
        }

        static void Mouse(int x, int y, int data, uint flags)
        {
            var i = new Native.INPUT { type = 0 };
            i.u.mi = new Native.MOUSEINPUT { dx = x, dy = y, mouseData = (uint)data, dwFlags = flags };
            Native.SendInput(1, new[] { i }, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        static void Key(ushort vk, ushort scan, uint flags)
        {
            var i = new Native.INPUT { type = 1 };
            i.u.ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags };
            Native.SendInput(1, new[] { i }, Marshal.SizeOf(typeof(Native.INPUT)));
        }
    }

    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
        [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int memcmp(byte[] a, byte[] b, UIntPtr n);

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion u; }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    }
}
