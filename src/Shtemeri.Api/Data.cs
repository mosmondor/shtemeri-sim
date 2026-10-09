// Copied verbatim from the season guide (chapter 9, API). Do not edit: fleets compile against this.
namespace Shtemeri.Api;

/// <summary>Something seen in low resolution. You do not know what it is or whose it is until you zoom.</summary>
public sealed class Blip
{
    /// <summary>
    /// Tracking id, private to the observing shtemer. Stays the same for as long as the object stays visible to it;
    /// an object that was lost and seen again gets a new id.
    /// </summary>
    public int Id { get; }
    /// <summary>Approximate position; the error grows with distance.</summary>
    public Vec2 Position { get; }
    public double Distance { get; }
    /// <summary>Direction from the observer to the blip, radians.</summary>
    public double Bearing { get; }
    /// <summary>Shtemers are Medium. Loot boxes and rockets in flight are Small. Pistol bullets are not visible.</summary>
    public BlipSize Size { get; }

    public Blip(int id, Vec2 position, double distance, double bearing, BlipSize size)
    {
        Id = id; Position = position; Distance = distance; Bearing = bearing; Size = size;
    }
}

/// <summary>The detailed answer to a Zoom.</summary>
public sealed class Contact
{
    /// <summary>The blip this answer belongs to.</summary>
    public int BlipId { get; }
    public ContactKind Kind { get; }
    /// <summary>Exact position.</summary>
    public Vec2 Position { get; }
    /// <summary>Metres per second.</summary>
    public Vec2 Velocity { get; }
    /// <summary>For shtemers: is it from your own fleet.</summary>
    public bool IsAlly { get; }
    /// <summary>For shtemers: number of the fleet (0..fleets-1). -1 otherwise.</summary>
    public int FleetId { get; }
    /// <summary>For shtemers: index inside its fleet (0 = that fleet's admiral). -1 otherwise.</summary>
    public int Index { get; }
    /// <summary>For shtemers: health. 0 otherwise.</summary>
    public double Health { get; }
    /// <summary>For loot boxes: what is inside. None otherwise.</summary>
    public LootKind Loot { get; }

    public Contact(int blipId, ContactKind kind, Vec2 position, Vec2 velocity, bool isAlly, int fleetId, int index, double health, LootKind loot)
    {
        BlipId = blipId; Kind = kind; Position = position; Velocity = velocity;
        IsAlly = isAlly; FleetId = fleetId; Index = index; Health = health; Loot = loot;
    }
}

/// <summary>
/// The safe zone is a circle that shrinks in stages: it holds, then moves and shrinks towards the next circle.
/// Outside the zone a shtemer loses <see cref="DamagePerTick"/> health every tick. In the end the zone closes completely.
/// </summary>
public readonly struct Zone
{
    public Vec2 Center { get; }
    public double Radius { get; }
    /// <summary>The circle the zone is shrinking (or will shrink) to.</summary>
    public Vec2 NextCenter { get; }
    public double NextRadius { get; }
    /// <summary>Ticks until the zone starts to shrink towards the next circle; 0 while it is shrinking.</summary>
    public int TicksToShrinkStart { get; }
    /// <summary>Ticks until the zone reaches the next circle.</summary>
    public int TicksToShrinkEnd { get; }
    /// <summary>Health lost per tick outside the zone right now. Grows with every stage.</summary>
    public double DamagePerTick { get; }

    public Zone(Vec2 center, double radius, Vec2 nextCenter, double nextRadius, int ticksToShrinkStart, int ticksToShrinkEnd, double damagePerTick)
    {
        Center = center; Radius = radius; NextCenter = nextCenter; NextRadius = nextRadius;
        TicksToShrinkStart = ticksToShrinkStart; TicksToShrinkEnd = ticksToShrinkEnd; DamagePerTick = damagePerTick;
    }

    public bool Contains(Vec2 point) => point.DistanceTo(Center) <= Radius;
    /// <summary>Will the point still be safe once the zone reaches the next circle.</summary>
    public bool NextContains(Vec2 point) => point.DistanceTo(NextCenter) <= NextRadius;

    /// <summary>
    /// Where the zone will be in <paramref name="ticksAhead"/> ticks, assuming the schedule known now: it holds until
    /// TicksToShrinkStart, then moves linearly to the next circle, reaching it at TicksToShrinkEnd. Further ahead than that
    /// the answer is the next circle itself (the stage after it is not known yet).
    /// </summary>
    public (Vec2 Center, double Radius) At(int ticksAhead)
    {
        if (ticksAhead <= TicksToShrinkStart || TicksToShrinkEnd <= TicksToShrinkStart) return (Center, Radius);
        if (ticksAhead >= TicksToShrinkEnd) return (NextCenter, NextRadius);
        double t = (ticksAhead - TicksToShrinkStart) / (double)(TicksToShrinkEnd - TicksToShrinkStart);
        return (Vec2.Lerp(Center, NextCenter, t), Radius + (NextRadius - Radius) * t);
    }

    /// <summary>Will the point be inside the zone in <paramref name="ticksAhead"/> ticks (see <see cref="At"/>).</summary>
    public bool ContainsAt(Vec2 point, int ticksAhead)
    {
        var (c, r) = At(ticksAhead);
        return point.DistanceTo(c) <= r;
    }
}

public readonly struct Obstacle
{
    public Vec2 Center { get; }
    public double Radius { get; }
    public Obstacle(Vec2 center, double radius) { Center = center; Radius = radius; }
}

/// <summary>A message from a fleet mate.</summary>
public sealed class Message
{
    /// <summary>Index of the sender.</summary>
    public int From { get; }
    /// <summary>Whatever the sender put there; the meaning is up to your code.</summary>
    public int Type { get; }
    public IReadOnlyList<double> Data { get; }

    public Message(int from, int type, IReadOnlyList<double> data) { From = from; Type = type; Data = data; }

    /// <summary>Reads Data[offset], Data[offset + 1] as a vector.</summary>
    public Vec2 GetVec(int offset = 0) => new(Data[offset], Data[offset + 1]);
}

public readonly struct HitEvent
{
    public double Damage { get; }
    public Weapon Weapon { get; }
    /// <summary>True for splash damage of an explosion, false for a direct hit.</summary>
    public bool Splash { get; }
    /// <summary>Unit vector the projectile was travelling along (the shooter is in the opposite direction). For splash: from the explosion towards you.</summary>
    public Vec2 Direction { get; }

    public HitEvent(double damage, Weapon weapon, bool splash, Vec2 direction)
    {
        Damage = damage; Weapon = weapon; Splash = splash; Direction = direction;
    }
}

public readonly struct CollisionEvent
{
    public CollisionKind Kind { get; }
    /// <summary>Unit vector pointing away from the thing you hit.</summary>
    public Vec2 Normal { get; }

    public CollisionEvent(CollisionKind kind, Vec2 normal) { Kind = kind; Normal = normal; }
}
