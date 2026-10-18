using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace KartDriftLocal
{
    sealed class NativeSession : IDisposable
    {
        readonly Process helper;
        readonly object sync = new object();
        readonly Dictionary<long, Dictionary<string, object>> replies = new Dictionary<long, Dictionary<string, object>>();
        long serial;
        bool ready, exited;
        string failure;
        public const string HelperHash = "19a14d99f785f5942f68e651d1885323aef7e618723d1e75512274b3d2827843";
        internal static readonly string[] Modules = { "local_vehicle_restore", "offline_analytics", "observe_startup_latency", "capture_latency_datagrams", "audit_reuse_ticket", "offline_playtime_notice", "local_english_language" };
        public static string Agent(Dictionary<string, string> sources, bool localIdentity)
        {
            var text = new StringBuilder("'use strict';\nconst modules={};\nfunction emit(v){send(['frida:stdout',JSON.stringify(v)+'\\n']);}\n");
            foreach (var pair in sources)
            {
                text.Append("modules[").Append(J.Write(pair.Key)).Append("]=(function(){const rpc={exports:{}};const send=(v)=>emit({kind:'module',module:").Append(J.Write(pair.Key)).Append(",payload:v});\n");
                if (pair.Key == "local_english_language") text.Append("globalThis.kdSimplified = true;\n");
                text.Append(pair.Value).Append("\nreturn rpc.exports;})();\n");
            }
            text.Append("rpc.exports={init(stage,p){");
            if (localIdentity) text.Append("modules.audit_reuse_ticket.provide('KartDriftLocalOfflineIdentity00001');");
            text.Append("emit({kind:'ready',pid:Process.id});},getFridaTerminalMode(){return 'cooked';},onFridaStdin(line){let q;try{q=JSON.parse(line);const value=modules[q.module][q.method](...(q.args||[]));emit({kind:'reply',id:q.id,ok:true,value:value===undefined?null:value});}catch(e){emit({kind:'reply',id:q?.id,ok:false,error:String(e)});}}};\n"); return text.ToString();
        }
        public NativeSession(int pid, string runtime, string cache, Action<string> log, Dictionary<string, string> testSources = null)
        {
            Directory.CreateDirectory(cache); string script = Path.Combine(cache, "local-session-" + pid + ".js");
            var sources = testSources ?? Modules.ToDictionary(name => name, name => Bundle.Text("tools/" + name + ".js")); File.WriteAllText(script, Agent(sources, testSources == null), new UTF8Encoding(false));
            var info = new ProcessStartInfo(runtime, "-p " + pid + " -s " + Quote(script) + " -i") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cache, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            info.EnvironmentVariables["TEMP"] = info.EnvironmentVariables["TMP"] = cache;
            helper = new Process { StartInfo = info, EnableRaisingEvents = true };
            helper.OutputDataReceived += delegate(object sender, DataReceivedEventArgs ev) {
                if (ev.Data == null) return;
                try
                {
                    var value = J.D(J.Read(ev.Data)); string kind = J.S(J.Get(value, "kind", ""));
                    lock (sync) { if (kind == "ready") ready = J.I(value["pid"]) == pid; else if (kind == "reply") replies[J.I(value["id"])] = value; else if (J.Same(J.Get(value, "type"), "error")) failure = "Native script initialization failed"; Monitor.PulseAll(sync); }
                    if (kind == "module") log(J.Write(value));
                }
                catch (Exception) { log("Native runtime produced a non-JSON line"); }
            };
            helper.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs ev) { if (!String.IsNullOrWhiteSpace(ev.Data)) log("native: " + ev.Data); };
            helper.Exited += delegate { lock (sync) { exited = true; Monitor.PulseAll(sync); } };
            try
            {
                helper.Start(); helper.BeginOutputReadLine(); helper.BeginErrorReadLine();
                var watch = Stopwatch.StartNew(); lock (sync) { while (!ready && failure == null && !exited && watch.ElapsedMilliseconds < 15000) Monitor.Wait(sync, 250); J.Need(ready, failure ?? "Native compatibility runtime did not initialize"); }
            }
            catch { Dispose(); throw; }
        }
        public object Call(string module, string method, params object[] args)
        {
            lock (sync)
            {
                long id = ++serial; helper.StandardInput.WriteLine(J.Write(J.M("id", id, "module", module, "method", method, "args", args))); helper.StandardInput.Flush();
                var watch = Stopwatch.StartNew(); while (!replies.ContainsKey(id) && !exited && watch.ElapsedMilliseconds < 10000) Monitor.Wait(sync, 250);
                J.Need(replies.ContainsKey(id), "Native runtime call timed out"); var result = replies[id]; replies.Remove(id); J.Need(J.B(result["ok"]), J.S(J.Get(result, "error", "Native runtime call failed"))); return J.Get(result, "value");
            }
        }
        public static string Quote(string value)
        {
            // Windows CommandLineToArgvW quoting, including backslashes before quotes/end.
            var result = new StringBuilder("\""); int slash = 0; foreach (char c in value) { if (c == '\\') { slash++; continue; } if (c == '"') result.Append('\\', slash * 2 + 1).Append(c); else result.Append('\\', slash).Append(c); slash = 0; } return result.Append('\\', slash * 2).Append('"').ToString();
        }
        public void Dispose()
        { if (helper == null) return; try { if (!helper.HasExited) { helper.Kill(); helper.WaitForExit(5000); } } catch (InvalidOperationException) { } finally { helper.Dispose(); } }
    }
}
