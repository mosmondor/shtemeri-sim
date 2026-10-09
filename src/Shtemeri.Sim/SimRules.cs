using Shtemeri.Api;

namespace Shtemeri.Sim;

/// <summary>
/// All numbers of the season (guide, chapter 8). The <see cref="IRules"/> part is what fleets see as <c>me.Rules</c>;
/// the rest are season facts the server does not expose to fleets. Values that the guide does not give are calibrated
/// from replays and telemetry (see NOTES.md) and are marked as such.
/// </summary>
public sealed class SimRules : IRules
{
    public int TicksPerSecond { get; init; } = 20;
    public int MaxTicks { get; init; } = 3600;
    public double ArenaSize { get; init; } = 100;
    public int InstructionBudget { get; init; } = 50000;

    public double ShtemerRadius { get; init; } = 1;
    public double MaxHealth { get; init; } = 250;
    public double ThrustAcceleration { get; init; } = 12;
    public double Drag { get; init; } = 1.5;
    public double TopSpeed { get; init; } = 8;
    public double SlopeAcceleration { get; init; } = 6;

    public double VisionRange { get; init; } = 32;
    public double VisionHalfAngle { get; init; } = 0.87;
    public double ProximityRange { get; init; } = 6;
    public double LookTurnRate { get; init; } = 0.21;
    public double BlipNoise { get; init; } = 0.03;

    public double MaxEnergy { get; init; } = 100;
    public double EnergyRegen { get; init; } = 0.4;
    public double ZoomCost { get; init; } = 8;
    public int MaxZoomsPerTick { get; init; } = 3;

    public double PistolSpeed { get; init; } = 40;
    public double PistolDamage { get; init; } = 4;
    public int PistolCooldown { get; init; } = 8;
    public double PistolRange { get; init; } = 30;
    public int PistolStartAmmo { get; init; } = 40;
    public int PistolMaxAmmo { get; init; } = 120;

    public double RocketSpeed { get; init; } = 18;
    public double RocketDirectDamage { get; init; } = 25;
    public double RocketSplashDamage { get; init; } = 35;
    public double RocketSplashRadius { get; init; } = 5;
    public int RocketCooldown { get; init; } = 50;
    public double RocketRange { get; init; } = 45;
    public int RocketStartAmmo { get; init; } = 1;
    public int RocketMaxAmmo { get; init; } = 6;

    public double PickupRadius { get; init; } = 1.8;
    public int LootAmmoAmount { get; init; } = 30;
    public int LootRocketAmount { get; init; } = 2;
    public double LootRepairAmount { get; init; } = 50;

    public int MaxMessageData { get; init; } = 8;
    public int MaxMessagesPerTick { get; init; } = 8;
    public int MaxLogLinesPerTick { get; init; } = 20;
    public int WarCryCooldownTicks { get; init; } = 400;

    // ---- season facts not in IRules (guide table) ----
    public int FleetSize { get; init; } = 4;
    public double SpawnRingRadius { get; init; } = 40;
    public double ShtemerHeight { get; init; } = 2;
    public double EyeHeight { get; init; } = 1.6;
    public double MuzzleHeight { get; init; } = 1.2;
    public double TargetHeight { get; init; } = 1.0;
    public double ParkingSpeed { get; init; } = 0.5;
    public double ParkingSlopeForce { get; init; } = 3;
    public int LootInitialBoxes { get; init; } = 10;
    public int LootSpawnInterval { get; init; } = 200;
    public int LootSpawnCount { get; init; } = 3;
    public int LootMaxBoxes { get; init; } = 16;
    public double ZoneStartRadius { get; init; } = 50;
    public double ZoneShrinkFactor { get; init; } = 0.62;
    public int ZoneStages { get; init; } = 6;
    public int ZoneHoldTicks { get; init; } = 200;
    public int ZoneShrinkTicks { get; init; } = 250;
    public double ZoneDamagePerStage { get; init; } = 0.3;
    public int MaxLogLineLength { get; init; } = 200;
    public int CostLineOfSight { get; init; } = 60;
    public int CostHeightAt { get; init; } = 5;
    public int CostLog { get; init; } = 20;

    // ---- calibrated (NOTES.md, stage 2) ----
    /// <summary>Projectiles appear this far from the shooter's centre, towards the target.</summary>
    public double MuzzleOffset { get; init; } = 1.1;
    /// <summary>Collision checks along a projectile's path are made at most this far apart.</summary>
    public double ProjectileSubstep { get; init; } = 0.5;
    /// <summary>Step of the terrain sampling in line-of-sight tests.</summary>
    public double LosStep { get; init; } = 0.25;
    /// <summary>Loot spawns uniformly inside this fraction of the current zone radius.</summary>
    public double LootSpawnZoneFraction { get; init; } = 0.9;
    /// <summary>Minimum clearance between a spawned box and the edge of a rock.</summary>
    public double LootRockClearance { get; init; } = 1.5;
    /// <summary>Initial boxes keep this far from the arena wall.</summary>
    public double LootWallMargin { get; init; } = 3.0;
    /// <summary>Kind probabilities of spawned boxes: Ammo, Rockets, Repair.</summary>
    public double[] LootKindWeights { get; init; } = { 0.457, 0.298, 0.245 };
    /// <summary>Fleet members sit this far from the fleet centre (radially in/out and tangentially).</summary>
    public double SpawnMemberOffset { get; init; } = 2 * Math.Sqrt(2);

    public static SimRules Season { get; } = new();
}
