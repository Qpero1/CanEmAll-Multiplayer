using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// Dead players' ragdoll bodies (built by the models add-on) are simulated on the host only; the host sends
    /// every body's bone poses ~15x a second and joiners' bodies follow them (like cutelings and cans).
    public static class CorpseSync
    {
        private static NetSession Net => MpManager.Net;

        public class Pose { public float Time; public Vector3[] Pos; public Quaternion[] Rot; }
        /// owner player id -> latest pose from the host (joiners only)
        public static readonly Dictionary<int, Pose> Poses = new Dictionary<int, Pose>();

        public static bool HostSimulates => !Net.Connected || Net.IsHost || !CoopSync.Active;

        /// Host: send one body's bones (owner = whose body).
        public static void SendPose(int owner, Vector3[] pos, Quaternion[] rot)
        {
            if (!Net.IsHost || !CoopSync.Active || pos == null || pos.Length == 0) return;
            var w = Net.Begin(Msg.CorpsePose);
            w.Put(owner);
            w.Put((byte)pos.Length);
            for (int i = 0; i < pos.Length; i++)
            {
                w.Put(pos[i].x); w.Put(pos[i].y); w.Put(pos[i].z);
                w.Put(rot[i].x); w.Put(rot[i].y); w.Put(rot[i].z); w.Put(rot[i].w);
            }
            Net.Send(DeliveryMethod.Unreliable);
        }

        public static void Receive(NetDataReader r)
        {
            int owner = r.GetInt();
            int n = r.GetByte();
            var p = new Pose { Time = Time.time, Pos = new Vector3[n], Rot = new Quaternion[n] };
            for (int i = 0; i < n; i++)
            {
                p.Pos[i] = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                p.Rot[i] = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
            }
            Poses[owner] = p;
        }

        public static void Clear(int owner) => Poses.Remove(owner);
    }
}
