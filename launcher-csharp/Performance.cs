using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KartDriftLocal
{
    sealed class PerformanceUnavailable : ArgumentException { public PerformanceUnavailable(string text) : base(text) { } }
    sealed class Performance
    {
        public const string Shared = "KartDriftLocalPerformanceV1";
        public readonly Dictionary<string, object> Vehicles = J.Map(Bundle.Data("local-speed-bodies"), "vehicles");
        readonly List<object> definitions = J.List(J.Map(Bundle.Data("local-gear-effects"), "Normal"), "kartGearDefVector");
        readonly List<object> layout = J.A(J.Read(Bundle.Text("data/local-body-layout.json")));
        readonly Dictionary<string, object> balloons = J.Map(Bundle.Data("local-balloon-rules"), "definition");
        readonly Dictionary<string, object> balloonEffects = J.Map(J.Map(Bundle.Data("local-balloon-effects"), "definition"), "effect");
        readonly string account;
        public Performance(string identity) { account = identity; }
        static readonly Dictionary<string, string> Fields = new Dictionary<string, string> {
            {"transAccelFactor","TransAccelFactor"},{"driftEscapeForce","DriftEscapeForce"},
            {"normalBoosterTime","NormalBoosterTime"},{"teamBoosterTime","TeamBoosterTime"},
            {"driftMaxGauge","DriftMaxGauge"},{"forceSlipEscapeFactor","ForceSlipEscapeFactor"},
            {"driftGaugePreservePercent","DriftGaugePreservePercent"},{"startForwardAccelFactorSpeed","StartForwardAccelFactorSpeed"},
            {"startBoosterTimeSpeed","StartBoosterTimeSpeed"},{"draftMulAccelFactor","DraftMulAccelFactor"},
            {"characterBonus","bCharacterBonus"},{"autoUseBoost","bAutoUseBoost"},
            {"driftBoostMulAccelFactorSpeed","DriftBoostMulAccelFactorSpeed"},{"driftBoostTickSpeed","DriftBoostTickSpeed"}};
        static bool BoolField(string key) { return key == "bCharacterBonus" || key == "bAutoUseBoost"; }
        public static byte[] Hex(string text) { var b = new byte[text.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(text.Substring(i * 2, 2), 16); return b; }
        Dictionary<string, object> Effects(Dictionary<string, object> settings)
        {
            var known = new HashSet<string>(definitions.Select(d => J.S(J.D(d)["item"])));
            J.Need(settings.Keys.All(known.Contains), "Unknown gear name"); var result = J.M();
            foreach (var value in definitions)
            {
                var d = J.D(value); J.Need(!J.Truth(J.Get(d, "engineParamVector")), "Engine-specific table requires explicit selection");
                long level = J.I(J.Get(settings, J.S(d["item"]), 0)); J.Need(level >= 0, "Invalid gear level"); if (level == 0) continue;
                var rows = J.List(d, "defaultParamVector").Select(J.D).Where(r => J.I(r["gear"]) == level).ToList();
                J.Need(rows.Count == 1, "Gear level absent from exact-match table");
                if (J.S(d["kartType"]) == "EKtKartType::Item") continue;
                foreach (var p in Fields)
                {
                    object delta = J.Map(rows[0], "param")[p.Key]; if (!J.Truth(delta)) continue;
                    J.Need(!result.ContainsKey(p.Value), "Overlapping effects require native operation ordering"); result[p.Value] = delta;
                }
            }
            return result;
        }
        public IEnumerable<KeyValuePair<string, Dictionary<string, object>>> BalloonEffects(Dictionary<string, object> state)
        {
            string item = J.Get(J.Map(J.Map(state, "itemPresetOn"), "itemMap"), "Balloon") as string;
            var rules = J.Map(balloons, "balloonDef"); if (item == null || !rules.ContainsKey(item)) yield break;
            int grade = (int)J.I(J.Get(J.D(J.Get(J.D(J.Get(state, "balloon", J.M())), item, J.M())), "grade", 0));
            var seen = new HashSet<string>();
            foreach (var tier in J.List(J.D(rules[item]), "grade").Take(grade + 1))
                foreach (var nameValue in J.List(J.D(tier), "effect"))
                {
                    string name = J.S(nameValue); var effect = J.D(balloonEffects[name]);
                    if (J.S(effect["raceType"]).ToLowerInvariant() == "speed" && seen.Add(name))
                        yield return new KeyValuePair<string, Dictionary<string, object>>(name, J.D(J.List(effect, "grade")[grade]));
                }
        }
        public Dictionary<string, object> BalloonNotice(Dictionary<string, object> state)
        {
            var param = J.M(); var names = J.L();
            foreach (var pair in BalloonEffects(state))
            { names.Add(pair.Key); foreach (var p in J.Map(pair.Value, "param")) param[p.Key] = J.N(J.Get(param, p.Key, 0)) + J.N(p.Value); }
            return J.M("_type", "PcBalloonEffectUpdated", "balloonEffect", J.M("param", param, "effect", names));
        }
        void Set(byte[] raw, string name, object before, object after)
        {
            int offset = (int)J.I(J.D(layout.Single(v => J.S(J.D(v)["name"]) == name))["offset"]);
            if (BoolField(name)) raw[offset] = (byte)(J.Truth(after) ? 1 : 0);
            else if (name == "DriftBoostTickSpeed") Buffer.BlockCopy(BitConverter.GetBytes(unchecked(BitConverter.ToUInt32(raw, offset) - (uint)J.I(before) + (uint)J.I(after))), 0, raw, offset, 4);
            else { float value = BitConverter.ToSingle(raw, offset); float rebased = (float)((float)(value - J.N(before)) + J.N(after)); Buffer.BlockCopy(BitConverter.GetBytes(rebased), 0, raw, offset, 4); }
        }
        public byte[] Body(string kart, Dictionary<string, object> upgrade)
        {
            if (!Vehicles.ContainsKey(kart)) throw new PerformanceUnavailable("该车型尚无本地竞速性能数据");
            var source = J.D(Vehicles[kart]);
            if (J.I(upgrade["engineType"]) != J.I(J.Map(source, "upgrade")["engineType"])) throw new PerformanceUnavailable("该车型的目标引擎尚无本地性能依据");
            var old = Effects(J.Map(J.Map(J.Map(source, "upgrade"), "gearInfo"), "gearSettings"));
            var next = Effects(J.Map(J.Map(upgrade, "gearInfo"), "gearSettings")); byte[] raw = Hex(J.S(source["rawBodyHex"]));
            foreach (string key in old.Keys.Union(next.Keys)) { object a = J.Get(old, key, 0), b = J.Get(next, key, 0); if (J.N(a) != J.N(b)) Set(raw, key, a, b); }
            return raw;
        }
        public Dictionary<string, object> Payload(Dictionary<string, object> state, string kart = null)
        {
            kart = kart ?? J.S(J.Map(J.Map(state, "itemPresetOn"), "itemMap")["Kart"]);
            var upgrades = J.Map(state, "ownKartsUpgradeInfo"); if (!upgrades.ContainsKey(kart)) throw new PerformanceUnavailable("该车型尚无本地玩家配置");
            byte[] raw = Body(kart, J.D(upgrades[kart]));
            foreach (var effect in BalloonEffects(state)) foreach (var p in J.Map(effect.Value, "bodyParam")) if (J.Truth(p.Value)) Set(raw, Char.ToUpperInvariant(p.Key[0]) + p.Key.Substring(1), 0, p.Value);
            var packed = new List<byte>(); foreach (var field in layout.Select(J.D)) packed.AddRange(raw.Skip((int)J.I(field["offset"])).Take((int)J.I(field["size"])));
            byte[] key, iv; using (var sha = SHA256.Create()) { key = sha.ComputeHash(Encoding.UTF8.GetBytes(account + Shared + "_key")); iv = sha.ComputeHash(Encoding.UTF8.GetBytes(account + Shared + "_iv")).Take(16).ToArray(); }
            return J.M("speedBodyParamStr", Convert.ToBase64String(Codec.Crypt(packed.ToArray(), key, iv, true)), "itemBodyParamStr", "");
        }
        static readonly HashSet<string> PushRequests = new HashSet<string>(new[] {"PqLoginComplete","PqItemPresetOnUpdate","PqItemPresetLoad","PqKartGearSet","PqKartGearReset","PqKartUpgrade","PqBalloonUpgrade"});
        public void Apply(Dictionary<string, object> state, Dictionary<string, object> request, Dictionary<string, object> response)
        {
            string t = J.S(request["_type"]); if (t == "PqLogin") response["sharedValue"] = Shared;
            if (t == "PqKartGearSet" || t == "PqKartGearReset" || t == "PqKartUpgrade") Payload(state, J.S(request["kartItemId"]));
            if (PushRequests.Contains(t)) { var push = Payload(state); push["_type"] = "PcKartBodyParamUpdated"; response["_localPerformancePush"] = push; }
            else if (t == "PqApplySpeedBodyParamForSingleRace") { J.Put(response, Payload(state)); response["_type"] = "PrApplySpeedBodyParamForSingleRace"; response["result"] = J.M("code", 0); response["desc"] = ""; }
            else if (t == "PqTimeAttackStart") Payload(state);
        }
    }
    static class Codec
    {
        static readonly byte[] Key = Performance.Hex("b0acd584d3b3c28447768cbbff48f6f3bcd92c3b8aa44f81b36e81e03cfef22a");
        public static byte[] Crypt(byte[] data, byte[] key, byte[] iv, bool encrypt)
        { using (var aes = Aes.Create()) { aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; using (var x = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor()) return x.TransformFinalBlock(data, 0, data.Length); } }
        public static string Encode(string text) { byte[] iv = new byte[16]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(iv); return Convert.ToBase64String(iv.Concat(Crypt(Encoding.UTF8.GetBytes(text), Key, iv, true)).ToArray()); }
        public static string Decode(string text) { byte[] raw = Convert.FromBase64String(text); return Encoding.UTF8.GetString(Crypt(raw.Skip(16).ToArray(), Key, raw.Take(16).ToArray(), false)); }
    }
}
