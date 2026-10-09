// Copied verbatim from the season guide (chapter 9, API). Do not edit: fleets compile against this.
namespace Shtemeri.Api;

/// <summary>
/// Base class of a fleet. You write ONE class deriving from Shtemer; the server creates one instance of it
/// per shtemer in your fleet. Instances share nothing (no memory, no vision) - they talk only through messages.
/// Instance fields keep their values between ticks. The instance with <see cref="ISelf.Index"/> == <see cref="Fleet.Admiral"/> is the admiral.
///
/// Per tick, for every living instance, the server calls in this order:
/// OnMessage (once per message received), OnHit, OnCollision (events from the previous tick), then OnTick.
/// All calls of one instance in one tick share a single instruction budget (<see cref="IRules.InstructionBudget"/>).
/// When the budget runs out the instance's tick ends immediately; commands issued so far still count.
/// </summary>
public abstract class Shtemer
{
    /// <summary>Called once, before the first OnTick of this instance.</summary>
    public virtual void OnStart(ISelf me) { }

    /// <summary>Called every tick while this shtemer is alive.</summary>
    public abstract void OnTick(ISelf me, ISenses senses);

    /// <summary>A message from a fleet mate, sent during the previous tick.</summary>
    public virtual void OnMessage(ISelf me, Message msg) { }

    /// <summary>This shtemer took projectile or splash damage during the previous tick.</summary>
    public virtual void OnHit(ISelf me, HitEvent e) { }

    /// <summary>This shtemer bumped into a wall, an obstacle or another shtemer during the previous tick.</summary>
    public virtual void OnCollision(ISelf me, CollisionEvent e) { }
}

/// <summary>Fleet-wide constants.</summary>
public static class Fleet
{
    /// <summary>Index of the admiral instance.</summary>
    public const int Admiral = 0;
}

public enum Weapon
{
    /// <summary>Fast, weak, plenty of ammo.</summary>
    Pistol = 0,
    /// <summary>Slow, strong, splash damage, explodes at the target point. Scarce.</summary>
    Rocket = 1,
}

public enum BlipSize { Small = 0, Medium = 1, Large = 2 }

public enum ContactKind { Shtemer = 0, Loot = 1, Rocket = 2 }

public enum LootKind { None = 0, Ammo = 1, Rockets = 2, Repair = 3 }

public enum CollisionKind { Wall = 0, Obstacle = 1, Shtemer = 2 }
