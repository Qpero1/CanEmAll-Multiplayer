using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace CanEmAllMP
{
    [HarmonyPatch]
    public static class Patches
    {
        public static bool ApplyingRemoteHit;
        private static bool _networkNextDay;

        private static NetSession Net => MpManager.Net;
        private static bool SyncingEnemiesAsClient => Net.IsClient && Net.Connected && Plugin.SyncEnemies.Value;

        // ---------------------------------------------------------------- enemies

        /// Client hitting a host-controlled creature -> forward to the host (the host replays the result back).
        /// Host -> remember which creature is being damaged so the final damage number can be captured.
        [HarmonyPrefix, HarmonyPatch(typeof(Enemy), nameof(Enemy.Damage))]
        public static bool EnemyDamagePrefix(Enemy __instance, float damage, Vector3 dir, Vector3 hitPosition,
            float damageRadiusMultiplier, bool showBlood, bool showDamageText, Weapon weapon,
            bool highChanceToScream, bool flashAndDeform, float probabilityMultiplier)
        {
            if (!Net.Connected || !Plugin.SyncEnemies.Value) return true;
            if (Net.IsClient)
            {
                if (EnemySync.Replaying) return true;
                if (EnemySync.IsRemote(__instance))
                {
                    EnemySync.ClientForwardHit(__instance, damage, dir, hitPosition, damageRadiusMultiplier,
                        showBlood, showDamageText, weapon, highChanceToScream, flashAndDeform, probabilityMultiplier);
                    return false;
                }
                return true;
            }
            EnemySync.DamageCtx = __instance;
            EnemySync.DamageCtxHit = float.NaN;
            return true;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Enemy), nameof(Enemy.Damage))]
        public static void EnemyDamagePostfix(Enemy __instance, float damage, Vector3 dir, Vector3 hitPosition,
            float damageRadiusMultiplier, bool showBlood, bool showDamageText, Weapon weapon,
            bool highChanceToScream, bool flashAndDeform, float probabilityMultiplier)
        {
            if (!Net.IsHost || EnemySync.DamageCtx == null) return;
            try
            {
                if (EnemySync.DamageCtx.Pointer == __instance.Pointer && !float.IsNaN(EnemySync.DamageCtxHit))
                    EnemySync.HostBroadcastDamage(__instance, damage, dir, hitPosition, damageRadiusMultiplier, showBlood,
                        showDamageText, weapon, highChanceToScream, flashAndDeform, probabilityMultiplier, EnemySync.DamageCtxHit);
            }
            catch (System.Exception e) { Plugin.Log.LogError(e); }
            finally { EnemySync.DamageCtx = null; }
        }

        /// Player stats: who killed what (host).
        [HarmonyPrefix, HarmonyPatch(typeof(Enemy), nameof(Enemy.Damage))]
        public static void StatsDamagePrefix(Enemy __instance, out bool __state)
        {
            __state = true;
            try { __state = !Net.IsHost || __instance.IsDead; } catch { }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Enemy), nameof(Enemy.Damage))]
        public static void StatsDamagePostfix(Enemy __instance, Weapon weapon, bool __state)
        {
            try { PlayerStats.AfterDamage(__instance, weapon, __state); } catch { }
        }

        /// Stats: any real death of the host itself (also when hosting alone, where no death message is sent).
        /// Deaths caused by a teammate's death message (SuicideFromNet) aren't the host's.
        [HarmonyPrefix, HarmonyPatch(typeof(GameManager), nameof(GameManager.Suicide))]
        public static void StatsSuicidePrefix()
        {
            try { Plugin.Log.LogInfo($"[stats] Suicide called (host {Net.IsHost}, fromNet {SuicideFromNet})"); } catch { }
            try { if (Net.IsHost && !SuicideFromNet) PlayerStats.RealDeath(Net.LocalId, "host"); } catch { }
        }

        // kills that skip Enemy.Damage: the shockwave skill and bleeding
        [HarmonyPrefix, HarmonyPatch(typeof(Enemy), nameof(Enemy.ShockwaveDamage))]
        public static void StatsShockPrefix(Enemy __instance, out bool __state)
        {
            __state = true;
            try { __state = !Net.IsHost || __instance.IsDead; } catch { }
        }
        [HarmonyPostfix, HarmonyPatch(typeof(Enemy), nameof(Enemy.ShockwaveDamage))]
        public static void StatsShockPostfix(Enemy __instance, bool __state)
        {
            try { PlayerStats.AfterOtherDamage(__instance, __state, true); } catch { }
        }
        [HarmonyPrefix, HarmonyPatch(typeof(Enemy), nameof(Enemy.BleedDamage))]
        public static void StatsBleedPrefix(Enemy __instance, out bool __state)
        {
            __state = true;
            try { __state = !Net.IsHost || __instance.IsDead; } catch { }
        }
        [HarmonyPostfix, HarmonyPatch(typeof(Enemy), nameof(Enemy.BleedDamage))]
        public static void StatsBleedPostfix(Enemy __instance, bool __state)
        {
            try { PlayerStats.AfterOtherDamage(__instance, __state, false); } catch { }
        }

        // damage that isn't a player's: turrets, the can processor, KillAll (end of day)
        [HarmonyPrefix, HarmonyPatch(typeof(Turret), nameof(Turret.Shoot))]
        public static void TurretShootPrefix() => PlayerStats.Ignore++;
        [HarmonyPostfix, HarmonyPatch(typeof(Turret), nameof(Turret.Shoot))]
        public static void TurretShootFinalizer() { if (PlayerStats.Ignore > 0) PlayerStats.Ignore--; }
        [HarmonyPrefix, CoroutinePatch(typeof(CanProcessor), "_ProcessEnemies_d__")]
        public static void CanProcessPrefix() => PlayerStats.Ignore++;
        [HarmonyPostfix, CoroutinePatch(typeof(CanProcessor), "_ProcessEnemies_d__")]
        public static void CanProcessFinalizer() { if (PlayerStats.Ignore > 0) PlayerStats.Ignore--; }
        [HarmonyPrefix, HarmonyPatch(typeof(GameManager), nameof(GameManager.KillAll))]
        public static void KillAllPrefix() => PlayerStats.Ignore++;
        [HarmonyPostfix, HarmonyPatch(typeof(GameManager), nameof(GameManager.KillAll))]
        public static void KillAllFinalizer() { if (PlayerStats.Ignore > 0) PlayerStats.Ignore--; }

        /// Replays use the host's final damage (crits/instant kills are random), so every machine agrees.
        [HarmonyPrefix, HarmonyPatch(typeof(GameManager), nameof(GameManager.OnHitEnemy))]
        public static bool OnHitEnemyPrefix(ref float __result)
        {
            if (!EnemySync.Replaying) return true;
            __result = EnemySync.ReplayHit;
            return false;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(GameManager), nameof(GameManager.OnHitEnemy))]
        public static void OnHitEnemyPostfix(Enemy enemy, float __result)
        {
            if (Net.IsHost && EnemySync.DamageCtx != null && enemy != null && enemy.Pointer == EnemySync.DamageCtx.Pointer)
                EnemySync.DamageCtxHit = __result;
        }

        /// A replayed hit/death must never pay out on the client (money is the host's).
        [HarmonyPrefix, HarmonyPatch(typeof(MoneyManager), nameof(MoneyManager.AddMoney))]
        public static bool AddMoneyPrefix() => !EnemySync.Replaying;

        [HarmonyPostfix, HarmonyPatch(typeof(MoneyManager), nameof(MoneyManager.AddMoney))]
        public static void AddMoneyPostfix(int amount, MoneyManager.CoinTextType type)
        {
            if (EnemySync.Replaying || !Net.Connected) return;
            try { Extras.SendMoneyPopup(amount, type); } catch { }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(MoneyManager), nameof(MoneyManager.ChangeMoney))]
        public static bool ChangeMoneyPrefix() => !EnemySync.Replaying;

        /// Health only changes on the creature's authority.
        [HarmonyPrefix, HarmonyPatch(typeof(Enemy), nameof(Enemy.BleedDamage))]
        public static bool EnemyBleedPrefix(Enemy __instance) => !EnemySync.IsRemote(__instance);

        [HarmonyPrefix, HarmonyPatch(typeof(Enemy), nameof(Enemy.ShockwaveDamage))]
        public static bool EnemyShockPrefix(Enemy __instance) => !EnemySync.IsRemote(__instance);

        /// Remote-controlled creatures are moved by the network, not their own AI/physics.
        [HarmonyPrefix, HarmonyPatch(typeof(Enemy), nameof(Enemy.FixedUpdate))]
        public static bool EnemyFixedUpdatePrefix(Enemy __instance) => !EnemySync.IsRemote(__instance);

        /// Grabbing a body: a hand grab may take it from anyone, a vacuum (OnGrabbed while Suck runs = storing it)
        /// only gets it if nobody else has it.
        [HarmonyPrefix, HarmonyPatch(typeof(Enemy), nameof(Enemy.OnGrabbed))]
        public static void EnemyGrabbedPrefix(Enemy __instance)
        {
            if (!Net.Connected || !Plugin.SyncEnemies.Value) return;
            try { EnemySync.LocalGrab(__instance, !VacuumHelper.InSuck); }
            catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        /// The vacuum: before Suck, things someone else holds are hidden from it; after Suck, we claim what it
        /// pulled or stored and let go of anything another player turned out to have first.
        [HarmonyPrefix, HarmonyPatch(typeof(Vacuum), nameof(Vacuum.Suck))]
        public static void VacuumSuckPrefix()
        {
            if (!Net.Connected || !Plugin.SyncEnemies.Value) return;
            try { VacuumHelper.BeforeSuck(); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Vacuum), nameof(Vacuum.Suck))]
        public static void VacuumSuckPostfix(Vacuum __instance)
        {
            if (!VacuumHelper.InSuck) return;
            try { VacuumHelper.AfterSuck(__instance); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(GameManager), nameof(GameManager.CleanAllScene))]
        public static void CleanAllScenePrefix()
        {
            if (!Net.IsHost || !Net.Connected) return;
            try { EnemySync.ReclaimAll(); CanSync.ReclaimAll(); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        // ---- upgrade-screen info box: what each player hovers is shown to everyone
        [HarmonyPostfix, HarmonyPatch(typeof(SkillPopUp), nameof(SkillPopUp.Show), new[] { typeof(SkillNode) })]
        public static void SkillPopUpShowPostfix(SkillPopUp __instance, SkillNode node)
        {
            try { Extras.LocalPopupShown(__instance, node); } catch { }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(SkillPopUp), nameof(SkillPopUp.Hide))]
        public static void SkillPopUpHidePostfix(SkillPopUp __instance)
        {
            try { Extras.LocalPopupHidden(__instance); } catch { }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(SkillPopUp), nameof(SkillPopUp.GetPointerPosition))]
        public static bool SkillPopUpPointerPrefix(SkillPopUp __instance, ref Vector2 __result)
        {
            try { if (Extras.RemotePopupPointer(__instance, out var p)) { __result = p; return false; } } catch { }
            return true;
        }

        /// End-of-day cleanup: a joiner also clears its copies of the host's creatures and cans.
        [HarmonyPostfix, HarmonyPatch(typeof(GameManager), nameof(GameManager.CleanAllScene))]
        public static void CleanAllScenePostfix()
        {
            if (!Net.IsClient || !Net.Connected) return;
            try { EnemySync.ClearPuppets(); CanSync.ClearPuppets(); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        /// Joiners' cans reach their reward machine too (same canning), but the host's identical cans are the
        /// ones that pay: keep the joiner's "+$" popup local instead of sending it as earnings.
        [HarmonyPrefix, HarmonyPatch(typeof(RewardMachine), nameof(RewardMachine.RewardCan))]
        public static void RewardCanPrefix(out long __state) => __state = MoneyManager.Instance != null ? MoneyManager.Instance.Money : 0;

        [HarmonyPostfix, HarmonyPatch(typeof(RewardMachine), nameof(RewardMachine.RewardCan))]
        public static void RewardCanPostfix(long __state) => AbsorbIfClient(__state);

        [HarmonyPrefix, HarmonyPatch(typeof(RewardMachine), nameof(RewardMachine.ProcessEnemies))]
        public static void RewardEnemiesPrefix(out long __state)
        {
            __state = MoneyManager.Instance != null ? MoneyManager.Instance.Money : 0;
            try { CanSync.BeforeRewardCheck(); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(RewardMachine), nameof(RewardMachine.ProcessEnemies))]
        public static void RewardEnemiesPostfix(long __state)
        {
            try { CanSync.AfterRewardCheck(); } catch (System.Exception e) { Plugin.Log.LogError(e); }
            AbsorbIfClient(__state);
        }

        // ---------------------------------------------------------------- shared cans

        // NOTE: never hook Can.FixedUpdate. Its native body is only 16 bytes, so the hook's jump overwrites the start
        // of Can.Initialize right after it, which breaks every can's texture/shiny look. Puppet cans are kinematic anyway.

        [HarmonyPostfix, HarmonyPatch(typeof(Can), nameof(Can.OnGrabbed))]
        public static void CanGrabbedPostfix(Can __instance)
        {
            if (Net.IsClient && Net.Connected) { try { CanSync.ReapplyLook(__instance); } catch { } }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Can), nameof(Can.OnGrabbed))]
        public static void CanGrabbedPrefix(Can __instance)
        {
            if (!Net.Connected || !Plugin.SyncEnemies.Value) return;
            try { CanSync.LocalGrab(__instance, !VacuumHelper.InSuck); }
            catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        private static void AbsorbIfClient(long before)
        {
            if (!Net.IsClient || !Net.Connected || !Plugin.SharedMoney.Value || !Plugin.SyncEnemies.Value) return;
            var mm = MoneyManager.Instance;
            if (mm == null) return;
            long delta = mm.Money - before;
            if (delta != 0) MpManager.Instance?.AbsorbLocalMoney(delta);
        }

        /// Burning a host creature: forward it (the visual burn still plays locally).
        [HarmonyPrefix, HarmonyPatch(typeof(Enemy), nameof(Enemy.AddBurn))]
        public static void AddBurnPrefix(Enemy __instance, float __0)
        {
            if (Net.IsClient && Net.Connected && !EnemySync.Replaying && EnemySync.IsRemote(__instance))
                EnemySync.ClientForwardBurn(__instance, __0);
        }

        // ---------------------------------------------------------------- explosions / shockwaves

        [HarmonyPostfix, HarmonyPatch(typeof(ParticlePoolManager), nameof(ParticlePoolManager.SpawnSmallExplosion))]
        public static void SmallExplosionPostfix(Vector3 position) => FxSync.OnLocalFx(FxSync.SmallExplosion, position, 0f);

        [HarmonyPostfix, HarmonyPatch(typeof(ParticlePoolManager), nameof(ParticlePoolManager.SpawnBigExplosion))]
        public static void BigExplosionPostfix(Vector3 position) => FxSync.OnLocalFx(FxSync.BigExplosion, position, 0f);

        /// Shockwaves damage over time, so the host's are the only real ones (clients' death-roll shockwaves are skipped
        /// and replaced by the host's, drawn with 0 damage).
        [HarmonyPrefix, HarmonyPatch(typeof(ParticlePoolManager), nameof(ParticlePoolManager.SpawnShockwave))]
        public static bool ShockwavePrefix() => FxSync.FromNet || !(Net.IsClient && Net.Connected && Plugin.SyncEnemies.Value);

        [HarmonyPostfix, HarmonyPatch(typeof(ParticlePoolManager), nameof(ParticlePoolManager.SpawnShockwave))]
        public static void ShockwavePostfix(Vector3 position, float radius)
        {
            if (Net.IsHost) FxSync.OnLocalFx(FxSync.Shockwave, position, radius);
        }

        /// Joiners' trucks never drive themselves: the game would keep sending empty trucks because nothing
        /// spawns on their side. Their truck mirrors the host's truck instead (EnemySync.ClientReceiveTruck).
        [HarmonyPrefix, HarmonyPatch(typeof(Truck), nameof(Truck.StartTruck))]
        public static bool StartTruckPrefix() => !SyncingEnemiesAsClient;

        [HarmonyPrefix, HarmonyPatch(typeof(Truck), nameof(Truck.SpawnEnemiesInsideTruck))]
        public static bool TruckSpawnPrefix() => !SyncingEnemiesAsClient;

        [HarmonyPrefix, HarmonyPatch(typeof(Truck), nameof(Truck.ReleaseEnemies))]
        public static bool TruckReleasePrefix() => !SyncingEnemiesAsClient;

        // ---------------------------------------------------------------- day flow

        /// The on-screen "next day" button: counts as "I'm ready" (see ContinueSync), the host releases everyone together.
        [HarmonyPrefix, HarmonyPatch(typeof(GameManager), nameof(GameManager.NextDay))]
        public static bool NextDayPrefix()
        {
            if (!CoopSync.Active || _networkNextDay) return true;
            if (!ContinueSync.EscapeHeld()) ContinueSync.LocalPressed(); // Escape is pause, not "ready"
            return false;
        }

        /// The continue key (input action "NextDay"): intercepted while in a session, see ContinueSync.
        [HarmonyPrefix, HarmonyPatch(typeof(UnityEngine.InputSystem.InputAction), nameof(UnityEngine.InputSystem.InputAction.IsPressed))]
        public static bool InputIsPressedPrefix(UnityEngine.InputSystem.InputAction __instance, ref bool __result)
        {
            try { return ContinueSync.OnIsPressed(__instance, ref __result); }
            catch { return true; }
        }

        public static void RunNetworkNextDay()
        {
            var gm = GameManager.Instance;
            if (gm == null) return;
            _networkNextDay = true;
            try { gm.NextDay(); }
            catch (System.Exception e) { Plugin.Log.LogError("NextDay failed: " + e); }
            finally { _networkNextDay = false; }
        }

        // ---------------------------------------------------------------- global pause

        [HarmonyPostfix, HarmonyPatch(typeof(GameManager), nameof(GameManager.UpdatePause))]
        public static void UpdatePausePostfix(GameManager __instance)
        {
            try { CoopSync.OnLocalPauseChanged(__instance.IsPaused); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        // ---------------------------------------------------------------- elevator waits for everyone

        [HarmonyPrefix, HarmonyPatch(typeof(GameManager), nameof(GameManager.OnTriggerOutsideElevator))]
        public static bool ElevatorOutsidePrefix() => CoopSync.OnLocalTrigger(CoopSync.Outside);

        // ---------------------------------------------------------------- exit door + exit elevator (everyone leaves together)

        [HarmonyPostfix, HarmonyPatch(typeof(ExitDoor), nameof(ExitDoor.Start))]
        public static void ExitDoorStartPostfix(ExitDoor __instance) => ExitSync.OnDoorStart(__instance);

        [HarmonyPostfix, HarmonyPatch(typeof(ExitDoor), nameof(ExitDoor.Open))]
        public static void ExitDoorOpenPostfix() { try { ExitSync.OnLocalOpen(); } catch (System.Exception e) { Plugin.Log.LogError(e); } }

        [HarmonyPrefix, HarmonyPatch(typeof(ExitDoor), nameof(ExitDoor.OnTriggerInsideElevator))]
        public static bool ExitElevatorPrefix()
        {
            try { return ExitSync.OnLocalEnter(); } catch (System.Exception e) { Plugin.Log.LogError(e); return true; }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(GameManager), nameof(GameManager.OnTriggerInsideElevator))]
        public static bool ElevatorInsidePrefix() => CoopSync.OnLocalTrigger(CoopSync.Inside);

        [HarmonyPostfix, HarmonyPatch(typeof(TriggerEvent), nameof(TriggerEvent.OnTriggerExit))]
        public static void TriggerExitPostfix(TriggerEvent __instance, Collider other)
        {
            if (!CoopSync.Active || other == null) return;
            try
            {
                var player = other.GetComponentInParent<Player>();
                if (player == null) return; // only the local player matters
                int kind = CoopSync.KindOf(__instance);
                if (kind == 0) return;
                var col = __instance.GetComponent<Collider>();
                var zoneCenter = col != null ? col.bounds.center : __instance.transform.position;
                CoopSync.OnLocalTriggerExit(kind, zoneCenter, player.transform.position);
            }
            catch { }
        }

        // ---------------------------------------------------------------- shared skill tree

        /// Joiners don't buy locally: they ask the host, which is the only one that can confirm a purchase
        /// (so two players can never buy the same upgrade twice).
        [HarmonyPrefix, HarmonyPatch(typeof(SkillNode), nameof(SkillNode.OnClick))]
        public static bool SkillClickPrefix(SkillNode __instance, out bool __state)
        {
            __state = __instance.skill != null && __instance.skill.isPurchased;
            if (Net.IsClient && Net.Connected && !SkillSync.Applying && !__state && __instance.skill != null)
            {
                try { if (SkillSync.ClientRequestBuy(__instance)) return false; } catch (System.Exception e) { Plugin.Log.LogError(e); }
            }
            return true;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(SkillNode), nameof(SkillNode.OnClick))]
        public static void SkillClickPostfix(SkillNode __instance, bool __state)
        {
            try
            {
                if (!__state && __instance.skill != null && __instance.skill.isPurchased)
                    SkillSync.OnLocalPurchase(__instance);
            }
            catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        /// End of the menu -> game transition (StartGameCoroutine, after its fade).
        [HarmonyPostfix, HarmonyPatch(typeof(MoneyManager), nameof(MoneyManager.StartGame))]
        public static void MoneyStartGamePostfix()
        {
            HostSave.GameStarted = true;
            if (HostSave.UsingHostSave) HostSave.MoneySettleUntil = UnityEngine.Time.unscaledTime + 1.5f;
        }

        // ---------------------------------------------------------------- suicide (tube / grinder) ends the day for all

        public static bool SuicideFromNet;

        [HarmonyPrefix, HarmonyPatch(typeof(GameManager), nameof(GameManager.Suicide))]
        public static void SuicidePrefix(GameManager __instance, out bool __state) => __state = __instance.IsSuiciding;

        /// Co-op: dying while a teammate is alive = spectate instead of ending the day.
        [HarmonyPrefix, HarmonyPatch(typeof(GameManager), nameof(GameManager.Suicide))]
        public static bool SuicideGatePrefix(float delay, MailId mailId)
        {
            try { return Spectate.BeforeDeath(delay, mailId); } catch (System.Exception e) { Plugin.Log.LogError(e); return true; }
        }

        /// First step of the death sequence: always a real call (the coroutine scheduler runs it), even when
        /// Suicide itself was compiled into the caller.
        /// (Found by name: the coroutine's generated number changes with game updates.)
        [HarmonyPrefix, CoroutinePatch(typeof(GameManager), "_SuicideCoroutine_d__")]
        public static bool SuicideStepPrefix(object __instance, ref bool __result)
        {
            try
            {
                if (Get<int>(__instance, "__1__state") != 0) return true;
                if (!Spectate.OnDeathSequenceStart(Get<GameManager>(__instance, "__4__this"), Get<float>(__instance, "delay"), Get<MailId>(__instance, "mailId")))
                { __result = false; return false; }
            }
            catch (System.Exception e) { Plugin.Log.LogError(e); }
            return true;
        }

        // read a field of a generated coroutine whose type we only know at runtime
        private static readonly Dictionary<(Type, string), PropertyInfo> _props = new Dictionary<(Type, string), PropertyInfo>();
        private static T Get<T>(object o, string name)
        {
            var t = o.GetType();
            if (!_props.TryGetValue((t, name), out var p)) _props[(t, name)] = p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return (T)p.GetValue(o);
        }

        // dead (spectating): no shooting, swinging, sucking or grabbing
        [HarmonyPrefix, HarmonyPatch(typeof(Pistol), nameof(Pistol.Update))] public static bool DeadPistol() => !Spectate.Dead;
        [HarmonyPrefix, HarmonyPatch(typeof(SMG), nameof(SMG.Update))] public static bool DeadSmg() => !Spectate.Dead;
        [HarmonyPrefix, HarmonyPatch(typeof(AK47), nameof(AK47.Update))] public static bool DeadAk() => !Spectate.Dead;
        [HarmonyPrefix, HarmonyPatch(typeof(Shotgun), nameof(Shotgun.Update))] public static bool DeadShotgun() => !Spectate.Dead;
        [HarmonyPrefix, HarmonyPatch(typeof(RocketLauncher), nameof(RocketLauncher.Update))] public static bool DeadRocket() => !Spectate.Dead;
        [HarmonyPrefix, HarmonyPatch(typeof(FlameThrower), nameof(FlameThrower.Update))] public static bool DeadFlame() => !Spectate.Dead;
        [HarmonyPrefix, HarmonyPatch(typeof(Chainsaw), nameof(Chainsaw.Update))] public static bool DeadChainsaw() => !Spectate.Dead;
        [HarmonyPrefix, HarmonyPatch(typeof(BaseballBat), nameof(BaseballBat.Update))] public static bool DeadBat() => !Spectate.Dead;
        [HarmonyPrefix, HarmonyPatch(typeof(Knife), nameof(Knife.Update))] public static bool DeadKnife() => !Spectate.Dead;
        [HarmonyPrefix, HarmonyPatch(typeof(Vacuum), nameof(Vacuum.Update))] public static bool DeadVacuum() => !Spectate.Dead;
        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.DeadBodyInput))] public static bool DeadGrab() => !Spectate.Dead;

        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.Damage))]
        public static bool DamageWhileDeadPrefix() => !Spectate.Dead;

        [HarmonyPostfix, HarmonyPatch(typeof(GameManager), nameof(GameManager.Suicide))]
        public static void SuicidePostfix(GameManager __instance, float delay, MailId mailId, bool __state)
        {
            if (SuicideFromNet || __state || !__instance.IsSuiciding || !CoopSync.Active) return;
            Spectate.Announce(delay, (int)mailId);
        }

        public static void ReceiveSuicide(int from, LiteNetLib.Utils.NetDataReader r)
        {
            float delay = r.GetFloat(); int mail = r.GetInt(); int who = r.GetInt();
            if (Net.IsHost)
            {
                who = from;
                PlayerStats.RealDeath(who, "message");
                Net.Begin(Msg.Suicide).Put(delay); Net.W.Put(mail); Net.W.Put(who);
                Net.SendExcept(from, DeliveryMethod.ReliableOrdered);
            }
            Plugin.Log.LogInfo($"[MP] {Net.NameOf(who)} died for real ({(MailId)mail}): the day ends");
            Spectate.OnNetDeath();
            var gm = GameManager.Instance;
            if (gm == null || !GameApi.InGame || gm.IsSuiciding) return;
            MpManager.Instance?.Toast($"{Net.NameOf(who)} ended the day");
            SuicideFromNet = true;
            try { gm.Suicide(delay, (MailId)mail); }
            catch (System.Exception e) { Plugin.Log.LogError("Suicide sync failed: " + e); }
            finally { SuicideFromNet = false; }
        }

        // ---------------------------------------------------------------- gambling announcements

        [HarmonyPrefix, HarmonyPatch(typeof(SlotMachine), nameof(SlotMachine.Spin))]
        public static void SpinPrefix(SlotMachine __instance, out bool __state) => __state = __instance.IsSpinning;

        [HarmonyPostfix, HarmonyPatch(typeof(SlotMachine), nameof(SlotMachine.Spin))]
        public static void SpinPostfix(SlotMachine __instance, bool __state)
        {
            if (__state || !__instance.IsSpinning) return;
            try { Extras.OnSpinStarted(__instance); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        // ---------------------------------------------------------------- shared slot machine (GambleSync)

        /// No spinning while someone's spin is still being shown on another machine.
        [HarmonyPrefix, HarmonyPatch(typeof(SlotMachine), nameof(SlotMachine.Spin))]
        public static bool SpinGatePrefix(SlotMachine __instance)
        {
            try { return GambleSync.AllowSpin(__instance); }
            catch (System.Exception e) { Plugin.Log.LogError(e); return true; }
        }

        /// A replayed spin lands on the spinner's result.
        [HarmonyPrefix, HarmonyPatch(typeof(SlotMachine), nameof(SlotMachine.GenerateResult))]
        public static bool GenerateResultPrefix(ref Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> __result)
        {
            try { return GambleSync.ForceResult(ref __result); }
            catch (System.Exception e) { Plugin.Log.LogError(e); return true; }
        }

        /// A real local spin: send its result to everyone.
        [HarmonyPostfix, HarmonyPatch(typeof(SlotMachine), nameof(SlotMachine.GenerateResult))]
        public static void GenerateResultPostfix(SlotMachine __instance, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> __result)
        {
            try { GambleSync.OnResultGenerated(__instance, __result); }
            catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        /// The result screen's payout callback: skipped for replays (money already arrives through the shared wallet).
        [HarmonyPrefix, HarmonyPatch(typeof(SlotMachine.__c__DisplayClass85_0), nameof(SlotMachine.__c__DisplayClass85_0._ShowResult_b__4))]
        public static bool ShowResultPayoutPrefix(SlotMachine.__c__DisplayClass85_0 __instance)
        {
            try { return GambleSync.AllowPayout(__instance); }
            catch (System.Exception e) { Plugin.Log.LogError(e); return true; }
        }

        /// Tab buttons: the game won't switch tabs while the slot machine spins; don't let someone else's
        /// replayed spin lock our tabs.
        [HarmonyPrefix, HarmonyPatch(typeof(GameManager), nameof(GameManager.OnClickButtonPanel))]
        public static void ButtonPanelPrefix(out bool __state)
        {
            __state = false;
            try { __state = GambleSync.LiftSpinForTabSwitch(); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(GameManager), nameof(GameManager.OnClickButtonPanel))]
        public static void ButtonPanelPostfix(bool __state)
        {
            if (!__state) return;
            try { GambleSync.RestoreSpinAfterTabSwitch(); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        // ---------------------------------------------------------------- sell factory (FactorySync)

        /// Only the host can sell the factory (rebirth); joiners get a message instead.
        [HarmonyPrefix, HarmonyPatch(typeof(PermanentUpgradeManager), nameof(PermanentUpgradeManager.Sell))]
        public static bool SellPrefix()
        {
            try { return FactorySync.AllowSell(); }
            catch (System.Exception e) { Plugin.Log.LogError(e); return true; }
        }

        /// The host sold: take everyone to the permanent upgrade screen with them.
        [HarmonyPostfix, HarmonyPatch(typeof(PermanentUpgradeManager), nameof(PermanentUpgradeManager.Sell))]
        public static void SellPostfix()
        {
            try { PermanentSync.OnHostSold(); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        /// Permanent upgrades are shared and host-confirmed, like the skill tree.
        [HarmonyPrefix, HarmonyPatch(typeof(PermanentUpgrade), nameof(PermanentUpgrade.OnClick))]
        public static bool PermClickPrefix(PermanentUpgrade __instance, out bool __state)
        {
            __state = false;
            try
            {
                __state = __instance.IsPurchased;
                return PermanentSync.OnClickPrefix(__instance);
            }
            catch (System.Exception e) { Plugin.Log.LogError(e); return true; }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(PermanentUpgrade), nameof(PermanentUpgrade.OnClick))]
        public static void PermClickPostfix(PermanentUpgrade __instance, bool __state)
        {
            try { PermanentSync.OnClickPostfix(__instance, __state); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        /// Hovering a permanent upgrade shows the info box: everyone sees what you hover, like on the skill tree.
        [HarmonyPostfix, HarmonyPatch(typeof(SkillPopUp), nameof(SkillPopUp.Show), new[] { typeof(PermanentUpgrade) })]
        public static void PermPopUpShowPostfix(SkillPopUp __instance, PermanentUpgrade __0)
        {
            try { Extras.LocalPermPopupShown(__instance, __0); } catch { }
        }

        // ---------------------------------------------------------------- friendly fire

        [HarmonyPostfix, HarmonyPatch(typeof(GameManager), nameof(GameManager.OnBulletImpact))]
        public static void BulletImpactPostfix(Vector3 point)
        {
            try { Extras.OnBulletImpact(point); } catch { }
        }

        // ---------------------------------------------------------------- saves

        /// While you're a client, the shared session must not overwrite your single-player save.
        [HarmonyPrefix, HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save)), AllOverloads]
        public static bool SavePrefix() => !(Net.IsClient && Net.Connected) && !HostSave.UsingHostSave;

        /// Same for Save(out string error), which the game's day end / death use since its update: when we skip it,
        /// report success, or the game shows a "couldn't save" pop-up.
        [HarmonyPrefix, HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save)), AllOverloads]
        public static bool SaveWithResultPrefix(ref bool __result)
        {
            if (SavePrefix()) return true;
            __result = true;
            return false;
        }

        /// Multiplayer save slots: redirect the one path every save read/write uses.
        [HarmonyPostfix, HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SavePath), MethodType.Getter)]
        public static void SavePathPostfix(ref string __result)
        {
            if (SaveSlots.Current > 0) __result = SaveSlots.PathFor(SaveSlots.Current);
        }

        /// The menu is (re)built: CONTINUE must look at the local save, not a multiplayer slot.
        [HarmonyPrefix, HarmonyPatch(typeof(Menu), nameof(Menu.Awake))]
        public static void MenuAwakePrefix()
        {
            try { if (!Net.Active) SaveSlots.ResetToLocal(true); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        /// The game saved (end of day): save the multiplayer stats next to it.
        [HarmonyPostfix, HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save)), AllOverloads]
        public static void SavePostfix()
        {
            try { PlayerStats.OnGameSaved(); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        /// Back on the main menu: give a former joiner their own save back.
        [HarmonyPostfix, HarmonyPatch(typeof(Menu), nameof(Menu.Awake))]
        public static void MenuAwakePostfix()
        {
            try { Palette.RefreshCursor(); } catch { }
            try { HostSave.OnMainMenu(); ContinueSync.ResetAction(); } catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        // ---------------------------------------------------------------- input while the F8 panel is open

        [HarmonyPostfix, HarmonyPatch(typeof(Player), nameof(Player.FixedUpdate))]
        public static void PlayerMovePostfix(Player __instance) => VacuumPull.AfterMove(__instance);

        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.RotateCamera))]
        public static bool RotateCameraPrefix() => !MpManager.MenuOpen && !Spectate.Dead;

        /// Stop WASD/jump reaching the player while typing in the panel.
        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.ReadInput))]
        public static bool ReadInputPrefix(Player __instance)
        {
            if (!MpManager.MenuOpen && !Spectate.Dead) return true;
            try { __instance.input = Vector3.zero; } catch { }
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Inventory), nameof(Inventory.Update))]
        public static bool InventoryUpdatePrefix() => !MpManager.MenuOpen && !Spectate.Dead;
    }
}
