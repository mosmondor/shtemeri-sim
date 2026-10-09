// Copied verbatim from the season guide (chapter 9, API). Do not edit: fleets compile against this.
namespace Shtemeri.Api;

/// <summary>The shtemer's own state and its commands. Valid only during the callback it was passed to.</summary>
public interface ISelf
{
    // ---- identity ----

    /// <summary>Index of this instance in the fleet, 0..FleetSize-1. 0 is the admiral.</summary>
    int Index { get; }
    int FleetSize { get; }
    bool IsAdmiral { get; }

    /// <summary>Whether the fleet mate with the given index is still alive (a fleet always knows who is alive, not where).</summary>
    bool IsAllyAlive(int index);

    // ---- state ----

    /// <summary>Current tick, starting at 0.</summary>
    int Tick { get; }
    Vec2 Position { get; }
    /// <summary>Terrain height under the shtemer, metres.</summary>
    double Altitude { get; }
    /// <summary>Metres per second.</summary>
    Vec2 Velocity { get; }
    double Health { get; }
    /// <summary>Energy pays for <see cref="Zoom"/>. Regenerates every tick.</summary>
    double Energy { get; }
    /// <summary>Direction the vision cone currently points, radians.</summary>
    double LookAngle { get; }

    int Ammo(Weapon weapon);
    /// <summary>Ticks until the weapon can fire again; 0 = ready.</summary>
    int Cooldown(Weapon weapon);

    /// <summary>The static map: terrain heights and obstacles. Fully known to everyone.</summary>
    IArena Arena { get; }
    /// <summary>The numbers of this match (speeds, ranges, damage, costs).</summary>
    IRules Rules { get; }
    /// <summary>Deterministic random numbers, separate for every instance.</summary>
    IRandom Random { get; }

    // ---- commands ----

    /// <summary>
    /// Sets the thrust: direction of acceleration, magnitude 0..1 (longer vectors are clamped to 1).
    /// The shtemer has inertia and drag: it speeds up towards the thrust direction, and coasts to a stop with Vec2.Zero.
    /// The setting persists until changed.
    /// </summary>
    void Thrust(Vec2 direction);

    /// <summary>Sets the desired look angle (radians). The vision cone turns towards it at <see cref="IRules.LookTurnRate"/> per tick. Persists until changed.</summary>
    void Look(double angle);

    /// <summary>Same as Look, towards a point in the arena.</summary>
    void LookAt(Vec2 point);

    /// <summary>
    /// Fires at a point in the arena. The projectile flies in a straight line from the muzzle towards that point
    /// (terrain height at the target included) and keeps going until it hits something or runs out of range;
    /// a rocket explodes when it reaches the target point. Terrain, obstacles and shtemers (friends too) stop projectiles.
    /// Returns false, and does nothing, when the weapon is cooling down or out of ammo. One shot per weapon per tick.
    /// </summary>
    bool Fire(Weapon weapon, Vec2 target);

    /// <summary>
    /// Requests details about a blip (use <see cref="Blip.Id"/> from this tick's senses). Costs <see cref="IRules.ZoomCost"/> energy.
    /// The answer arrives in next tick's <see cref="ISenses.Contacts"/>, if the object is still visible then.
    /// Returns false when there is not enough energy, the id is unknown, or the per-tick zoom limit is reached.
    /// </summary>
    bool Zoom(int blipId);

    /// <summary>
    /// Sends a message to one fleet mate. It is delivered at the start of the next tick, anywhere in the arena.
    /// At most <see cref="IRules.MaxMessageData"/> numbers per message and <see cref="IRules.MaxMessagesPerTick"/> messages per tick; the rest is dropped.
    /// </summary>
    void Send(int toIndex, int type, params double[] data);

    /// <summary>Sends a message to every other living fleet mate. Counts as one message.</summary>
    void Broadcast(int type, params double[] data);

    /// <summary>Writes a line to this shtemer's telemetry log (what the owner reads after the match). Limited per tick.</summary>
    void Log(string text);

    /// <summary>
    /// War cry: the shtemer shouts one of the fixed messages (index 0..<see cref="WarCry.Count"/>-1 into the table in the guide).
    /// It shows up as a small speech bubble above the shtemer in the viewer and is written to the replay; it has no effect on the match.
    /// At most one shout per shtemer every <see cref="IRules.WarCryCooldownTicks"/> ticks (20 seconds).
    /// Returns false, and does nothing, for an index outside the table or while the shtemer is still on cooldown.
    /// </summary>
    // Default body: a class compiled before war cry that implements ISelf itself still loads (REVIEW-6); the real ISelf overrides it.
    bool Shout(int message) => false;
}

