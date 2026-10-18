using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace KartDriftLocal
{
    sealed class Database : IDisposable
    {
        IntPtr handle;
        const string Lib = "winsqlite3.dll";
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] p, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_text(IntPtr stmt, int i, byte[] text, int n, IntPtr destructor);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_blob(IntPtr stmt, int i, byte[] data, int n, IntPtr destructor);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_int64(IntPtr stmt, int i, long value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_null(IntPtr stmt, int i);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_type(IntPtr stmt, int i);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int i);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(IntPtr stmt, int i);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern long sqlite3_column_int64(IntPtr stmt, int i);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_backup_init(IntPtr dst, byte[] dn, IntPtr src, byte[] sn);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_backup_step(IntPtr backup, int pages);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_backup_finish(IntPtr backup);
        static byte[] Z(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }
        void Check(int code) { if (code != 0) throw new InvalidOperationException("SQLite error " + code + ": " + ReadText(sqlite3_errmsg(handle))); }
        static string ReadText(IntPtr p)
        { if (p == IntPtr.Zero) return ""; int n = 0; while (Marshal.ReadByte(p, n) != 0) n++; var b = new byte[n]; Marshal.Copy(p, b, 0, n); return Encoding.UTF8.GetString(b); }
        public Database(string path, bool readOnly = false)
        { if (!readOnly) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))); Check(sqlite3_open_v2(Z(path), out handle, readOnly ? 1 : 6, IntPtr.Zero)); Check(sqlite3_busy_timeout(handle, 10000)); }
        public List<object[]> Query(string sql, params object[] values)
        {
            IntPtr stmt; Check(sqlite3_prepare_v2(handle, Z(sql), -1, out stmt, IntPtr.Zero));
            try
            {
                for (int i = 0; i < values.Length; i++)
                {
                    object v = values[i]; int code;
                    if (v == null) code = sqlite3_bind_null(stmt, i + 1);
                    else if (v is byte[]) code = sqlite3_bind_blob(stmt, i + 1, ((byte[])v).Length == 0 ? new byte[1] : (byte[])v, ((byte[])v).Length, new IntPtr(-1));
                    else if (J.IsInt(v)) code = sqlite3_bind_int64(stmt, i + 1, J.I(v));
                    else { byte[] text = Z((string)v); code = sqlite3_bind_text(stmt, i + 1, text, text.Length - 1, new IntPtr(-1)); }
                    Check(code);
                }
                var rows = new List<object[]>(); int step;
                while ((step = sqlite3_step(stmt)) == 100)
                {
                    var row = new object[sqlite3_column_count(stmt)];
                    for (int i = 0; i < row.Length; i++)
                    {
                        int type = sqlite3_column_type(stmt, i);
                        if (type == 5) row[i] = null;
                        else if (type == 1) row[i] = sqlite3_column_int64(stmt, i);
                        else if (type == 4) { var b = new byte[sqlite3_column_bytes(stmt, i)]; if (b.Length > 0) Marshal.Copy(sqlite3_column_blob(stmt, i), b, 0, b.Length); row[i] = b; }
                        else row[i] = ReadText(sqlite3_column_text(stmt, i));
                    }
                    rows.Add(row);
                }
                if (step != 101) Check(step);
                return rows;
            }
            finally { sqlite3_finalize(stmt); }
        }
        public void Dispose() { if (handle != IntPtr.Zero) { sqlite3_close(handle); handle = IntPtr.Zero; } }
        public static void Backup(string source, string destination)
        {
            using (var src = new Database(source, true)) using (var dst = new Database(destination))
            {
                IntPtr copy = sqlite3_backup_init(dst.handle, Z("main"), src.handle, Z("main"));
                if (copy == IntPtr.Zero) throw new InvalidOperationException("Cannot create save backup");
                int step = sqlite3_backup_step(copy, -1); int end = sqlite3_backup_finish(copy);
                J.Need(step == 101 && end == 0 && (string)dst.Query("PRAGMA integrity_check")[0][0] == "ok", "Save backup integrity failed");
            }
        }
    }
}
