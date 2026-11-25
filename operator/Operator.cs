// EPOLISI assist — программа оператора (Windows). Книга компьютеров + просмотр/управление
// без браузера: прямое подключение к серверу по WebSocket (как и агент), поэтому не зависит
// от антивируса/VPN в браузере, которые иногда рвут веб-сокет.
// Собирается встроенным в Windows компилятором .NET Framework 4.x (C# 5), см. build.ps1.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("EPOLISI assist — оператор")]
[assembly: System.Reflection.AssemblyProduct("EPOLISI assist")]
[assembly: System.Reflection.AssemblyCompany("EPOLISI")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace EpolisiOperator
{
    static class Program
    {
        public const string BaseUrl = "https://assist.avto-sugurta.uz";

        [STAThread]
        static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var api = new Api();
            string savedUser = Config.Load().User;
            using (var login = new LoginForm(api, savedUser))
            {
                if (login.ShowDialog() != DialogResult.OK) return;
            }
            Application.Run(new BookForm(api));
        }
    }

    // ---------- настройки: запоминаем только логин (не пароль) ----------
    class Config
    {
        static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EPOLISI assist", "operator.cfg");
        public string User = "";

        public static Config Load()
        {
            var c = new Config();
            try { c.User = File.ReadAllText(FilePath).Trim(); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            return c;
        }

        public static void Save(string user)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, user);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    class ApiError : Exception { public ApiError(string m) : base(m) { } }

    // ---------- обычные запросы (вход, книга, список онлайн) ----------
    class Api
    {
        public readonly CookieContainer Cookies = new CookieContainer();
        readonly JavaScriptSerializer json = new JavaScriptSerializer();

        object Call(string method, string path, object body)
        {
            var req = (HttpWebRequest)WebRequest.Create(Program.BaseUrl + path);
            req.Method = method;
            req.CookieContainer = Cookies;
            req.Timeout = 15000;
            req.UserAgent = "EPOLISI-assist-operator";
            if (body != null)
            {
                req.ContentType = "application/json";
                var data = Encoding.UTF8.GetBytes(json.Serialize(body));
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return json.DeserializeObject(sr.ReadToEnd());
            }
            catch (WebException ex)
            {
                string msg = "Нет связи с сервером";
                if (ex.Response != null)
                {
                    try
                    {
                        using (var sr = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                        {
                            var d = json.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                            if (d != null && d.ContainsKey("error")) msg = (string)d["error"];
                        }
                    }
                    catch (Exception) { }
                }
                throw new ApiError(msg);
            }
        }

        public bool Login(string user, string pass, out string error)
        {
            try
            {
                Call("POST", "/api/login", new Dictionary<string, object> { { "user", user }, { "password", pass } });
                error = null;
                return true;
            }
            catch (ApiError ex) { error = ex.Message; return false; }
        }

        public Dictionary<string, object> Me() { return Call("GET", "/api/me", null) as Dictionary<string, object>; }

        public static List<object> ToList(object r)
        {
            var list = new List<object>();
            var en = r as IEnumerable;
            if (en != null) foreach (var x in en) list.Add(x);
            return list;
        }

        public List<object> Hosts(string q)
        {
            return ToList(Call("GET", "/api/hosts?q=" + Uri.EscapeDataString(q ?? ""), null));
        }

        public List<object> Online()
        {
            return ToList(Call("GET", "/api/online", null));
        }

        public string Akey(string id)
        {
            var d = Call("GET", "/api/akey?id=" + id, null) as Dictionary<string, object>;
            return d != null && d.ContainsKey("akey") ? (string)d["akey"] : null;
        }

        public object Call2(string method, string path, object body) { return Call(method, path, body); }

        public List<object> Users() { return ToList(Call("GET", "/api/users", null)); }

        public void AddUser(string user, string pass)
        {
            Call("POST", "/api/users", new Dictionary<string, object> { { "user", user }, { "password", pass } });
        }

        public void DeleteUser(string user) { Call("DELETE", "/api/users/" + Uri.EscapeDataString(user), null); }

        public List<object> Groups() { return ToList(Call("GET", "/api/groups", null)); }

        public Dictionary<string, object> Grants() { return Call("GET", "/api/grants", null) as Dictionary<string, object>; }

        public void ToggleGrant(string kind, string target, string user, bool grant)
        {
            Call("POST", "/api/grants", new Dictionary<string, object> {
                { "kind", kind }, { "target", target }, { "user", user }, { "grant", grant } });
        }

        public List<object> Logs(string q) { return ToList(Call("GET", "/api/logs?q=" + Uri.EscapeDataString(q ?? ""), null)); }

        public byte[] Export()
        {
            var req = (HttpWebRequest)WebRequest.Create(Program.BaseUrl + "/api/export");
            req.CookieContainer = Cookies;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var ms = new MemoryStream())
            {
                resp.GetResponseStream().CopyTo(ms);
                return ms.ToArray();
            }
        }

        public void Import(string json)
        {
            var req = (HttpWebRequest)WebRequest.Create(Program.BaseUrl + "/api/import");
            req.Method = "POST";
            req.CookieContainer = Cookies;
            req.ContentType = "application/json";
            var data = Encoding.UTF8.GetBytes(json);
            req.ContentLength = data.Length;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            try { using (req.GetResponse()) { } }
            catch (WebException ex)
            {
                if (ex.Response == null) throw;
                using (var sr = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                {
                    var d = this.json.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                    throw new ApiError(d != null && d.ContainsKey("error") ? (string)d["error"] : "Ошибка импорта");
                }
            }
        }
    }

    // ---------- окно входа ----------
    class LoginForm : Form
    {
        readonly Api api;
        readonly TextBox user, pass;
        readonly Label err;

        public LoginForm(Api api, string savedUser)
        {
            this.api = api;
            Text = "EPOLISI assist — вход";
            ClientSize = new Size(340, 230);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;

            Controls.Add(new Label { Text = "EPOLISI assist", Font = new Font("Segoe UI", 16f, FontStyle.Bold), Location = new Point(20, 16), AutoSize = true });
            Controls.Add(new Label { Text = "Логин", Location = new Point(20, 66), AutoSize = true, ForeColor = Color.Gray });
            user = new TextBox { Location = new Point(20, 86), Width = 300, Text = savedUser ?? "" };
            Controls.Add(user);
            Controls.Add(new Label { Text = "Пароль", Location = new Point(20, 116), AutoSize = true, ForeColor = Color.Gray });
            pass = new TextBox { Location = new Point(20, 136), Width = 300, UseSystemPasswordChar = true };
            Controls.Add(pass);
            err = new Label { Location = new Point(20, 166), Width = 300, Height = 32, ForeColor = Color.FromArgb(209, 36, 47) };
            Controls.Add(err);
            var ok = new Button { Text = "Войти", Location = new Point(176, 190), Size = new Size(144, 32), DialogResult = DialogResult.None };
            ok.Click += delegate { DoLogin(); };
            Controls.Add(ok);
            AcceptButton = ok;
            (user.Text.Length > 0 ? (Control)pass : user).Select();
        }

        void DoLogin()
        {
            string u = user.Text.Trim(), p = pass.Text;
            if (u.Length == 0 || p.Length == 0) { err.Text = "Введите логин и пароль"; return; }
            string error;
            Cursor = Cursors.WaitCursor;
            bool ok = api.Login(u, p, out error);
            Cursor = Cursors.Default;
            if (ok) { Config.Save(u); DialogResult = DialogResult.OK; Close(); }
            else err.Text = error ?? "Не удалось войти";
        }
    }

    // ---------- вспомогательный ввод текста/пароля в диалоге ----------
    static class Prompt
    {
        public static string Show(IWin32Window owner, string title, string label, bool password)
        {
            using (var f = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(360, 150),
                MaximizeBox = false,
                MinimizeBox = false,
                Font = new Font("Segoe UI", 10f),
                AutoScaleMode = AutoScaleMode.Dpi
            })
            {
                var l = new Label { Text = label, Location = new Point(12, 10), Size = new Size(336, 44) };
                var t = new TextBox { Location = new Point(12, 60), Width = 336, UseSystemPasswordChar = password };
                var okb = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(176, 104), Size = new Size(84, 30) };
                var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(264, 104), Size = new Size(84, 30) };
                f.Controls.AddRange(new Control[] { l, t, okb, cancel });
                f.AcceptButton = okb; f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? t.Text : null;
            }
        }
    }

    // ---------- главное окно: книга компьютеров ----------
    class BookForm : Form
    {
        readonly Api api;
        readonly TextBox search;
        readonly ListView list;
        readonly Button btnUsers, btnGroups, btnLogs, btnExport, btnImport, btnGrants;
        readonly System.Windows.Forms.Timer refresh;
        List<object> hosts = new List<object>();
        List<object> online = new List<object>();
        bool isOwner;

        public BookForm(Api api)
        {
            this.api = api;
            Text = "EPOLISI assist";
            ClientSize = new Size(1040, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            var top = new Panel { Dock = DockStyle.Top, Height = 44 };
            search = new TextBox { Location = new Point(10, 10), Width = 300 };
            search.TextChanged += delegate { LoadHosts(); };
            top.Controls.Add(search);
            var addBtn = new Button { Text = "+ Добавить", Location = new Point(320, 8), Size = new Size(110, 28) };
            addBtn.Click += delegate { EditHost(null); };
            top.Controls.Add(addBtn);
            var refreshBtn = new Button { Text = "Обновить", Location = new Point(436, 8), Size = new Size(90, 28) };
            refreshBtn.Click += delegate { LoadHosts(); LoadOnline(); };
            top.Controls.Add(refreshBtn);
            btnUsers = new Button { Text = "Помощники", Location = new Point(532, 8), Size = new Size(100, 28), Visible = false };
            btnUsers.Click += delegate { new UsersDialog(api).ShowDialog(this); };
            top.Controls.Add(btnUsers);
            btnGroups = new Button { Text = "Филиалы", Location = new Point(638, 8), Size = new Size(90, 28), Visible = false };
            btnGroups.Click += delegate { OpenGroups(); };
            top.Controls.Add(btnGroups);
            btnLogs = new Button { Text = "Журнал", Location = new Point(734, 8), Size = new Size(90, 28), Visible = false };
            btnLogs.Click += delegate { new LogsDialog(api).ShowDialog(this); };
            top.Controls.Add(btnLogs);
            btnExport = new Button { Text = "Экспорт", Location = new Point(830, 8), Size = new Size(90, 28), Visible = false };
            btnExport.Click += delegate { DoExport(); };
            top.Controls.Add(btnExport);
            btnImport = new Button { Text = "Импорт", Location = new Point(926, 8), Size = new Size(90, 28), Visible = false };
            btnImport.Click += delegate { DoImport(); };
            top.Controls.Add(btnImport);
            Controls.Add(top);

            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false
            };
            list.Columns.Add("", 24);
            list.Columns.Add("Название", 220);
            list.Columns.Add("Программа", 110);
            list.Columns.Add("ID / адрес", 140);
            list.Columns.Add("Телефон", 110);
            list.Columns.Add("Заметка", 160);
            list.DoubleClick += delegate { ConnectSelected(); };
            Controls.Add(list);
            list.BringToFront();

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
            var connectBtn = new Button { Text = "Подключить", Location = new Point(10, 8), Size = new Size(110, 28) };
            connectBtn.Click += delegate { ConnectSelected(); };
            bottom.Controls.Add(connectBtn);
            var editBtn = new Button { Text = "Изменить", Location = new Point(128, 8), Size = new Size(90, 28) };
            editBtn.Click += delegate { if (list.SelectedItems.Count > 0) EditHost((Dictionary<string, object>)list.SelectedItems[0].Tag); };
            bottom.Controls.Add(editBtn);
            var delBtn = new Button { Text = "Удалить", Location = new Point(226, 8), Size = new Size(90, 28) };
            delBtn.Click += delegate { DeleteSelected(); };
            bottom.Controls.Add(delBtn);
            btnGrants = new Button { Text = "Доступ", Location = new Point(324, 8), Size = new Size(90, 28), Visible = false };
            btnGrants.Click += delegate { if (list.SelectedItems.Count > 0) new GrantsDialog(api, (Dictionary<string, object>)list.SelectedItems[0].Tag).ShowDialog(this); };
            bottom.Controls.Add(btnGrants);
            var logoutBtn = new Button { Text = "Выход", Location = new Point(940, 8), Size = new Size(90, 28) };
            logoutBtn.Click += delegate { Application.Restart(); };
            bottom.Controls.Add(logoutBtn);
            Controls.Add(bottom);
            bottom.BringToFront();

            Load += delegate { LoadMe(); LoadHosts(); LoadOnline(); };
            refresh = new System.Windows.Forms.Timer { Interval = 10000 };
            refresh.Tick += delegate { LoadOnline(); };
            refresh.Start();
        }

        void LoadMe()
        {
            try
            {
                var me = api.Me();
                isOwner = me != null && me.ContainsKey("owner") && (bool)me["owner"];
                Text = "EPOLISI assist — вы: " + (me != null ? me["user"] : "");
                foreach (var b in new[] { btnUsers, btnGroups, btnLogs, btnExport, btnImport, btnGrants }) b.Visible = isOwner;
            }
            catch (ApiError) { }
        }

        void OpenGroups()
        {
            List<object> groups;
            try { groups = api.Groups(); }
            catch (ApiError ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); return; }
            if (groups.Count == 0) { MessageBox.Show("Пока нет ни одной группы — укажите «Группу» у записи в книге.", "EPOLISI assist"); return; }
            new GroupsDialog(api, groups).ShowDialog(this);
        }

        void DoExport()
        {
            using (var sfd = new SaveFileDialog { FileName = "epolisi-assist-backup.json", Filter = "JSON|*.json" })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllBytes(sfd.FileName, api.Export()); MessageBox.Show("Сохранено.", "EPOLISI assist"); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); }
            }
        }

        void DoImport()
        {
            using (var ofd = new OpenFileDialog { Filter = "JSON|*.json" })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                try { api.Import(File.ReadAllText(ofd.FileName)); MessageBox.Show("Импортировано.", "EPOLISI assist"); LoadHosts(); }
                catch (ApiError ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); }
            }
        }

        void LoadHosts()
        {
            try { hosts = api.Hosts(search.Text); Render(); }
            catch (ApiError ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); }
        }

        void LoadOnline()
        {
            try { online = api.Online(); Render(); }
            catch (ApiError) { }
        }

        static string S(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }

        bool IsOnlineId(string remoteId)
        {
            string digits = Digits(remoteId);
            foreach (Dictionary<string, object> a in online) if (S(a, "id") == digits) return true;
            return false;
        }

        static string Digits(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") if (char.IsDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        void Render()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Dictionary<string, object> h in hosts)
            {
                bool ea = S(h, "program") == "EPOLISI assist";
                bool on = ea && IsOnlineId(S(h, "remote_id"));
                var it = new ListViewItem(new[] { ea ? (on ? "●" : "○") : "", S(h, "name"), S(h, "program"), S(h, "remote_id"), S(h, "phone"), S(h, "note") });
                it.Tag = h;
                if (ea) it.ForeColor = on ? Color.FromArgb(26, 127, 55) : Color.Gray;
                list.Items.Add(it);
            }
            list.EndUpdate();
        }

        void ConnectSelected()
        {
            if (list.SelectedItems.Count == 0) { MessageBox.Show("Выберите запись в книге.", "EPOLISI assist"); return; }
            var h = (Dictionary<string, object>)list.SelectedItems[0].Tag;
            if (S(h, "program") != "EPOLISI assist") { MessageBox.Show("Эта запись не через EPOLISI assist — подключитесь её собственной программой.", "EPOLISI assist"); return; }
            string id = Digits(S(h, "remote_id"));
            if (id.Length != 9) { MessageBox.Show("ID агента должен быть из 9 цифр.", "EPOLISI assist"); return; }
            string pw = S(h, "password");
            Connect(id, S(h, "name"), pw);
        }

        void Connect(string id, string name, string pw)
        {
            if (string.IsNullOrEmpty(pw))
            {
                try { pw = api.Akey(id); } catch (ApiError) { }
            }
            if (string.IsNullOrEmpty(pw))
            {
                pw = Prompt.Show(this, "EPOLISI assist", "Пароль агента " + id + ":", false);
                if (string.IsNullOrEmpty(pw)) return;
            }
            new ViewForm(api, id, name, pw).Show();
        }

        void EditHost(Dictionary<string, object> h)
        {
            using (var f = new HostEditDialog(h))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var body = f.Result;
                    if (h != null) body["id"] = h["id"];
                    api.Call2("POST", "/api/hosts", body);
                    LoadHosts();
                }
                catch (ApiError ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); }
            }
        }

        void DeleteSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            var h = (Dictionary<string, object>)list.SelectedItems[0].Tag;
            if (MessageBox.Show("Удалить «" + S(h, "name") + "»?", "EPOLISI assist", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try { api.Call2("DELETE", "/api/hosts/" + S(h, "id"), null); LoadHosts(); }
            catch (ApiError ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); }
        }
    }

    // ---------- добавить/изменить запись книги ----------
    class HostEditDialog : Form
    {
        readonly TextBox name, program, remoteId, password, phone, grp, note;
        public Dictionary<string, object> Result;

        public HostEditDialog(Dictionary<string, object> h)
        {
            Text = h == null ? "Новая запись" : "Изменить запись";
            ClientSize = new Size(360, 400);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            int y = 10;
            name = Field("Название*", h, "name", ref y);
            program = Field("Программа (EPOLISI assist, AnyDesk…)", h, "program", ref y);
            if (h == null) program.Text = "EPOLISI assist";
            remoteId = Field("ID / адрес", h, "remote_id", ref y);
            password = Field("Пароль", h, "password", ref y);
            phone = Field("Телефон", h, "phone", ref y);
            grp = Field("Группа (филиал)", h, "grp", ref y);
            note = Field("Заметка", h, "note", ref y);
            var ok = new Button { Text = "Сохранить", DialogResult = DialogResult.None, Location = new Point(176, y + 10), Size = new Size(160, 32) };
            ok.Click += delegate { Save(); };
            Controls.Add(ok);
            AcceptButton = ok;
            ClientSize = new Size(360, y + 56);
        }

        TextBox Field(string label, Dictionary<string, object> h, string key, ref int y)
        {
            Controls.Add(new Label { Text = label, Location = new Point(12, y), AutoSize = true, ForeColor = Color.Gray });
            y += 20;
            var t = new TextBox { Location = new Point(12, y), Width = 336 };
            if (h != null && h.ContainsKey(key) && h[key] != null) t.Text = h[key].ToString();
            Controls.Add(t);
            y += 32;
            return t;
        }

        void Save()
        {
            if (name.Text.Trim().Length == 0) { MessageBox.Show("Укажите название.", "EPOLISI assist"); return; }
            Result = new Dictionary<string, object> {
                { "name", name.Text }, { "program", program.Text }, { "remote_id", remoteId.Text },
                { "password", password.Text }, { "phone", phone.Text }, { "grp", grp.Text }, { "note", note.Text }
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // ---------- помощники (создание/удаление дополнительных логинов) ----------
    class UsersDialog : Form
    {
        readonly Api api;
        readonly Panel listPanel;
        readonly TextBox nu, np;

        public UsersDialog(Api api)
        {
            this.api = api;
            Text = "Помощники";
            ClientSize = new Size(380, 420);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;

            Controls.Add(new Label {
                Text = "У каждого свой логин и пароль. Видят только то, что добавили сами, или то, куда вы дали доступ (кнопка «Доступ» у записи).",
                Location = new Point(12, 10), Size = new Size(356, 40), ForeColor = Color.Gray });

            listPanel = new Panel { Location = new Point(12, 56), Size = new Size(356, 270), AutoScroll = true, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(listPanel);

            Controls.Add(new Label { Text = "Логин", Location = new Point(12, 334), AutoSize = true, ForeColor = Color.Gray });
            nu = new TextBox { Location = new Point(12, 352), Width = 150 };
            Controls.Add(nu);
            Controls.Add(new Label { Text = "Пароль (от 6 символов)", Location = new Point(168, 334), AutoSize = true, ForeColor = Color.Gray });
            np = new TextBox { Location = new Point(168, 352), Width = 120 };
            Controls.Add(np);
            var addBtn = new Button { Text = "Добавить", Location = new Point(296, 350), Size = new Size(72, 26) };
            addBtn.Click += delegate { Add(); };
            Controls.Add(addBtn);

            var closeBtn = new Button { Text = "Закрыть", DialogResult = DialogResult.OK, Location = new Point(276, 382), Size = new Size(92, 28) };
            Controls.Add(closeBtn);
            AcceptButton = closeBtn;

            Load += delegate { Reload(); };
        }

        void Reload()
        {
            listPanel.Controls.Clear();
            List<object> users;
            try { users = api.Users(); } catch (ApiError ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); return; }
            if (users.Count == 0) { listPanel.Controls.Add(new Label { Text = "Помощников пока нет", Location = new Point(8, 8), AutoSize = true, ForeColor = Color.Gray }); return; }
            int y = 6;
            foreach (string u in users)
            {
                listPanel.Controls.Add(new Label { Text = u, Location = new Point(8, y + 5), AutoSize = true });
                var del = new Button { Text = "Удалить", Location = new Point(258, y), Size = new Size(84, 26) };
                string uu = u;
                del.Click += delegate { Delete(uu); };
                listPanel.Controls.Add(del);
                y += 34;
            }
        }

        void Add()
        {
            string user = nu.Text.Trim(), pass = np.Text;
            if (user.Length == 0 || pass.Length == 0) { MessageBox.Show("Введите логин и пароль.", "EPOLISI assist"); return; }
            try { api.AddUser(user, pass); nu.Text = ""; np.Text = ""; Reload(); }
            catch (ApiError ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); }
        }

        void Delete(string u)
        {
            if (MessageBox.Show("Удалить помощника «" + u + "»? Доступы, которые вы ему дали, тоже удалятся.", "EPOLISI assist", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try { api.DeleteUser(u); Reload(); }
            catch (ApiError ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); }
        }
    }

    static class GrantHelpers
    {
        public static HashSet<string> GrantedSet(Dictionary<string, object> map, string kind, string target)
        {
            var set = new HashSet<string>();
            if (map == null || !map.ContainsKey(kind)) return set;
            var sub = map[kind] as Dictionary<string, object>;
            if (sub == null || !sub.ContainsKey(target)) return set;
            foreach (var u in Api.ToList(sub[target])) set.Add((string)u);
            return set;
        }
    }

    // ---------- доступ к одной записи: по компьютеру и/или по всему филиалу ----------
    class GrantsDialog : Form
    {
        readonly Api api;
        readonly Dictionary<string, object> host;
        readonly Panel panel;

        public GrantsDialog(Api api, Dictionary<string, object> host)
        {
            this.api = api; this.host = host;
            Text = "Доступ: " + host["name"];
            ClientSize = new Size(360, 420);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;

            panel = new Panel { Location = new Point(12, 12), Size = new Size(336, 356), AutoScroll = true, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(panel);
            var closeBtn = new Button { Text = "Закрыть", DialogResult = DialogResult.OK, Location = new Point(256, 380), Size = new Size(92, 28) };
            Controls.Add(closeBtn);
            AcceptButton = closeBtn;

            Load += delegate { Reload(); };
        }

        void Reload()
        {
            panel.Controls.Clear();
            List<object> users;
            Dictionary<string, object> map;
            try { users = api.Users(); map = api.Grants(); }
            catch (ApiError ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); return; }
            if (users.Count == 0) { panel.Controls.Add(new Label { Text = "Сначала добавьте помощника.", Location = new Point(8, 8), AutoSize = true, ForeColor = Color.Gray }); return; }
            int y = 8;
            string grp = host.ContainsKey("grp") && host["grp"] != null ? (string)host["grp"] : "";
            string hostId = host["id"].ToString();
            if (!string.IsNullOrEmpty(grp))
            {
                panel.Controls.Add(new Label { Text = "Весь филиал «" + grp + "»:", Location = new Point(8, y), AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) });
                y += 24;
                var granted = GrantHelpers.GrantedSet(map, "group", grp);
                foreach (string u in users)
                {
                    var cb = new CheckBox { Text = u, Location = new Point(16, y), AutoSize = true, Checked = granted.Contains(u) };
                    string uu = u;
                    cb.CheckedChanged += delegate { Toggle("group", grp, uu, cb.Checked); };
                    panel.Controls.Add(cb);
                    y += 26;
                }
                y += 8;
                panel.Controls.Add(new Label { Text = "Только этот компьютер:", Location = new Point(8, y), AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) });
                y += 24;
            }
            var grantedHost = GrantHelpers.GrantedSet(map, "host", hostId);
            foreach (string u in users)
            {
                var cb = new CheckBox { Text = u, Location = new Point(16, y), AutoSize = true, Checked = grantedHost.Contains(u) };
                string uu = u;
                cb.CheckedChanged += delegate { Toggle("host", hostId, uu, cb.Checked); };
                panel.Controls.Add(cb);
                y += 26;
            }
        }

        void Toggle(string kind, string target, string user, bool on)
        {
            try { api.ToggleGrant(kind, target, user, on); }
            catch (ApiError ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); }
        }
    }

    // ---------- доступ сразу ко всему филиалу ----------
    class GroupsDialog : Form
    {
        readonly Api api;
        readonly ComboBox sel;
        readonly Panel panel;

        public GroupsDialog(Api api, List<object> groups)
        {
            this.api = api;
            Text = "Доступ по филиалам";
            ClientSize = new Size(340, 400);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;

            Controls.Add(new Label {
                Text = "Откройте доступ ко всему филиалу сразу — помощник увидит все компьютеры этой группы, включая те, что добавят позже.",
                Location = new Point(12, 10), Size = new Size(316, 44), ForeColor = Color.Gray });
            sel = new ComboBox { Location = new Point(12, 60), Width = 316, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (string g in groups) sel.Items.Add(g);
            if (sel.Items.Count > 0) sel.SelectedIndex = 0;
            sel.SelectedIndexChanged += delegate { Reload(); };
            Controls.Add(sel);

            panel = new Panel { Location = new Point(12, 96), Size = new Size(316, 254), AutoScroll = true, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(panel);

            var closeBtn = new Button { Text = "Закрыть", DialogResult = DialogResult.OK, Location = new Point(236, 358), Size = new Size(92, 28) };
            Controls.Add(closeBtn);
            AcceptButton = closeBtn;

            Load += delegate { Reload(); };
        }

        void Reload()
        {
            panel.Controls.Clear();
            if (sel.SelectedItem == null) return;
            string grp = (string)sel.SelectedItem;
            List<object> users;
            Dictionary<string, object> map;
            try { users = api.Users(); map = api.Grants(); }
            catch (ApiError ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); return; }
            if (users.Count == 0) { panel.Controls.Add(new Label { Text = "Сначала добавьте помощника.", Location = new Point(8, 8), AutoSize = true, ForeColor = Color.Gray }); return; }
            var granted = GrantHelpers.GrantedSet(map, "group", grp);
            int y = 8;
            foreach (string u in users)
            {
                var cb = new CheckBox { Text = u, Location = new Point(8, y), AutoSize = true, Checked = granted.Contains(u) };
                string uu = u;
                cb.CheckedChanged += delegate
                {
                    try { api.ToggleGrant("group", grp, uu, cb.Checked); }
                    catch (ApiError ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); }
                };
                panel.Controls.Add(cb);
                y += 26;
            }
        }
    }

    // ---------- журнал подключений сотрудников ----------
    class LogsDialog : Form
    {
        readonly Api api;
        readonly TextBox q;
        readonly ListView lv;

        public LogsDialog(Api api)
        {
            this.api = api;
            Text = "Журнал подключений";
            ClientSize = new Size(760, 480);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;

            Controls.Add(new Label { Text = "Кто из сотрудников и когда подключался к чужим компьютерам. Хранится 1 год.", Location = new Point(12, 10), AutoSize = true, ForeColor = Color.Gray });
            q = new TextBox { Location = new Point(12, 36), Width = 400 };
            q.TextChanged += delegate { Reload(); };
            Controls.Add(q);

            lv = new ListView { Location = new Point(12, 66), Size = new Size(736, 360), View = View.Details, FullRowSelect = true };
            lv.Columns.Add("Начало", 130);
            lv.Columns.Add("Кто", 90);
            lv.Columns.Add("Куда", 170);
            lv.Columns.Add("Длительность", 100);
            lv.Columns.Add("Клики/нажатия", 100);
            lv.Columns.Add("IP", 120);
            Controls.Add(lv);

            var closeBtn = new Button { Text = "Закрыть", DialogResult = DialogResult.OK, Location = new Point(658, 434), Size = new Size(92, 28) };
            Controls.Add(closeBtn);
            AcceptButton = closeBtn;

            Load += delegate { Reload(); };
        }

        void Reload()
        {
            List<object> rows;
            try { rows = api.Logs(q.Text); }
            catch (ApiError ex) { MessageBox.Show(ex.Message, "EPOLISI assist"); return; }
            lv.Items.Clear();
            foreach (Dictionary<string, object> r in rows)
            {
                string start = r["start_ts"].ToString();
                string dur = "идёт сейчас";
                if (r.ContainsKey("end_ts") && r["end_ts"] != null)
                {
                    DateTime s, e;
                    if (DateTime.TryParse(start, out s) && DateTime.TryParse(r["end_ts"].ToString(), out e))
                        dur = Math.Max(0, (int)(e - s).TotalMinutes) + " мин";
                }
                string inputs = r.ContainsKey("inputs") && r["inputs"] != null ? r["inputs"].ToString() : "0";
                string dest = r.ContainsKey("agent_name") && r["agent_name"] != null && r["agent_name"].ToString().Length > 0
                    ? r["agent_name"].ToString() : r["agent_id"].ToString();
                var it = new ListViewItem(new[] { start, r["username"].ToString(), dest, dur, inputs,
                    r.ContainsKey("ip") && r["ip"] != null ? r["ip"].ToString() : "" });
                if (inputs == "0") it.ForeColor = Color.FromArgb(209, 36, 47);
                lv.Items.Add(it);
            }
        }
    }

    // PictureBox получает собственный HWND — клавиатурные сообщения идут именно сюда,
    // а не в форму-владельца, поэтому перехватывать их нужно прямо в этом контроле.
    class CaptureBox : PictureBox
    {
        const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        public Func<int, bool, bool, bool> OnKey; // (vk, down, extended) -> true, если обработано (не отдавать дальше)

        protected override bool IsInputKey(Keys keyData) { return true; } // забираем Tab/стрелки/Enter себе, а не на навигацию по диалогу

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN || m.Msg == WM_KEYUP || m.Msg == WM_SYSKEYUP)
            {
                int vk = m.WParam.ToInt32() & 0xFF;
                bool down = m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN;
                bool ext = (m.LParam.ToInt64() & 0x1000000) != 0;
                if (OnKey != null && OnKey(vk, down, ext)) return;
            }
            base.WndProc(ref m);
        }
    }

    // ---------- окно сеанса: экран агента + управление ----------
    class ViewForm : Form
    {
        readonly Api api;
        readonly string id, pw;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly CaptureBox pic;
        readonly Panel bar, filesPanel;
        readonly Label title, status, role, toast;
        readonly Button btnTake, btnFiles, btnFull, btnClip;
        readonly ComboBox qual;
        readonly ListBox fileList;
        readonly Label filePath, fileProg;

        ClientWebSocket ws;
        CancellationTokenSource cts;
        volatile bool connected, isController, closing;
        int frames, bytesCount, resW, resH;
        readonly System.Windows.Forms.Timer statTimer, moveTimer, toastTimer;
        PointF? pendingMove;
        bool fullscreen;
        Rectangle normalBounds;
        FormBorderStyle normalBorder;

        string curPath = "";
        List<Dictionary<string, object>> fileEntries = new List<Dictionary<string, object>>();
        MemoryStream downloadBuf;
        string downloadName;

        public ViewForm(Api api, string id, string name, string pw)
        {
            this.api = api; this.id = id; this.pw = pw;
            Text = "EPOLISI assist — " + name;
            ClientSize = new Size(1100, 680);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;

            bar = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Color.FromArgb(246, 246, 248) };
            title = new Label { Text = name, Location = new Point(10, 10), AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            status = new Label { Text = "Подключение…", Location = new Point(160, 11), AutoSize = true, ForeColor = Color.Gray };
            role = new Label { Text = "", Location = new Point(400, 11), AutoSize = true, ForeColor = Color.FromArgb(31, 111, 235) };
            btnTake = new Button { Text = "Взять управление", Location = new Point(500, 6), Size = new Size(150, 26), Visible = false };
            btnTake.Click += delegate { Send(new Dictionary<string, object> { { "type", "control" } }); };
            qual = new ComboBox { Location = new Point(660, 7), Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
            qual.Items.AddRange(new object[] { "Быстро", "Норма", "Чётко" });
            qual.SelectedIndex = 1;
            qual.SelectedIndexChanged += delegate { SendQuality(); };
            btnClip = new Button { Text = "📋 Агенту", Location = new Point(758, 6), Size = new Size(84, 26) };
            btnClip.Click += delegate { SendClipToAgent(); };
            btnFiles = new Button { Text = "Файлы", Location = new Point(846, 6), Size = new Size(70, 26) };
            btnFiles.Click += delegate { ToggleFiles(); };
            btnFull = new Button { Text = "⛶", Location = new Point(922, 6), Size = new Size(34, 26) };
            btnFull.Click += delegate { ToggleFullscreen(); };
            var btnEnd = new Button { Text = "Отключиться", Location = new Point(962, 6), Size = new Size(110, 26), ForeColor = Color.FromArgb(209, 36, 47) };
            btnEnd.Click += delegate { Close(); };
            bar.Controls.AddRange(new Control[] { title, status, role, btnTake, qual, btnClip, btnFiles, btnFull, btnEnd });
            Controls.Add(bar);

            pic = new CaptureBox { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom, TabStop = true };
            pic.OnKey = HandleKey;
            pic.MouseDown += Pic_MouseDown; pic.MouseUp += Pic_MouseUp; pic.MouseMove += Pic_MouseMove; pic.MouseWheel += Pic_MouseWheel;
            Controls.Add(pic);
            pic.BringToFront();

            toast = new Label { BackColor = Color.FromArgb(230, 20, 20, 20), ForeColor = Color.White, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
            Controls.Add(toast);
            toast.BringToFront();

            filesPanel = new Panel { Dock = DockStyle.Right, Width = 300, BackColor = Color.FromArgb(246, 246, 248), Visible = false };
            filesPanel.Controls.Add(new Label { Text = "Файлы агента", Font = new Font("Segoe UI", 10f, FontStyle.Bold), Location = new Point(10, 8), AutoSize = true });
            filePath = new Label { Location = new Point(10, 32), Size = new Size(280, 32), ForeColor = Color.Gray, Font = new Font("Consolas", 8f) };
            filesPanel.Controls.Add(filePath);
            fileList = new ListBox { Location = new Point(10, 68), Size = new Size(280, 440) };
            fileList.DoubleClick += FileList_DoubleClick;
            filesPanel.Controls.Add(fileList);
            var upBtn = new Button { Text = "↑ Вверх", Location = new Point(10, 514), Size = new Size(90, 26) };
            upBtn.Click += delegate { Send(new Dictionary<string, object> { { "type", "fls" }, { "path", curPath }, { "up", 1 } }); };
            filesPanel.Controls.Add(upBtn);
            var homeBtn = new Button { Text = "Рабочий стол", Location = new Point(106, 514), Size = new Size(100, 26) };
            homeBtn.Click += delegate { Send(new Dictionary<string, object> { { "type", "fls" }, { "path", "" } }); };
            filesPanel.Controls.Add(homeBtn);
            var sendBtn = new Button { Text = "Отправить агенту", Location = new Point(10, 546), Size = new Size(280, 28) };
            sendBtn.Click += delegate { UploadFiles(); };
            filesPanel.Controls.Add(sendBtn);
            fileProg = new Label { Location = new Point(10, 580), Size = new Size(280, 40), ForeColor = Color.Gray };
            filesPanel.Controls.Add(fileProg);
            Controls.Add(filesPanel);

            statTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            statTimer.Tick += delegate { UpdateStatus(); };
            statTimer.Start();
            moveTimer = new System.Windows.Forms.Timer { Interval = 35 };
            moveTimer.Tick += delegate { FlushMove(); };
            moveTimer.Start();
            toastTimer = new System.Windows.Forms.Timer { Interval = 4000 };
            toastTimer.Tick += delegate { toastTimer.Stop(); toast.Visible = false; };

            FormClosing += delegate { closing = true; Disconnect(); };
            Shown += delegate { Connect(); };
        }

        // ---------- соединение ----------
        async void Connect()
        {
            status.Text = "Подключение…";
            cts = new CancellationTokenSource();
            try { await RunSocket(cts.Token); }
            catch (Exception) { }
            if (!closing)
            {
                connected = false;
                Ui(delegate { status.Text = "Соединение прервано"; status.ForeColor = Color.FromArgb(209, 36, 47); ShowReconnect(); });
            }
        }

        void ShowReconnect()
        {
            var btn = new Button { Text = "Подключиться снова", AutoSize = true };
            btn.Location = new Point((pic.Width - 180) / 2, (pic.Height - 30) / 2);
            btn.Click += delegate { pic.Controls.Clear(); Connect(); };
            pic.Controls.Clear();
            pic.Controls.Add(btn);
        }

        async Task RunSocket(CancellationToken ct)
        {
            var sock = new ClientWebSocket();
            sock.Options.SetRequestHeader("Origin", Program.BaseUrl);
            sock.Options.Cookies = api.Cookies;
            ws = sock;
            using (sock)
            {
                var uri = new Uri(Program.BaseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/ws/view?id=" + id);
                await sock.ConnectAsync(uri, ct);
                await SendRaw(new Dictionary<string, object> { { "type", "auth" }, { "pw", pw } });
                var buf = new byte[262144];
                var ms = new MemoryStream();
                while (sock.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await sock.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Text)
                        HandleText(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                    else
                        HandleFrame(ms.ToArray());
                }
            }
        }

        void HandleFrame(byte[] data)
        {
            Image img;
            try { using (var s = new MemoryStream(data)) img = Image.FromStream(s); }
            catch (Exception) { return; }
            frames++; bytesCount += data.Length;
            Ui(delegate
            {
                var old = pic.Image;
                pic.Image = img;
                if (old != null) old.Dispose();
                if (pic.Controls.Count > 0) pic.Controls.Clear();
            });
            Send(new Dictionary<string, object> { { "type", "ack" } });
        }

        void HandleText(string text)
        {
            var m = json.DeserializeObject(text) as Dictionary<string, object>;
            if (m == null || !m.ContainsKey("type")) return;
            string t = (string)m["type"];
            if (t == "connected")
            {
                connected = true;
                Ui(delegate
                {
                    title.Text = m["name"] + " · " + id;
                    status.Text = "Подключено"; status.ForeColor = Color.Gray;
                    SendQuality();
                    pic.Focus();
                });
                SetRole((bool)m["controller"], 1);
            }
            else if (t == "role") SetRole((bool)m["controller"], Convert.ToInt32(m["viewers"]));
            else if (t == "info") { resW = Convert.ToInt32(m["w"]); resH = Convert.ToInt32(m["h"]); }
            else if (t == "clip")
            {
                string clipText = m.ContainsKey("text") ? (string)m["text"] : "";
                Ui(delegate
                {
                    try { Clipboard.SetText(clipText); } catch (Exception) { }
                    ShowToast("Буфер агента скопирован — можно вставить (Ctrl+V)");
                });
            }
            else if (t != null && t.Length > 0 && t[0] == 'f') Ui(delegate { HandleFile(t, m); });
            else if (t == "error")
            {
                connected = false;
                string msg = m.ContainsKey("msg") ? (string)m["msg"] : "Ошибка";
                Ui(delegate { status.Text = msg; status.ForeColor = Color.FromArgb(209, 36, 47); ShowReconnect(); });
            }
        }

        void SetRole(bool ctrl, int viewers)
        {
            isController = ctrl;
            Ui(delegate
            {
                role.Text = ctrl ? "Вы управляете" : ("Вы смотрите" + (viewers > 1 ? " (" + viewers + ")" : ""));
                btnTake.Visible = !ctrl;
                qual.Enabled = ctrl;
                btnFiles.Enabled = ctrl;
                btnClip.Enabled = ctrl;
                if (!ctrl) filesPanel.Visible = false;
            });
        }

        void SendQuality()
        {
            if (!isController) return;
            string mode = qual.SelectedIndex == 0 ? "fast" : qual.SelectedIndex == 2 ? "hq" : "normal";
            Send(new Dictionary<string, object> { { "type", "q" }, { "mode", mode } });
        }

        void UpdateStatus()
        {
            if (!connected) return;
            status.Text = string.Format("Подключено · {0}×{1} · {2} кадр/с · {3} КБ/с", resW, resH, frames, bytesCount / 1024);
            frames = 0; bytesCount = 0;
        }

        void Disconnect()
        {
            try { cts.Cancel(); } catch (Exception) { }
            try { if (ws != null) ws.Abort(); } catch (Exception) { }
        }

        async void Send(Dictionary<string, object> o) { await SendRaw(o); }

        async Task SendRaw(Dictionary<string, object> o)
        {
            var sock = ws;
            if (sock == null || sock.State != WebSocketState.Open) return;
            try
            {
                var data = Encoding.UTF8.GetBytes(json.Serialize(o));
                await sock.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception) { }
        }

        void Ui(MethodInvoker a)
        {
            if (IsDisposed) return;
            try { if (InvokeRequired) BeginInvoke(a); else a(); } catch (InvalidOperationException) { }
        }

        // ---------- мышь ----------
        PointF Pos(Point p)
        {
            if (pic.Image == null || pic.Width == 0 || pic.Height == 0) return new PointF(0, 0);
            float iw = pic.Image.Width, ih = pic.Image.Height;
            float s = Math.Min(pic.Width / iw, pic.Height / ih);
            float dw = iw * s, dh = ih * s;
            float ox = (pic.Width - dw) / 2, oy = (pic.Height - dh) / 2;
            float nx = Clamp((p.X - ox) / dw), ny = Clamp((p.Y - oy) / dh);
            return new PointF(nx, ny);
        }

        static float Clamp(float v) { return Math.Max(0, Math.Min(1, v)); }

        static string Btn(MouseButtons b) { return b == MouseButtons.Right ? "r" : b == MouseButtons.Middle ? "m" : "l"; }

        void Pic_MouseDown(object sender, MouseEventArgs e)
        {
            pic.Focus();
            if (!connected || !isController) return;
            var p = Pos(e.Location);
            Send(new Dictionary<string, object> { { "type", "b" }, { "b", Btn(e.Button) }, { "d", 1 }, { "x", p.X }, { "y", p.Y } });
        }

        void Pic_MouseUp(object sender, MouseEventArgs e)
        {
            if (!connected || !isController) return;
            var p = Pos(e.Location);
            Send(new Dictionary<string, object> { { "type", "b" }, { "b", Btn(e.Button) }, { "d", 0 }, { "x", p.X }, { "y", p.Y } });
        }

        void Pic_MouseMove(object sender, MouseEventArgs e)
        {
            if (!connected || !isController) return;
            pendingMove = Pos(e.Location);
        }

        void FlushMove()
        {
            if (pendingMove == null) return;
            var p = pendingMove.Value; pendingMove = null;
            Send(new Dictionary<string, object> { { "type", "m" }, { "x", p.X }, { "y", p.Y } });
        }

        void Pic_MouseWheel(object sender, MouseEventArgs e)
        {
            if (!connected || !isController) return;
            Send(new Dictionary<string, object> { { "type", "w" }, { "d", e.Delta } });
        }

        // ---------- клавиатура: настоящие коды Win32 напрямую из CaptureBox.WndProc ----------
        bool HandleKey(int vk, bool down, bool ext)
        {
            if (down && vk == 0x51 && ModifierKeys == (Keys.Control | Keys.Alt | Keys.Shift))
            {
                if (fullscreen) ToggleFullscreen();
                return true; // не отдаём дальше и не пересылаем агенту
            }
            if (connected && isController)
                Send(new Dictionary<string, object> { { "type", "k" }, { "vk", vk }, { "d", down ? 1 : 0 }, { "x", ext ? 1 : 0 } });
            return true; // всегда забираем себе — иначе Tab/стрелки/Alt переключают фокус здесь, а не у агента
        }

        // ---------- полноэкранный режим ----------
        void ToggleFullscreen()
        {
            if (!fullscreen)
            {
                normalBounds = Bounds; normalBorder = FormBorderStyle;
                fullscreen = true;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;
                bar.Visible = false;
                ShowToast("Полноэкранный режим. Для выхода: Ctrl+Alt+Shift+Q");
            }
            else
            {
                fullscreen = false;
                FormBorderStyle = normalBorder;
                Bounds = normalBounds;
                bar.Visible = true;
            }
            pic.Focus();
        }

        void ShowToast(string text)
        {
            toast.Text = text;
            toast.Size = new Size(460, 36);
            toast.Location = new Point((ClientSize.Width - toast.Width) / 2, ClientSize.Height - 70);
            toast.Visible = true;
            toast.BringToFront();
            toastTimer.Stop(); toastTimer.Start();
        }

        // ---------- файлы ----------
        void SendClipToAgent()
        {
            if (!isController) { MessageBox.Show("Сейчас управляете не вы.", "EPOLISI assist"); return; }
            try
            {
                if (!Clipboard.ContainsText()) { MessageBox.Show("В буфере обмена нет текста.", "EPOLISI assist"); return; }
                Send(new Dictionary<string, object> { { "type", "clip" }, { "text", Clipboard.GetText() } });
                ShowToast("Отправлено агенту");
            }
            catch (Exception) { }
        }

        void ToggleFiles()
        {
            filesPanel.Visible = !filesPanel.Visible;
            if (filesPanel.Visible) Send(new Dictionary<string, object> { { "type", "fls" }, { "path", "" } });
        }

        void HandleFile(string t, Dictionary<string, object> m)
        {
            if (t == "flist")
            {
                curPath = m.ContainsKey("path") ? (string)m["path"] : "";
                filePath.Text = curPath.Length > 0 ? curPath : "Рабочий стол";
                fileEntries = new List<Dictionary<string, object>>();
                var entries = Api.ToList(m["entries"]);
                fileList.Items.Clear();
                foreach (Dictionary<string, object> en in entries)
                {
                    fileEntries.Add(en);
                    bool dir = Convert.ToInt32(en["d"]) != 0;
                    long sz = en.ContainsKey("s") ? Convert.ToInt64(en["s"]) : 0;
                    fileList.Items.Add((dir ? "📁 " : "📄 ") + en["n"] + (!dir && sz > 0 ? "  (" + (sz / 1024) + " КБ)" : ""));
                }
            }
            else if (t == "fgetmeta") { downloadBuf = new MemoryStream(); downloadName = (string)m["name"]; }
            else if (t == "fdata")
            {
                string b64 = m.ContainsKey("data") ? (string)m["data"] : "";
                if (b64.Length > 0 && downloadBuf != null) { var b = Convert.FromBase64String(b64); downloadBuf.Write(b, 0, b.Length); }
            }
            else if (t == "fend")
            {
                if (downloadBuf != null) SaveDownload();
            }
            else if (t == "fok") fileProg.Text = m.ContainsKey("msg") ? (string)m["msg"] : "Готово";
            else if (t == "ferr") fileProg.Text = m.ContainsKey("msg") ? (string)m["msg"] : "Ошибка";
        }

        void FileList_DoubleClick(object sender, EventArgs e)
        {
            int i = fileList.SelectedIndex;
            if (i < 0 || i >= fileEntries.Count) return;
            var en = fileEntries[i];
            bool dir = Convert.ToInt32(en["d"]) != 0;
            if (dir) Send(new Dictionary<string, object> { { "type", "fls" }, { "path", en["p"] } });
            else
            {
                fileProg.Text = "Скачивание " + en["n"] + "…";
                Send(new Dictionary<string, object> { { "type", "fget" }, { "path", en["p"] } });
            }
        }

        void SaveDownload()
        {
            using (var sfd = new SaveFileDialog { FileName = downloadName })
            {
                if (sfd.ShowDialog(this) == DialogResult.OK)
                    File.WriteAllBytes(sfd.FileName, downloadBuf.ToArray());
            }
            fileProg.Text = "Скачано: " + downloadName;
            downloadBuf = null;
        }

        async void UploadFiles()
        {
            using (var ofd = new OpenFileDialog { Multiselect = true })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                foreach (var path in ofd.FileNames)
                {
                    var fi = new FileInfo(path);
                    fileProg.Text = "Отправка " + fi.Name + "…";
                    await SendRaw(new Dictionary<string, object> { { "type", "fput" }, { "name", fi.Name }, { "size", fi.Length } });
                    using (var fs = File.OpenRead(path))
                    {
                        var buf = new byte[48000];
                        int n;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                            await SendRaw(new Dictionary<string, object> { { "type", "fdata" }, { "data", Convert.ToBase64String(buf, 0, n) } });
                    }
                    await SendRaw(new Dictionary<string, object> { { "type", "fend" } });
                }
                fileProg.Text = "Отправлено. У агента на рабочем столе, папка «EPOLISI assist».";
            }
        }
    }

}
