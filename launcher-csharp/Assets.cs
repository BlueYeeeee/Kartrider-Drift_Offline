using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KartDriftLocal
{
    sealed partial class ProfileStore
    {
        public const string BaseUrl = "http://127.0.0.1:59874";
        public const int MaxAssetBytes = 64 * 1024 * 1024;
        internal static readonly HashSet<string> AssetRequests = new HashSet<string>("PqLiveryUpdate PqLiveryDBUpdate PqLiveryDelete PqLiveryStickerUpdate PqLiveryStickerDelete PqSignedUrl PqReplayUpdate PqUserReplayDBUpdate PqReplayList PqUserReplaySignedUrl PqReplaySignedUrl PqReplayDelete".Split(' '));
        static Dictionary<string, object> EmptyLivery() { return J.M("liveryId", "", "liveryUniqueId", "", "liveryRevision", 0, "kartId", "", "createTime", 0, "expireTime", 0, "state", 0); }
        public static string Url(string key) { return BaseUrl + "/local-assets/" + key; }
        static string Reserve(Database db, string kind, object metadata) { string key = Guid.NewGuid().ToString("N"); db.Query("INSERT INTO assets VALUES (?, ?, ?, NULL)", key, kind, J.Write(metadata)); return key; }
        public static Dictionary<string, object> Link(string key) { return J.M("url", Url(key), "verb", "PUT", "headers", J.M(), "body", "", "valueIndex", J.L(), "metadata", ""); }
        static Dictionary<string, object> NativeLink(string key, object metadata) { var link = Link(key); link["body"] = J.Write(J.M("Request", J.M("key", key))); link["valueIndex"] = J.L("Request", "value"); link["metadata"] = J.Write(metadata); return link; }
        static Dictionary<string, object> Uploaded(Database db, string key, string kind)
        { var rows = db.Query("SELECT kind, metadata, length(data)>0 FROM assets WHERE key=?", key); J.Need(rows.Count == 1 && (string)rows[0][0] == kind && rows[0][2] != null && (long)rows[0][2] != 0, "Binary upload is not complete"); return J.D(J.Read((string)rows[0][1])); }
        public static byte[] Download(string key, string kind, byte[] content)
        {
            if (kind != "sticker" && kind != "livery") return content;
            var value = J.D(J.Read(new UTF8Encoding(false, true).GetString(content))); var request = OptionalMap(value, "Request");
            if (J.Get(request, "value") is Dictionary<string, object>) return content;
            if (J.Get(value, "data") is Dictionary<string, object>) return Encoding.UTF8.GetBytes(J.Write(J.M("Request", J.M("key", key, "value", value["data"]))));
            throw new ArgumentException("Missing native Request.value object");
        }
        public int ReadAsset(string key, out byte[] content)
        {
            content = null; using (var db = Open())
            { var rows = db.Query("SELECT kind,data FROM assets WHERE key=?", key); if (rows.Count == 0 || rows[0][1] == null || ((byte[])rows[0][1]).Length == 0) return 404; try { content = Download(key, (string)rows[0][0], (byte[])rows[0][1]); return 200; } catch (ArgumentException) { return 409; } catch (InvalidOperationException) { return 409; } }
        }
        public int PutAsset(string key, byte[] content)
        {
            if (content.Length == 0) return 400; if (content.Length > MaxAssetBytes) return 413; int status = 200;
            using (var db = Open()) Transaction(db, delegate {
                var rows = db.Query("SELECT data,kind,metadata FROM assets WHERE key=?", key); if (rows.Count == 0) { status = 404; return; } var row = rows[0];
                if (row[0] != null && ((byte[])row[0]).Length > 0 && !((byte[])row[0]).SequenceEqual(content)) { status = 409; return; }
                string kind = (string)row[1];
                if (kind == "sticker" || kind == "livery")
                {
                    try { var envelope = J.D(J.Read(new UTF8Encoding(false, true).GetString(content))); var request = OptionalMap(envelope, "Request"); if (!J.Same(J.Get(request, "key"), key) || !(J.Get(request, "value") is Dictionary<string, object>)) { status = 400; return; } }
                    catch (ArgumentException) { status = 400; return; } catch (InvalidOperationException) { status = 400; return; }
                }
                if (kind == "sticker") { var state = Snapshot(db); var value = J.Map(J.D(J.Read((string)row[2])), "sticker"); var entries = J.List(state, "liveryStickerVector"); entries.RemoveAll(v => J.Same(J.D(v)["liveryStickerId"], value["liveryStickerId"])); entries.Add(value); Save(db, state); }
                db.Query("UPDATE assets SET data=? WHERE key=?", content, key);
            }); return status;
        }
        static void Walk(object node, Action<Dictionary<string, object>> update)
        { var map = node as Dictionary<string, object>; if (map != null) { update(map); foreach (var child in map.Values.ToList()) Walk(child, update); } var list = node as List<object>; if (list != null) foreach (var child in list) Walk(child, update); }
        static bool Readable(Database db, string key, string kind)
        { var rows = db.Query("SELECT kind,data FROM assets WHERE key=?", key); if (rows.Count == 0 || (string)rows[0][0] != kind || rows[0][1] == null || ((byte[])rows[0][1]).Length == 0) return false; try { Download(key, kind, (byte[])rows[0][1]); return true; } catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; } }
        void RepairAssets(Database db, Dictionary<string, object> state)
        {
            foreach (string kind in new[] { "livery", "sticker" })
            {
                string vector = kind == "livery" ? "liveryVector" : "liveryStickerVector", field = kind == "livery" ? "liveryId" : "liveryStickerId", keyField = kind == "livery" ? "liveryUniqueId" : "liveryStickerUniqueId", counter = kind == "livery" ? "nextLiveryId" : "nextStickerId";
                var entries = J.A(J.Get(state, vector, J.L())); long serial = Math.Max(1, J.I(J.Get(state, counter, 1))); foreach (var v in entries.Select(J.D)) if (J.NumericId(J.Get(v, field))) serial = Math.Max(serial, checked(Int64.Parse(J.S(v[field])) + 1));
                foreach (var value in entries.Select(J.D))
                {
                    if (!J.Same(J.Get(value, field), "0")) continue; string key = J.S(value[keyField]); if (!Readable(db, key, kind)) continue;
                    var metadata = J.D(J.Read((string)db.Query("SELECT metadata FROM assets WHERE key=?", key)[0][0])); string identity = serial.ToString();
                    Action<Dictionary<string, object>> replace = delegate(Dictionary<string, object> node) { if (J.Same(J.Get(node, keyField), key) && J.Same(J.Get(node, field), "0")) node[field] = identity; };
                    Walk(state, replace); Walk(metadata, replace); db.Query("UPDATE assets SET metadata=? WHERE key=?", J.Write(metadata), key);
                    if (kind == "livery") foreach (var pair in OptionalMap(state, "vehiclePresets"))
                    { if (!J.Same(J.Get(OptionalMap(J.D(pair.Value), "livery"), keyField), key)) continue; var cache = J.Map(J.Map(state, "customPresets"), "kartPreset"); string old = J.S(J.Get(cache, pair.Key, "")); if (old.StartsWith("0,", StringComparison.Ordinal)) cache[pair.Key] = identity + old.Substring(1); }
                    serial++; state[counter] = serial;
                }
            }
            foreach (var value in J.A(J.Get(state, "liveryVector", J.L())).Select(J.D))
            {
                if (!J.Same(J.Get(value, "liveryRevision"), 0)) continue; string key = J.S(value["liveryUniqueId"]); if (!Readable(db, key, "livery")) continue;
                var metadata = J.D(J.Read((string)db.Query("SELECT metadata FROM assets WHERE key=?", key)[0][0])); if (!J.Same(J.Get(metadata, "liveryUniqueId"), key) || !J.Same(J.Get(metadata, "liveryRevision"), 0)) continue;
                Action<Dictionary<string, object>> repair = delegate(Dictionary<string, object> node) { if (J.Same(J.Get(node, "liveryUniqueId"), key) && J.Same(J.Get(node, "liveryRevision"), 0)) node["liveryRevision"] = 1; };
                Walk(state, repair); Walk(metadata, repair); db.Query("UPDATE assets SET metadata=? WHERE key=?", J.Write(metadata), key);
            }
        }
        static IEnumerable<Dictionary<string, object>> Presets(Dictionary<string, object> state)
        { return new[] { J.Map(state, "itemPresetOn") }.Concat(J.List(state, "itemPresetSlotVector").Select(v => OptionalMap(J.D(v), "itemPreset"))).Concat(OptionalMap(state, "vehiclePresets").Values.Select(J.D)); }
        void ProjectAssets(Database db, Dictionary<string, object> response)
        {
            var valid = new HashSet<string>();
            foreach (string kind in new[] { "livery", "sticker" })
            {
                string vector = kind == "livery" ? "liveryVector" : "liveryStickerVector", urls = kind == "livery" ? "liverySignedUrlVector" : "liveryStickerSignedUrlVector", keyField = kind == "livery" ? "liveryUniqueId" : "liveryStickerUniqueId";
                var entries = J.A(J.Get(response, vector, J.L())).Where(v => Readable(db, J.S(J.D(v)[keyField]), kind)).ToList(); response[vector] = entries; response[urls] = entries.Select(v => { string key = J.S(J.D(v)[keyField]); valid.Add(key); return (object)Url(key); }).ToList();
            }
            response["itemPresetOn"] = J.Copy(J.Get(response, "itemPresetOn", J.M())); response["itemPresetSlotVector"] = J.Copy(J.Get(response, "itemPresetSlotVector", J.L()));
            foreach (var preset in new[] { J.Map(response, "itemPresetOn") }.Concat(J.List(response, "itemPresetSlotVector").Select(v => OptionalMap(J.D(v), "itemPreset"))))
            { string key = J.Get(OptionalMap(preset, "livery"), "liveryUniqueId") as string; if (!String.IsNullOrEmpty(key) && !valid.Contains(key)) preset["livery"] = EmptyLivery(); }
        }
        static void DeleteDesign(Database db, string kind, string field, object identity)
        { foreach (var row in db.Query("SELECT key,metadata FROM assets WHERE kind=?", kind)) { var metadata = J.D(J.Read((string)row[1])); if (kind == "sticker") metadata = OptionalMap(metadata, "sticker"); if (J.Same(J.Get(metadata, field), identity)) db.Query("DELETE FROM assets WHERE key=?", (string)row[0]); } }
        static long NextId(Dictionary<string, object> state, string counter, IEnumerable<object> identities)
        { long serial = J.I(J.Get(state, counter, 1)); foreach (var id in identities) if (J.NumericId(id)) serial = Math.Max(serial, checked(Int64.Parse(J.S(id)) + 1)); state[counter] = checked(serial + 1); return serial; }
        bool ApplyAssets(Database db, Dictionary<string, object> state, Dictionary<string, object> q, Dictionary<string, object> r)
        {
            string t = J.S(q["_type"]); r["_type"] = "Pr" + t.Substring(2);
            if (t == "PqLiveryStickerUpdate" || t == "PqLiveryUpdate")
            {
                bool sticker = t == "PqLiveryStickerUpdate"; string field = sticker ? "liverySticker" : "livery", idField = sticker ? "liveryStickerId" : "liveryId", keyField = sticker ? "liveryStickerUniqueId" : "liveryUniqueId", vector = sticker ? "liveryStickerVector" : "liveryVector";
                var value = CopyMap(q[field]); if (sticker) J.S(q["base64Image"]); else J.S(value["kartId"]);
                if (!J.Truth(J.Get(value, idField)) || J.Same(value[idField], "0")) value[idField] = NextId(state, sticker ? "nextStickerId" : "nextLiveryId", J.List(state, vector).Select(v => J.D(v)[idField])).ToString();
                if (!sticker)
                {
                    long revision = 0; foreach (var v in J.List(state, vector).Select(J.D).Concat(db.Query("SELECT metadata FROM assets WHERE kind='livery'").Select(row => J.D(J.Read((string)row[0]))))) if (J.Same(J.Get(v, idField), value[idField])) revision = Math.Max(revision, J.I(J.Get(v, "liveryRevision", 0)));
                    value["liveryRevision"] = revision + 1; value["createTime"] = J.Millis();
                }
                string key = Reserve(db, sticker ? "sticker" : "livery", J.M()); value[keyField] = key; var metadata = sticker ? J.M("sticker", value, "base64Image", q["base64Image"]) : value; db.Query("UPDATE assets SET metadata=? WHERE key=?", J.Write(metadata), key);
                var linkMeta = J.M("author", rankIdentity["accountId"], "time", J.Now()); if (!sticker) linkMeta["revision"] = value["liveryRevision"]; r[field] = value; r["dataStoreLink"] = NativeLink(key, linkMeta); return true;
            }
            if (t == "PqLiveryDBUpdate")
            {
                var value = J.D(q["livery"]); if (!J.Truth(q["upLoadSucceed"])) { r["livery"] = value; r["upLoadSucceed"] = false; return false; }
                var saved = Uploaded(db, J.S(value["liveryUniqueId"]), "livery"); J.Need(J.Same(value, saved), "Livery confirmation differs from reservation"); var entries = J.List(state, "liveryVector"); var current = entries.Select(J.D).FirstOrDefault(v => J.Same(v["liveryId"], saved["liveryId"]));
                if (current != null && J.I(current["liveryRevision"]) >= J.I(saved["liveryRevision"])) { r["livery"] = saved; r["upLoadSucceed"] = J.Same(current, saved); return false; }
                entries.RemoveAll(v => J.Same(J.D(v)["liveryId"], saved["liveryId"])); entries.Add(saved); foreach (var preset in Presets(state)) if (J.Same(J.Get(OptionalMap(preset, "livery"), "liveryId"), saved["liveryId"])) preset["livery"] = J.Copy(saved); r["livery"] = saved; r["upLoadSucceed"] = true; return true;
            }
            if (t == "PqLiveryDelete" || t == "PqLiveryStickerDelete")
            {
                bool sticker = t == "PqLiveryStickerDelete"; string field = sticker ? "liveryStickerId" : "liveryId", vector = sticker ? "liveryStickerVector" : "liveryVector"; object identity = q[field]; DeleteDesign(db, sticker ? "sticker" : "livery", field, identity); J.List(state, vector).RemoveAll(v => J.Same(J.D(v)[field], identity));
                if (!sticker) { foreach (var preset in Presets(state)) if (J.Same(J.Get(OptionalMap(preset, "livery"), "liveryId"), identity)) preset["livery"] = EmptyLivery(); var cache = J.Map(J.Map(state, "customPresets"), "kartPreset"); foreach (var kart in cache.Keys.ToList()) { string text = J.S(cache[kart]); int comma = text.IndexOf(','); if (comma >= 0 && text.Substring(0, comma) == J.S(identity)) cache[kart] = text.Substring(comma); } } r[field] = identity; return true;
            }
            if (t == "PqSignedUrl") { var ids = J.A(q["uniqueId"]); J.Need(ids.All(v => v is string), "Invalid asset identities"); if (J.Same(J.Get(q, "contents"), "trialtower")) { r["signedUrl"] = TrialTower.SignedUrls(ids); return false; } var urls = J.M(); foreach (var id in ids) if (db.Query("SELECT 1 FROM assets WHERE key=? AND length(data)>0", id).Count > 0) urls[J.S(id)] = Url(J.S(id)); r["signedUrl"] = urls; return false; }
            if (t == "PqReplayUpdate")
            {
                var value = J.M(); foreach (string key in "id title createDate trackId gameId memo recorderName recorderMachinePlatform recorderAccountPlatform seasonCount replaySubversion bGhost roundIndex".Split(' ')) value[key] = J.Copy(q[key]);
                if (!J.Truth(value["id"])) { long id = NextId(state, "nextReplayId", OptionalMap(state, "replays").Keys.Concat(OptionalMap(state, "pendingReplays").Keys).Cast<object>()); J.Need(id <= Int32.MaxValue, "Local replay ID range exhausted"); value["id"] = id.ToString(); }
                string identity = J.S(value["id"]); J.S(value["gameId"]); value["owner"] = "local"; string keyAsset = Reserve(db, "replay", value); J.EnsureMap(state, "pendingReplays")[identity] = keyAsset; r["replayData"] = value; r["dataStoreLink"] = Link(keyAsset); return true;
            }
            if (t == "PqUserReplayDBUpdate")
            {
                string identity = J.S(q["id"]); var pending = OptionalMap(state, "pendingReplays"); if (!pending.ContainsKey(identity)) { J.Need(OptionalMap(state, "replays").ContainsKey(identity), "No replay upload reservation"); return false; }
                string key = J.S(pending[identity]); var value = Uploaded(db, key, "replay"); var ownership = J.EnsureMap(state, "replayAssets"); object previous = J.Get(ownership, identity); J.EnsureMap(state, "replays")[identity] = value; ownership[identity] = key; pending.Remove(identity); if (previous != null && !J.Same(previous, key)) db.Query("DELETE FROM assets WHERE key=? AND kind='replay'", previous); return true;
            }
            if (t == "PqReplayList") { r["replayMap"] = J.Get(state, "replays", J.M()); return false; }
            if (t == "PqUserReplaySignedUrl" || t == "PqReplaySignedUrl")
            { string identity = J.Get(q, "id") as string; if (t == "PqReplaySignedUrl") identity = OptionalMap(state, "replays").Where(p => J.Same(J.D(p.Value)["gameId"], q["gameId"]) && J.Same(J.D(p.Value)["roundIndex"], q["roundIndex"])).Select(p => p.Key).FirstOrDefault(); var assets = OptionalMap(state, "replayAssets"); J.Need(identity != null && assets.ContainsKey(identity), "Replay is unavailable"); string key = J.S(assets[identity]); Uploaded(db, key, "replay"); r["signedUrl"] = Url(key); return false; }
            if (t == "PqReplayDelete")
            { string identity = J.S(q["id"]); var pending = OptionalMap(state, "pendingReplays"); object key = J.Get(pending, identity); pending.Remove(identity); if (key != null) db.Query("DELETE FROM assets WHERE key=?", key); if (!J.Truth(q["dbDelete"])) return key != null; var assets = OptionalMap(state, "replayAssets"); object published = J.Get(assets, identity); assets.Remove(identity); OptionalMap(state, "replays").Remove(identity); if (published != null) db.Query("DELETE FROM assets WHERE key=?", published); return true; }
            throw new InvalidOperationException("Unhandled asset request");
        }
    }
}
