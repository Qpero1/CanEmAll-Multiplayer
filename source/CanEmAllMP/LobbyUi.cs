using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static CanEmAllMP.MenuUi;

namespace CanEmAllMP
{
    /// The LOBBY screen (main menu): after HOST GAME the session is open and players can connect while the host
    /// waits; START GAME loads the save and everyone in the lobby gets it once the host's game is up.
    /// Joiners see the same screen ("waiting for the host to start") until the host's save arrives.
    /// Joining mid-game still works as before.
    public static class LobbyUi
    {
        private const int MaxRows = 8;
        private static NetSession Net => MpManager.Net;

        private static GameObject _screen, _startBtn;
        private static TMP_Text _save, _addr, _status, _header, _leaveLbl;
        private class Row { public GameObject Go, Kick; public Image Dot; public TMP_Text Name, Ping, KickLabel; public int Id = -1; public float ConfirmUntil; }
        private static readonly List<Row> _rows = new List<Row>();
        private static float _nextRefresh;

        public static bool Open => _screen != null && _screen.activeSelf;
        /// The host is running a session but hasn't started the game yet: joiners are let in to wait.
        public static bool HostInLobby => Net.IsHost && !GameApi.InGame;

        public static void Show()
        {
            try
            {
                if (_screen == null) Build();
                if (_screen == null) return;
                var m = Menu.Instance;
                if (m != null) m.menuPanel.SetActive(false);
                _screen.SetActive(true);
                Refresh();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[UI] lobby: " + e);
                try { if (_screen != null) UnityEngine.Object.Destroy(_screen); } catch { }
                _screen = null;
            }
        }

        public static void Tick()
        {
            if (!Open) return;
            if (GameApi.InGame) { _screen.SetActive(false); return; }
            if (!Net.Active)
            {
                // left, kicked, or couldn't connect: back to the MULTIPLAYER screen (its status line says why)
                _screen.SetActive(false);
                MenuUi.ReturnFromLobby();
                return;
            }
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.25f;
            Refresh();
        }

        private static void Build()
        {
            var m = Menu.Instance;
            var canvas = m.menuPanel.transform.parent;
            var settings = Child(canvas, "Settings");
            var scr = Clone(settings.gameObject, canvas);
            scr.name = "MP_Lobby";
            scr.SetActive(false);
            scr.transform.SetSiblingIndex(settings.GetSiblingIndex() + 1);
            _screen = scr;
            var st = scr.transform;

            var title = Label(Child(st, "Title")); if (title != null) title.text = "LOBBY";
            Kill(Child(st, "Audio")); Kill(Child(st, "Graphics"));

            var tabs = Child(st, "Buttons");
            _startBtn = Child(tabs, "General")?.gameObject;
            Kill(Child(tabs, "Audio")); Kill(Child(tabs, "Graphics"));
            if (_startBtn != null)
            {
                var t = Label(_startBtn.transform);
                if (t != null) { t.text = "START GAME"; t.enableWordWrapping = false; var r = RT(t.transform); if (r != null) r.sizeDelta = new Vector2(150f, r.sizeDelta.y); }
                var play = SpriteOf(Child(Find(m.menuPanel.transform, "NewGame"), "Icon"));
                var ic = Child(_startBtn.transform, "Icon"); if (ic != null && play != null) ic.GetComponent<Image>().sprite = play;
                OnClick(_startBtn, OnStart);
            }
            var back = Child(st, "Back");
            _leaveLbl = Label(back);
            if (_leaveLbl != null) _leaveLbl.text = "LEAVE";
            OnClick(back.gameObject, OnLeave);

            var box = Child(st, "General");
            var tLine = Child(box, "Line");
            var tRow = Child(box, "InvertYAxis") ?? Child(box, "Headbob");
            var ddImg = Find(tRow, "Dropdown")?.GetComponent<Image>();
            var old = new List<Transform>();
            for (int i = 0; i < box.childCount; i++) old.Add(box.GetChild(i));

            Line(box, tLine);
            _save = TextRow(box, tRow, "Save");
            Line(box, tLine);
            _addr = TextRow(box, tRow, "Address");
            Line(box, tLine);
            _status = TextRow(box, tRow, "Status");
            Line(box, tLine);
            var head = TextRow(box, tRow, "Players");
            _header = Label(Child(box, "MP_Players"));
            head.text = "PING"; head.fontSize = 12;
            Line(box, tLine);

            _rows.Clear();
            for (int i = 0; i < MaxRows; i++)
            {
                var row = NewRow(box, tRow, "Player", false);
                row.name = "MP_LobbyRow" + i;
                var rr = RT(row); rr.sizeDelta = new Vector2(rr.sizeDelta.x, 26f);
                var r = new Row { Go = row.gameObject };
                var dot = Clone(tLine.gameObject, row);
                Place(RT(dot), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(14f, 14f));
                r.Dot = dot.GetComponent<Image>();
                if (ddImg != null) { r.Dot.sprite = ddImg.sprite; r.Dot.type = Image.Type.Sliced; r.Dot.pixelsPerUnitMultiplier = ddImg.pixelsPerUnitMultiplier * 2f; }
                r.Name = Label(row);
                var nr = RT(r.Name.transform);
                nr.anchoredPosition = new Vector2(nr.anchoredPosition.x + 22f, nr.anchoredPosition.y);
                nr.sizeDelta = new Vector2(400f, nr.sizeDelta.y);
                r.Name.fontSize = 15; r.Name.enableWordWrapping = false; r.Name.overflowMode = TextOverflowModes.Ellipsis;
                r.Ping = Clone(r.Name.gameObject, row).GetComponent<TMP_Text>();
                r.Ping.gameObject.name = "Ping";
                Place(RT(r.Ping.transform), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-100f, 0f), new Vector2(120f, 24f));
                r.Ping.alignment = TextAlignmentOptions.MidlineRight;

                // KICK (host): the BACK button, shrunk by sizing (its hover animation resets scale)
                var k = Clone(back.gameObject, Child(row, "Image"));
                k.name = "Kick";
                Kill(Child(k.transform, "Icon"));
                const float ks = 0.5f;
                var kt = Label(k.transform);
                if (kt != null) { kt.text = "KICK"; Stretch(RT(kt.transform)); kt.alignment = TextAlignmentOptions.Center; kt.fontSize = kt.fontSize * 0.65f; }
                var ki = k.GetComponent<Image>(); if (ki != null) ki.pixelsPerUnitMultiplier = ki.pixelsPerUnitMultiplier / ks;
                var rc = RT(Child(k.transform, "Raycast")); if (rc != null) rc.sizeDelta = rc.sizeDelta * ks;
                Place(RT(k), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(170f * ks, 64f * ks));
                r.Kick = k; r.KickLabel = kt;
                OnClick(k, () => OnKick(r));
                _rows.Add(r);
            }

