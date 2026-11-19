using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    /// <summary>
    /// "Sell factory" screen (PermanentUpgradeManager): joiners see the host's numbers, only the host can sell.
    ///
    /// UpdateOfferTexts shows "$" + SaveData.totalMoney and CalculateRebirthPoints(totalMoney);
    /// UpdatePointsText shows SaveData.upgradePoints. The host sends both values; joiners copy them into their
    /// in-memory save (joiners never write their save while connected) and refresh the texts.
    /// </summary>
    public static class FactorySync
    {
        private static NetSession Net => MpManager.Net;

        private static long _sentTotal = -1, _sentKilled = -1, _sentCans = -1;
        private static int _sentPoints = -1;
        private static float _timer, _lastSentAt;

        private static bool _haveHost;
        private static long _hostTotal;
        private static int _hostPoints;
        private static long _hostKilled = -1, _hostCans = -1; // the board: creatures killed / cans (children fed)
        private static float _lastToast;

        public static void Reset()
        {
            _sentTotal = -1; _sentPoints = -1; _sentKilled = -1; _sentCans = -1; _timer = 0; _haveHost = false;
            _hostKilled = -1; _hostCans = -1;
        }

        public static void Tick()
        {
            if (!CoopSync.Active) { if (_haveHost || _sentTotal >= 0) Reset(); return; }
            var save = SaveManager.Instance?.CurrentSaveData;
            if (save == null) return;

            if (Net.IsHost)
            {
                _timer += Time.unscaledDeltaTime;
                if (_timer < 0.5f) return;
                _timer = 0;
                long total = save.totalMoney;
                int points = save.upgradePoints;
                long killed = save.totalKilled, cans = save.totalCans;
                string life = LifetimeKey(save);
                if (total == _sentTotal && points == _sentPoints && killed == _sentKilled && cans == _sentCans
                    && life == _sentLife && Time.unscaledTime - _lastSentAt < 5f) return;
                _sentLife = life;
                _sentTotal = total; _sentPoints = points; _sentKilled = killed; _sentCans = cans; _lastSentAt = Time.unscaledTime;
                var w = Net.Begin(Msg.HostStats); w.Put(total); w.Put(points); w.Put(killed); w.Put(cans);
                PutLifetime(w, save);
                Net.Send(DeliveryMethod.ReliableOrdered);
            }
            else if (_haveHost)
            {
                if (save.totalMoney != _hostTotal || save.upgradePoints != _hostPoints)
                {
                    // our own earnings would make the copy drift: keep showing the host's numbers
                    save.totalMoney = _hostTotal;
                    save.upgradePoints = _hostPoints;
                    RefreshTexts();
                }
                if (_hostKilled >= 0 && (save.totalKilled != _hostKilled || save.totalCans != _hostCans))
                {
                    // the big board (creatures killed / children fed) shows the host's totals
                    save.totalKilled = _hostKilled;
                    save.totalCans = _hostCans;
                    try { GameManager.Instance?.UpdateScreenText(); } catch { }
                }
            }
        }

        public static void Receive(NetDataReader r)
        {
            _hostTotal = r.GetLong();
            _hostPoints = r.GetInt();
            if (r.AvailableBytes >= 16) { _hostKilled = r.GetLong(); _hostCans = r.GetLong(); }
            if (r.AvailableBytes >= 48)
            {
                // the host's lifetime stats (what the ending shows): copy into our in-memory save
                long k = r.GetLong(), c = r.GetLong(), m = r.GetLong(), g = r.GetLong(), gw = r.GetLong();
                int days = r.GetInt(), sold = r.GetInt();
                try
                {
                    var ls = SaveManager.Instance?.CurrentSaveData?.lifetimeStats;
                    if (ls != null)
                    {
                        ls.totalKilled = k; ls.totalCans = c; ls.totalMoneyEarned = m; ls.totalGambled = g;
                        ls.totalGamblingWinnings = gw; ls.totalDaysWorked = days; ls.totalFactoriesSold = sold;
                    }
                }
                catch { }
            }
            _haveHost = true;
        }

        private static string _sentLife = "";

        private static string LifetimeKey(SaveData save)
        {
            try
            {
                var ls = save.lifetimeStats;
                return ls == null ? "" : $"{ls.totalKilled}|{ls.totalCans}|{ls.totalMoneyEarned}|{ls.totalGambled}|{ls.totalGamblingWinnings}|{ls.totalDaysWorked}|{ls.totalFactoriesSold}";
            }
            catch { return ""; }
        }

        private static void PutLifetime(NetDataWriter w, SaveData save)
        {
            LifetimeStats ls = null;
            try { ls = save.lifetimeStats; } catch { }
            if (ls == null) return;
            w.Put(ls.totalKilled); w.Put(ls.totalCans); w.Put(ls.totalMoneyEarned); w.Put(ls.totalGambled);
            w.Put(ls.totalGamblingWinnings); w.Put(ls.totalDaysWorked); w.Put(ls.totalFactoriesSold);
        }

        private static void RefreshTexts()
        {
            var pum = PermanentUpgradeManager.Instance;
            if (pum == null) return;
            try { pum.UpdateOfferTexts(); } catch { }
            try { pum.UpdatePointsText(); } catch { }
        }

        /// Prefix of PermanentUpgradeManager.Sell. false = refuse.
        public static bool AllowSell()
        {
            if (!CoopSync.Active || Net.IsHost || PermanentSync.Applying) return true; // Applying = the host's sell, replayed
            if (Time.unscaledTime - _lastToast > 1.5f)
            {
                _lastToast = Time.unscaledTime;
                MpManager.Instance?.Toast("Only the host can sell the factory");
            }
            return false;
        }
    }
}
