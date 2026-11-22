using LiteNetLib.Utils;
using UnityEngine;

namespace CanEmAllMP
{
    // Every packet starts with one of these bytes.
    public enum Msg : byte
    {
        Hello = 1,          // client -> host: name, mod version
        Welcome = 2,        // host -> client: your player id, host's game state
        PlayerJoined = 3,   // host -> all: id, name
        PlayerLeft = 4,     // host -> all: id
        PlayerState = 5,    // anyone -> host -> others: id, pos, yaw, pitch, weapon (unreliable)
        MoneyDelta = 6,     // client -> host: money earned/spent locally
        MoneyTotal = 7,     // host -> clients: authoritative shared money
        DayTimer = 8,       // host -> clients: GameManager.Timer (unreliable)
        NextDay = 9,        // host -> clients: host pressed "next day"
        RequestNextDay = 10,// client -> host: client pressed "next day"
        EnemySnapshot = 11, // host -> clients: all live enemies (unreliable)
        EnemyHit = 12,      // client -> host: client damaged a puppet enemy
        Chat = 13,
        Pause = 14,          // anyone: bool paused, int who
        ElevatorReady = 15,  // client -> host: byte kind, bool ready
        ElevatorGo = 16,     // host -> clients: byte kind (release the elevator)
        SkillPurchased = 17, // anyone: int skillId, int who
        SkillState = 18,     // host -> new client: all purchased skill ids
        ElevatorStatus = 19, // host -> clients: waiting text ("" = none), byte hostElevatorState
        HostSave = 20,
        EnemyDamaged = 21,   // host -> clients: replay an Enemy.Damage (args + host's final damage)
        EnemyGone = 22,      // host -> clients: creature no longer exists
        EnemyOwner = 23,     // host -> clients: ushort id, byte holder (255 = nobody), byte epoch, byte answerTo, byte answerSeq
        EnemyClaim = 24,     // client -> host: ushort id, byte seq, bool force (hand grab) - always answered with EnemyOwner
        EnemyOwned = 25,     // client -> host: byte n, n x (ushort id, vec pos, quat rot, bool active) of bodies I hold
        EnemyConsumed = 26,  // (unused)
        EnemyProcessed = 28, // (unused)
        Fx = 29,             // anyone: byte type (1 small explosion, 2 big explosion, 3 shockwave), vec pos, float radius
        EnemyBurn = 30,      // client -> host: ushort id, float amount
        CanSnapshot = 31,    // host -> clients: changed cans (id, pos, rot, price, qty, flags, texture, holder, epoch)
        CanGone = 32,        // host -> clients: can no longer exists
        CanOwner = 33,       // host -> clients: same layout as EnemyOwner
        CanClaim = 34,       // client -> host: same layout as EnemyClaim
        CanOwned = 35,       // client -> host: same layout as EnemyOwned
        CanRewarded = 36,    // client -> host: my can went into the reward machine (ushort id, vec pos)
        Suicide = 37,        // anyone: float delay, int mailId, int who
        SkillBuyRequest = 38,// client -> host: int skillId
        ContinueReady = 39,  // client -> host: I pressed continue
        ContinueGo = 40,     // host -> clients: everyone is ready, continue now
        ContinueStatus = 41, // host -> clients: "Waiting for X to continue" ("" = none)
        SkillBuyResult = 42, // host -> requester: int skillId, string reason (only sent when it failed)
        Announce = 43,       // anyone: string shown to everyone
        FriendlyFire = 44,   // host -> clients: bool
        PlayerHit = 45,      // client -> host: int target | host -> target: int target, int shooter
        Cursor = 46,         // anyone: int id, float x, float y (skill-tree space; NaN = not on the screen)
        EnemyRelease = 47,   // client -> host: ushort id, vec pos, quat rot, vec velocity (I let go of it, it's yours again)
        CanRelease = 48,     // client -> host: same for cans
        PlayerColor = 49,    // client -> host: int id, byte colour | host -> all: int id, byte colour
        Lighting = 50,       // host -> clients: bool isDay
        MoneyPopup = 51,     // anyone: int amount, byte type (show the "+$" pop-up for money someone else earned)
        GambleSpin = 52,     // anyone: int spinner, int seq, long bet, byte n, n x int prize (replay this slot machine spin)
        GambleDone = 53,     // anyone: int spinner, int seq, int who (who has finished seeing that spin)
        GambleBet = 54,      // anyone: int who, long bet (the slot machine's bet box)
        HostStats = 55,      // host -> clients: long SaveData.totalMoney, int SaveData.upgradePoints (sell factory screen)
        FactorySold = 56,    // host -> clients: the host sold the factory, run PermanentUpgradeManager.Sell too
        PermBuyRequest = 57, // client -> host: int skillId (a permanent upgrade)
        ExitDoorOpen = 59,   // anyone: the exit door was opened (host relays)
        ExitReady = 60,      // client -> host: bool (standing in the exit elevator)
        ExitGo = 61,         // host -> clients: everyone's in, start the exit elevator
        ExitStatus = 62,     // host -> clients: string waiting text
        CorpsePose = 63,
        PauseMode = 70,
        GambleStat = 71,
        EnemyDeform = 73,    // host -> clients: ushort id, byte n, n x Vector4 (the creature's damage / broken-part shader slots)
        MailOpen = 72,       // anyone: int mailId, int who (open that letter in the mail tab; host relays)     // client -> host: long bet, long won (one finished spin of mine)      // host -> clients: bool (true = shared pause, false = everyone pauses only their own menu)
        CrateSnapshot = 65,  // host -> clients: byte n, n x (ushort id, pos, rot, bool active, byte holder, byte epoch)
        CrateOwner = 66,     // host -> clients: same layout as CanOwner
        CrateClaim = 67,     // client -> host: same layout as CanClaim
        CrateOwned = 68,     // client -> host: same layout as CanOwned (holder streams it)
        CrateRelease = 69,   // client -> host: same layout as CanRelease
        PlayerStats = 64,    // host -> clients: byte n, n x (int id, int kills, int deaths, int ping to host)     // host -> clients: int owner, byte n, n x (pos xyz, rot xyzw): a dead player's ragdoll
        PermBought = 58,     // host -> clients: int skillId, int who (apply that permanent upgrade)
        TruckState = 27,     // host -> clients: truck body/door transform + facility door animator state       // host -> new client: gzipped SaveData json chunk (int total, int offset, bytes)
    }

