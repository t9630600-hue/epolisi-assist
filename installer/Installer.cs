// EPOLISI assist — установщик. Скачивает актуальную программу-агента с сервера,
// ставит её в папку пользователя (без прав администратора), делает ярлыки
// на рабочем столе и в меню «Пуск», по желанию — автозапуск с Windows.
// Собирается встроенным в Windows компилятором .NET Framework 4.x, см. build.ps1.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("EPOLISI assist — установка")]
[assembly: System.Reflection.AssemblyProduct("EPOLISI assist")]
[assembly: System.Reflection.AssemblyCompany("EPOLISI")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace EpolisiInstaller
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallForm());
        }
    }

    class InstallForm : Form
    {
        const string ServerUrl = "https://assist.avto-sugurta.uz";
        const string AppName = "EPOLISI assist";

        readonly CheckBox autoStart;
        readonly Label status;
        readonly ProgressBar bar;
        readonly Button install;

        public InstallForm()
        {
            Text = "Установка EPOLISI assist";
            ClientSize = new Size(420, 254);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            var head = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.FromArgb(31, 111, 235) };
            head.Controls.Add(new Label { Text = "EPOLISI assist", ForeColor = Color.White, Font = new Font("Segoe UI", 16f, FontStyle.Bold), AutoSize = true, Location = new Point(18, 16) });
            Controls.Add(head);

            Controls.Add(new Label {
                Text = "Программа для удалённой помощи. Установится для вашего пользователя Windows — права администратора не нужны.",
                Location = new Point(18, 74), Size = new Size(384, 44), ForeColor = Color.Gray });

            autoStart = new CheckBox { Text = "Запускать вместе с Windows", Checked = true, AutoSize = true, Location = new Point(18, 122) };
            Controls.Add(autoStart);

            status = new Label { Location = new Point(18, 156), Size = new Size(384, 42), ForeColor = Color.Gray };
            Controls.Add(status);

            bar = new ProgressBar { Location = new Point(18, 204), Size = new Size(288, 24), Style = ProgressBarStyle.Marquee, Visible = false };
            Controls.Add(bar);

            install = new Button { Text = "Установить", Location = new Point(314, 204), Size = new Size(90, 28) };
            install.Click += delegate { DoInstall(); };
            Controls.Add(install);
            AcceptButton = install;
        }

        async void DoInstall()
        {
            install.Enabled = false;
            autoStart.Enabled = false;
            bar.Visible = true;
            status.ForeColor = Color.Gray;
            status.Text = "Скачивание…";
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
                Directory.CreateDirectory(dir);
                string exePath = Path.Combine(dir, "EPOLISI-assist.exe");

                await Task.Run(delegate
                {
                    using (var wc = new WebClient()) wc.DownloadFile(ServerUrl + "/download", exePath);
                });

                status.Text = "Создание ярлыков…";
                CreateShortcut(exePath, Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", AppName + ".lnk"));
                CreateShortcut(exePath, Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"));

                if (autoStart.Checked)
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                        k.SetValue(AppName, "\"" + exePath + "\" /min");
                }

                status.Text = "Готово. Запускаю программу…";
                Process.Start(exePath);
                await Task.Delay(700);
                Close();
            }
            catch (Exception ex)
            {
                bar.Visible = false;
                install.Enabled = true;
                autoStart.Enabled = true;
                status.ForeColor = Color.FromArgb(209, 36, 47);
                status.Text = "Не удалось установить: " + ex.Message;
            }
        }

        static void CreateShortcut(string targetExe, string shortcutPath)
        {
            string vbs = Path.Combine(Path.GetTempPath(), "epolisi_shortcut_" + Guid.NewGuid().ToString("N") + ".vbs");
            string script =
                "Set oWS = WScript.CreateObject(\"WScript.Shell\")\r\n" +
                "Set oLink = oWS.CreateShortcut(\"" + shortcutPath + "\")\r\n" +
                "oLink.TargetPath = \"" + targetExe + "\"\r\n" +
                "oLink.WorkingDirectory = \"" + Path.GetDirectoryName(targetExe) + "\"\r\n" +
                "oLink.IconLocation = \"" + targetExe + ",0\"\r\n" +
                "oLink.Save\r\n";
            File.WriteAllText(vbs, script);
            var psi = new ProcessStartInfo { FileName = "wscript.exe", Arguments = "//B //Nologo \"" + vbs + "\"", UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)) p.WaitForExit(5000);
            try { File.Delete(vbs); } catch (Exception) { }
        }
    }
}
