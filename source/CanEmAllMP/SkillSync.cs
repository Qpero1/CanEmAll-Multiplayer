using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using Il2CppInterop.Runtime;

namespace CanEmAllMP
{
    /// <summary>
    /// Skill tree upgrades are shared: when anyone buys a node, every player gets it.
    /// The buyer pays once (their SetMoney goes through the shared wallet); everyone else
    /// gets the node applied for free via the same path the game uses when loading a save.
    /// </summary>
    public static class SkillSync
    {
        private static NetSession Net => MpManager.Net;
        public static bool Applying;

        /// Every node in the skill tree. SkillManager.skillNodes turned out not to hold the real tree, so prefer the
        /// skill tree screen's own list, then SkillManager, then every SkillNode object in the scene.
        private static List<SkillNode> _nodes = new List<SkillNode>();
        private static float _nodesAt = -100f;

        public static List<SkillNode> AllNodes()
        {
            if (_nodes.Count > 0 && Time.unscaledTime - _nodesAt < 5f && _nodes[0] != null) return _nodes;
            var list = new List<SkillNode>();
            try
            {
                var view = GameManager.Instance?.skillTreeView;
                if (view?.nodes != null) foreach (var n in view.nodes) if (n != null && n.skill != null) list.Add(n);
                // the final node (and the root) are kept outside the nodes list: without them buying the last
                // upgrade never synced ("not found on host")
                foreach (var extra in new[] { view?.masterKeyNode, view?.rootNode })
                    if (extra != null && extra.skill != null && !list.Exists(n => n.Pointer == extra.Pointer)) list.Add(extra);
            }
            catch { }
            if (list.Count == 0)
            {
                try
                {
                    var sm = SkillManager.Instance;
                    if (sm?.skillNodes != null) foreach (var n in sm.skillNodes) if (n != null && n.skill != null) list.Add(n);
                }
                catch { }
            }
            if (list.Count == 0)
            {
                try
                {
                    foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<SkillNode>()))
                    {
                        var n = o?.TryCast<SkillNode>();
                        if (n != null && n.skill != null && n.gameObject.scene.IsValid()) list.Add(n);
                    }
                }
                catch { }
            }
            _nodes = list; _nodesAt = Time.unscaledTime;
            if (_logged < 3) { _logged++; Plugin.Log.LogInfo($"[MP] skill tree: {list.Count} nodes found"); }
            return _nodes;
        }
        private static int _logged;

        public static SkillNode FindNode(int skillId)
        {
            foreach (var n in AllNodes())
                if (n != null && n.skill != null && (int)n.skill.skillId == skillId) return n;
            _nodesAt = -100f; // force a fresh search next time
            return null;
        }

        /// Local purchase happened (postfix on SkillNode.OnClick).
        public static void OnLocalPurchase(SkillNode node)
        {
            if (Applying || !CoopSync.Active || node?.skill == null) return;
            int id = (int)node.skill.skillId;
            Net.Begin(Msg.SkillPurchased).Put(id);
            Net.W.Put(Net.LocalId);
            Net.Send(DeliveryMethod.ReliableOrdered);
        }

        /// Joiner clicked an unbought node: if it's affordable and unlocked, ask the host instead of buying locally.
        public static bool ClientRequestBuy(SkillNode node)
        {
            bool locked = false;
            try { locked = node.IsLocked; } catch { }
            long money = MoneyManager.Instance != null ? MoneyManager.Instance.Money : -1;
            Plugin.Log.LogInfo($"[MP] buy click {node.skill.skillId}: locked={locked} money={money} price={node.skill.price}");
            if (locked || money < node.skill.price) return false; // let the game show its own "can't buy" feedback
            Net.Begin(Msg.SkillBuyRequest).Put((int)node.skill.skillId);
            Net.Send(DeliveryMethod.ReliableOrdered);
            return true;
        }

        /// Host: a joiner wants to buy. First come, first served; pays from the shared wallet.
        public static void HostReceiveBuyRequest(int from, NetDataReader r)
        {
            int id = r.GetInt();
            var node = FindNode(id);
            var mm = MoneyManager.Instance;
            if (node?.skill != null && node.skill.isPurchased)
            {
                // the host already owns it (e.g. it didn't sync before): just give it to them
                Plugin.Log.LogInfo($"[MP] buy request {(SkillId)id} from {Net.NameOf(from)}: host already owns it, sending it over");
                Net.Begin(Msg.SkillPurchased).Put(id);
                Net.W.Put(Net.LocalId);
                Net.SendTo(from, DeliveryMethod.ReliableOrdered);
                return;
            }
            string why = node?.skill == null ? "not found on host" : mm == null ? "host not in game"
                       : mm.Money < node.skill.price ? "not enough money" : null;
            Plugin.Log.LogInfo($"[MP] buy request {(SkillId)id} from {Net.NameOf(from)}: {why ?? "ok"}");
            if (why != null)
            {
                Net.Begin(Msg.SkillBuyResult).Put(id); Net.W.Put(why);
                Net.SendTo(from, DeliveryMethod.ReliableOrdered);
                return;
            }
            int price = node.skill.price;
            mm.SetMoney(mm.Money - price);
            if (!Apply(id))
            {
                Net.Begin(Msg.SkillBuyResult).Put(id); Net.W.Put("couldn't apply on host");
                Net.SendTo(from, DeliveryMethod.ReliableOrdered);
                return;
            }
            Net.Begin(Msg.SkillPurchased).Put(id);
            Net.W.Put(from);
            Net.Send(DeliveryMethod.ReliableOrdered);
            MpManager.Instance?.Feed($"{Net.NameOf(from)} bought {(SkillId)id}");
        }

        /// Host -> a newly joined client: everything the host has bought.
        public static void SendFullState(int toPlayer)
        {
            var ids = new List<int>();
            foreach (var n in AllNodes())
                if (n != null && n.skill != null && n.skill.isPurchased) ids.Add((int)n.skill.skillId);
            var w = Net.Begin(Msg.SkillState);
            w.Put(ids.Count);
            foreach (var id in ids) w.Put(id);
            Net.SendTo(toPlayer, DeliveryMethod.ReliableOrdered);
        }

        public static void Receive(int from, Msg type, NetDataReader r)
        {
            if (type == Msg.SkillPurchased)
            {
                int id = r.GetInt();
                int who = r.GetInt();
                if (Net.IsHost)
                {
                    who = from;
                    Net.Begin(Msg.SkillPurchased).Put(id); Net.W.Put(who);
                    Net.SendExcept(from, DeliveryMethod.ReliableOrdered);
                }
                bool ok = Apply(id);
                Plugin.Log.LogInfo($"[MP] purchase {(SkillId)id} by {Net.NameOf(who)} applied={ok}");
                if (ok) MpManager.Instance?.Feed($"{Net.NameOf(who)} bought {(SkillId)id}");
            }
            else if (type == Msg.SkillState)
            {
                // a joiner loading the host's save gets these skills from the save itself
                if (HostSave.WaitingForSave || HostSave.UsingHostSave) return;
                int n = r.GetInt();
                int applied = 0;
                // apply in a few passes so requirements bought later in the list still work out
                var ids = new List<int>();
                for (int i = 0; i < n; i++) ids.Add(r.GetInt());
                foreach (var id in ids) if (Apply(id)) applied++;
                if (applied > 0) MpManager.Instance?.Note($"Synced {applied} upgrade(s) from the host");
            }
        }

        /// Give the local player a skill without paying for it. Returns true if it was newly applied.
        public static bool Apply(int skillId)
        {
            var node = FindNode(skillId);
            if (node == null || node.skill == null || node.skill.isPurchased) return false;
            Applying = true;
            try
            {
                node.skill.isPurchased = true;
                node.ApplyInitialState(); // purchased visuals + RefreshLockState + skill.ApplySkill()

                var save = SaveManager.Instance?.CurrentSaveData;
                var list = save?.unlockedSkills;
                if (list != null && !list.Contains((SkillId)skillId)) list.Add((SkillId)skillId);

                bool refreshed = false;
                try { var view = node.skillTreeView; if (view != null) { view.NodePurchased(node); refreshed = true; } } catch { }
                if (!refreshed)
                    foreach (var n in AllNodes()) { try { n.RefreshLockState(); } catch { } }
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"Applying skill {(SkillId)skillId} failed: {e}");
                return false;
            }
            finally { Applying = false; }
        }
    }
}
