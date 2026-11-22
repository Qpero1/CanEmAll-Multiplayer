using System;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CanEmAllMP
{
    /// The mail tab (end-of-day screen): when anyone opens a letter, everyone's mail tab opens the same one,
    /// so the group reads together. Uses the game's own MailManager.SelectMail(id).
    public static class MailSync
    {
        private static NetSession Net => MpManager.Net;
        private static bool _applying;

        /// Postfix of MailManager.Open: our player clicked a letter.
        public static void LocalOpened(MailEntry entry)
        {
            if (_applying || !Net.Connected || entry == null) return;
            int id;
            try { var sm = entry.savedMail; if (sm == null) return; id = (int)sm.id; } catch { return; }
            Net.Begin(Msg.MailOpen).Put(id); Net.W.Put(Net.LocalId);
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        public static void Receive(int from, NetDataReader r)
        {
            int id = r.GetInt(); int who = r.GetInt();
            if (Net.IsHost)
            {
                who = from;
                Net.Begin(Msg.MailOpen).Put(id); Net.W.Put(who);
                Net.SendExcept(from, DeliveryMethod.ReliableOrdered);
            }
            var mm = MailManager.Instance;
            if (mm == null) return;
            var mail = (MailId)id;
            try { if (!mm.HasReceived(mail)) return; } catch { return; }   // a letter we don't have (shouldn't happen on a shared save)
            _applying = true;
            try { mm.SelectMail(mail); }
            catch (Exception e) { Plugin.Log.LogWarning("[MP] mail sync: " + e.Message); }
            finally { _applying = false; }
        }
    }
}
