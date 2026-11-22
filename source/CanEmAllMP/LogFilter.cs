using System;
using HarmonyLib;

namespace CanEmAllMP
{
    /// Joiners' copies of cutelings, cans and baskets are kinematic (they follow the host instead of simulating),
    /// and some of the game's own code (conveyors, shockwaves, panicking cutelings...) still sets their velocity.
    /// Unity ignores that on a kinematic body and prints a warning every frame, filling the log. Harmless, so the
    /// mod drops just these two warnings before BepInEx writes them. Everything else is logged as normal.
    public static class LogFilter
    {
        public static void Install(Harmony harmony)
        {
            try
            {
                var t = AccessTools.TypeByName("BepInEx.Unity.IL2CPP.Logging.IL2CPPUnityLogSource");
                var m = t != null ? AccessTools.Method(t, "UnityLogCallback") : null;
                if (m == null) { Plugin.Log.LogInfo("[log] Unity log source not found; kinematic warnings stay"); return; }
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(LogFilter), nameof(Prefix)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("[log] filter: " + e.Message); }
        }

        static int _dropped;

        public static bool Prefix(string logLine)
        {
            if (logLine != null && logLine.StartsWith("Setting ", StringComparison.Ordinal)
                && logLine.EndsWith("velocity of a kinematic body is not supported.", StringComparison.Ordinal))
            {
                _dropped++;
                return false;
            }
            return true;
        }
    }
}
