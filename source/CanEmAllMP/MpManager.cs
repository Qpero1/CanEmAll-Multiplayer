using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using Il2CppInterop.Runtime.Attributes;

namespace CanEmAllMP
{
    /// <summary>Lives on a DontDestroyOnLoad object; drives networking every frame and draws the F8 panel.</summary>
    public class MpManager : MonoBehaviour
    {
        public MpManager(IntPtr ptr) : base(ptr) { }

        public static MpManager Instance;
        public static readonly NetSession Net = new NetSession();
        public static bool MenuOpen;

        private readonly Dictionary<int, RemoteAvatar> _avatars = new Dictionary<int, RemoteAvatar>();
        private float _sendTimer, _timerSyncTimer, _enemyTimer;
        private string _addrField, _portField, _nameField, _codeField;
        private CursorLockMode _prevLock;
        private bool _prevVisible;
        private readonly List<string> _feed = new List<string>();
        private readonly List<float> _feedTime = new List<float>();

        // shared money bookkeeping
        private double _lastKnownMoney = double.NaN;
        private double _lastBroadcastMoney = double.NaN;
        private float _moneyResend;
        private int _tickErr;

        public void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            _addrField = Plugin.LastAddress.Value;
            _codeField = Plugin.LastCode.Value;
            _portField = Plugin.Port.Value.ToString();
            _nameField = Plugin.PlayerName.Value;
            Net.LocalColor = Plugin.PlayerColor.Value;
            Net.OnPlayerColor += id => { if (_avatars.TryGetValue(id, out var av)) av.RefreshColor(); };

            Net.OnPlayerJoined += id => { if (Net.IsHost) Extras.ResendLighting(); if (id != Net.LocalId) { GetAvatar(id); Feed($"{Net.NameOf(id)} joined"); Toast($"{Net.NameOf(id)} joined"); } OnSomeoneJoined(id); };
            Net.OnPlayerLeft += (id, name) => { PlayerStats.PlayerLeft(id, name); RemoveAvatar(id); if (Net.IsHost) { EnemySync.HostPlayerLeft(id); CanSync.HostPlayerLeft(id); CrateSync.HostPlayerLeft(id); ContinueSync.PlayerLeft(id); } Extras.PlayerLeft(id); GambleSync.PlayerLeft(id); var how = Net.LeftWasKick ? "was kicked" : "left"; Feed($"{name} {how}"); Toast($"{name} {how}"); };
            Net.OnConnectedToHost += () => { Feed("Connected to host"); _lastKnownMoney = double.NaN; HostSave.OnConnected(); };
            Net.OnDisconnected += reason =>
            {
                ClearAvatars(); EnemySync.Reset(); CanSync.Reset(); CrateSync.Reset(); CoopSync.Reset(); ContinueSync.Reset(); Extras.Reset(); GambleSync.Reset(); FactorySync.Reset();
                Palette.RefreshCursor();
                bool hostLeft = reason == DisconnectReason.RemoteConnectionClose;
                var msg = !string.IsNullOrEmpty(Net.LastRejectReason) ? Net.LastRejectReason
                        : hostLeft ? "Host left"
                        : reason == DisconnectReason.ConnectionFailed ? (!NetSession.UseDirect ? "Couldn't reach the host" : "Couldn't reach the host (check the IP / port forwarding)")
                        : "Lost connection to host (" + reason + ")";
                Feed(msg); Toast(msg);
                if (HostSave.UsingHostSave && GameManager.Instance != null && !HostSave.InMainMenu)
                {
                    // the joiner has no save of their own loaded: send them back to the main menu
                    // (GameManager.Exit resets timeScale and reloads the scene; Menu.Awake restores their own save)
                    if (MenuOpen) CloseMenuForGameplay();
                    try { GameManager.Instance.Exit(); } catch (Exception e) { Plugin.Log.LogError("Exit to menu failed: " + e); }
                    return;
                }
                if (HostSave.UsingHostSave) Feed("You were on the host's save, so nothing was saved.");
            };
            Net.OnLobbyCode += code =>
            {
                try { GUIUtility.systemCopyBuffer = code; } catch { }
                Note($"Lobby code: {code} (copied to clipboard)");
                Toast("Lobby code " + code);
            };
            Net.OnMessage += HandleMessage;
            Net.HostAcceptsJoins = () => GameApi.InGame || LobbyUi.HostInLobby;
        }

        // Other players' avatars (body capsule for friendly fire, held weapon models, ragdolls) must never push our
        // own player: when everyone spawns on the same spot in the elevator, that shoved people through the walls.
        // Bullets still hit them (raycasts don't care about IgnoreCollision).
        private IntPtr _ignorePlayer;
        private float _ignoreFastUntil, _nextIgnore;
        private readonly List<Collider> _mine = new List<Collider>();

