using System;
using System.IO;
using System.Text;
using System.Diagnostics;

namespace KartDriftLocal
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0)
                {
                    // GUI executables have no console code page. Bind inherited
                    // test/worker pipes directly instead of SetConsoleCP.
                    Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)));
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
                    Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
                }
                if (args.Length == 2 && args[0] == "--write-launch-state") { Launcher.AtomicJson(args[1], J.M("command", Environment.CommandLine, "cwd", Environment.CurrentDirectory, "marker", Environment.GetEnvironmentVariable("KARTDRIFT_LAUNCH_TEST"))); return 0; }
                if (args.Length == 2 && args[0] == "--launch-contract")
                {
                    var info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--write-launch-state " + GameProcess.Quote(args[1])) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(args[1]) }; info.EnvironmentVariables["KARTDRIFT_LAUNCH_TEST"] = "中文环境检查";
                    using (var child = Launcher.StartGame(info)) { J.Need(child.WaitForExit(10000) && child.ExitCode == 0, "Owned launch probe failed"); }
                    var result = J.D(J.Read(File.ReadAllText(args[1], Encoding.UTF8))); J.Need(J.S(result["command"]).StartsWith("KartDrift ", StringComparison.Ordinal), "Launch contract differs: argv[0] must be KartDrift"); J.Need(J.S(result["marker"]) == "中文环境检查", "Child environment changed"); return 0;
                }
                if (args.Length == 2 && args[0] == "--core-rpc") { Rpc(args[1]); return 0; }
                if (args.Length == 2 && args[0] == "--ui-preview") { Launcher.PreviewUi(args[1]); return 0; }
                if (args.Length == 2 && args[0] == "--self-test") { SelfTest(args[1]); return 0; }
                if (args.Length == 3 && args[0] == "--serve-test")
                { var store = new ProfileStore(args[1], Bundle.Templates()); store.Performance = new Performance(J.S(J.D(J.Read(J.S(Bundle.Templates()["PqLogin"])))["accountId"])); using (var gateway = new Gateway(store, Int32.Parse(args[2]), message => Console.Error.WriteLine(message))) { Console.WriteLine(J.Write(J.M("port", gateway.Port, "pingPort", gateway.PingPort))); Console.Out.Flush(); Console.ReadLine(); } return 0; }
                if (args.Length == 4 && args[0] == "--native-sdk-check") { SdkTest(args[1], args[2], args[3]); return 0; }
                if (args.Length >= 1 && (args[0] == "--worker" || args[0] == "--smoke-test")) { J.Need(args.Length == 3 || args.Length == 4, "Expected game, data directory and optional culture"); if (args[0] == "--smoke-test") SmokeTest(args[1], args[2], args.Length == 4 ? args[3] : "zh-Hans"); else Launcher.Run(args[1], args[2], false, message => Console.WriteLine(message), args.Length == 4 ? args[3] : "zh-Hans"); return 0; }
                if (args.Length == 2 && args[0] == "--check") { Console.WriteLine(J.Write(J.M("game", Launcher.CheckGame(args[1]), "runtime", Launcher.CheckRuntime()))); return 0; }
                if (args.Length != 0) throw new ArgumentException("Unknown command"); Launcher.Ui(); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        static void SelfTest(string report)
        {
            string directory = Path.Combine(Launcher.Root, "cache", "self-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                Launcher.CheckRuntime(); var templates = Bundle.Templates(); var store = new ProfileStore(Path.Combine(directory, "player.sqlite3"), templates); var state = store.Snapshot();
                GameLanguage.Resources();
                string originalState = J.Write(state);
                foreach (string culture in GameLanguage.Codes)
                {
                    store.SetGameLanguage(culture); var changed = store.Snapshot();
                    var options = J.Map(J.Map(changed, "optionSetting"), "gameOptions");
                    J.Need(J.S(options["DriveAssistOption"]).Contains("Culture=" + culture + ";"), "Selected language was not saved");
                    var originalOptions = J.Map(J.Map(state, "optionSetting"), "gameOptions");
                    if (originalOptions.ContainsKey("DriveAssistOption")) options["DriveAssistOption"] = originalOptions["DriveAssistOption"]; else options.Remove("DriveAssistOption");
                    J.Need(J.Write(changed) == originalState, "Language selection altered unrelated profile data");
                    string settingsText = GameLanguage.SettingsText("[/Script/KartDrift.KartGameUserSettings]\nCultureIndex=1\nLastCultureIndex=1\n[Other]\nCultureIndex=9", culture);
                    J.Need(settingsText.Contains("CultureIndex=" + GameLanguage.Index(culture) + "\r\nLastCultureIndex=1") && settingsText.EndsWith("[Other]\r\nCultureIndex=9"), "Language setting escaped its section");
                }
                J.Need(GameLanguage.Option("A=1;Culture=ko-kr;B=2;", "en-US") == "A=1;Culture=en-US;B=2;", "Culture replacement altered other options");
                J.Need(GameLanguage.Option("A=1;", "zh-Hans") == "A=1;Culture=zh-Hans;", "Missing culture was not added");
                J.Need(J.S(state["riderName"]) == "LocalDriver" && J.Map(state, "records").Count == 0 && J.List(state, "liveryVector").Count == 0 && J.List(state, "liveryStickerVector").Count == 0, "Anonymous seed failed");
                foreach (var value in J.Map(state, "ownKartsUpgradeInfo").Values) J.Need(J.I(J.Map(J.D(value), "gearInfo")["totalGearCount"]) == 51, "Gear budget failed");
                var perf = new Performance(J.S(J.D(J.Read(J.S(templates["PqLogin"])))["accountId"])); perf.Payload(state);
                string text = "便携 C# 中文检查"; J.Need(Codec.Decode(Codec.Encode(text)) == text, "Protocol codec failed");
                using (var label = new System.Windows.Forms.Label { Text = text }) J.Need(label.Text == text, "Windows Forms dependency failed");
                string config = Path.Combine(directory, "Game.ini"); byte[] original = Encoding.UTF8.GetBytes("; 配置原文\r\n[User]\r\nValue=1\r\n"); File.WriteAllBytes(config, original);
                using (var route = new LocalRoute(config, directory)) J.Need(File.ReadAllText(config).Contains("\r\nServerAddrs="), "Route installation failed");
                J.Need(Convert.ToBase64String(File.ReadAllBytes(config)) == Convert.ToBase64String(original), "Route byte restoration failed");
                string empty = Path.Combine(directory, "EmptyGame.ini");
                using (var route = new LocalRoute(empty, directory))
                { string installed = File.ReadAllText(empty); J.Need(installed.Contains("\r\nServerAddrs=") && !installed.Contains("+ServerAddrs=") && !installed.Contains("!ServerAddrs="), "Empty user config has no plain server route"); File.WriteAllText(empty, installed.Replace("\r\n", "\n").Trim() + "\n", new UTF8Encoding(false)); }
                J.Need(!File.Exists(empty), "Fresh config was not removed after normalized rewrite");
                using (var route = new LocalRoute(config, directory)) { string reformatted = File.ReadAllText(config).Replace("\r\n", "\n"); File.WriteAllText(config, reformatted, new UTF8Encoding(false)); }
                J.Need(Convert.ToBase64String(File.ReadAllBytes(config)) == Convert.ToBase64String(original), "Semantically equal config was not restored byte-for-byte");
                string recovery = Path.Combine(directory, "csharp-config-recovery.json"); var interrupted = new LocalRoute(config, directory); File.WriteAllText(config, "external change"); bool refused = false;
                try { LocalRoute.Recover(config, recovery); } catch (ArgumentException) { refused = true; }
                J.Need(refused && File.Exists(recovery) && File.ReadAllText(config) == "external change", "External config conflict was overwritten");
                var saved = J.D(J.Read(File.ReadAllText(recovery))); File.WriteAllBytes(config, Convert.FromBase64String(J.S(saved["installed"]))); interrupted.Dispose();
                J.Need(Convert.ToBase64String(File.ReadAllBytes(config)) == Convert.ToBase64String(original), "Interrupted route recovery failed");
                Launcher.AtomicJson(report, J.M("ok", true, "exe", Process.GetCurrentProcess().MainModule.FileName, "runtime", Launcher.CheckRuntime(), "saveRoot", Launcher.Root, "scope", "Native dependencies, anonymous profile and performance; no game started"));
            }
            finally { Directory.Delete(directory, true); }
        }
        static void SdkTest(string sdk, string probe, string report)
        {
            var manifest = J.D(J.Read(Bundle.Text("release/manifest.json")));
            J.Need(Launcher.Hash(sdk) == J.S(manifest["sdkSha256"]), "SDK fixture hash mismatch");
            J.Need(Path.GetFileName(probe) == "NativeSdkProbe.exe", "Use the native SDK fixture");
            using (var adapter = new NativeAdapter(Launcher.CheckRuntime(), J.S(manifest["nativeSha256"])))
            {
                Process process = null;
                try
                {
                    var info = new ProcessStartInfo(Path.GetFullPath(probe), GameProcess.Quote(Path.GetFullPath(sdk)) + " " + GameProcess.Quote(Path.GetFullPath(report))) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(probe)) };
                    adapter.Configure(info); process = GameProcess.Start(info, adapter.AttachBeforeResume);
                    J.Need(process.WaitForExit(15000), "Native SDK fixture timed out"); adapter.ThrowIfFailed(); J.Need(process.ExitCode == 0, "Native SDK fixture failed: " + process.ExitCode);
                    var result = J.D(J.Read(File.ReadAllText(report))); result["native"] = adapter.Status(); result["gameClientStarted"] = false;
                    Launcher.AtomicJson(report, result); Console.WriteLine(J.Write(result));
                }
                finally { if (process != null) { try { if (!process.HasExited) { process.Kill(); process.WaitForExit(10000); } } finally { process.Dispose(); } } }
            }
        }
        static void SmokeTest(string game, string data, string culture)
        {
            string settings = Path.Combine(Path.GetDirectoryName(Launcher.Config), "GameUserSettings.ini");
            byte[] before = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
            var attributes = before == null ? FileAttributes.Normal : File.GetAttributes(settings);
            try { Launcher.Run(game, data, true, message => Console.WriteLine(message), culture); }
            finally
            {
                if (before == null) { if (File.Exists(settings)) File.Delete(settings); }
                else { File.WriteAllBytes(settings, before); File.SetAttributes(settings, attributes); }
            }
        }
        static void Rpc(string path)
        {
            ProfileStore store = null; string line;
            while ((line = Console.ReadLine()) != null)
            {
                try
                {
                    var op = J.D(J.Read(line)); string command = J.S(op["op"]); object result;
                    switch (command)
                    {
                        case "init": store = new ProfileStore(path, op.ContainsKey("templates") ? J.Map(op, "templates") : Bundle.Templates()); result = store.Snapshot(); break;
                        case "snapshot": result = store.Snapshot(); break;
                        case "apply": store.Performance = J.Truth(J.Get(op, "performance", false)) ? new Performance(J.S(J.Get(op, "account", "LocalTestPlayer"))) : null; result = store.Apply(J.Map(op, "request"), J.Map(op, "response")); break;
                        case "body": result = Convert.ToBase64String(new Performance("LocalTestPlayer").Body(J.S(op["kart"]), J.Map(op, "upgrade"))); break;
                        case "payload": result = new Performance(J.S(J.Get(op, "account", "LocalTestPlayer"))).Payload(J.Map(op, "state")); break;
                        case "put": result = store.PutAsset(J.S(op["key"]), Convert.FromBase64String(J.S(op["data"]))); break;
                        case "get": byte[] data; int status = store.ReadAsset(J.S(op["key"]), out data); result = J.M("status", status, "data", data == null ? null : Convert.ToBase64String(data)); break;
                        case "backup": Database.Backup(path, J.S(op["destination"])); result = true; break;
                        default: throw new ArgumentException("Unknown core command");
                    }
                    Console.WriteLine(J.Write(J.M("ok", true, "result", result)));
                }
                catch (Exception error) { Console.WriteLine(J.Write(J.M("ok", false, "error", error.Message, "kind", error.GetType().Name))); }
            }
        }
    }
}
