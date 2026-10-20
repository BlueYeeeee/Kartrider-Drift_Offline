using System;
using System.Collections.Generic;
using System.Linq;

namespace KartDriftLocal
{
    sealed partial class ProfileStore
    {
        public readonly string Path;
        public Performance Performance;
        public readonly Dictionary<string, object> GearRules = J.Map(Bundle.Data("local-gear-rules"), "rules");
        readonly Dictionary<string, object> limits = Bundle.Data("local-vehicle-gear-limits");
        readonly Dictionary<string, object> costumes = Bundle.Data("local-costume-rules");
        readonly Dictionary<string, object> upgradeRules = Bundle.Data("local-upgrade-rules");
        readonly Dictionary<string, object> upgradeVehicles = Bundle.Data("local-upgrade-vehicles");
        readonly Dictionary<string, object> balloonRules = J.Map(Bundle.Data("local-balloon-rules"), "definition");
        readonly List<object> tracks = J.List(Bundle.Data("local-tracks"), "tracks");
        readonly Dictionary<string, object> rankIdentity;
        static readonly string[] LoginFields = ("itemPresetOn itemEquipmentOn itemPresetSlotVector ownKartsUpgradeInfo optionSetting licenseData tutorialData liveryVector liveryStickerVector emblemItemIdOn customProperties crossPlayEnabled autoReady quickMatchData ownCostumeAttrInfo rankMatchTierData balloon riderName").Split(' ');
        static readonly string[] VehicleSlots = "PartBack PartFront PartSide PartHandle PartPlate PartWheel KartAura KartBooster SkidMark".Split(' ');
        public ProfileStore(string path, Dictionary<string, object> templates)
        {
            Path = path; var login = J.D(J.Read(J.S(templates["PqLogin"])));
            rankIdentity = J.M("accountId", login["accountId"], "country", J.Get(J.Map(login, "profile"), "countryCode", ""), "platform", J.Get(J.Map(login, "profile"), "platform", 0));
            var seed = J.M(); foreach (string k in LoginFields) if (login.ContainsKey(k)) seed[k] = J.Copy(login[k]);
            J.EnsureMap(seed, "ownCostumeAttrInfo"); var inventory = J.D(J.Read(J.S(templates["PqInventory"])));
            seed["inventory"] = inventory.Where(p => p.Key.StartsWith("inventoryData", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value);
            seed["records"] = J.D(J.Read(J.S(templates["PqTimeAttackGetRecords"])))["records"];
            var presets = J.D(J.Read(J.S(templates["PqCustomPresetList"]))); seed["customPresets"] = J.M("kartPreset", presets["kartPreset"], "charPreset", presets["charPreset"]);
            using (var db = Open())
            {
                db.Query("CREATE TABLE IF NOT EXISTS profile (id INTEGER PRIMARY KEY CHECK(id=1), data TEXT NOT NULL)");
                db.Query("CREATE TABLE IF NOT EXISTS assets (key TEXT PRIMARY KEY, kind TEXT NOT NULL, metadata TEXT NOT NULL, data BLOB)");
                Transaction(db, delegate {
                    db.Query("INSERT OR IGNORE INTO profile VALUES (1, ?)", J.Write(seed)); var state = Snapshot(db);
                    foreach (string key in LoginFields) if (!state.ContainsKey(key) && seed.ContainsKey(key)) state[key] = J.Copy(seed[key]);
                    var seen = new HashSet<string>();
                    foreach (var value in J.Map(state, "inventory").Values)
                    {
                        var inv = J.D(value); if (!inv.ContainsKey("itemList")) continue; var entries = J.L();
                        foreach (var entry in J.List(inv, "itemList"))
                        {
                            string text = J.S(entry); string[] fields = text.Split(',');
                            if (J.Map(balloonRules, "balloonDef").ContainsKey(fields[0])) { J.Need(fields.Length == 2, "Unsupported balloon inventory format"); if (!seen.Add(fields[0])) continue; text = fields[0] + ",1"; }
                            entries.Add(text);
                        }
                        inv["itemList"] = entries;
                    }
                    // Fixed ratings use verified captures; native defaults cover unsampled vehicles.
                    var sampledVehicles = J.Map(Bundle.Data("local-speed-bodies"), "vehicles");
                    foreach (var pair in J.Map(state, "ownKartsUpgradeInfo"))
                    {
                        object definition;
                        if (!J.Map(limits, "vehicles").TryGetValue(pair.Key, out definition) || J.S(J.D(definition)["kartType"]) != "EKtKartType::Speed") continue;
                        var settings = J.Map(J.Map(J.D(pair.Value), "gearInfo"), "gearSettings");
                        var defaults = sampledVehicles.ContainsKey(pair.Key)
                            ? J.Map(J.Map(J.Map(J.Map(sampledVehicles, pair.Key), "upgrade"), "gearInfo"), "gearSettings")
                            : J.Map(J.D(definition), "defaults");
                        foreach (var rule in GearRules) if (!J.B(J.D(rule.Value)["tunable"])) settings[rule.Key] = J.Copy(defaults[rule.Key]);
                    }
                    SyncRankName(state);
                    RepairAssets(db, state);
                    VehicleDefaults.Repair(state);
                    if (!J.Same(J.Get(state, "gearBudget"), 51))
                    {
                        state["gearBudget"] = 51; state["legacyOverBudget"] = J.L();
                        foreach (var pair in J.Map(state, "ownKartsUpgradeInfo"))
                        {
                            var upgrade = J.D(pair.Value); var info = J.Map(upgrade, "gearInfo"); long spent = Spent(pair.Key, upgrade, J.Map(info, "gearSettings"));
                            info["totalGearCount"] = 51; info["remainGearCount"] = Math.Max(0, 51 - spent); if (spent > 51) J.List(state, "legacyOverBudget").Add(pair.Key);
                        }
                    }
                    Save(db, state);
                });
            }
        }
        public Database Open() { return new Database(Path); }
        static void Transaction(Database db, Action action) { db.Query("BEGIN IMMEDIATE"); try { action(); db.Query("COMMIT"); } catch { db.Query("ROLLBACK"); throw; } }
        internal static Dictionary<string, object> Snapshot(Database db) { return J.D(J.Read((string)db.Query("SELECT data FROM profile WHERE id=1")[0][0])); }
        internal static void Save(Database db, Dictionary<string, object> state) { db.Query("UPDATE profile SET data=? WHERE id=1", J.Write(state)); }
        public Dictionary<string, object> Snapshot() { using (var db = Open()) return Snapshot(db); }
        static Dictionary<string, object> CopyMap(object value) { return J.D(J.Copy(value)); }
        static Dictionary<string, object> OptionalMap(Dictionary<string, object> state, string key) { return J.D(J.Get(state, key, J.M())); }
        public List<object> GearValues(string kart, Dictionary<string, object> upgrade, string name)
        {
            var vehicles = J.Map(limits, "vehicles"); if (!vehicles.ContainsKey(kart)) return J.L(); var vehicle = J.D(vehicles[kart]);
            if (J.S(vehicle["kartType"]) != "EKtKartType::Speed") return null;
            var rule = J.Map(J.Map(limits, "rules"), name); long limit = J.I(J.Map(vehicle, "gearLimit")[name]);
            if (J.I(J.Map(vehicle, "defaults")[name]) < 0 || !J.B(J.Map(GearRules, name)["tunable"]) || limit <= 0 || !J.List(rule, "engineTypes").Any(v => J.Same(v, J.Get(upgrade, "engineType"))) || J.S(rule["kartType"]) == "EKtKartType::Item") return J.L();
            J.Need(J.S(rule["unlockGrade"]) == "EKtItemGrade::Common", "Unresolved speed gear grade restriction");
            return J.List(J.Map(GearRules, name), "values").Where(v => J.B(rule["onoff"]) || J.I(v) <= limit).ToList();
        }
        long Spent(string kart, Dictionary<string, object> upgrade, Dictionary<string, object> settings)
        { return settings.Where(p => GearRules.ContainsKey(p.Key) && J.B(J.Map(GearRules, p.Key)["tunable"]) && (GearValues(kart, upgrade, p.Key) == null || GearValues(kart, upgrade, p.Key).Count != 0)).Sum(p => J.I(p.Value)); }
        internal static void Remember(Dictionary<string, object> state)
        {
            var preset = J.Map(state, "itemPresetOn"); var items = J.Map(preset, "itemMap"); var custom = J.Map(state, "customPresets");
            string kart = J.Get(items, "Kart") as string;
            if (!String.IsNullOrEmpty(kart))
            {
                var saved = J.M("itemMap", items.Where(p => VehicleSlots.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value)); if (preset.ContainsKey("livery")) saved["livery"] = J.Copy(preset["livery"]);
                J.EnsureMap(state, "vehiclePresets")[kart] = saved; object id = J.Get(OptionalMap(preset, "livery"), "liveryId", "");
                J.Map(custom, "kartPreset")[kart] = (J.NumericId(id) ? J.S(id) : "") + "," + J.S(J.Get(items, "PartWheel", ""));
            }
            string character = J.Get(items, "Character") as string;
            if (!String.IsNullOrEmpty(character)) J.Map(custom, "charPreset")[character] = String.Join(",", "Costume MotionFinish MotionRetire MotionVictory MotionDefeat".Split(' ').Select(k => J.S(J.Get(items, k, ""))));
        }
        static void Notice(Dictionary<string, object> response, Dictionary<string, object> notice) { J.EnsureList(response, "_localProfileNotifications").Add(notice); }
        public Dictionary<string, object> Apply(Dictionary<string, object> request, Dictionary<string, object> response)
        {
            // Work on a private reply too: failed performance validation must not leak mutation notices.
            response = CopyMap(response);
            using (var db = Open()) Transaction(db, delegate { Apply(db, Snapshot(db), request, response); }); return response;
        }
        void Apply(Database db, Dictionary<string, object> state, Dictionary<string, object> q, Dictionary<string, object> r)
        {
            string t = J.S(q["_type"]); bool changed = false;
            if (AssetRequests.Contains(t)) changed = ApplyAssets(db, state, q, r);
            else switch (t)
            {
                case "PqCheckString": J.S(q["text"]); J.Need(J.I(q["type"]) >= 0 && J.I(q["type"]) <= 2, "Invalid local text check"); r["_type"] = "PrCheckString"; r["notSupportedLetter"] = ""; break;
                case "PqRiderNameChangeInfo": case "PqRiderNameChangeReserve": case "PqRiderNameChangeConfirm": case "PqRiderNameChangeReserveCancel": case "PqRiderNameDuplicateCheck": case "PqSetRiderName":
                    r["_type"] = "Pr" + t.Substring(2);
                    if (t == "PqRiderNameChangeReserve" || t == "PqRiderNameDuplicateCheck" || t == "PqSetRiderName")
                    {
                        string name = J.S(q["riderName"]); J.Need(!String.IsNullOrWhiteSpace(name) && name.Length <= 64 && !name.Any(c => c < 32 || Char.IsSurrogate(c)), "Invalid local rider name"); r["notSupportedLetter"] = "";
                        if (t == "PqRiderNameChangeReserve") { state["pendingRiderName"] = name; changed = true; }
                        else if (t == "PqSetRiderName") { state["riderName"] = name; state.Remove("pendingRiderName"); changed = true; }
                    }
                    else if (t == "PqRiderNameChangeReserveCancel") { state.Remove("pendingRiderName"); changed = true; }
                    else if (t == "PqRiderNameChangeConfirm")
                    { J.Need(state.ContainsKey("pendingRiderName"), "No reserved local rider name"); state["riderName"] = state["pendingRiderName"]; state.Remove("pendingRiderName"); state["riderNameChange"] = J.M("changeCount", J.I(J.Get(OptionalMap(state, "riderNameChange"), "changeCount", 0)) + 1, "changeDate", J.Now()); r["changeRiderName"] = state["riderName"]; changed = true; }
                    if (t == "PqRiderNameChangeInfo" || t == "PqRiderNameChangeConfirm") { var info = CopyMap(J.Get(state, "riderNameChange", J.M("changeCount", 0))); info["changeDate"] = "0001-01-01T00:00:00Z"; r["riderNameChange"] = info; } break;
                case "PqEmblemItemIdUpdate": state["emblemItemIdOn"] = J.S(q["itemId"]); changed = true; break;
                case "PqSetCrossPlayStatus": state["crossPlayEnabled"] = J.B(q["status"]); changed = true; break;
                case "PqSetAutoReady": state["autoReady"] = J.B(q["autoReady"]); changed = true; break;
                case "PqSetQuickMatchData": state["quickMatchData"] = CopyMap(q["data"]); changed = true; break;
                case "PqAddNews": case "PqRemoveNews":
                    string newsId = J.S(q["id"]); var news = J.EnsureMap(state, "news"); if (t == "PqAddNews") news[newsId] = J.S(q["value"]); else news.Remove(newsId); r["_type"] = "PrPacket"; changed = true; break;
                case "PqChangeBeginnerSetting": long flag = J.I(q["flag"]); J.Need(flag >= Int32.MinValue && flag <= Int32.MaxValue, "Invalid beginner setting flag"); state["beginnerFlag"] = flag; r["_type"] = "PrPacket"; changed = true; break;
                case "PqBalloonCharge": case "PqBalloonUpgrade":
                    string item = J.S(q["itemId"]); var balloonDefs = J.Map(balloonRules, "balloonDef"); J.Need(balloonDefs.ContainsKey(item), "Unknown balloon"); var rule = J.D(balloonDefs[item]);
                    var stacks = J.Map(state, "inventory").Values.SelectMany(v => J.A(J.Get(J.D(v), "itemList", J.L()))).Select(v => J.S(v).Split(',')).Where(v => v[0] == item).ToList();
                    if (t == "PqBalloonCharge")
                    { long amount = J.I(q["amount"]); J.Need(amount > 0 && amount <= J.I(rule["chargeMax"]) && stacks.Count == 1 && stacks[0].Length == 2 && stacks[0][1] == "1", "Invalid balloon charge"); Notice(r, J.M("_type", "PcInventoryUpdated", "inventoryDataDiff", J.L(J.M("itemId", item, "itemDataBefore", J.M("itemCount", 1), "itemDataAfter", J.M("itemCount", 1))))); }
                    else
                    { long grade = J.I(q["targetGrade"]); var owned = J.EnsureMap(state, "balloon"); long current = J.I(J.Get(J.D(J.Get(owned, item, J.M())), "grade", 0)); J.Need(stacks.Count > 0 && grade >= current && grade <= J.I(balloonRules["maxGrade"]) && grade < J.List(rule, "grade").Count, "Invalid balloon upgrade"); owned[item] = J.M("grade", grade); Notice(r, J.M("_type", "PcBalloonUpgrade", "change", J.M(item, J.M("grade", grade)))); changed = true; }
                    r["_type"] = "PrPacket"; break;
                case "PqQuickChatUpdate": long index = J.I(q["idx"]); string text = J.S(q["quickChat"]); J.Need(index >= 0 && index < 32, "Invalid local quick chat slot"); var chats = J.EnsureList(state, "quickChatList"); while (chats.Count <= index) chats.Add(""); chats[(int)index] = text; changed = true; break;
                case "PqDisplayTierTypeUpdate": long tier = J.I(q["displayTierType"]); J.Need(tier >= 0 && tier <= 2, "Invalid rank display selection"); J.Map(state, "rankMatchTierData")["displayTier"] = tier; changed = true; break;
                case "PqSetCustomProperty": string nameKey = J.S(q["name"]); string json = J.S(q["valueJsonString"]); J.Read(json); J.EnsureMap(state, "customProperties")[nameKey] = json; changed = true; break;
                case "PqTutorialComplete": state["tutorialData"] = CopyMap(q["tutorialData"]); J.Put(r, J.M("_type", "PrTutorialComplete", "tutorialData", state["tutorialData"])); changed = true; break;
                case "PqLicenseStart": J.S(q["licenseMissionId"]); J.S(q["trackId"]); J.Put(r, J.M("_type", "PrLicenseStart", "licenseMissionId", q["licenseMissionId"], "startTime", J.Now())); break;
                case "PqLicenseClear": string mission = J.S(q["licenseMissionId"]); long stars = J.I(q["starCount"]); J.Need(stars >= 0, "Invalid license result"); var missions = J.Map(J.Map(state, "licenseData"), "licenseMissionDataMap"); if (!missions.ContainsKey(mission)) missions[mission] = J.M("starCount", 0, "rewardDateVector", J.L()); var entry = J.Map(missions, mission); entry["starCount"] = Math.Max(stars, J.I(entry["starCount"])); J.Put(r, J.M("_type", "PrLicenseClear", "licenseMissionId", mission, "starCount", entry["starCount"], "licenseData", state["licenseData"])); changed = true; break;
                case "PqUpdateCommunicationFavorite": var favorites = J.M(); foreach (string key in "emoticonIds emotionIds quickChatIds".Split(' ')) { J.Need(J.A(q[key]).All(v => v is string), "Invalid communication favorites"); favorites[key] = J.Copy(q[key]); } state["communicationFavorites"] = favorites; r["_type"] = "PrUpdateCommunicationFavorite"; changed = true; break;
                case "PqChangeCostumeAttrInfo": string costume = J.S(q["costumeId"]), attr = J.S(q["changeAttrName"]); object val = q["changeAttrValue"]; var defaults = J.Map(costumes, "defaults"); J.Need(defaults.ContainsKey(costume), "Unknown costume"); if (attr == "maskCostumeId") J.Need(!J.Truth(val) || defaults.ContainsKey(J.S(val)), "Unknown appearance costume"); else J.Need(J.Map(costumes, "values").ContainsKey(attr) && J.List(J.Map(costumes, "values"), attr).Any(v => J.Same(v, val)), "Unknown costume attribute"); var attrs = J.EnsureMap(state, "ownCostumeAttrInfo"); if (!attrs.ContainsKey(costume)) attrs[costume] = J.Copy(defaults[costume]); J.Map(attrs, costume)[attr] = val; J.Put(r, J.M("_type", "PrChangeCostumeAttrInfo", "costumeId", costume, "changedAttr", attrs[costume])); changed = true; break;
                case "PqItemPresetOnUpdate":
                    var preset = CopyMap(q["itemPreset"]); var items = J.Map(preset, "itemMap"); var oldItems = J.Map(J.Map(state, "itemPresetOn"), "itemMap"); object oldKart = J.Get(oldItems, "Kart"), kart = J.Get(items, "Kart"); Remember(state); object saved;
                    bool resetDefaultWheel = !J.Same(kart, oldKart) && J.Same(J.Get(items, "PartWheel"), J.Get(oldItems, "PartWheel"));
                    if (!J.Same(kart, oldKart) && kart is string && OptionalMap(state, "vehiclePresets").TryGetValue((string)kart, out saved)) { foreach (string slot in VehicleSlots) items.Remove(slot); J.Put(items, CopyMap(J.Map(J.D(saved), "itemMap"))); preset.Remove("livery"); if (J.D(saved).ContainsKey("livery")) preset["livery"] = J.Copy(J.D(saved)["livery"]); resetDefaultWheel = false; }
                    var livery = OptionalMap(preset, "livery"); if (J.Truth(J.Get(livery, "liveryUniqueId")) && !J.Same(J.Get(livery, "kartId"), kart)) preset["livery"] = EmptyLivery();
                    VehicleDefaults.Normalize(preset, resetDefaultWheel: resetDefaultWheel); state["itemPresetOn"] = CopyMap(preset); Remember(state); J.Put(r, J.M("_type", "PrItemPresetOnUpdate", "itemPreset", preset)); changed = true; break;
                case "PqItemEquipmentOnUpdate": var equipment = CopyMap(q["itemEquipment"]); J.Need(equipment.ContainsKey("items"), "Invalid equipment"); state["itemEquipmentOn"] = equipment; J.Put(r, J.M("_type", "PrItemEquipmentOnUpdate", "itemEquipment", equipment)); changed = true; break;
                case "PqExtendItemPresetSlot": var extend = J.List(state, "itemPresetSlotVector"); if (extend.Count < 100) { extend.Add(J.M("name", "", "itemPreset", J.M())); changed = true; } J.Put(r, J.M("_type", "PrExtendItemPresetSlot", "slotNum", extend.Count)); break;
                case "PqItemPresetSave": case "PqItemPresetLoad": case "PqItemPresetDelete": case "PqItemPresetUpdateName":
                    long slotIndex = J.I(q["itemPresetIdx"]); J.Need(slotIndex >= 0 && slotIndex <= 99, "Invalid preset index"); var slots = J.List(state, "itemPresetSlotVector"); int i = (int)slotIndex;
                    if (t == "PqItemPresetSave") { while (slots.Count <= i) slots.Add(J.M("name", "", "itemPreset", J.M())); slots[i] = J.M("name", q["name"], "itemPreset", J.Copy(state["itemPresetOn"])); J.Put(r, J.Map(J.M("v", slots[i]), "v")); r["itemPresetIdx"] = i; }
                    else { J.Need(i < slots.Count && J.Truth(J.Get(J.D(slots[i]), "itemPreset")), "Preset slot is empty"); if (t == "PqItemPresetLoad") { state["itemPresetOn"] = J.Copy(J.D(slots[i])["itemPreset"]); VehicleDefaults.Normalize(J.Map(state, "itemPresetOn")); Remember(state); r["itemPreset"] = state["itemPresetOn"]; } else if (t == "PqItemPresetDelete") { slots[i] = J.M("name", "", "itemPreset", J.M()); r["itemPresetIdx"] = i; } else { J.D(slots[i])["name"] = q["name"]; r["itemPresetIdx"] = i; r["name"] = q["name"]; } }
                    r["_type"] = "Pr" + t.Substring(2); changed = true; break;
                case "PqOptionSettingUpdate": state["optionSetting"] = CopyMap(q["optionSetting"]); changed = true; break;
                case "PqKartUpgrade": Upgrade(state, q, r); changed = true; break;
                case "PqKartGearSet": case "PqKartGearReset": Gears(state, q, r); changed = true; break;
                case "PqTimeAttackStart": var activeKart = J.S(J.Map(J.Map(state, "itemPresetOn"), "itemMap")["Kart"]); state["activeRace"] = J.M("gameId", r["gameId"], "trackId", q["trackId"], "beginnerFlag", J.Get(state, "beginnerFlag", 0), "itemPresetOn", J.Copy(state["itemPresetOn"]), "itemEquipmentOn", J.Copy(state["itemEquipmentOn"]), "upgrade", J.Copy(J.Map(state, "ownKartsUpgradeInfo")[activeKart])); changed = true; break;
                case "PqTimeAttackUpdate": changed = RaceResult(db, state, q, r); break;
                case "PqTimeAttackRegistReplay": RegisterGhost(db, state, q, r); changed = true; break;
            }
            if ((t == "PqQuickChatUpdate" || t == "PqLoginComplete") && state.ContainsKey("quickChatList")) r["_localProfileNotifications"] = J.L(J.M("_type", "PcQuickChatList", "quickChatList", J.Copy(state["quickChatList"])));
            if (new[] { "PqLoginComplete", "PqItemPresetOnUpdate", "PqItemPresetLoad", "PqBalloonUpgrade" }.Contains(t)) Notice(r, new Performance(J.S(rankIdentity["accountId"])).BalloonNotice(state));
            if (t == "PqLoginComplete" && J.Truth(J.Get(state, "news"))) { var news = J.Map(state, "news"); Notice(r, J.M("_type", "PcNews", "inserted", news.Keys.ToList(), "insertedValue", news.Values.ToList(), "removed", J.L())); }
            if (Performance != null) Performance.Apply(state, q, r);
            if (changed) { SyncRankName(state); Save(db, state); }
            Project(db, state, q, r);
        }
        void Gears(Dictionary<string, object> state, Dictionary<string, object> q, Dictionary<string, object> r)
        {
            string kart = J.S(q["kartItemId"]), t = J.S(q["_type"]); var upgrade = J.Map(J.Map(state, "ownKartsUpgradeInfo"), kart); var info = J.Map(upgrade, "gearInfo"); var previous = J.Map(info, "gearSettings"); var settings = J.M();
            var input = t == "PqKartGearReset" ? J.M() : J.D(q["gearSettings"]); J.Need(input.Keys.All(GearRules.ContainsKey), "Unknown gear setting");
            foreach (var pair in GearRules)
            {
                var rule = J.D(pair.Value); var allowed = GearValues(kart, upgrade, pair.Key); object before = J.Get(previous, pair.Key, 0); object value;
                if (t == "PqKartGearReset") value = J.B(rule["tunable"]) && (allowed == null || allowed.Count != 0) ? 0 : before;
                else { value = J.Get(input, pair.Key, before); J.I(value); J.Need(J.List(rule, "values").Any(v => J.Same(v, value)), "Invalid gear allocation"); J.Need(J.B(rule["tunable"]) || J.Same(value, before), "Cannot modify a fixed vehicle parameter"); J.Need(J.Same(value, before) || allowed == null || allowed.Any(v => J.Same(v, value)), "Gear is locked or exceeds this vehicle limit"); }
                settings[pair.Key] = value;
            }
            long remaining = J.I(info["totalGearCount"]) - Spent(kart, upgrade, settings); J.Need(remaining >= 0, "Gear budget exceeded"); if (t == "PqKartGearReset") info["hadReset"] = 1;
            info["gearSettings"] = settings; info["remainGearCount"] = remaining; J.Put(r, J.M("_type", "Pr" + t.Substring(2), "kartItemId", kart, "remainGears", remaining, "gearSettings", settings)); if (state.ContainsKey("legacyOverBudget")) J.List(state, "legacyOverBudget").Remove(kart);
        }
        void Upgrade(Dictionary<string, object> state, Dictionary<string, object> q, Dictionary<string, object> r)
        {
            string kart = J.S(q["kartItemId"]); var upgrade = J.Map(J.Map(state, "ownKartsUpgradeInfo"), kart); var rules = J.Map(upgradeRules, "definition"); var grades = J.Map(upgradeRules, "grades"); long grade = J.I(upgrade["grade"]), engine = J.I(upgrade["engineType"]);
            J.Need(!J.List(rules, "UnupgradableKart").Contains(kart), "Vehicle cannot be upgraded"); long effective = grade == -1 || grade == 255 ? J.I(J.Map(upgradeVehicles, "baseGrades")[kart]) : grade;
            var row = J.List(rules, "kartUpgradeDefVector").Select(J.D).Single(v => J.I(grades[J.S(v["grade"])]) == effective);
            if (engine == J.I(J.Map(upgradeVehicles, "engines")["N1"])) { J.Need(effective < J.I(grades[J.S(rules["MaxGrade"])]), "Vehicle is already at its grade limit"); upgrade["grade"] = effective + 1; upgrade["level"] = 0; }
            else if (engine == J.I(J.Map(upgradeVehicles, "engines")["A2"])) { var custom = J.D(J.Get(J.Map(upgradeVehicles, "maxLevels"), kart, J.M())); long max = J.I(J.Get(custom, effective.ToString(), Math.Min(J.I(row["maxLevel"]), J.List(J.Map(row, "upgradeInfo"), "levelGearCountVector").Count))); long level = J.I(upgrade["level"]); J.Need(level >= 0 && level < max, "Vehicle is already at its level limit"); upgrade["level"] = level + 1; }
            else throw new ArgumentException("Unknown vehicle engine type");
            J.Put(r, J.M("_type", "PrKartUpgrade", "kartGrade", upgrade["grade"], "kartLevel", upgrade["level"])); Notice(r, J.M("_type", "PcKartUpgradeInfo", "kartItemId", kart, "upgradeInfo", J.Copy(upgrade)));
        }
        static void SyncRankName(Dictionary<string, object> state)
        {
            object name = J.Get(state, "riderName", "");
            if (!J.Same(J.Get(state, "rankRiderName"), name)) { RankChanged(state); state["rankRiderName"] = J.Copy(name); }
        }
        static void RankChanged(Dictionary<string, object> state) { state["rankVersion"] = J.I(J.Get(state, "rankVersion", 1)) % 2147483647 + 1; }
        bool RaceResult(Database db, Dictionary<string, object> state, Dictionary<string, object> q, Dictionary<string, object> r)
        {
            var value = CopyMap(q["timeAttack"]); bool updated = false; r["dataStoreLink"] = J.M("url", "", "verb", "", "headers", J.M(), "body", "", "valueIndex", J.L(), "metadata", "");
            if (J.Same(q["status"], 0) && !J.Truth(J.Get(q, "bCheat", false)))
            {
                var race = OptionalMap(state, "activeRace"); J.Need(race.Count > 0 && J.Same(q["gameId"], race["gameId"]) && J.Same(value["trackId"], race["trackId"]), "Race result does not match the active race"); long time = J.I(value["recordTime"]); J.Need(time > 0, "Invalid race time"); string track = J.S(value["trackId"]); var records = J.Map(state, "records");
                if (!records.ContainsKey(track) || time < J.I(J.Map(records, track)["lapTime"]))
                {
                    var upgrade = J.Map(race, "upgrade"); string kart = J.S(J.Map(J.Map(race, "itemPresetOn"), "itemMap")["Kart"]);
                    records[track] = J.M("trackId", track, "lapTime", time, "mileage", value["recordMileage"], "kartId", kart, "kartLevel", upgrade["level"], "kartGrade", upgrade["grade"], "beginnerFlag", J.Get(race, "beginnerFlag", 0), "replayPath", "", "version", 0, "updateDate", J.Now(), "preview", J.M("itemPresetOn", race["itemPresetOn"], "itemEquipmentOn", race["itemEquipmentOn"]), "emblemId", J.Get(state, "emblemItemIdOn", ""));
                    J.EnsureMap(state, "recordUpgrades")[track] = J.Copy(upgrade); RankChanged(state); updated = true;
                    string identity = J.Truth(J.Get(value, "timeAttackUniqueId")) ? J.S(value["timeAttackUniqueId"]) : Guid.NewGuid().ToString("N"); value["timeAttackUniqueId"] = identity; var uploads = J.EnsureMap(state, "ghostUploads"); J.Need(!uploads.ContainsKey(identity), "Time attack upload identity already exists");
                    var metadata = J.M("trackId", track, "gameId", race["gameId"], "recordTime", time, "registered", false); string key = Reserve(db, "ghost", metadata); var upload = CopyMap(metadata); upload["key"] = key; uploads[identity] = upload; J.EnsureMap(state, "bestGhostIdByTrack")[track] = identity; r["dataStoreLink"] = Link(key);
                }
            }
            J.Put(r, J.M("_type", "PrTimeAttackUpdate", "timeAttack", value, "isUpdated", updated, "gameId", q["gameId"])); return updated;
        }
        void RegisterGhost(Database db, Dictionary<string, object> state, Dictionary<string, object> q, Dictionary<string, object> r)
        {
            string identity = J.S(q["timeAttackUniqueId"]); var uploads = OptionalMap(state, "ghostUploads"); J.Need(uploads.ContainsKey(identity), "No ghost upload reservation"); var upload = J.Map(uploads, identity); string key = J.S(upload["key"]); Uploaded(db, key, "ghost"); upload["registered"] = true; string track = J.S(upload["trackId"]);
            if (J.Same(J.Get(OptionalMap(state, "bestGhostIdByTrack"), track), identity)) { var record = J.Map(J.Map(state, "records"), track); string url = Url(key); if (!J.Same(record["replayPath"], url)) RankChanged(state); record["replayPath"] = url; } r["_type"] = "PrPacket";
        }
        Dictionary<string, object> Record(Database db, object value)
        {
            var record = CopyMap(value); string address = J.S(J.Get(record, "replayPath", "")), prefix = BaseUrl + "/local-assets/"; string key = address.StartsWith(prefix, StringComparison.Ordinal) ? address.Substring(prefix.Length) : "";
            if (db.Query("SELECT 1 FROM assets WHERE key=? AND kind='ghost' AND length(data)>0", key).Count == 0) record["replayPath"] = ""; return record;
        }
        void Project(Database db, Dictionary<string, object> state, Dictionary<string, object> q, Dictionary<string, object> r)
        {
            switch (J.S(q["_type"]))
            {
                case "PqLogin": foreach (string key in LoginFields) if (state.ContainsKey(key)) r[key] = J.Copy(state[key]); if (state.ContainsKey("riderName")) J.EnsureMap(r, "profile")["riderName"] = state["riderName"]; ProjectAssets(db, r); break;
                case "PqInventory": J.Put(r, CopyMap(state["inventory"])); break;
                case "PqTimeAttackGetTracks": J.Put(r, J.M("_type", "PrTimeAttackGetTracks", "trackList", tracks.Select(v => (object)J.M("trackId", v, "count", J.Map(state, "records").ContainsKey(J.S(v)) ? 1 : 0)).ToList())); break;
                case "PqTimeAttackGetRecords": r["records"] = J.Map(state, "records").ToDictionary(p => p.Key, p => (object)Record(db, p.Value)); break;
                case "PqTimeAttackGetRankVersion": J.Put(r, J.M("_type", "PrTimeAttackGetRankVersion", "version", J.Get(state, "rankVersion", 1))); break;
                case "PqTimeAttackGetRankers":
                    string track = J.S(q["trackId"]); var records = J.Map(state, "records"); var rows = J.L(); var mine = J.L();
                    if (records.ContainsKey(track)) { var record = Record(db, records[track]); mine.Add(record); var row = CopyMap(rankIdentity); J.Put(row, J.M("rank", 1, "riderName", J.Get(state, "riderName", ""), "lapTime", record["lapTime"], "preview", J.Copy(record["preview"]), "kartLevel", record["kartLevel"], "kartGrade", record["kartGrade"], "emblemItemIdOn", J.Get(record, "emblemId", ""), "replayPath", record["replayPath"])); if (OptionalMap(state, "recordUpgrades").ContainsKey(track)) row["kartUpgradeInfo"] = J.Copy(J.Map(state, "recordUpgrades")[track]); rows.Add(row); }
                    J.Put(r, J.M("_type", "PrTimeAttackGetRankers", "trackId", track, "myRank", mine.Count, "myRecord", mine, "totalRank", rows, "platformRank", J.Copy(rows), "countryRank", J.Copy(rows))); break;
                case "PqCustomPresetList": J.Put(r, CopyMap(state["customPresets"])); break;
                case "PqGetCommunicationFavorites": r["_type"] = "PrGetCommunicationFavorites"; J.Put(r, CopyMap(J.Get(state, "communicationFavorites", J.M("emoticonIds", J.L(), "emotionIds", J.L(), "quickChatIds", J.L())))); break;
            }
        }
    }
}