/// <summary>The war cry table is fixed: messages are numbered 0..Count-1 and the numbers never change (new ones are only added at the end).</summary>
public static class WarCry
{
    /// <summary>Number of messages in the table (valid indices are 0..Count-1).</summary>
    public const int Count = 48;
}

/// <summary>What the shtemer perceives this tick.</summary>
public interface ISenses
{
    /// <summary>
    /// Free low-resolution vision: everything inside the vision cone (and anything very close, all around)
    /// that is not hidden behind terrain or an obstacle. A blip tells you something is there and roughly how big - not what it is.
    /// </summary>
    IReadOnlyList<Blip> Blips { get; }

    /// <summary>Detailed answers to the <see cref="ISelf.Zoom"/> calls made during the previous tick.</summary>
    IReadOnlyList<Contact> Contacts { get; }

    /// <summary>The shrinking safe zone. Known to everyone.</summary>
    Zone Zone { get; }
}

public interface IArena
{
    /// <summary>The arena is a square [0, Size] x [0, Size], walled.</summary>
    double Size { get; }
    /// <summary>Terrain height at a point, metres.</summary>
    double HeightAt(Vec2 point);
    /// <summary>Rocks: impassable, they block vision and projectiles.</summary>
    IReadOnlyList<Obstacle> Obstacles { get; }
    /// <summary>True when the point is outside the arena or inside an obstacle.</summary>
    bool IsBlocked(Vec2 point);
    /// <summary>True when a shtemer standing at <paramref name="from"/> could see a shtemer standing at <paramref name="to"/> (terrain and obstacles only; range and cone are not checked).</summary>
    bool HasLineOfSight(Vec2 from, Vec2 to);
}

public interface IRandom
{
    /// <summary>Uniform in [0, 1).</summary>
    double NextDouble();
    /// <summary>Uniform integer in [0, maxExclusive).</summary>
    int Next(int maxExclusive);
    /// <summary>Uniform in [min, max).</summary>
    double Range(double min, double max);
}

/// <summary>All numbers of the match. Distances in metres, time in ticks unless said otherwise.</summary>
public interface IRules
{
    int TicksPerSecond { get; }
    int MaxTicks { get; }
    double ArenaSize { get; }
    int InstructionBudget { get; }

    double ShtemerRadius { get; }
    double MaxHealth { get; }
    /// <summary>Acceleration at full thrust, m/s².</summary>
    double ThrustAcceleration { get; }
    /// <summary>Drag coefficient, 1/s. Top speed on flat ground = ThrustAcceleration / Drag.</summary>
    double Drag { get; }
    double TopSpeed { get; }
    /// <summary>Downhill pull per unit of slope, m/s². Uphill is slower, downhill faster.</summary>
    double SlopeAcceleration { get; }

    double VisionRange { get; }
    /// <summary>Half of the vision cone's opening angle, radians.</summary>
    double VisionHalfAngle { get; }
    /// <summary>Anything closer than this is sensed all around, even behind cover.</summary>
    double ProximityRange { get; }
    /// <summary>Radians per tick.</summary>
    double LookTurnRate { get; }
    /// <summary>Blip position error grows with distance: up to Distance * BlipNoise metres.</summary>
    double BlipNoise { get; }

    double MaxEnergy { get; }
    double EnergyRegen { get; }
    double ZoomCost { get; }
    int MaxZoomsPerTick { get; }

    double PistolSpeed { get; }
    double PistolDamage { get; }
    int PistolCooldown { get; }
    double PistolRange { get; }
    int PistolStartAmmo { get; }
    int PistolMaxAmmo { get; }

    double RocketSpeed { get; }
    /// <summary>Extra damage to the shtemer a rocket hits directly (on top of splash).</summary>
    double RocketDirectDamage { get; }
    /// <summary>Splash damage at the centre of the explosion; falls linearly to 0 at RocketSplashRadius.</summary>
    double RocketSplashDamage { get; }
    double RocketSplashRadius { get; }
    int RocketCooldown { get; }
    double RocketRange { get; }
    int RocketStartAmmo { get; }
    int RocketMaxAmmo { get; }

    double PickupRadius { get; }
    int LootAmmoAmount { get; }
    int LootRocketAmount { get; }
    double LootRepairAmount { get; }

    int MaxMessageData { get; }
    int MaxMessagesPerTick { get; }
    int MaxLogLinesPerTick { get; }

    /// <summary>Minimum ticks between two war cries of one shtemer (<see cref="ISelf.Shout"/>).</summary>
    int WarCryCooldownTicks => 400;   // default body for classes compiled before war cry that implement IRules (REVIEW-6)
}