    public struct PlayerSnapshot
    {
        public int Id;
        public Vector3 Pos;
        public float Yaw, Pitch;
        public int Weapon;   // index into Inventory.allWeapons (-1 = none)
        public int Action;   // bits 0-1: vacuum state (0 idle, 1 sucking, 2 throwing); bit 2: fire held
        public int Gestures; // low nibble: attack count, high nibble: point count (see Gestures)
        public int Emote;    // 0 none, 1.. = DanceN (set by the models add-on's emote wheel)

        public void Write(NetDataWriter w)
        {
            w.Put(Id); w.Put(Pos.x); w.Put(Pos.y); w.Put(Pos.z);
            w.Put(Yaw); w.Put(Pitch); w.Put((sbyte)Weapon); w.Put((byte)Action); w.Put((byte)Gestures); w.Put((byte)Emote);
        }

        public static PlayerSnapshot Read(NetDataReader r)
        {
            var s = new PlayerSnapshot();
            s.Id = r.GetInt();
            s.Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            s.Yaw = r.GetFloat(); s.Pitch = r.GetFloat(); s.Weapon = r.GetSByte(); s.Action = r.GetByte(); s.Gestures = r.GetByte(); s.Emote = r.GetByte();
            return s;
        }
    }

    public static class WriterExt
    {
        public static void PutVec(this NetDataWriter w, Vector3 v) { w.Put(v.x); w.Put(v.y); w.Put(v.z); }
        public static Vector3 GetVec(this NetDataReader r) => new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        public static void PutQuat(this NetDataWriter w, Quaternion q) { w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w); }
        public static Quaternion GetQuat(this NetDataReader r) => new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
    }
}
