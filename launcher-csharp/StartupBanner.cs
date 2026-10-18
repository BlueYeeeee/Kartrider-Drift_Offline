using System.Collections.Generic;

namespace KartDriftLocal
{
    static class StartupBanner
    {
        public const string Text = "单机版免费游玩，开源地址 https://github.com/BlueYeeeee/Kartrider-Drift_Offline";
        // PcMessage shape is taken from a captured native server announcement.
        public static Dictionary<string, object> Create()
        { return J.M("_type", "PcMessage", "message", Text, "localizedMessage", J.M()); }
    }
}
