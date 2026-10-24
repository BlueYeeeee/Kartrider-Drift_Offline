using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KartDriftLocal
{
    static class GameProcess
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        { public int size; public string reserved, desktop, title; public int x, y, width, height, charsX, charsY, fill, flags; public short show, reservedSize; public IntPtr reservedData, input, output, error; }
        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInfo { public IntPtr process, thread; public int pid, tid; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr handle, uint code);
        public static string Quote(string value)
        {
            // Windows CommandLineToArgvW quoting, including backslashes before quotes/end.
            var result = new StringBuilder("\""); int slash = 0; foreach (char c in value) { if (c == '\\') { slash++; continue; } if (c == '"') result.Append('\\', slash * 2 + 1).Append(c); else result.Append('\\', slash).Append(c); slash = 0; } return result.Append('\\', slash * 2).Append('"').ToString();
        }
        public static Process Start(ProcessStartInfo info, Action<Process> beforeResume = null)
        {
            // Match the proven Python launch exactly: lpApplicationName selects
            // the image; the independent command line starts with KartDrift.
            string block = String.Join("\0", info.EnvironmentVariables.Keys.Cast<string>().OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Select(k => k + "=" + info.EnvironmentVariables[k])) + "\0\0";
            IntPtr environment = Marshal.StringToHGlobalUni(block); var startup = new StartupInfo(); startup.size = Marshal.SizeOf(typeof(StartupInfo)); var native = new ProcessInfo();
            Process result = null;
            try
            {
                uint flags = 0x400 | 0x4; if (info.CreateNoWindow) flags |= 0x8000000;
                if (!CreateProcessW(info.FileName, new StringBuilder("KartDrift " + info.Arguments), IntPtr.Zero, IntPtr.Zero, false, flags, environment, info.WorkingDirectory, ref startup, out native)) throw new Win32Exception(Marshal.GetLastWin32Error());
                result = Process.GetProcessById(native.pid); var retainedHandle = result.Handle;
                if (beforeResume != null) beforeResume(result);
                if (ResumeThread(native.thread) == UInt32.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error()); return result;
            }
            catch { if (native.process != IntPtr.Zero) TerminateProcess(native.process, 1); if (result != null) result.Dispose(); throw; }
            finally { if (native.thread != IntPtr.Zero) CloseHandle(native.thread); if (native.process != IntPtr.Zero) CloseHandle(native.process); Marshal.FreeHGlobal(environment); }
        }
    }
}
