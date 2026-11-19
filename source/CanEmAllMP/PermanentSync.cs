using System;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>
    /// Selling the factory and the permanent upgrade screen are shared.
    ///
    /// PermanentUpgradeManager.Sell (host only, see FactorySync.AllowSell) awards CalculateRebirthPoints(totalMoney)
    /// to SaveData.upgradePoints, resets the run in place (money, skills, scene, player, weapons...) and shows
    /// permanentUpgradePanel. When the host sells, every joiner runs the same Sell() so everyone lands on that screen.
    ///
    /// PermanentUpgrade.OnClick: if not bought and upgradePoints >= skill.price -> points -= price,
    /// unlockedPermanentUpgrades.Add(skillId), apply. Host-authoritative like the skill tree: a joiner's click asks
    /// the host, the host buys it, everyone applies it.
    /// </summary>
    public static class PermanentSync
    {
        private static NetSession Net => MpManager.Net;

        /// A sell / purchase is being replayed from the network: let the game's own code run.
        public static bool Applying;

        // ================================================================ selling

        /// Postfix of PermanentUpgradeManager.Sell on the host: take everyone along.
        public static void OnHostSold()
        {
            if (Applying || !CoopSync.Active || !Net.IsHost) return;
            Net.Begin(Msg.FactorySold);
            Net.Send(DeliveryMethod.ReliableOrdered);
            MpManager.Instance?.Feed("Factory sold");
        }

        private static void ClientSell()
        {
            var pum = PermanentUpgradeManager.Instance;
            if (pum == null || !GameApi.InGame) { Plugin.Log.LogWarning("[MP] host sold the factory but we're not in game"); return; }
            RunAbsorbingMoney(() => pum.Sell());
            MpManager.Instance?.Toast("The host sold the factory");
        }

        // ================================================================ permanent upgrades

        public static PermanentUpgrade Find(int skillId)
        {
            try
            {
                var nodes = PermanentUpgradeManager.Instance?.nodes;
                if (nodes == null) return null;
                foreach (var n in nodes)
                    if (n != null && n.skill != null && (int)n.skill.skillId == skillId) return n;
            }
            catch { }
            return null;
        }

        /// Prefix of PermanentUpgrade.OnClick. false = handled here (a joiner asks the host instead).
        public static bool OnClickPrefix(PermanentUpgrade up)
        {
            if (Applying || !CoopSync.Active || Net.IsHost || up?.skill == null) return true;
            if (up.IsPurchased || !up.CanPurchase) return true;   // let the game show its own feedback
            Net.Begin(Msg.PermBuyRequest).Put((int)up.skill.skillId);
            Net.Send(DeliveryMethod.ReliableOrdered);
            return false;
        }

        /// Postfix of PermanentUpgrade.OnClick on the host: a purchase happened -> everyone gets it.
        public static void OnClickPostfix(PermanentUpgrade up, bool wasPurchased)
        {
            if (Applying || !CoopSync.Active || !Net.IsHost || up?.skill == null) return;
            if (wasPurchased || !up.IsPurchased) return;
            Broadcast((int)up.skill.skillId, Net.LocalId);
        }

        private static void Broadcast(int id, int who)
        {
            var w = Net.Begin(Msg.PermBought); w.Put(id); w.Put(who);
            Net.Send(DeliveryMethod.ReliableOrdered);
            MpManager.Instance?.Feed($"{Net.NameOf(who)} bought {(SkillId)id}");
        }

        private static void HostBuyRequest(int from, int id)
        {
            var up = Find(id);
            string why = up?.skill == null ? "not found on host" : up.IsPurchased ? "already bought"
                       : !up.CanPurchase ? "not enough points" : null;
            if (why == null)
            {
                Applying = true;
                try { up.OnClick(); } catch (Exception e) { Plugin.Log.LogError(e); }
                finally { Applying = false; }
                if (!up.IsPurchased) why = "couldn't buy on host";
            }
            if (why != null)
            {
                Plugin.Log.LogInfo($"[MP] permanent upgrade {(SkillId)id} for {Net.NameOf(from)} refused: {why}");
                var w = Net.Begin(Msg.SkillBuyResult); w.Put(id); w.Put(why);
                Net.SendTo(from, DeliveryMethod.ReliableOrdered);
                return;
            }
            Broadcast(id, from);
        }

        /// Joiner: the host bought it; buy it locally too (points are topped up first, the host's are the real ones).
        private static void ClientApply(int id)
        {
            var up = Find(id);
            if (up?.skill == null || up.IsPurchased) return;
            var save = SaveManager.Instance?.CurrentSaveData;
            Applying = true;
            try
            {
                if (save != null) save.upgradePoints += up.skill.price; // OnClick takes it right back
                RunAbsorbingMoney(() => up.OnClick());
                if (!up.IsPurchased) Plugin.Log.LogWarning($"[MP] permanent upgrade {(SkillId)id} didn't apply");
                try { PermanentUpgradeManager.Instance?.UpdatePointsText(); } catch { }
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
            finally { Applying = false; }
        }

        // ================================================================ network

        public static void Receive(int from, Msg type, NetDataReader r)
        {
            switch (type)
            {
                case Msg.FactorySold when Net.IsClient:
                    Applying = true;
                    try { ClientSell(); } catch (Exception e) { Plugin.Log.LogError(e); }
                    finally { Applying = false; }
                    break;
                case Msg.PermBuyRequest when Net.IsHost:
                    HostBuyRequest(from, r.GetInt());
                    break;
                case Msg.PermBought when Net.IsClient:
                {
                    int id = r.GetInt(), who = r.GetInt();
                    ClientApply(id);
                    MpManager.Instance?.Feed($"{Net.NameOf(who)} bought {(SkillId)id}");
                    break;
                }
            }
        }

        /// Money the game changes while replaying the host's action isn't ours to report: the host's own copy
        /// of the same action already changed the shared wallet.
        private static void RunAbsorbingMoney(Action a)
        {
            long before = MoneyManager.Instance != null ? MoneyManager.Instance.Money : 0;
            try { a(); }
            finally
            {
                long after = MoneyManager.Instance != null ? MoneyManager.Instance.Money : before;
                if (after != before) MpManager.Instance?.AbsorbLocalMoney(after - before);
            }
        }
    }
}
