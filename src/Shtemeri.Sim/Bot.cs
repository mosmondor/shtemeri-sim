using Shtemeri.Api;

namespace Shtemeri.Sim;

/// <summary>Engine-side state of one shtemer.</summary>
internal sealed class Bot
{
    public int Gid, Slot, Index;
    public Shtemer Code = null!;
    public Agent Agent = null!;
    public Rng Random = null!;
    public bool Alive = true, Started;

    public Vec2 Pos, Vel, Thrust;
    public double Hp, Energy, Look, LookTarget;
    public readonly int[] Ammo = new int[2];
    public readonly int[] Cd = new int[2];
    public int NextShoutTick;

    // vision: object key -> blip id, rebuilt every tick; ids stay while the object stays in view
    public Dictionary<long, int> BlipIds = new(), NextBlipIds = new();
    public int NextBlipId = 1;
    public readonly List<Blip> Blips = new();
    public readonly Dictionary<int, long> BlipKeyById = new();
    public readonly List<(int BlipId, long Key)> ZoomsRequested = new();
    public readonly List<(int BlipId, long Key)> ZoomsToAnswer = new();
    public readonly List<Contact> Contacts = new();

    // events delivered at the start of the next tick
    public List<Message> Inbox = new(), NextInbox = new();
    public List<HitEvent> Hits = new(), NextHits = new();
    public List<CollisionEvent> Collisions = new(), NextCollisions = new();

    // per-tick command bookkeeping
    public bool FiredPistol, FiredRocket;
    public int ZoomsThisTick, MessagesThisTick, LogLinesThisTick;
    public readonly List<(Weapon W, Vec2 Target)> Shots = new();

    public int LastDamager = -1;
    public string LastCause = "zone";
    public int DeathTick = -1;
    public long BudgetUsed;
    public TelemetryCommands? Tel;
}
