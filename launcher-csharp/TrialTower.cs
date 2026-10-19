using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace KartDriftLocal
{
    static class TrialTower
    {
        static readonly Dictionary<string, object> Files = J.Map(Bundle.Data("local-trial-tower"), "files");
        public static int Read(string name, out byte[] content)
        {
            content = null; object definition;
            if (!Files.TryGetValue(name, out definition)) return 404;
            string path = Path.Combine(Launcher.Root, "TrialTower", name);
            if (!File.Exists(path)) return 404;
            var expected = J.D(definition); byte[] data = File.ReadAllBytes(path);
            if (data.Length != J.I(expected["bytes"]) || data.Length < 4 || data[0] != 71 || data[1] != 86 || data[2] != 65 || data[3] != 83) return 409;
            using (var sha = SHA256.Create())
                if (BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant() != J.S(expected["sha256"])) return 409;
            content = data; return 200;
        }
        public static Dictionary<string, object> SignedUrls(List<object> identities)
        {
            var urls = J.M(); foreach (string name in identities.Cast<string>())
            { byte[] content; if (Read(name, out content) == 200) urls[name] = ProfileStore.BaseUrl + "/trialtower/" + name; }
            return urls;
        }
    }
}
