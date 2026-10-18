using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KartDriftLocal
{
    static class GameLanguage
    {
        internal static readonly string[] Codes = { "zh-Hans", "en-US", "zh-hant", "ko-kr" };
        internal static readonly string[] Names = { "简体中文", "English", "繁體中文", "한국어" };
        internal static int Index(string culture)
        {
            string[] native = { "ko-kr", "zh-hant", "en-US", "zh-Hans" };
            int index = Array.FindIndex(native, v => v.Equals(culture, StringComparison.OrdinalIgnoreCase));
            J.Need(index >= 0, "Unsupported game language"); return index;
        }
        internal static string Option(string value, string culture)
        {
            Index(culture);
            var pattern = new Regex(@"(^|;)Culture=[^;]*");
            return pattern.IsMatch(value) ? pattern.Replace(value, m => m.Groups[1].Value + "Culture=" + culture)
                : value.TrimEnd(';') + ";Culture=" + culture + ";";
        }
        internal static string SettingsText(string content, string culture)
        {
            const string section = "[/Script/KartDrift.KartGameUserSettings]";
            var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
            int start = lines.FindIndex(v => v.Trim().Equals(section, StringComparison.OrdinalIgnoreCase));
            if (start < 0) { lines.Add(section); start = lines.Count - 1; }
            int end = start + 1; while (end < lines.Count && !lines[end].TrimStart().StartsWith("[")) end++;
            for (int i = end - 1; i > start; i--) if (Regex.IsMatch(lines[i], @"^\s*CultureIndex\s*=", RegexOptions.IgnoreCase)) lines.RemoveAt(i);
            lines.Insert(start + 1, "CultureIndex=" + Index(culture));
            return String.Join("\r\n", lines);
        }
        internal static void Configure(string path, string culture)
        {
            string content = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, SettingsText(content, culture), new UTF8Encoding(true));
        }
        internal static string Resources()
        {
            string root = Path.Combine(Launcher.Root, "Localization", "Game");
            var hashes = J.Map(J.D(J.Read(Bundle.Text("release/manifest.json"))), "localizationSha256");
            foreach (var pair in hashes) J.Need(Launcher.Hash(Path.Combine(root, pair.Key)) == J.S(pair.Value), "Language resource verification failed. Extract the complete package.");
            return root;
        }
        internal static string LocalizationText(string content, string game, string resources)
        {
            // Saved config uses repeated plain keys. Keep unrelated sections and settings.
            bool target = false;
            var lines = content.Replace("\r\n", "\n").Split('\n').Where(line => {
                string text = line.Trim();
                if (text.StartsWith("[") && text.EndsWith("]")) target = text.Equals("[Internationalization]", StringComparison.OrdinalIgnoreCase);
                return !(target && Regex.IsMatch(text, @"^[+!.-]?LocalizationPaths\s*=", RegexOptions.IgnoreCase));
            }).ToList();
            lines.Add("[Internationalization]");
            lines.Add("LocalizationPaths=" + Path.GetFullPath(game).Replace('\\', '/'));
            lines.Add("LocalizationPaths=" + Path.GetFullPath(resources).Replace('\\', '/'));
            return String.Join("\r\n", lines) + "\r\n";
        }
    }
    sealed partial class ProfileStore
    {
        internal void SetGameLanguage(string culture)
        {
            GameLanguage.Index(culture);
            using (var db = Open()) Transaction(db, delegate {
                var state = Snapshot(db); var options = J.Map(J.Map(state, "optionSetting"), "gameOptions");
                options["DriveAssistOption"] = GameLanguage.Option(J.S(J.Get(options, "DriveAssistOption", "")), culture);
                Save(db, state);
            });
        }
    }
}
