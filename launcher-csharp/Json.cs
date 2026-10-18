using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace KartDriftLocal
{
    static class J
    {
        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 128 * 1024 * 1024, RecursionLimit = 200 }; }
        public static object Read(string text) { return Normalize(Serializer().DeserializeObject(text)); }
        static object Normalize(object value)
        {
            var map = value as Dictionary<string, object>;
            if (map != null) return map.ToDictionary(p => p.Key, p => Normalize(p.Value));
            var array = value as object[];
            return array == null ? value : array.Select(Normalize).ToList();
        }
        public static string Write(object value) { return Serializer().Serialize(value); }
        public static object Copy(object value) { return Read(Write(value)); }
        public static Dictionary<string, object> D(object value) { var d = value as Dictionary<string, object>; if (d == null) throw new ArgumentException("Expected object"); return d; }
        public static List<object> A(object value) { var a = value as List<object>; if (a == null) throw new ArgumentException("Expected array"); return a; }
        public static Dictionary<string, object> M(params object[] pairs)
        { var result = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) result[(string)pairs[i]] = pairs[i + 1]; return result; }
        public static List<object> L(params object[] values) { return values.ToList(); }
        public static object Get(Dictionary<string, object> d, string key, object fallback = null) { object v; return d.TryGetValue(key, out v) ? v : fallback; }
        public static Dictionary<string, object> Map(Dictionary<string, object> d, string key) { return D(d[key]); }
        public static List<object> List(Dictionary<string, object> d, string key) { return A(d[key]); }
        public static Dictionary<string, object> EnsureMap(Dictionary<string, object> d, string key) { if (!d.ContainsKey(key)) d[key] = M(); return D(d[key]); }
        public static List<object> EnsureList(Dictionary<string, object> d, string key) { if (!d.ContainsKey(key)) d[key] = L(); return A(d[key]); }
        public static string S(object value) { if (!(value is string)) throw new ArgumentException("Expected string"); return (string)value; }
        public static bool IsInt(object value) { return value is int || value is long; }
        public static long I(object value) { if (!IsInt(value)) throw new ArgumentException("Expected integer"); return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        public static double N(object value) { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        public static bool B(object value) { if (!(value is bool)) throw new ArgumentException("Expected boolean"); return (bool)value; }
        public static bool Truth(object value)
        { if (value == null) return false; if (value is string) return ((string)value).Length != 0; if (value is bool) return (bool)value; if (value is ICollection) return ((ICollection)value).Count != 0; return N(value) != 0; }
        public static bool Same(object a, object b)
        {
            var x = a as Dictionary<string, object>; var y = b as Dictionary<string, object>;
            if (x != null && y != null) return x.Count == y.Count && x.All(p => y.ContainsKey(p.Key) && Same(p.Value, y[p.Key]));
            var l = a as List<object>; var r = b as List<object>;
            if (l != null && r != null) return l.Count == r.Count && !l.Where((v, i) => !Same(v, r[i])).Any();
            if (a == null || b == null) return a == b;
            if (IsInt(a) && IsInt(b)) return I(a) == I(b);
            return a.Equals(b);
        }
        public static void Put(Dictionary<string, object> target, Dictionary<string, object> values) { foreach (var p in values) target[p.Key] = p.Value; }
        public static string Now() { return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture); }
        public static long Millis() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }
        public static void Need(bool valid, string message) { if (!valid) throw new ArgumentException(message); }
        public static bool NumericId(object value) { return value is string && ((string)value).Length > 0 && ((string)value).All(c => c >= '0' && c <= '9'); }
    }

    static class Bundle
    {
        static readonly Dictionary<string, string> Files = Load();
        static Dictionary<string, string> Load()
        {
            var files = new Dictionary<string, string>();
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LocalData.zip"))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                foreach (var entry in zip.Entries)
                    using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) files[entry.FullName] = reader.ReadToEnd();
            return files;
        }
        public static string Text(string name) { return Files[name]; }
        public static Dictionary<string, object> Data(string name) { return J.D(J.Read(Text("data/" + name + ".json"))); }
        public static Dictionary<string, object> Templates() { return J.D(J.Read(Text("release/responses.json"))); }
    }
}
