using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KartDriftLocal
{
    // This adapter loads only into the process created by GameProcess.Start.
    // It never opens an arbitrary PID or modifies the installed game/SDK files.
    sealed class NativeAdapter : IDisposable
    {
        readonly MemoryMappedFile mapping;
        readonly MemoryMappedViewAccessor view;
        readonly string channel = "Local\\KartDriftLocal-" + Guid.NewGuid().ToString("N");
        readonly string library;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint free);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stack, IntPtr entry, IntPtr parameter, uint flags, out uint thread);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeThread(IntPtr thread, out uint code);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        public NativeAdapter(string library, string expectedHash)
        {
            this.library = Path.GetFullPath(library);
            J.Need(File.Exists(this.library) && Launcher.Hash(this.library) == expectedHash, "原生兼容模块缺失或校验失败。");
            mapping = MemoryMappedFile.CreateNew(channel, 4096, MemoryMappedFileAccess.ReadWrite);
            view = mapping.CreateViewAccessor(); view.Write(0, 0x4b444f4e); view.Write(4, 1);
        }
        public void Configure(ProcessStartInfo info) { info.EnvironmentVariables["KD_LOCAL_NATIVE_CHANNEL"] = channel; }
        public void AttachBeforeResume(Process process)
        {
            view.Write(8, process.Id); Thread.MemoryBarrier();
            byte[] path = Encoding.Unicode.GetBytes(library + "\0");
            IntPtr memory = VirtualAllocEx(process.Handle, IntPtr.Zero, (UIntPtr)path.Length, 0x3000, 4);
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr thread = IntPtr.Zero; bool completed = false;
            try
            {
                UIntPtr written;
                if (!WriteProcessMemory(process.Handle, memory, path, (UIntPtr)path.Length, out written) || written.ToUInt64() != (ulong)path.Length) throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr load = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
                J.Need(load != IntPtr.Zero, "无法解析系统模块加载入口。");
                uint id; thread = CreateRemoteThread(process.Handle, IntPtr.Zero, UIntPtr.Zero, load, memory, 0, out id);
                if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                J.Need(WaitForSingleObject(thread, 15000) == 0, "原生兼容模块加载超时。"); completed = true;
                uint code;
                J.Need(GetExitCodeThread(thread, out code) && code != 0 && code != 0xc0000005, "原生兼容模块加载失败。");
                var timer = Stopwatch.StartNew();
                while (view.ReadInt32(12) == 0 && timer.ElapsedMilliseconds < 15000)
                { ThrowIfFailed(); J.Need(!process.HasExited, "测试进程在兼容模块就绪前退出。"); Thread.Sleep(5); }
                ThrowIfFailed(); J.Need(view.ReadInt32(12) == 1, "原生兼容模块初始化超时。");
            }
            finally
            {
                // A timed-out thread might still be reading the remote path;
                // GameProcess will terminate its own child on this failure.
                if (completed || thread == IntPtr.Zero) VirtualFreeEx(process.Handle, memory, UIntPtr.Zero, 0x8000);
                if (thread != IntPtr.Zero) CloseHandle(thread);
            }
        }
        public void ThrowIfFailed()
        {
            int error = view.ReadInt32(20);
            J.Need(error == 0, "原生兼容检查失败，错误代码 " + error + "。请查看日志。");
        }
        public void Settle()
        {
            view.Write(24, 1); var timer = Stopwatch.StartNew();
            while ((view.ReadInt32(16) & 63) != 63 && timer.ElapsedMilliseconds < 15000) { ThrowIfFailed(); Thread.Sleep(10); }
            ThrowIfFailed();
            J.Need((view.ReadInt32(16) & 63) == 63 && view.ReadInt32(32) > 0 && view.ReadInt32(36) > 0,
                "游戏登录后必要的原生适配尚未全部就绪。");
        }
        public object Status()
        {
            return J.M("ready", view.ReadInt32(12), "flags", view.ReadInt32(16), "error", view.ReadInt32(20),
                "sdkInitializations", view.ReadInt32(28), "tickets", view.ReadInt32(32), "logins", view.ReadInt32(36),
                "restoredPresets", view.ReadInt32(40), "expandedLanguages", view.ReadInt32(44),
                "polyglotChanges", view.ReadInt32(48), "blockedNotices", view.ReadInt32(52));
        }
        public void Dispose() { view.Dispose(); mapping.Dispose(); }
    }
}
