using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KartDriftLocal
{
    static partial class Launcher
    {
        static readonly Dictionary<string, string> EnglishMessages = new Dictionary<string, string> {
            {"正在核对客户端和运行库…", "Checking the game client and runtime…"},
            {"正在本地登录…", "Signing in locally…"},
            {"本地游戏运行中，退出游戏后再关闭启动器。", "Game running. Exit the game before closing this launcher."},
            {"游戏已退出，存档保留。", "Game closed. Your save data has been kept."},
            {"本地登录验证超时。", "Local sign-in timed out."},
            {"游戏尚未完成本地登录就退出，请查看 logs/launcher.log。", "The game closed before local sign-in completed. See logs/launcher.log."},
            {"游戏或第三方启动器正在运行，请先正常退出。", "The game or another launcher is running. Please close it first."},
            {"没有找到游戏，请选择包含 KartDrift 文件夹的游戏目录。", "Game not found. Select the directory containing the KartDrift folder."},
            {"客户端版本不匹配，未启动游戏或修改配置。", "Client version mismatch. The game was not started and settings were not changed."},
            {"游戏内的 nexon_api_x64.dll 缺失或版本不匹配，请使用同版本完整客户端；无需安装 Nexon 启动器。", "The game's nexon_api_x64.dll is missing or incompatible. Use the complete matching client; Nexon Launcher is not required."},
            {"原生兼容运行库缺失或校验失败，请重新解压完整目录。", "The native runtime is missing or failed verification. Extract the complete package again."},
            {"旧启动器仍有待恢复的连接配置，请先运行旧版完成恢复。", "The previous launcher has pending connection settings. Run it to finish recovery first."},
            {"游戏连接配置为只读，请先恢复可写权限。", "Game connection settings are read-only. Restore write access first."},
            {"配置恢复路径不一致。", "The settings recovery path does not match."},
            {"连接配置被其他程序修改，已保留恢复备份，请处理冲突后重试。", "Another program changed the connection settings. The recovery backup has been kept; resolve the conflict and retry."}
        };
        static string UiText(string message, bool english)
        { string translated; return english && EnglishMessages.TryGetValue(message, out translated) ? translated : message; }

        internal static Form CreateUi(bool? previewEnglish = null)
        {
            string settingsPath = Path.Combine(Root, "settings.json"); var settings = J.M();
            if (File.Exists(settingsPath)) try { settings = J.D(J.Read(File.ReadAllText(settingsPath))); } catch (ArgumentException) { }
            bool english = previewEnglish ?? J.Same(J.Get(settings, "language"), "en");
            var window = new Form { Text = "KartDrift Offline Launcher", ClientSize = new Size(620, 330), StartPosition = FormStartPosition.CenterScreen, Font = new Font("Microsoft YaHei UI", 10), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false };
            var title = new Label { Name = "Title", Location = new Point(24, 20), AutoSize = true, Font = new Font(window.Font.FontFamily, 17, FontStyle.Bold) };
            var languageLabel = new Label { Name = "LanguageLabel", Text = "语言 / Language", Location = new Point(350, 20), Size = new Size(118, 29), TextAlign = ContentAlignment.MiddleRight };
            var language = new ComboBox { Name = "Language", Location = new Point(474, 20), Size = new Size(120, 29), DropDownStyle = ComboBoxStyle.DropDownList };
            language.Items.AddRange(new object[] { "简体中文", "English" }); language.SelectedIndex = english ? 1 : 0;
            var path = new TextBox { Name = "GamePath", Text = J.S(J.Get(settings, "game", "")), Location = new Point(24, 67), Size = new Size(440, 27) };
            var browse = new Button { Name = "Browse", Location = new Point(474, 65), Size = new Size(120, 31) };
            var gameLanguageLabel = new Label { Name = "GameLanguageLabel", Location = new Point(24, 109), Size = new Size(160, 29), TextAlign = ContentAlignment.MiddleLeft };
            var gameLanguage = new ComboBox { Name = "GameLanguage", Location = new Point(190, 109), Size = new Size(210, 29), DropDownStyle = ComboBoxStyle.DropDownList };
            gameLanguage.Items.AddRange(GameLanguage.Names);
            int selectedLanguage = Array.FindIndex(GameLanguage.Codes, v => v.Equals(J.S(J.Get(settings, "gameLanguage", "zh-Hans")), StringComparison.OrdinalIgnoreCase));
            gameLanguage.SelectedIndex = selectedLanguage < 0 ? 0 : selectedLanguage;
            var detail = new Label { Name = "SaveLocation", Location = new Point(24, 153), AutoSize = true };
            var status = new Label { Name = "Status", Location = new Point(24, 184), Size = new Size(570, 43) };
            var start = new Button { Name = "Start", Location = new Point(24, 243), Size = new Size(370, 44) };
            var folder = new Button { Name = "SaveFolder", Location = new Point(410, 243), Size = new Size(184, 44) }; bool running = false;
            string currentStatus = null;
            Action render = delegate {
                title.Text = english ? "KartDrift Offline Launcher" : "KartDrift离线启动器";
                gameLanguageLabel.Text = english ? "Game language" : "游戏语言";
                browse.Text = english ? "Browse…" : "选择目录";
                detail.Text = english ? "Save data: local-data beside the launcher." : "存档在启动器同级 local-data 目录";
                start.Text = english ? "Start Offline Game" : "启动本地游戏";
                folder.Text = english ? "Open Save Folder" : "打开存档目录";
                status.Text = currentStatus == null ? (english ? "Select a matching game client and game language." : "请选择游戏目录和游戏语言。") : UiText(currentStatus, english);
            };
            language.SelectedIndexChanged += delegate {
                english = language.SelectedIndex == 1; settings["language"] = english ? "en" : "zh"; render();
                if (!previewEnglish.HasValue) try { AtomicJson(settingsPath, settings); } catch (IOException error) { status.Text = error.Message; }
            };
            gameLanguage.SelectedIndexChanged += delegate {
                settings["gameLanguage"] = GameLanguage.Codes[gameLanguage.SelectedIndex];
                if (!previewEnglish.HasValue) try { AtomicJson(settingsPath, settings); } catch (IOException error) { status.Text = error.Message; }
            };
            browse.Click += delegate {
                using (var dialog = new FolderBrowserDialog { Description = english ? "Select the directory containing the KartDrift folder" : "选择包含 KartDrift 文件夹的游戏目录", SelectedPath = path.Text })
                    if (dialog.ShowDialog(window) == DialogResult.OK) path.Text = dialog.SelectedPath;
            };
            Action<string> update = message => { if (!window.IsDisposed) window.BeginInvoke((Action)(() => { currentStatus = message; render(); })); };
            start.Click += delegate {
                if (String.IsNullOrWhiteSpace(path.Text)) { status.Text = english ? "Select the game directory first." : "请先选择游戏目录。"; return; }
                string selected = path.Text, culture = GameLanguage.Codes[gameLanguage.SelectedIndex]; settings["gameLanguage"] = culture; settings["game"] = selected; settings["language"] = english ? "en" : "zh";
                try { AtomicJson(settingsPath, settings); } catch (IOException error) { status.Text = error.Message; return; }
                running = true; start.Enabled = browse.Enabled = path.Enabled = gameLanguage.Enabled = false;
                Task.Run(() => { try { Run(selected, Root, false, update, culture); } catch (Exception error) { update(error.Message); } finally { window.BeginInvoke((Action)(() => { running = false; start.Enabled = browse.Enabled = path.Enabled = gameLanguage.Enabled = true; })); } });
            };
            folder.Click += delegate { string save = Path.Combine(Root, "local-data"); Directory.CreateDirectory(save); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", NativeSession.Quote(save)) { UseShellExecute = true }); };
            window.FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (running) { e.Cancel = true; MessageBox.Show(window, english ? "Exit the game first and wait for local saving to finish." : "请先正常退出游戏，等待本地服务保存完成。", english ? "Game is running" : "游戏仍在运行"); }
            };
            window.Controls.AddRange(new Control[] { title, languageLabel, language, path, browse, gameLanguageLabel, gameLanguage, detail, status, start, folder }); render(); return window;
        }
        public static void Ui()
        { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(CreateUi()); }
        internal static void PreviewUi(string directory)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Directory.CreateDirectory(directory);
            foreach (bool english in new[] { false, true }) using (var form = CreateUi(english))
            {
                form.PerformLayout();
                using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(form.BackColor);
                    foreach (Control control in form.Controls) using (var part = new Bitmap(control.Width, control.Height))
                    { control.DrawToBitmap(part, new Rectangle(Point.Empty, part.Size)); graphics.DrawImageUnscaled(part, control.Location); }
                    bitmap.Save(Path.Combine(directory, english ? "launcher-en.png" : "launcher-zh.png"), ImageFormat.Png);
                }
            }
        }
    }
}
