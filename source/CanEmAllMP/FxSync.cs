using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>
    /// Explosions and shockwaves are drawn for everyone. Damage is never duplicated:
    ///  - explosion visuals (ParticlePoolManager.Spawn*Explosion) are pure effects; the damage they come with is
    ///    already applied through Enemy.Damage, which goes to the host once.
    ///  - shockwaves deal damage over time, so only the host spawns real ones; everyone else draws a 0-damage copy.
    /// </summary>
    public static class FxSync
    {
        public const byte SmallExplosion = 1, BigExplosion = 2, Shockwave = 3;
        public static bool FromNet;
        private static NetSession Net => MpManager.Net;

        public static void OnLocalFx(byte type, Vector3 pos, float radius)
        {
            if (FromNet || !CoopSync.Active || !Plugin.SyncEnemies.Value) return;
            Net.Begin(Msg.Fx).Put(type);
            Net.W.PutVec(pos);
            Net.W.Put(radius);
            Net.Send(DeliveryMethod.ReliableUnordered);
        }

        public static void Receive(int from, NetDataReader r)
        {
            byte type = r.GetByte(); var pos = r.GetVec(); float radius = r.GetFloat();
            if (Net.IsHost)
            {
                Net.Begin(Msg.Fx).Put(type); Net.W.PutVec(pos); Net.W.Put(radius);
                Net.SendExcept(from, DeliveryMethod.ReliableUnordered);
            }
            var pm = ParticlePoolManager.Instance;
            if (pm == null || !GameApi.InGame) return;
            FromNet = true;
            try
            {
                if (type == SmallExplosion) pm.SpawnSmallExplosion(pos);
                else if (type == BigExplosion) pm.SpawnBigExplosion(pos);
                else if (type == Shockwave) pm.SpawnShockwave(pos, radius, 0f);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("FX replay failed: " + e.Message); }
            finally { FromNet = false; }
        }
    }
}