            foreach (var o in old) Kill(o);
            Strip(scr);
            Plugin.Log.LogInfo("[UI] lobby built");
        }

        private static void OnStart()
        {
            if (!Net.IsHost || GameApi.InGame) return;
            var m = Menu.Instance;
            if (m == null) return;
            bool exists = SaveSlots.Exists(SaveSlots.Current); // SaveSlots.Use already loaded it into memory
            _screen.SetActive(false);
            m.menuPanel.SetActive(true);
            int waiting = Net.Players.Count - 1;
            MpManager.Instance?.Feed(waiting > 0 ? $"Starting - {waiting} player(s) will join once the game has loaded" : "Starting - friends can still join mid-game");
            if (exists) m.ContinueGame(); else m.NewGame();
        }

        private static void OnKick(Row r)
        {
            if (!Net.IsHost || r.Id <= 0) return;
            if (Time.unscaledTime > r.ConfirmUntil)
            {
                r.ConfirmUntil = Time.unscaledTime + 3f; // click again to confirm
                if (r.KickLabel != null) r.KickLabel.text = "SURE?";
                return;
            }
            string name = Net.NameOf(r.Id);
            Net.Kick(r.Id);
            MpManager.Instance?.Toast(name + " was kicked");
            r.ConfirmUntil = 0f;
        }

        private static void OnLeave()
        {
            MpManager.Instance?.Disconnect();
        }

        private static void Refresh()
        {
            try
            {
                bool host = Net.IsHost;
                if (_startBtn != null && _startBtn.activeSelf != host) _startBtn.SetActive(host);
                if (_leaveLbl != null) _leaveLbl.text = host ? "CLOSE" : "LEAVE";

                if (_save != null) _save.text = host ? SaveSlots.Describe(SaveSlots.Current) : "Host's save";
                if (_addr != null) _addr.text = host ? $"UDP port {Plugin.Port.Value} - friends join with your public IP" : $"{Plugin.LastAddress.Value}:{Plugin.Port.Value}";
                if (_status != null)
                {
                    string st;
                    if (host) st = Net.Players.Count > 1 ? "Ready - press START GAME" : "Waiting for players (you can start any time)";
                    else if (!Net.Connected) st = Net.Status ?? "Connecting...";
                    else if (HostSave.WaitingForSave) st = "Waiting for the host to start";
                    else st = Net.Status ?? "";
                    _status.text = st;
                }

                var ids = new List<int>(Net.Players.Keys);
                ids.Sort();
                int max = host ? Net.MaxPlayers : 0;
                string cap = host && max > 0 ? "/" + max : "";
                string more = ids.Count > MaxRows ? $"  (+{ids.Count - MaxRows} more)" : "";
                if (_header != null) _header.text = $"Players ({ids.Count}{cap}){more}";
                for (int i = 0; i < _rows.Count; i++)
                {
                    var r = _rows[i];
                    bool show = i < ids.Count;
                    if (r.Go.activeSelf != show) r.Go.SetActive(show);
                    if (!show) continue;
                    int id = ids[i];
                    r.Dot.color = Palette.ForPlayer(id);
                    r.Name.text = Net.NameOf(id) + (id == 0 ? " (host)" : "") + (id == Net.LocalId ? " (you)" : "");
                    r.Ping.text = PauseUi.PingText(id);
                    if (r.Id != id) { r.Id = id; r.ConfirmUntil = 0f; }
                    bool kickable = host && id != Net.LocalId;
                    if (r.Kick.activeSelf != kickable) r.Kick.SetActive(kickable);
                    if (kickable && r.KickLabel != null && Time.unscaledTime > r.ConfirmUntil && r.KickLabel.text != "KICK") r.KickLabel.text = "KICK";
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[UI] lobby refresh: " + e.Message); }
        }
    }
}
