using System;
using System.Collections.Generic;

namespace KartDriftLocal
{
    static class VehicleDefaults
    {
        static readonly Dictionary<string, object> Data = Bundle.Data("local-vehicle-default-parts");
        static readonly Dictionary<string, object> Vehicles = J.Map(Data, "vehicles"), Parts = J.Map(Data, "parts");
        public static void Normalize(Dictionary<string, object> preset, string identity = null)
        {
            object raw; if (!preset.TryGetValue("itemMap", out raw) || !(raw is Dictionary<string, object>)) return;
            var items = J.D(raw); string kart = identity ?? J.Get(items, "Kart") as string; if (kart == null || !Vehicles.ContainsKey(kart)) return;
            var defaults = J.D(Vehicles[kart]);
            // Handles are native model defaults, not removable custom equipment.
            if (defaults.ContainsKey("PartHandle")) items["PartHandle"] = defaults["PartHandle"];
            // Retain the selected custom tire. Repair an empty slot or a default
            // tire carried across from a different model using explicit IDs.
            if (defaults.ContainsKey("PartWheel"))
            {
                string wheel = J.Get(items, "PartWheel") as string; bool wrongDefault = wheel != null && Parts.ContainsKey(wheel) && J.B(J.Map(Parts, wheel)["default"]) && !J.Same(wheel, defaults["PartWheel"]);
                if (String.IsNullOrEmpty(wheel) || wrongDefault) items["PartWheel"] = defaults["PartWheel"];
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
