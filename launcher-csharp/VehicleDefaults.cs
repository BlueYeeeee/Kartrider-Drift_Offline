using System;
using System.Collections.Generic;
using System.Linq;

namespace KartDriftLocal
{
    static class VehicleDefaults
    {
        static readonly Dictionary<string, object> Data = Bundle.Data("local-vehicle-default-parts");
        static readonly Dictionary<string, object> Vehicles = J.Map(Data, "vehicles"), Parts = J.Map(Data, "parts");
        public static void Normalize(Dictionary<string, object> preset, string identity = null, bool resetDefaultWheel = false)
        {
            object raw; if (!preset.TryGetValue("itemMap", out raw) || !(raw is Dictionary<string, object>)) return;
            var items = J.D(raw); string kart = identity ?? J.Get(items, "Kart") as string;
            if (identity == null && (J.Truth(J.Get(items, "Kart")) || J.Truth(J.Get(items, "Character"))))
                foreach (var pair in J.Map(Data, "globalDefaults")) if (!J.Truth(J.Get(items, pair.Key))) items[pair.Key] = pair.Value;
            if (kart != null && Vehicles.ContainsKey(kart))
            {
                var defaults = J.D(J.Copy(J.Map(Data, "common")));
                J.Put(defaults, J.D(Vehicles[kart]));
                NormalizeGroup(items, defaults, true, kart, resetDefaultWheel);
            }
            string character = J.Get(items, "Character") as string;
            var characters = J.Map(Data, "characters");
            if (character != null && characters.ContainsKey(character)) NormalizeGroup(items, J.D(characters[character]), false, character);
        }
        static void NormalizeGroup(Dictionary<string, object> items, Dictionary<string, object> defaults, bool vehicle, string owner, bool resetDefaultWheel = false)
        {
            foreach (var pair in items.ToArray())
            {
                string item = pair.Value as string;
                if (item == null || !Parts.ContainsKey(item)) continue;
                var part = J.Map(Parts, item);
                bool belongs = vehicle ? item.StartsWith("Kart:", StringComparison.Ordinal) : item.StartsWith("Motion:", StringComparison.Ordinal) || item.StartsWith("Costume:", StringComparison.Ordinal);
                if (belongs && J.B(part["default"]) && J.Same(J.Get(part, "slot"), pair.Key) && !defaults.ContainsKey(pair.Key)) items.Remove(pair.Key);
            }
            foreach (var pair in defaults)
            {
                string current = J.Get(items, pair.Key) as string;
                // A selected/saved wheel remains usable even when it is another kart's default.
                bool selectedWheel = vehicle && pair.Key == "PartWheel" && !resetDefaultWheel && current != null && Parts.ContainsKey(current) && J.Same(J.Get(J.Map(Parts, current), "slot"), "PartWheel");
                bool wrongDefault = !selectedWheel && current != null && Parts.ContainsKey(current) && J.B(J.Map(Parts, current)["default"]) && (vehicle || !J.List(J.Map(Parts, current), "parents").Contains(owner));
                if (pair.Key == "PartHandle" || String.IsNullOrEmpty(current) || wrongDefault) items[pair.Key] = pair.Value;
            }
        }
        public static void Repair(Dictionary<string, object> state)
        {
            Normalize(J.Map(state, "itemPresetOn"));
            foreach (var value in J.List(state, "itemPresetSlotVector")) { var slot = J.D(value); object preset; if (slot.TryGetValue("itemPreset", out preset) && preset is Dictionary<string, object>) Normalize(J.D(preset)); }
            var cache = J.Map(J.Map(state, "customPresets"), "kartPreset"); object histories;
            if (state.TryGetValue("vehiclePresets", out histories)) foreach (var pair in J.D(histories))
            {
                var preset = J.D(pair.Value);
                if (cache.ContainsKey(pair.Key)) { string cached = J.S(cache[pair.Key]); int split = cached.IndexOf(','); var oldItems = J.EnsureMap(preset, "itemMap"); if (!J.Truth(J.Get(oldItems, "PartWheel")) && split >= 0 && split < cached.Length - 1) oldItems["PartWheel"] = cached.Substring(split + 1); }
                Normalize(preset, pair.Key);
                if (!cache.ContainsKey(pair.Key)) continue; var items = J.Map(preset, "itemMap"); string text = J.S(cache[pair.Key]); int comma = text.IndexOf(','); if (comma >= 0) cache[pair.Key] = text.Substring(0, comma + 1) + J.S(J.Get(items, "PartWheel", ""));
            }
        }
    }
}