        private void IgnoreAvatarCollisions()
        {
            if (_avatars.Count == 0) return;
            var pl = Player.Instance;
            if (pl == null) return;
            float now = Time.unscaledTime;
            if (pl.Pointer != _ignorePlayer) { _ignorePlayer = pl.Pointer; _ignoreFastUntil = now + 3f; } // new day / new scene
            if (now < _nextIgnore && now > _ignoreFastUntil) return;
            _nextIgnore = now + 0.25f;
            _mine.Clear();
            foreach (var c in pl.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Collider>(), true))
            {
                var col = c?.TryCast<Collider>();
                if (col != null) _mine.Add(col);
            }
            if (_mine.Count == 0) return;
            foreach (var a in _avatars.Values)
            {
                if (a.Root == null) continue;
                foreach (var c in a.Root.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Collider>(), true))
                {
                    var col = c?.TryCast<Collider>();
                    if (col == null) continue;
                    foreach (var m in _mine) Physics.IgnoreCollision(m, col, true);
                }
            }
        }

        private bool _wasInGame;
        private string _toast;
        private float _toastUntil;

        /// Big centred message for ~2.5 seconds.
        public void Toast(string text)
        {
            _toast = text;
            _toastUntil = Time.unscaledTime + 2.5f;
        }

        /// A local money change that must not be reported to the host (its own copy of the same event pays).
        public void AbsorbLocalMoney(long delta)
        {
            if (!double.IsNaN(_lastKnownMoney)) _lastKnownMoney += delta;
        }

        private void LeaveSession()
        {
            bool wasHost = Net.IsHost;
            Net.Shutdown();          // sends disconnects: clients see "Host left", host sees "<name> left"
            ClearAvatars(); EnemySync.Reset(); CanSync.Reset(); CrateSync.Reset(); CoopSync.Reset(); ContinueSync.Reset(); Extras.Reset(); GambleSync.Reset(); FactorySync.Reset();
            HostSave.OnMainMenu();   // a joiner gets their own save back
            Feed(wasHost ? "Stopped hosting (back on the main menu)" : "Left the session (back on the main menu)");
        }

        /// Log only (combat details, setup chatter): kept out of the on-screen feed so it stays quiet.
        public void Note(string s) => Plugin.Log.LogInfo("[MP] " + s);

        public void Feed(string s)
        {
            Plugin.Log.LogInfo("[MP] " + s);
            _feed.Add(s); _feedTime.Add(Time.unscaledTime);
            if (_feed.Count > 6) { _feed.RemoveAt(0); _feedTime.RemoveAt(0); }
        }

        // ------------------------------------------------------------------ update

        public void Update()
        {
            try { Net.Poll(); } catch (Exception e) { Plugin.Log.LogError(e); }

            if (MenuKeyPressed()) ToggleMenu();
            try { Loc.Tick(); } catch (Exception e) { if (_uiErr++ < 3) Plugin.Log.LogError(e); }
            try { SteamJoin.Tick(); } catch (Exception e) { if (_uiErr++ < 3) Plugin.Log.LogError(e); }
            try { Palette.TickSystemCursor(Net.Active ? Net.MyColor : (Plugin.PlayerColor.Value < 0 ? 0 : Plugin.PlayerColor.Value)); } catch { }
            CanDiagnostics.Tick();
            if (!_tweensSized) SizeTweens();
            try { FlushLobby(); } catch (Exception e) { if (_uiErr++ < 3) Plugin.Log.LogError(e); }
            try { LobbyUi.Tick(); } catch (Exception e) { if (_uiErr++ < 3) Plugin.Log.LogError(e); }
            try { PlayerStats.Tick(); } catch (Exception e) { if (_uiErr++ < 3) Plugin.Log.LogError(e); }
            try { TickNotices(); } catch (Exception e) { if (_uiErr++ < 3) Plugin.Log.LogError(e); }
            try { SpectateUi.Tick(); } catch (Exception e) { if (_uiErr++ < 3) Plugin.Log.LogError(e); }
            try { Scoreboard.Tick(); } catch (Exception e) { if (_uiErr++ < 3) Plugin.Log.LogError(e); }
            try { PauseUi.Tick(); } catch (Exception e) { if (_uiErr++ < 3) Plugin.Log.LogError(e); }
            try { MenuUi.Tick(); } catch (Exception e) { if (_uiErr++ < 3) Plugin.Log.LogError(e); }
            Gestures.Poll();
            try { HitMarker.Tick(); } catch { }

            // Anyone who goes from playing back to the main menu leaves the session.
            if (GameApi.InGame) _wasInGame = true;
            else if (HostSave.InMainMenu && _wasInGame)
            {
                _wasInGame = false;
                if (Net.Active) LeaveSession();
                else HostSave.OnMainMenu(); // e.g. the host already left: still give the joiner their own save back
            }

            try { StuckRescue.Tick(); } catch (Exception e) { Plugin.Log.LogError(e); } // host or solo
            if (!Net.Connected)
            {
                if (Spectate.Dead) Spectate.Revive("disconnected"); // never leave anyone frozen without controls
                return;
            }

            // host: the config setting is the truth (it used to apply only after toggling it in F8)
            if (Net.IsHost && Extras.FriendlyFire != Plugin.FriendlyFire.Value) Extras.SetFriendlyFire(Plugin.FriendlyFire.Value, true);
            CoopSync.HostTickPauseMode();
            CoopSync.KeepRunningIfLocal();
            foreach (var a in _avatars.Values) a.Tick();
            try { IgnoreAvatarCollisions(); } catch (Exception e) { if (_tickErr++ < 3) Plugin.Log.LogWarning("[MP] avatar collisions: " + e.Message); }
            try { VacuumPull.Update(_avatars.Values); } catch (Exception e) { Plugin.Log.LogError(e); }
            try { Spectate.Tick(); } catch (Exception e) { Plugin.Log.LogError(e); }

            _sendTimer += Time.unscaledDeltaTime;
            if (_sendTimer >= 0.05f) // 20 Hz
            {
                _sendTimer = 0;
                SendLocalState();
            }

            if (Plugin.SharedMoney.Value) MoneySync();
            try { CoopSync.Tick(); } catch (Exception e) { Plugin.Log.LogError(e); }
            try { ExitSync.Tick(); } catch (Exception e) { Plugin.Log.LogError(e); }
            try { ContinueSync.Tick(); } catch (Exception e) { Plugin.Log.LogError(e); }
            try { Extras.TickGambling(); } catch (Exception e) { Plugin.Log.LogError(e); }
            try { GambleSync.Tick(); } catch (Exception e) { Plugin.Log.LogError(e); }
            try { FactorySync.Tick(); } catch (Exception e) { Plugin.Log.LogError(e); }

            if (Net.IsHost)
            {
                _timerSyncTimer += Time.unscaledDeltaTime;
                if (_timerSyncTimer >= 0.5f)
                {
                    _timerSyncTimer = 0;
                    if (GameApi.InGame)
                    {
                        Net.Begin(Msg.DayTimer).Put(GameApi.DayTimer);
                        Net.W.Put((byte)(GameApi.IsNight ? 1 : 0)); // sun / moon icon
                        Net.Send(DeliveryMethod.Sequenced);
                    }
                }
                try { Extras.HostTickLighting(); } catch { }
                if (Plugin.SyncEnemies.Value)
                {
                    _enemyTimer += Time.unscaledDeltaTime;
                    if (_enemyTimer >= 1f / 15f)
                    {
                        _enemyTimer = 0;
                        Guard("enemy broadcast", () => EnemySync.HostBroadcast(Net));
                        Guard("truck broadcast", () => EnemySync.HostBroadcastTruck(Net));
                        Guard("can broadcast", () => CanSync.HostBroadcast(Net));
                        Guard("crate broadcast", () => CrateSync.HostBroadcast(Net));
                    }
                    Guard("enemy tick", EnemySync.HostTick);
                    Guard("can tick", CanSync.HostTick);
                    Guard("crate tick", CrateSync.HostTick);
                }
            }
            else
            {
                try { Extras.ClientTickLighting(); } catch { }
                if (Plugin.SyncEnemies.Value)
                {
                    Guard("enemy tick", EnemySync.ClientTick);
                    Guard("can tick", CanSync.ClientTick);
                    Guard("crate tick", CrateSync.ClientTick);
                }
            }
            VacuumHelper.Tick();
            try { Extras.TickCursors(); } catch (System.Exception e) { if (_tickErr++ < 3) Plugin.Log.LogWarning("[MP] cursors: " + e); }
        }

        /// Run one sync step; an error in one never stops the others (and is logged a few times, not every frame).
        private readonly Dictionary<string, int> _errCounts = new Dictionary<string, int>();
        [HideFromIl2Cpp]
        private void Guard(string what, Action step)
        {
            try { step(); }
            catch (Exception e)
            {
                _errCounts.TryGetValue(what, out var n);
                _errCounts[what] = n + 1;
                if (n < 5) Plugin.Log.LogError($"[MP] {what} failed: {e}");
            }
        }

        private static bool _legacyInputBroken;
        private static bool MenuKeyPressed()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null) return kb[Plugin.MenuKey.Value].wasPressedThisFrame;
            }
            catch { }
            if (_legacyInputBroken) return false;
            try { return Input.GetKeyDown(KeyCode.F8); }
            catch { _legacyInputBroken = true; return false; }
        }

        private void SendLocalState()
        {
            if (!GameApi.TryGetLocalPose(out var pos, out var yaw, out var pitch)) return;
            var s = new PlayerSnapshot { Id = Net.LocalId, Pos = pos, Yaw = yaw, Pitch = pitch, Weapon = GameApi.CurrentWeaponGlobalIndex, Action = GameApi.CurrentWeaponAction | (Gestures.Firing ? 4 : 0) | (Spectate.Dead || Spectate.LocalSuiciding ? 8 : 0), Gestures = Gestures.Packed, Emote = Gestures.Emote };
            Net.Begin(Msg.PlayerState);
            s.Write(Net.W);
            Net.Send(DeliveryMethod.Sequenced);
        }

        // DOTween starts with room for 200 tweens and grows (with a warning each time) - multiplayer's extra pop-ups
        // need more, so give it plenty once it's running.
        private bool _tweensSized;
        private float _tweenCheckAt;
        private void SizeTweens()
        {
            if (Time.unscaledTime < _tweenCheckAt) return;
            _tweenCheckAt = Time.unscaledTime + 1f;
            try
            {
                if (!DG.Tweening.DOTween.initialized && Time.unscaledTime < 20f) return;
                DG.Tweening.DOTween.SetTweensCapacity(1250, 500);
                _tweensSized = true;
            }
            catch (Exception e) { _tweensSized = true; Plugin.Log.LogWarning("DOTween capacity: " + e.Message); }
        }

        // players who joined the lobby: they get the save once the host's game has started
        private readonly HashSet<int> _lobbyWaiting = new HashSet<int>();
        private float _lobbySendAt = -1f;

        private void FlushLobby()
        {
            if (!Net.IsHost) { _lobbyWaiting.Clear(); _lobbySendAt = -1f; return; }
            Net.MaxPlayers = Plugin.MaxPlayers.Value <= 0 ? 0 : Mathf.Clamp(Plugin.MaxPlayers.Value, 2, 8);
            if (_lobbyWaiting.Count == 0) return;
            if (!GameApi.InGame || !HostSave.GameStarted || HostBetweenDays()) { _lobbySendAt = -1f; return; }
            if (_lobbySendAt < 0f) { _lobbySendAt = Time.unscaledTime + 1f; return; } // let the money settle
            if (Time.unscaledTime < _lobbySendAt) return;
            var ids = new List<int>(_lobbyWaiting);
            _lobbyWaiting.Clear(); _lobbySendAt = -1f;
            foreach (var id in ids) if (Net.Players.ContainsKey(id)) OnSomeoneJoined(id);
        }

        /// The host is on the end-of-day "good night" screen, the upgrade / continue screen, or game over.
        private static bool HostBetweenDays()
        {
            if (ContinueSync.InPhase) return true;
            try
            {
                var gm = GameManager.Instance;
                if (gm == null) return false;
                if (gm.IsGameOver) return true;
                var gn = gm.screenGoodNight;
                if (gn != null && gn.activeInHierarchy) return true;
            }
            catch { }
            return false;
        }

        private void OnSomeoneJoined(int id)
        {
            if (!Net.IsHost || id == 0) return;
            EnemySync.ResendDeform();
            if (!GameApi.InGame) { _lobbyWaiting.Add(id); return; } // in the lobby: wait for START GAME
            if (HostBetweenDays())
            {
                // our save is between days (end-of-day / upgrade screen): a joiner loading it would start a day on
                // their own, alive in the elevator. Hold them until our next day starts.
                if (_lobbyWaiting.Add(id))
                {
                    Note($"{Net.NameOf(id)} will join when the next day starts");
                    Net.Begin(Msg.Chat).Put("The host is between days - you'll join as soon as the next day starts");
                    Net.SendTo(id, DeliveryMethod.ReliableOrdered);
                }
                return;
            }
            Net.Begin(Msg.FriendlyFire).Put(Extras.FriendlyFire);
            Net.SendTo(id, DeliveryMethod.ReliableOrdered);
            CoopSync.SendPauseMode(id);
            // bring the new client up to date: the host's save first (joiners on the main menu load it)
            try { HostSave.SendTo(id); } catch (Exception e) { Plugin.Log.LogError("Sending save failed: " + e); }
            try { SkillSync.SendFullState(id); } catch (Exception e) { Plugin.Log.LogError(e); }
            CoopSync.ResendStatus();
            var gm0 = GameManager.Instance;
            if (gm0 != null && gm0.IsPaused) { Net.Begin(Msg.Pause).Put(true); Net.W.Put(0); Net.SendTo(id, DeliveryMethod.ReliableOrdered); }
            if (Plugin.SharedMoney.Value && GameApi.InGame)
            {
                Net.Begin(Msg.MoneyTotal).Put(GameApi.Money);
                Net.SendTo(id, DeliveryMethod.ReliableOrdered);
            }
        }

        // ------------------------------------------------------------------ money

        private void MoneySync()
        {
            if (!GameApi.InGame) { _lastKnownMoney = double.NaN; return; }
            double m = GameApi.Money;
            if (Net.IsHost)
            {
                _moneyResend += Time.unscaledDeltaTime;
                if (m != _lastBroadcastMoney || _moneyResend > 2f) // periodic resend self-heals any drift
                {
                    _moneyResend = 0;
                    _lastBroadcastMoney = m;
                    Net.Begin(Msg.MoneyTotal).Put(m);
                    Net.Send(DeliveryMethod.ReliableOrdered);
                }
            }
            else
            {
                if (double.IsNaN(_lastKnownMoney) || Time.unscaledTime < HostSave.MoneySettleUntil) { _lastKnownMoney = m; return; }
                double delta = m - _lastKnownMoney;
                if (Math.Abs(delta) > 0.0001)
                {
                    Net.Begin(Msg.MoneyDelta).Put(delta);
                    Net.Send(DeliveryMethod.ReliableOrdered);
                    _lastKnownMoney = m;
                }
            }
        }

        // ------------------------------------------------------------------ messages

        [HideFromIl2Cpp]
        private void HandleMessage(int from, Msg type, NetPacketReader r)
        {
            switch (type)
            {
                case Msg.PlayerState:
                {
                    var s = PlayerSnapshot.Read(r);
                    if (Net.IsHost)
                    {
                        s.Id = from; // never trust the client's id
                        Net.Begin(Msg.PlayerState); s.Write(Net.W);
                        Net.SendExcept(from, DeliveryMethod.Sequenced);
                    }
                    if (s.Id != Net.LocalId) GetAvatar(s.Id).Push(s);
                    break;
                }
                case Msg.MoneyDelta when Net.IsHost:
                {
                    var d = r.GetDouble();
                    if (GameApi.InGame) GameApi.Money = GameApi.Money + d; // broadcast happens in MoneySync
                    break;
                }
                case Msg.MoneyTotal when Net.IsClient:
                {
                    var total = r.GetDouble();
                    if (GameApi.InGame)
                    {
                        // keep any local change we haven't reported yet
                        double unsent = double.IsNaN(_lastKnownMoney) ? 0 : GameApi.Money - _lastKnownMoney;
                        GameApi.Money = total + unsent;
                        _lastKnownMoney = total;
                    }
                    break;
                }
                case Msg.DayTimer when Net.IsClient:
                {
                    var t = r.GetFloat();
                    if (GameApi.InGame && Mathf.Abs(GameApi.DayTimer - t) > 1.5f) GameApi.DayTimer = t;
                    if (r.AvailableBytes >= 1 && GameApi.InGame) GameApi.IsNight = r.GetByte() != 0;
                    break;
                }
                case Msg.Announce: Extras.ReceiveAnnounce(from, r); break;
                case Msg.HostStats when Net.IsClient: FactorySync.Receive(r); break;
                case Msg.FactorySold:
                case Msg.PermBuyRequest:
                case Msg.PermBought:
                    PermanentSync.Receive(from, type, r);
                    break;
                case Msg.GambleSpin:
                case Msg.GambleDone:
                case Msg.GambleBet:
                    GambleSync.Receive(from, type, r);
                    break;
                case Msg.PlayerHit: Extras.ReceiveHit(from, r); break;
                case Msg.Cursor: Extras.ReceiveCursor(from, r); break;
                case Msg.Lighting when !Net.IsHost: Extras.ReceiveLighting(r); break;
                case Msg.MoneyPopup: Extras.ReceiveMoneyPopup(from, r); break;
                case Msg.PauseMode when Net.IsClient: CoopSync.Receive(from, type, r); break;
                case Msg.GambleStat when Net.IsHost: PlayerStats.ReceiveGamble(from, r); break;
                case Msg.MailOpen: MailSync.Receive(from, r); break;
                case Msg.FriendlyFire when Net.IsClient:
                {
                    bool ff = r.GetBool();
                    if (ff != Extras.FriendlyFire) Feed("Friendly fire " + (ff ? "ON" : "OFF"));
                    Extras.SetFriendlyFire(ff, false);
                    break;
                }
                case Msg.ContinueReady:
                case Msg.ContinueGo:
                case Msg.ContinueStatus:
                    ContinueSync.Receive(from, type, r);
                    break;
                case Msg.SkillBuyResult when Net.IsClient:
                {
                    int sid = r.GetInt(); string why = r.GetString(100);
                    Toast($"Couldn't buy {(SkillId)sid}: {why}");
                    Plugin.Log.LogInfo($"[MP] buy {(SkillId)sid} refused by host: {why}");
                    break;
                }
                case Msg.EnemySnapshot when Net.IsClient:
                    EnemySync.ClientReceive(r);
                    break;
                case Msg.EnemyDeform when Net.IsClient:
                    EnemySync.ClientReceiveDeform(r);
                    break;
                case Msg.EnemyDamaged when Net.IsClient:
                    EnemySync.ClientReceiveDamage(r);
                    break;
                case Msg.EnemyGone when Net.IsClient:
                    EnemySync.ClientReceiveGone(r);
                    break;
                case Msg.EnemyOwner when Net.IsClient: EnemySync.Hold.ClientReceiveOwner(r); break;
                case Msg.EnemyClaim when Net.IsHost: EnemySync.Hold.HostReceiveClaim(from, r); break;
                case Msg.EnemyOwned when Net.IsHost: EnemySync.Hold.HostReceiveStream(from, r); break;
                case Msg.EnemyRelease when Net.IsHost: EnemySync.Hold.HostReceiveRelease(from, r); break;
                case Msg.CanSnapshot when Net.IsClient: CanSync.ClientReceive(r); break;
                case Msg.CanGone when Net.IsClient: CanSync.ClientReceiveGone(r); break;
                case Msg.CanOwner when Net.IsClient: CanSync.Hold.ClientReceiveOwner(r); break;
                case Msg.CanClaim when Net.IsHost: CanSync.Hold.HostReceiveClaim(from, r); break;
                case Msg.CanOwned when Net.IsHost: CanSync.Hold.HostReceiveStream(from, r); break;
                case Msg.CanRewarded when Net.IsHost: CanSync.HostReceiveRewarded(from, r); break;
                case Msg.CanRelease when Net.IsHost: CanSync.Hold.HostReceiveRelease(from, r); break;
                case Msg.CrateSnapshot when Net.IsClient: CrateSync.ClientReceive(r); break;
                case Msg.CrateOwner when Net.IsClient: CrateSync.Hold.ClientReceiveOwner(r); break;
                case Msg.CrateClaim when Net.IsHost: CrateSync.Hold.HostReceiveClaim(from, r); break;
                case Msg.CrateOwned when Net.IsHost: CrateSync.Hold.HostReceiveStream(from, r); break;
                case Msg.CrateRelease when Net.IsHost: CrateSync.Hold.HostReceiveRelease(from, r); break;
                case Msg.EnemyBurn when Net.IsHost:
                    EnemySync.HostReceiveBurn(r, from);
                    break;
                case Msg.Fx:
                    FxSync.Receive(from, r);
                    break;
                case Msg.TruckState when Net.IsClient:
                    EnemySync.ClientReceiveTruck(r);
                    break;
                case Msg.EnemyHit when Net.IsHost:
                    EnemySync.HostApplyHit(from, r);
                    break;
                case Msg.Pause:
                case Msg.ElevatorReady:
                case Msg.ElevatorGo:
                    CoopSync.Receive(from, type, r);
                    break;
                case Msg.Suicide:
                    Patches.ReceiveSuicide(from, r);
                    break;
                case Msg.SkillBuyRequest when Net.IsHost:
                    SkillSync.HostReceiveBuyRequest(from, r);
                    break;
                case Msg.SkillPurchased:
                case Msg.SkillState:
                    SkillSync.Receive(from, type, r);
                    break;
                case Msg.PlayerStats when Net.IsClient:
                    PlayerStats.Receive(r);
                    break;
                case Msg.CorpsePose when Net.IsClient:
                    CorpseSync.Receive(r);
                    break;
                case Msg.ExitDoorOpen:
                case Msg.ExitReady:
                case Msg.ExitGo:
                case Msg.ExitStatus:
                    ExitSync.Receive(from, type, r);
                    break;
                case Msg.ElevatorStatus:
                    CoopSync.Receive(from, type, r);
                    break;
                case Msg.HostSave when Net.IsClient:
                    HostSave.Receive(r);
                    break;
                case Msg.Chat:
                {
                    var text = r.GetString(200);
                    if (Net.IsHost) { Net.Begin(Msg.Chat).Put($"{Net.NameOf(from)}: {text}"); Net.SendExcept(from, DeliveryMethod.ReliableOrdered); Feed($"{Net.NameOf(from)}: {text}"); }
                    else Feed(text);
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ avatars

        [HideFromIl2Cpp]
        private RemoteAvatar GetAvatar(int id)
        {
            if (!_avatars.TryGetValue(id, out var a) || a.Root == null)
            {
                a = new RemoteAvatar(id);
                a.SetCollider(Extras.FriendlyFire && !a.Dead);
                _avatars[id] = a;
            }
            return a;
        }

        public void SetAvatarColliders(bool on)
        {
            foreach (var a in _avatars.Values) a.SetCollider(on);
        }

        [HideFromIl2Cpp]
        public System.Collections.Generic.IEnumerable<RemoteAvatar> Avatars => _avatars.Values;

        [HideFromIl2Cpp]
        public RemoteAvatar AvatarById(int id) => _avatars.TryGetValue(id, out var a) ? a : null;

        public bool IsAvatarRoot(Transform t)
        {
            foreach (var a in _avatars.Values) if (a.Root != null && a.Root.transform == t) return true;
            return false;
        }

        /// Player id whose body contains this point, or -1.
        public int AvatarAt(Vector3 point)
        {
            foreach (var a in _avatars.Values) if (a.Contains(point)) return a.Id;
            return -1;
        }

        private void RemoveAvatar(int id)
        {
            if (_avatars.TryGetValue(id, out var a)) { a.Destroy(); _avatars.Remove(id); }
        }

        private void ClearAvatars()
        {
            foreach (var a in _avatars.Values) a.Destroy();
            _avatars.Clear();
        }

        // ------------------------------------------------------------------ UI

        private UnityEngine.EventSystems.EventSystem _blockedEventSystem;

        /// IMGUI clicks also reach the game's uGUI (e.g. the pause menu's Resume button sits right
        /// under our panel), so switch the game's EventSystem off while the panel is open.
        private void SetGameUiBlocked(bool blocked)
        {
            try
            {
                if (blocked)
                {
                    var es = UnityEngine.EventSystems.EventSystem.current;
                    if (es != null && es.enabled) { es.enabled = false; _blockedEventSystem = es; }
                }
                else if (_blockedEventSystem != null)
                {
                    _blockedEventSystem.enabled = true;
                    _blockedEventSystem = null;
                }
            }
            catch { _blockedEventSystem = null; }
        }

        /// The MULTIPLAYER button in the main menu.
        public void OpenMenuFromUi() { if (!MenuOpen) ToggleMenu(); }

        /// Used by the game-style MULTIPLAYER screen.
        public string FieldName => _nameField ?? "";
        /// The address box holds the lobby code in Online mode, the IP in Direct mode.
        public string FieldAddress => (NetSession.UseOnline ? _codeField : _addrField) ?? "";
        public string FieldPort => _portField ?? "";
        public void ApplyFields(string name, string address, string port)
        {
            _nameField = name; _portField = port;
            if (NetSession.UseOnline) _codeField = address; else _addrField = address;
            SaveFields();
        }
        public void Disconnect()
        {
            Net.Shutdown(); ClearAvatars(); EnemySync.Reset(); CanSync.Reset(); CrateSync.Reset(); CoopSync.Reset(); ContinueSync.Reset(); Extras.Reset(); GambleSync.Reset(); FactorySync.Reset(); Feed("Left session");
            if (HostSave.InMainMenu) HostSave.OnMainMenu();
        }

        private void ToggleMenu()
        {
            MenuOpen = !MenuOpen;
            _focusedBox = 0;
            if (MenuOpen)
            {
                _prevLock = Cursor.lockState; _prevVisible = Cursor.visible;
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                SetGameUiBlocked(true);
            }
            else
            {
                Cursor.lockState = _prevLock; Cursor.visible = _prevVisible;
                SetGameUiBlocked(false);
            }
        }

        private int _lateErr, _uiErr;

        public void LateUpdate()
        {
            try { Spectate.LateTick(); }
            catch (Exception e) { if (_lateErr++ < 3) Plugin.Log.LogError("spectate camera: " + e); }
        }

        public void OnGUI()
        {
            try { Spectate.DrawHud(); } catch { }
            try
            {
                var ev = Event.current;
                if (ev != null && ev.type == EventType.Repaint) Extras.SendCursor(ev.mousePosition);
                Extras.DrawCursors(Styles.Left);
            }
            catch { }
            DrawNameTags();
            DrawHud();
            if (MenuOpen) DrawPanel();
        }

        private void DrawNameTags()
        {
            if (!Net.Connected) return;
            // IMGUI draws over the game's menus, so hide the tags whenever a menu is up: paused, the cursor is free
            // (shop / skill tree / end-of-day screens / emote wheel), or one of our screens is open
            if (GameApi.IsPaused || MenuOpen || MenuUi.ScreenOpen || PauseUi.Open || Cursor.lockState != CursorLockMode.Locked) return;
            var cam = Camera.main;
            if (cam == null) return;
            foreach (var a in _avatars.Values)
            {
                if (a.Root == null || !a.Root.activeSelf) continue;
                var wp = a.Position + Vector3.up * 2.2f;
                var sp = cam.WorldToScreenPoint(wp);
                if (sp.z <= 0) continue;
                float dist = Vector3.Distance(cam.transform.position, wp);
                var label = $"{Net.NameOf(a.Id)}  ({dist:0}m)";
                var r = new Rect(sp.x - 80, Screen.height - sp.y - 12, 160, 24);
                GUI.color = Color.black; GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), label, Styles.Center);
                GUI.color = Palette.ForPlayer(a.Id); GUI.Label(r, label, Styles.Center);
            }
            GUI.color = Color.white;
        }

        private string CurrentWaitText() =>
            !string.IsNullOrEmpty(ContinueSync.VisibleText) ? ContinueSync.VisibleText
            : !string.IsNullOrEmpty(ExitSync.WaitingText) ? ExitSync.WaitingText : CoopSync.WaitingText;

        private void TickNotices()
        {
            string toast = _toast != null && Time.unscaledTime < _toastUntil ? _toast : null;
            NoticeUi.Tick(Loc.T(CurrentWaitText()), Loc.T(toast));
        }

        private void DrawHud()
        {
            if (_toast != null && Time.unscaledTime < _toastUntil && !NoticeUi.Working)
            {
                var tr = new Rect(0, Screen.height * 0.32f, Screen.width, 40);
                var old = Styles.Center.fontSize;
                Styles.Center.fontSize = 28;
                GUI.color = Color.black; GUI.Label(new Rect(tr.x + 2, tr.y + 2, tr.width, tr.height), Loc.T(_toast), Styles.Center);
                GUI.color = Color.white; GUI.Label(tr, Loc.T(_toast), Styles.Center);
                Styles.Center.fontSize = old;
            }
            var waitText = Loc.T(CurrentWaitText());
            if (!string.IsNullOrEmpty(waitText) && !NoticeUi.Working)
            {
                var wr = new Rect(0, Screen.height * 0.18f, Screen.width, 30);
                GUI.color = Color.black; GUI.Label(new Rect(wr.x + 2, wr.y + 2, wr.width, wr.height), waitText, Styles.Center);
                GUI.color = Color.white; GUI.Label(wr, waitText, Styles.Center);
            }
            float y = 8;
            if (Net.Active && MenuOpen)   // status line only while the F8 panel is open (Esc > MULTIPLAYER / Tab show players + ping)
            {
                var line = Net.IsHost ? $"MP Host | {(Net.Online ? (Net.LobbyCode != null ? "code " + Net.LobbyCode + " | " : "getting code... | ") : "")}{Net.Players.Count} player(s)"
                                      : $"MP Client | ping {Net.PingMs}ms | {Net.Status}";
                GUI.Label(new Rect(10, y, 600, 22), line);
                y += 20;
            }
            for (int i = 0; i < _feed.Count; i++)
            {
                if (Time.unscaledTime - _feedTime[i] > 8f) continue;
                GUI.Label(new Rect(10, y, 700, 22), Loc.T(_feed[i]));
                y += 18;
            }
        }

        private void DrawPanel()
        {
            float w = 400, h = 404;
            float x = (Screen.width - w) / 2, y = (Screen.height - h) / 2;
            GUI.Box(new Rect(x, y, w, h), "Can 'Em All Multiplayer  (" + Plugin.MenuKey.Value + " to close)");
            float cy = y + 30;

            GUI.Label(new Rect(x + 15, cy, 80, 22), "Name"); _nameField = TextBox(1, new Rect(x + 100, cy, 260, 22), _nameField, 24); cy += 30;
            GUI.Label(new Rect(x + 15, cy + 3, 80, 22), "Colour");
            {
                int sel = Net.Active ? Net.MyColor : Palette.Clamp(Plugin.PlayerColor.Value < 0 ? 0 : Plugin.PlayerColor.Value);
                int pick = Palette.Swatches(new Rect(x + 100, cy, 260, 26), sel, Net.Active ? (System.Func<int, bool>)Net.ColorTaken : null);
                if (pick >= 0) { Plugin.PlayerColor.Value = pick; Net.RequestColor(pick); }
            }
            cy += 36;
            bool online = NetSession.UseOnline;
            GUI.Label(new Rect(x + 15, cy, 80, 22), "Connect");
            if (!Net.Active && GUI.Button(new Rect(x + 100, cy, 170, 22), online ? "Online (lobby code)" : "Direct IP"))
                Plugin.Connection.Value = online ? "Direct" : "Online";
            else if (Net.Active) GUI.Label(new Rect(x + 100, cy, 260, 22), Net.Online ? "Online" + (Net.IsHost && Net.LobbyCode != null ? " - code " + Net.LobbyCode : "") : "Direct IP");
            cy += 30;
            if (online)
            {
                GUI.Label(new Rect(x + 15, cy, 80, 22), "Code"); _codeField = TextBox(2, new Rect(x + 100, cy, 200, 22), _codeField, 8);
                if (GUI.Button(new Rect(x + 305, cy, 55, 22), "Paste")) { try { var c = GUIUtility.systemCopyBuffer; if (!string.IsNullOrEmpty(c)) _codeField = c.Trim(); } catch { } } cy += 36;
            }
            else
            {
                GUI.Label(new Rect(x + 15, cy, 80, 22), "Address"); _addrField = TextBox(2, new Rect(x + 100, cy, 200, 22), _addrField, 64);
                if (GUI.Button(new Rect(x + 305, cy, 55, 22), "Paste")) { try { var c = GUIUtility.systemCopyBuffer; if (!string.IsNullOrEmpty(c)) _addrField = c.Trim(); } catch { } } cy += 30;
                GUI.Label(new Rect(x + 15, cy, 80, 22), "Port"); _portField = TextBox(3, new Rect(x + 100, cy, 80, 22), _portField, 5); cy += 6;
            }
            cy += 30;

            if (!Net.Active)
            {
                if (GameApi.InGame && !MenuUi.MultiplayerSession)
                {
                    // a save opened with CONTINUE / NEW GAME is single player
                    GUI.Label(new Rect(x + 15, cy, 350, 34), "Single-player game. To play together, go to the main menu:\nMULTIPLAYER > HOST GAME (or JOIN GAME).");
                }
                else
                {
                    if (GUI.Button(new Rect(x + 15, cy, 170, 32), "Host"))
                    {
                        SaveFields();
                        if (GameApi.InGame) { if (Net.StartHost()) Note(Net.HostingText); }
                        else { MenuOpen = true; ToggleMenu(); MenuUi.HostFromMenu(); }
                    }
                    if (!GameApi.InGame && GUI.Button(new Rect(x + 195, cy, 170, 32), "Join"))
                    {
                        SaveFields();
                        Net.StartJoin();
                    }
                }
            }
            else
            {
                if (GUI.Button(new Rect(x + 15, cy, 350, 32), Net.IsHost ? "Stop hosting" : "Disconnect"))
                {
                    Disconnect();
                }
            }
            cy += 42;
            GUI.Label(new Rect(x + 15, cy, 350, 22), "Status: " + Net.Status); cy += 22;
            if (!Net.IsClient)
            {
                if (GUI.Button(new Rect(x + 15, cy, 200, 22), "Friendly fire: " + (Plugin.FriendlyFire.Value ? "ON" : "OFF")))
                {
                    Plugin.FriendlyFire.Value = !Plugin.FriendlyFire.Value;
                    Extras.SetFriendlyFire(Plugin.FriendlyFire.Value, true);
                    if (Net.IsHost) Extras.Announce("Friendly fire " + (Plugin.FriendlyFire.Value ? "ON" : "OFF"));
                }
            }
            else GUI.Label(new Rect(x + 15, cy, 350, 22), "Friendly fire: " + (Extras.FriendlyFire ? "ON" : "OFF") + " (host decides)");
            cy += 26;
            if (Net.Active)
            {
                var names = new List<string>();
                foreach (var p in Net.Players.Values) names.Add(p.Id == Net.LocalId ? p.Name + " (you)" : p.Name);
                GUI.Label(new Rect(x + 15, cy, 350, 44), "Players: " + string.Join(", ", names));
            }
            cy += 44;
            GUI.Label(new Rect(x + 15, cy, 350, 40), online
                ? "Host: press Host and send friends the lobby code.\nJoiner: Join from the MAIN MENU with the code."
                : "Host: load your save, press Host, forward UDP " + Plugin.Port.Value + ".\nJoiner: Join from the MAIN MENU to play on the host's save.");
        }

        // GUI.TextField is stripped from this game's build, so this is a minimal replacement:
        // click a box to focus it, type, Backspace deletes, Enter/Tab/click elsewhere unfocuses.
        private int _focusedBox;
        private string TextBox(int id, Rect r, string value, int maxLen)
        {
            value ??= "";
            bool focused = _focusedBox == id;
            var e = Event.current;
            if (e != null && e.type == EventType.MouseDown)
            {
                if (r.Contains(e.mousePosition)) { _focusedBox = id; focused = true; e.Use(); }
                else if (focused) { _focusedBox = 0; focused = false; }
            }
            if (focused && e != null && e.type == EventType.KeyDown)
            {
                var kc = e.keyCode;
                char ch = e.character;
                if (kc == KeyCode.Backspace) { if (value.Length > 0) value = value.Substring(0, value.Length - 1); e.Use(); }
                else if (kc == KeyCode.Return || kc == KeyCode.KeypadEnter || kc == KeyCode.Tab || kc == KeyCode.Escape) { _focusedBox = 0; e.Use(); }
                else if (ch >= ' ' && ch != 127 && value.Length < maxLen) { value += ch; e.Use(); }
            }
            bool caret = focused && ((int)(Time.unscaledTime * 2f) % 2 == 0);
            GUI.Box(r, "");
            GUI.Label(new Rect(r.x + 4, r.y + 2, r.width - 8, r.height - 2), value + (caret ? "|" : ""));
            return value;
        }

        /// Close the panel without restoring the menu's free cursor (the game is about to lock it).
        public static void CloseMenuForGameplay()
        {
            MenuOpen = false;
            if (Instance != null) { Instance._focusedBox = 0; Instance.SetGameUiBlocked(false); }
        }

        private void SaveFields()
        {
            Plugin.PlayerName.Value = string.IsNullOrWhiteSpace(_nameField) ? "Player" : _nameField.Trim();
            Plugin.LastAddress.Value = _addrField ?? "";
            Plugin.LastCode.Value = (_codeField ?? "").Trim();
            if (int.TryParse(_portField, out var p) && p > 0 && p < 65536) Plugin.Port.Value = p;
            Net.LocalName = Plugin.PlayerName.Value;
        }

        private static class Styles
        {
            private static GUIStyle _left;
            public static GUIStyle Left
            {
                get
                {
                    if (_left == null)
                    {
                        _left = new GUIStyle();
                        _left.alignment = TextAnchor.UpperLeft;
                        _left.fontStyle = FontStyle.Bold;
                        _left.fontSize = 14;
                        _left.normal.textColor = Color.white;
                    }
                    return _left;
                }
            }
            private static GUIStyle _center;
            public static GUIStyle Center
            {
                get
                {
                    if (_center == null)
                    {
                        _center = new GUIStyle();
                        _center.alignment = TextAnchor.MiddleCenter;
                        _center.fontStyle = FontStyle.Bold;
                        _center.fontSize = 14;
                        _center.normal.textColor = Color.white;
                    }
                    return _center;
                }
            }
        }
    }
}
