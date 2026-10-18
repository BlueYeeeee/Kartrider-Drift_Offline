using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KartDriftLocal
{
    sealed class LocalRoute : IDisposable
    {
        readonly string config, journal;
        public LocalRoute(string path, string data, string gameLocalization = null, string localization = null)
        {
            config = System.IO.Path.GetFullPath(path); journal = System.IO.Path.Combine(data, "csharp-config-recovery.json"); Recover(config, journal);
            J.Need(!File.Exists(System.IO.Path.Combine(data, "config-recovery.json")), "旧启动器仍有待恢复的连接配置，请先运行旧版完成恢复。");
            bool existed = File.Exists(config); byte[] before = existed ? File.ReadAllBytes(config) : new byte[0]; var attributes = existed ? File.GetAttributes(config) : FileAttributes.Normal;
            J.Need((attributes & FileAttributes.ReadOnly) == 0, "游戏连接配置为只读，请先恢复可写权限。"); string content = new UTF8Encoding(false, true).GetString(before); if (content.StartsWith("\ufeff", StringComparison.Ordinal)) content = content.Substring(1);
            string routed = RouteText(content);
            if (localization != null) routed = GameLanguage.LocalizationText(routed, gameLocalization, localization);
            byte[] installed = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(routed)).ToArray();
            Launcher.AtomicJson(journal, J.M("path", config, "existed", existed, "attributes", (int)attributes, "before", Convert.ToBase64String(before), "installed", Convert.ToBase64String(installed)));
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(config)); File.WriteAllBytes(config, installed); } catch { Recover(config, journal); throw; }
        }
        internal static string RouteText(string content)
        {
            // This is a generated user Game.ini, not a Default*.ini merge layer.
            // Use the plain key consumed by the proven standalone gateway.
            var keep = new List<string>(); var section = new List<string>(); bool target = false;
            foreach (string line in content.Replace("\r\n", "\n").Split('\n'))
            {
                string text = line.Trim(); if (text.StartsWith("[") && text.EndsWith("]")) { target = text.Equals("[/Script/KartDrift.KdServerConfig]", StringComparison.OrdinalIgnoreCase); if (!target) keep.Add(line); continue; }
                if (!target) { keep.Add(line); continue; }
                int equal = text.IndexOf('='); string key = equal < 0 ? "" : text.Substring(0, equal).Trim().TrimStart('+', '-', '!', '.');
                if (!key.Equals("ServerAddrs", StringComparison.OrdinalIgnoreCase) && !key.Equals("PreAuthKey", StringComparison.OrdinalIgnoreCase)) section.Add(line);
            }
            keep.Add("[/Script/KartDrift.KdServerConfig]"); keep.AddRange(section); keep.Add("ServerAddrs=(Name=\"kd-live\",HttpUrl=\"ws://127.0.0.1:59874\")"); keep.Add("PreAuthKey=aHZG9LwcB0ROdoy0/lX789kYTjI7UpF+Ap23X4Dq+yo="); keep.Add(""); return String.Join("\r\n", keep);
        }
        static string IniMeaning(byte[] data)
        {
            if (data == null) return null; string section = ""; var values = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in new UTF8Encoding(false, true).GetString(data).TrimStart('\ufeff').Replace("\r\n", "\n").Split('\n'))
            { string line = raw.Trim(); if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue; if (line.StartsWith("[") && line.EndsWith("]")) { section = line.ToLowerInvariant(); continue; } int equal = line.IndexOf('='); string key = section + "\n" + (equal < 0 ? line : line.Substring(0, equal).Trim()).ToLowerInvariant(), value = equal < 0 ? "" : line.Substring(equal + 1).Trim(); if (!values.ContainsKey(key)) values[key] = new List<string>(); values[key].Add(value); }
            return J.Write(values);
        }
        internal static void Recover(string config, string journal)
        {
            if (!File.Exists(journal)) return; var saved = J.D(J.Read(File.ReadAllText(journal, Encoding.UTF8))); J.Need(String.Equals(System.IO.Path.GetFullPath(config), J.S(saved["path"]), StringComparison.OrdinalIgnoreCase), "配置恢复路径不一致。");
            byte[] current = File.Exists(config) ? File.ReadAllBytes(config) : null, before = J.B(saved["existed"]) ? Convert.FromBase64String(J.S(saved["before"])) : null, installed = Convert.FromBase64String(J.S(saved["installed"]));
            bool same = current == null ? before == null : before != null && current.SequenceEqual(before);
            J.Need(same || current != null && (current.SequenceEqual(installed) || IniMeaning(current) == IniMeaning(installed)), "连接配置被其他程序修改，已保留恢复备份，请处理冲突后重试。");
            if (!same) { if (before == null) File.Delete(config); else { File.WriteAllBytes(config, before); File.SetAttributes(config, (FileAttributes)J.I(saved["attributes"])); } } File.Delete(journal);
        }
        public void Dispose() { Recover(config, journal); }
    }
    static partial class Launcher
    {
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        public static string Config { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"KartDrift\Saved\Config\WindowsNoEditor\Game.ini"); } }
        public static void AtomicJson(string path, object value)
        { string temp = path + ".tmp"; File.WriteAllText(temp, J.Write(value), new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
        public static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        public static string CheckGame(string path)
        {
            path = System.IO.Path.GetFullPath(path); const string name = "KartDrift-Win64-Shipping.exe";
            string exe = new[] { path, System.IO.Path.Combine(path, @"KartDrift\Binaries\Win64\" + name), System.IO.Path.Combine(path, @"Binaries\Win64\" + name) }.FirstOrDefault(p => File.Exists(p) && System.IO.Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase));
            J.Need(exe != null, "没有找到游戏，请选择包含 KartDrift 文件夹的游戏目录。"); var manifest = J.D(J.Read(Bundle.Text("release/manifest.json")));
            J.Need(new FileInfo(exe).Length == J.I(manifest["gameBytes"]) && Hash(exe) == J.S(manifest["gameSha256"]), "客户端版本不匹配，未启动游戏或修改配置。"); return exe;
        }
        static void CheckSdk(string exe)
        {
            string sdk = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe), "nexon_api_x64.dll"); var manifest = J.D(J.Read(Bundle.Text("release/manifest.json")));
            J.Need(File.Exists(sdk) && Hash(sdk) == J.S(manifest["sdkSha256"]), "游戏内的 nexon_api_x64.dll 缺失或版本不匹配，请使用同版本完整客户端；无需安装 Nexon 启动器。");
        }
        public static string CheckRuntime()
        { string helper = System.IO.Path.Combine(Root, @"runtime\frida-inject.exe"); J.Need(File.Exists(helper) && Hash(helper) == NativeSession.HelperHash, "原生兼容运行库缺失或校验失败，请重新解压完整目录。"); return helper; }
        internal static Process StartGame(ProcessStartInfo info) { return GameProcess.Start(info); }
        public static void Run(string game, string data, bool smoke, Action<string> status, string culture = "zh-Hans")
        {
            data = System.IO.Path.GetFullPath(data); Directory.CreateDirectory(data); string cache = System.IO.Path.Combine(data, "cache"), logs = System.IO.Path.Combine(data, "logs"); Directory.CreateDirectory(cache); Directory.CreateDirectory(logs);
            var logLock = new object(); Action<string> log = text => { lock (logLock) File.AppendAllText(System.IO.Path.Combine(logs, "launcher.log"), J.Now() + " " + text + Environment.NewLine, Encoding.UTF8); };
            using (var instance = new FileStream(System.IO.Path.Combine(data, "launcher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                status("正在核对客户端和运行库…"); string exe = CheckGame(game), runtime = CheckRuntime();
                CheckSdk(exe); log("Launcher " + J.S(J.D(J.Read(Bundle.Text("release/manifest.json")))["version"]) + "; fixed local SDK adapter; game and SDK hashes verified");
                J.Need(!Process.GetProcesses().Any(p => { try { using (p) return p.ProcessName.Equals("KartDrift-Win64-Shipping", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("跑2体验版启动器.1.0.0.2c", StringComparison.OrdinalIgnoreCase); } catch (InvalidOperationException) { return false; } }), "游戏或第三方启动器正在运行，请先正常退出。");
                string save = System.IO.Path.Combine(data, @"local-data\player.sqlite3"); if (File.Exists(save)) Database.Backup(save, System.IO.Path.Combine(data, @"backups\" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff") + "\\player.sqlite3"));
                var templates = Bundle.Templates(); var store = new ProfileStore(save, templates); store.Performance = new Performance(J.S(J.D(J.Read(J.S(templates["PqLogin"])))["accountId"]));
                string localization = GameLanguage.Resources();
                store.SetGameLanguage(culture);
                GameLanguage.Configure(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Config), "GameUserSettings.ini"), culture);
                string nativeLocalization = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe), @"..\..\Content\Localization\Game"));
                using (var gateway = new Gateway(store, 59874, log)) using (var route = new LocalRoute(Config, data, nativeLocalization, localization))
                {
                    var info = new ProcessStartInfo(exe, "-nexonarena -serviceid:1050768515 -nexonsn:1 -sid:00000000-0000-0000-0000-000000000001 -Server:kd-live -httpproxy=\"\"") { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(exe) };
                    foreach (string key in info.EnvironmentVariables.Keys.Cast<string>().ToList()) if (new[] { "http_proxy", "https_proxy", "all_proxy", "no_proxy", "__compat_layer" }.Contains(key.ToLowerInvariant())) info.EnvironmentVariables.Remove(key);
                    info.EnvironmentVariables["TEMP"] = info.EnvironmentVariables["TMP"] = cache;
                    Process gameProcess = null; NativeSession session = null;
                    try
                    {
                        gameProcess = StartGame(info); log("Owned game PID " + gameProcess.Id); status("正在本地登录…");
                        session = new NativeSession(gameProcess.Id, runtime, cache, log); bool settled = false; var timer = Stopwatch.StartNew(); long nextCheck = 0; string last = null;
                        while (!gameProcess.WaitForExit(250))
                        {
                            if (gateway.LoginComplete && !settled) { log("playtime notice settle " + J.Write(session.Call("offline_playtime_notice", "settle"))); log("analytics settle " + J.Write(session.Call("offline_analytics", "settle"))); log("local sdk state " + J.Write(session.Call("audit_reuse_ticket", "status"))); settled = true; status("本地游戏运行中，退出游戏后再关闭启动器。"); }
                            if (timer.ElapsedMilliseconds >= nextCheck) { nextCheck = timer.ElapsedMilliseconds + 10000; try { string current = J.Write(session.Call("offline_analytics", "status")); if (current != last) { log("analytics status " + current); last = current; } } catch (Exception error) { log("analytics status unavailable: " + error.GetType().Name); } }
                            if (smoke && gateway.LoginComplete) break;
                            if (smoke && timer.ElapsedMilliseconds > 90000) throw new InvalidOperationException("本地登录验证超时。");
                        }
                        J.Need(gateway.LoginComplete, "游戏尚未完成本地登录就退出，请查看 logs/launcher.log。");
                        AtomicJson(System.IO.Path.Combine(data, "last-run.json"), J.M("ok", true, "loginComplete", gateway.LoginComplete, "smoke", smoke, "pid", gameProcess.Id));
                    }
                    finally
                    {
                        // A bounded diagnostic or failed startup owns this process. Stop it before detaching.
                        if (gameProcess != null) { try { if (!gameProcess.HasExited) { gameProcess.Kill(); gameProcess.WaitForExit(10000); } } finally { gameProcess.Dispose(); } }
                        if (session != null) session.Dispose();
                    }
                }
            }
            status("游戏已退出，存档保留。");
        }
    }
}
