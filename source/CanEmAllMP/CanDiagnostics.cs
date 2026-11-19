using System;
using System.Collections.Generic;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>
    /// Read-only logging (no hooks): what the game is about to can, and what each new can looks like.
    /// Enabled by [Debug] CanDiagnostics.
    /// </summary>
    public static class CanDiagnostics
    {
        private static readonly HashSet<IntPtr> _seenEnemies = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> _seenCans = new HashSet<IntPtr>();
        private static int _lines;
        private static bool _idsLogged;

        public static void Tick()
        {
            if (!Plugin.CanDiagnostics.Value || _lines > 300) return;
            var gm = GameManager.Instance;
            if (gm == null || !GameApi.InGame) return;
            try
            {
                if (!_idsLogged)
                {
                    _idsLogged = true;
                    Log($"static ids: TextureIndexId={Can.TextureIndexId} ShinyId={Can.ShinyId}");
                }
                var procs = gm.canProcessors;
                if (procs != null)
                    for (int i = 0; i < procs.Length; i++)
                    {
                        var set = procs[i]?.processingEnemies;
                        if (set == null) continue;
                        foreach (var e in set)
                        {
                            if (e == null || !_seenEnemies.Add(e.Pointer)) continue;
                            Log($"processor {i} canning '{e.gameObject.name}' canIndex={e.canIndex} canValue={e.canValue} isShiny(field)={e.isShiny} IsShiny={e.IsShiny} dead={e.IsDead} puppet={EnemySync.IsPuppet(e)}");
                        }
                    }
                var cans = gm.registeredCans;
                if (cans != null)
                    foreach (var c in cans)
                    {
                        if (c == null || c.IsInPool || !_seenCans.Add(c.Pointer)) continue;
                        float tex = -1, shiny = -1;
                        var pb = c.propertyBlock;
                        if (pb != null) { tex = pb.GetFloat(Can.TextureIndexId); shiny = pb.GetFloat(Can.ShinyId); }
                        Log($"new can price={c.price} qty={c.quantity} IsShiny={c.IsShiny} IsSmoked={c.IsSmoked} pbTex={tex} pbShiny={shiny} puppet={CanSync.IsPuppet(c)}");
                    }
                if (_seenCans.Count > 2000) _seenCans.Clear();
                if (_seenEnemies.Count > 2000) _seenEnemies.Clear();
            }
            catch (Exception ex) { Log("diag error: " + ex.Message); }
        }

        private static void Log(string s) { _lines++; Plugin.Log.LogInfo("[diag] " + s); }
    }
}
