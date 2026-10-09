using Shtemeri.Api;
using Shtemeri.Sim.Hosting;
using Shtemeri.Sim.Runtime;

namespace Shtemeri.Sim;

/// <summary>One fleet taking part in a match.</summary>
public sealed class FleetEntry
{
    public required string Name { get; init; }
    public string Owner { get; init; } = "local";
    public required FleetProgram Program { get; init; }
}

public sealed class MatchOptions
{
    /// <summary>Keep frames and events for a replay v1 file.</summary>
    public bool RecordReplay { get; init; } = true;
    /// <summary>Enforce the instruction budget (approximate meter). Off: fleets run unmetered.</summary>
    public bool EnforceBudget { get; init; } = true;
    /// <summary>Stop after this many ticks (default: the season's MaxTicks).</summary>
    public int? MaxTicks { get; init; }
    /// <summary>Seed of the engine's own randomness (blip noise, generated boxes, me.Random). Default: the setup seed.
    /// Different values replay the same arena, zone and box schedule with different luck.</summary>
    public ulong? RngSeed { get; init; }
    /// <summary>Slots whose telemetry (server telemetry v1 layout) is recorded.</summary>
    public IReadOnlyCollection<int> TelemetrySlots { get; init; } = Array.Empty<int>();
}

/// <summary>Per-fleet totals, the same fields as replay v1 <c>result.stats</c>.</summary>
public sealed class FleetStats
{
    public int Fleet, Place, Kills, ShotsPistol, ShotsRocket, Hits, Loot, SurvivedTicks, BudgetExceeded, Errors;
    public double DamageDealt, DamageTaken;
    public bool Crashed;
    public long BudgetSum; public int BudgetTicks; public long BudgetMax;
    public string? FirstError;
}

/// <summary>
/// The deterministic match engine. One call to <see cref="Run"/> plays a whole match.
///
/// Tick order (calibrated against server telemetry, NOTES.md):
/// 1. every living shtemer gets its senses (blips from the state at the start of the tick, answers to last tick's zooms),
///    then OnStart (first tick), OnMessage, OnHit, OnCollision, OnTick, sharing one instruction budget;
/// 2. projectiles are created from the shooters' start-of-tick positions;
/// 3. energy regenerates, the vision cone turns, shtemers move and collide;
/// 4. projectiles fly (hits are checked against the moved shtemers), rockets explode;
/// 5. zone damage, pickups, deaths and box drops, eliminations, scheduled boxes, cooldowns.
/// </summary>
public sealed class Match
{
    internal readonly SimRules Rules;
    internal readonly Arena Arena;
    internal readonly IArena FleetArena;
    internal readonly Bot[] Bots;
    internal int Tick;
    /// <summary>Receives every <c>me.Log</c> line: (gid, tick, text).</summary>
    public Action<int, int, string>? LogLine { get; set; }

    private readonly MatchSetup _setup;
    private readonly IReadOnlyList<FleetEntry> _fleets;
    private readonly MatchOptions _opt;
    private readonly Rng _rng;
    private readonly ReplayRecorder? _rec;
    private readonly FleetStats[] _stats;
    private readonly List<Projectile> _projectiles = new();
    private readonly List<Box> _boxes = new();
    private readonly bool[] _fleetAlive;
    private readonly List<int> _placementsFromLast = new();   // slots in order of elimination (last place first)
    private int _nextProjectileId, _nextBoxId;
    private readonly double _dt;
    private readonly int _maxTicks;
    private readonly Senses _senses = new();

    private sealed class Projectile
    {
        public int Id, Kind, Owner;
        public double X, Y, Z, Dx, Dy, Dz, Speed, Traveled, MaxTravel;
        public bool Alive = true;
    }

    private sealed class Box { public int Id; public LootKind Kind; public Vec2 Pos; }

    public Match(MatchSetup setup, IReadOnlyList<FleetEntry> fleets, SimRules? rules = null, MatchOptions? options = null)
    {
        Rules = rules ?? SimRules.Season;
        _setup = setup; _fleets = fleets; _opt = options ?? new MatchOptions();
        if (setup.SlotAngles.Count < fleets.Count) throw new ArgumentException("setup has fewer slots than fleets");
        Arena = setup.Arena;
        FleetArena = new MeteredArena(Arena, Rules);
        ulong rngSeed = _opt.RngSeed ?? setup.Seed;
        _rng = Rng.Derive(rngSeed, 0xB10B);
        _dt = 1.0 / Rules.TicksPerSecond;
        _maxTicks = _opt.MaxTicks ?? Rules.MaxTicks;
        int n = Rules.FleetSize;
        Bots = new Bot[fleets.Count * n];
        _stats = new FleetStats[fleets.Count];
        _fleetAlive = new bool[fleets.Count];
        var centre = new Vec2(Rules.ArenaSize / 2, Rules.ArenaSize / 2);
        for (int s = 0; s < fleets.Count; s++)
        {
            _stats[s] = new FleetStats { Fleet = s };
            _fleetAlive[s] = true;
            double a = setup.SlotAngles[s];
            var u = Vec2.FromAngle(a); var v = new Vec2(-u.Y, u.X);
            var fc = centre + u * Rules.SpawnRingRadius;
            double o = Rules.SpawnMemberOffset;
            var offsets = new[] { -u * o, -v * o, u * o, v * o };
            for (int i = 0; i < n; i++)
            {
                var b = new Bot
                {
                    Gid = s * n + i, Slot = s, Index = i,
                    Pos = fc + offsets[i % 4] * (1 + i / 4),
                    Hp = Rules.MaxHealth, Energy = Rules.MaxEnergy,
                    Random = Rng.Derive(rngSeed, 1000 + (ulong)(s * n + i)),
                };
                b.Look = b.LookTarget = (centre - b.Pos).Angle;
                b.Ammo[0] = Rules.PistolStartAmmo; b.Ammo[1] = Rules.RocketStartAmmo;
                b.Agent = new Agent(this, b);
                try { b.Code = fleets[s].Program.Create(); }
                catch (Exception e) { _stats[s].Crashed = true; _stats[s].FirstError ??= "constructor: " + e.Message; b.Code = new Idle(); }
                Bots[b.Gid] = b;
            }
        }
        foreach (var l in setup.InitialLoot) _boxes.Add(new Box { Id = _nextBoxId++, Kind = l.Kind, Pos = l.Position });
        if (_opt.RecordReplay) _rec = new ReplayRecorder(setup, fleets, Rules);
        foreach (int slot in _opt.TelemetrySlots)
        {
            if (slot < 0 || slot >= fleets.Count) continue;
            _tel[slot] = new TelemetryRecorder(slot, _opt.RngSeed ?? setup.Seed, Rules);
            for (int i = 0; i < n; i++) Bots[slot * n + i].Tel = new TelemetryCommands();
        }
    }

    private readonly Dictionary<int, TelemetryRecorder> _tel = new();

    private sealed class Idle : Shtemer { public override void OnTick(ISelf me, ISenses senses) { } }

    public MatchResult Run()
    {
        int lastTick = 0;
        for (Tick = 0; Tick < _maxTicks; Tick++)
        {
            lastTick = Tick;
            ThinkAll();
            foreach (var tr in _tel.Values) tr.EndTick();
            ApplyCommands();
            Physics();
            Projectiles();
            ZoneDamage();
            Pickups();
            bool over = Deaths();
            SpawnLoot();
            EndOfTick();
            if (_rec != null && (Tick % 2 == 0 || over || Tick == _maxTicks - 1)) RecordFrame();
            if (over) break;
        }
        return Finish(lastTick + 1);
    }

    // ------------------------------------------------------------------ thinking

    private void ThinkAll()
    {
        var zone = ZoneAt(Tick);
        _senses.Zone = zone;
        foreach (var tr in _tel.Values) tr.BeginTick(Tick, zone);
        foreach (var b in Bots)
        {
            if (!b.Alive) continue;
            BuildSenses(b);
        }
        // (telemetry ticks are closed after thinking)
        foreach (var b in Bots)
        {
            if (!b.Alive) continue;
            b.FiredPistol = b.FiredRocket = false;
            b.ZoomsThisTick = b.MessagesThisTick = b.LogLinesThisTick = 0;
            b.Shots.Clear();
            b.ZoomsRequested.Clear();
            var senses = new Senses { Blips = b.Blips.ToArray(), Contacts = b.Contacts.ToArray(), Zone = zone };
            var st = _stats[b.Slot];
            var tel = b.Tel;
            if (tel != null)
            {
                tel.Clear();
                tel.Pos = b.Pos; tel.Vel = b.Vel; tel.Hp = b.Hp; tel.Energy = b.Energy; tel.Look = b.Look;
                tel.Ammo0 = b.Ammo[0]; tel.Ammo1 = b.Ammo[1]; tel.Cd0 = b.Cd[0]; tel.Cd1 = b.Cd[1];
            }
            if (_opt.EnforceBudget) Meter.Begin(Rules.InstructionBudget);
            try
            {
                if (!b.Started) { b.Started = true; b.Code.OnStart(b.Agent); }
                foreach (var m in b.Inbox) b.Code.OnMessage(b.Agent, m);
                foreach (var h in b.Hits) b.Code.OnHit(b.Agent, h);
                foreach (var c in b.Collisions) b.Code.OnCollision(b.Agent, c);
                b.Code.OnTick(b.Agent, senses);
            }
            catch (BudgetExceededException) { st.BudgetExceeded++; }
            catch (Exception e)
            {
                if (e is System.Reflection.TargetInvocationException { InnerException: BudgetExceededException }) st.BudgetExceeded++;
                else { st.Errors++; st.FirstError ??= $"t{Tick} #{b.Index}: {e.GetType().Name}: {e.Message}"; }
            }
            finally
            {
                if (_opt.EnforceBudget)
                {
                    long used = Meter.End();
                    b.BudgetUsed = used; st.BudgetSum += used; st.BudgetTicks++; st.BudgetMax = Math.Max(st.BudgetMax, used);
                }
                if (tel != null)
                {
                    tel.Thrust = b.Thrust; tel.LookTarget = b.LookTarget;
                    _tel[b.Slot].Shtemer(b, Arena.Height(b.Pos.X, b.Pos.Y), senses.Blips, senses.Contacts, b.Inbox, b.Hits, b.Collisions, tel, b.BudgetUsed);
                }
            }
        }
    }

    private static long KeyBot(int gid) => gid;
    private static long KeyBox(int id) => 1_000_000L + id;
    private static long KeyRocket(int id) => 2_000_000L + id;

    private void BuildSenses(Bot b)
    {
        // answers to the zooms of the previous tick: only for objects still in view under the same blip id
        b.Contacts.Clear();
        b.ZoomsToAnswer.Clear();
        b.ZoomsToAnswer.AddRange(b.ZoomsRequested);

        b.Blips.Clear();
        b.BlipKeyById.Clear();
        b.NextBlipIds.Clear();
        double ex = b.Pos.X, ey = b.Pos.Y, ez = Arena.Height(ex, ey) + Rules.EyeHeight;
        foreach (var o in Bots)
        {
            if (!o.Alive || o == b) continue;
            double tz = Arena.Height(o.Pos.X, o.Pos.Y) + Rules.TargetHeight;
            if (Sees(b, ex, ey, ez, o.Pos.X, o.Pos.Y, tz)) AddBlip(b, KeyBot(o.Gid), o.Pos, BlipSize.Medium);
        }
        foreach (var box in _boxes)
        {
            double tz = Arena.Height(box.Pos.X, box.Pos.Y) + Rules.TargetHeight;
            if (Sees(b, ex, ey, ez, box.Pos.X, box.Pos.Y, tz)) AddBlip(b, KeyBox(box.Id), box.Pos, BlipSize.Small);
        }
        foreach (var p in _projectiles)
        {
            if (!p.Alive || p.Kind != 1) continue;
            if (Sees(b, ex, ey, ez, p.X, p.Y, p.Z)) AddBlip(b, KeyRocket(p.Id), new Vec2(p.X, p.Y), BlipSize.Small);
        }
        (b.BlipIds, b.NextBlipIds) = (b.NextBlipIds, b.BlipIds);

        foreach (var (blipId, key) in b.ZoomsToAnswer)
        {
            if (!b.BlipIds.TryGetValue(key, out int id) || id != blipId) continue;
            var c = MakeContact(b, blipId, key);
            if (c != null) b.Contacts.Add(c);
        }
        b.ZoomsRequested.Clear();
    }

    private bool Sees(Bot b, double ex, double ey, double ez, double tx, double ty, double tz)
    {
        double dx = tx - ex, dy = ty - ey, d2 = dx * dx + dy * dy;
        if (d2 <= Rules.ProximityRange * Rules.ProximityRange) return true;
        if (d2 > Rules.VisionRange * Rules.VisionRange) return false;
        double ang = Math.Abs(Vec2.AngleDiff(b.Look, Math.Atan2(dy, dx)));
        if (ang > Rules.VisionHalfAngle) return false;
        return Arena.LineOfSight(ex, ey, ez, tx, ty, tz);
    }

    private void AddBlip(Bot b, long key, Vec2 truePos, BlipSize size)
    {
        if (!b.BlipIds.TryGetValue(key, out int id)) id = b.NextBlipId++;
        b.NextBlipIds[key] = id;
        double dist = truePos.DistanceTo(b.Pos);
        var p = truePos + _rng.InDisk(dist * Rules.BlipNoise);
        var rel = p - b.Pos;
        b.Blips.Add(new Blip(id, p, rel.Length, rel.Angle, size));
        b.BlipKeyById[id] = key;
    }

    private Contact? MakeContact(Bot observer, int blipId, long key)
    {
        if (key < 1_000_000)
        {
            var o = Bots[(int)key];
            return new Contact(blipId, ContactKind.Shtemer, o.Pos, o.Vel, o.Slot == observer.Slot, o.Slot, o.Index, o.Hp, LootKind.None);
        }
        if (key < 2_000_000)
        {
            var box = _boxes.FirstOrDefault(x => x.Id == key - 1_000_000);
            return box == null ? null : new Contact(blipId, ContactKind.Loot, box.Pos, Vec2.Zero, false, -1, -1, 0, box.Kind);
        }
        var p = _projectiles.FirstOrDefault(x => x.Id == key - 2_000_000 && x.Alive);
        if (p == null) return null;
        double h = Math.Sqrt(p.Dx * p.Dx + p.Dy * p.Dy);
        var vel = new Vec2(p.Dx, p.Dy) * (p.Speed * Rules.TicksPerSecond);
        return new Contact(blipId, ContactKind.Rocket, new Vec2(p.X, p.Y), vel, false, -1, -1, 0, LootKind.None);
    }

    internal void RecordZoom(Bot b, long key)
    {
        if (_rec == null) return;
        Vec2 p = key < 1_000_000 ? Bots[(int)key].Pos
            : key < 2_000_000 ? (_boxes.FirstOrDefault(x => x.Id == key - 1_000_000)?.Pos ?? b.Pos)
            : _projectiles.Where(x => x.Id == key - 2_000_000).Select(x => new Vec2(x.X, x.Y)).FirstOrDefault();
        _rec.Event(Tick, "zoom", ("s", b.Gid), ("x", p.X), ("y", p.Y));
    }

    internal void RecordShout(Bot b, int message) => _rec?.Event(Tick, "shout", ("s", b.Gid), ("m", message));

    internal void QueueMessage(Bot from, int toIndex, int type, double[] data)
    {
        var to = Bots[from.Slot * Rules.FleetSize + toIndex];
        if (!to.Alive) return;
        var copy = data == null ? Array.Empty<double>() : data.Take(Rules.MaxMessageData).ToArray();
        to.NextInbox.Add(new Message(from.Index, type, copy));
    }

    // ------------------------------------------------------------------ commands

    private void ApplyCommands()
    {
        foreach (var b in Bots)
        {
            if (!b.Alive) continue;
            foreach (var (w, target) in b.Shots)
            {
                var st = _stats[b.Slot];
                if (w == Weapon.Pistol) st.ShotsPistol++; else st.ShotsRocket++;
                _rec?.Event(Tick, "fire", ("s", b.Gid), ("w", (int)w), ("x", target.X), ("y", target.Y));
                Spawn(b, w, target);
            }
        }
    }

    private void Spawn(Bot b, Weapon w, Vec2 target)
    {
        double mx = b.Pos.X, my = b.Pos.Y, mz = Arena.Height(mx, my) + Rules.MuzzleHeight;
        double tx = target.X, ty = target.Y, tz = Arena.Height(tx, ty) + Rules.TargetHeight;
        double dx = tx - mx, dy = ty - my, dz = tz - mz;
        double hl = Math.Sqrt(dx * dx + dy * dy);
        if (hl < 1e-6) { dx = Math.Cos(b.Look); dy = Math.Sin(b.Look); dz = 0; hl = 1; }
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        dx /= len; dy /= len; dz /= len;
        bool rocket = w == Weapon.Rocket;
        var p = new Projectile
        {
            Id = _nextProjectileId++, Kind = rocket ? 1 : 0, Owner = b.Gid,
            Dx = dx, Dy = dy, Dz = dz,
            Speed = (rocket ? Rules.RocketSpeed : Rules.PistolSpeed) * _dt,
            X = mx + dx * Rules.MuzzleOffset, Y = my + dy * Rules.MuzzleOffset, Z = mz + dz * Rules.MuzzleOffset,
            Traveled = Rules.MuzzleOffset,
            // range counts from the muzzle (server: rockets end at most 46.1 m from the shooter's centre on the
            // 50th move, pistol bullets hit up to 31.1 m on the 15th; calib/range.py)
            MaxTravel = rocket ? Math.Min(Rules.MuzzleOffset + Rules.RocketRange, len) : Rules.MuzzleOffset + Rules.PistolRange,
        };
        _projectiles.Add(p);
    }

    // ------------------------------------------------------------------ movement

    private void Physics()
    {
        foreach (var b in Bots)
        {
            if (!b.Alive) continue;
            b.Energy = Math.Min(Rules.MaxEnergy, b.Energy + Rules.EnergyRegen);
            double d = Vec2.AngleDiff(b.Look, b.LookTarget);
            d = Math.Clamp(d, -Rules.LookTurnRate, Rules.LookTurnRate);
            b.Look = Vec2.NormalizeAngle(b.Look + d);

            var g = Arena.Gradient(b.Pos.X, b.Pos.Y);
            bool parked = b.Thrust.X == 0 && b.Thrust.Y == 0 && b.Vel.Length < Rules.ParkingSpeed
                          && Rules.SlopeAcceleration * g.Length < Rules.ParkingSlopeForce;
            if (parked) b.Vel = Vec2.Zero;
            else
            {
                var a = b.Thrust * Rules.ThrustAcceleration - b.Vel * Rules.Drag - g * Rules.SlopeAcceleration;
                b.Vel += a * _dt;
            }
            b.Pos += b.Vel * _dt;
        }
        ResolveCollisions();
    }

    private void ResolveCollisions()
    {
        double R = Rules.ShtemerRadius, size = Rules.ArenaSize;
        var obs = Arena.Obstacles;
        // One pass, as on the server: in a crowd some overlap is left (server: 12.6 % of touching pairs closer than
        // 1.99 m, minimum 1.84 m), and it is resolved over the next ticks.
        for (int pass = 0; pass < 1; pass++)
        {
            bool report = pass == 0;
            // shtemer pairs: push apart, perfectly inelastic along the normal
            for (int i = 0; i < Bots.Length; i++)
            {
                var a = Bots[i]; if (!a.Alive) continue;
                for (int j = i + 1; j < Bots.Length; j++)
                {
                    var c = Bots[j]; if (!c.Alive) continue;
                    var dv = a.Pos - c.Pos; double d2 = dv.LengthSquared;
                    if (d2 >= 4 * R * R) continue;
                    double d = Math.Sqrt(d2);
                    var n = d > 1e-9 ? dv / d : Vec2.FromAngle(a.Gid);
                    double push = (2 * R - d) / 2;
                    a.Pos += n * push; c.Pos -= n * push;
                    double va = a.Vel.Dot(n), vc = c.Vel.Dot(n);
                    if (va - vc < 0)
                    {
                        double avg = (va + vc) / 2;
                        a.Vel += n * (avg - va); c.Vel += n * (avg - vc);
                    }
                    if (report)
                    {
                        a.NextCollisions.Add(new CollisionEvent(CollisionKind.Shtemer, n));
                        c.NextCollisions.Add(new CollisionEvent(CollisionKind.Shtemer, -n));
                    }
                }
            }
            foreach (var b in Bots)
            {
                if (!b.Alive) continue;
                for (int k = 0; k < obs.Count; k++)
                {
                    var dv = b.Pos - obs[k].Center; double lim = obs[k].Radius + R;
                    double d2 = dv.LengthSquared;
                    if (d2 >= lim * lim) continue;
                    double d = Math.Sqrt(d2);
                    var n = d > 1e-9 ? dv / d : new Vec2(1, 0);
                    b.Pos = obs[k].Center + n * lim;
                    double vn = b.Vel.Dot(n);
                    if (vn < 0) b.Vel -= n * vn;
                    if (report) b.NextCollisions.Add(new CollisionEvent(CollisionKind.Obstacle, n));
                }
                Wall(b, ref report, b.Pos.X < R, new Vec2(1, 0), new Vec2(R, b.Pos.Y));
                Wall(b, ref report, b.Pos.X > size - R, new Vec2(-1, 0), new Vec2(size - R, b.Pos.Y));
                Wall(b, ref report, b.Pos.Y < R, new Vec2(0, 1), new Vec2(b.Pos.X, R));
                Wall(b, ref report, b.Pos.Y > size - R, new Vec2(0, -1), new Vec2(b.Pos.X, size - R));
            }
        }
    }

    private static void Wall(Bot b, ref bool report, bool hit, Vec2 n, Vec2 clamped)
    {
        if (!hit) return;
        b.Pos = clamped;
        double vn = b.Vel.Dot(n);
        if (vn < 0) b.Vel -= n * vn;
        if (report) b.NextCollisions.Add(new CollisionEvent(CollisionKind.Wall, n));
    }

    // ------------------------------------------------------------------ projectiles

    private void Projectiles()
    {
        double R = Rules.ShtemerRadius, H = Rules.ShtemerHeight, size = Rules.ArenaSize;
        foreach (var p in _projectiles)
        {
            if (!p.Alive) continue;
            double remaining = p.Speed;
            int n = (int)Math.Ceiling(p.Speed / Rules.ProjectileSubstep);
            double sub = p.Speed / n;
            while (remaining > 1e-12 && p.Alive)
            {
                double step = Math.Min(sub, remaining);
                if (p.Kind == 1) step = Math.Min(step, p.MaxTravel - p.Traveled);
                remaining -= sub;
                p.X += p.Dx * step; p.Y += p.Dy * step; p.Z += p.Dz * step; p.Traveled += step;
                // rocket: explodes at its end point (target or range). Pistol: the whole step is checked for hits,
                // and the bullet is removed after the step that reaches its range.
                bool reachedEnd = p.Kind == 1 && p.Traveled >= p.MaxTravel - 1e-9;

                bool stop = p.X < 0 || p.Y < 0 || p.X > size || p.Y > size
                            || p.Z < Arena.Height(p.X, p.Y)
                            || Arena.InsideRock(p.X, p.Y, 0) >= 0;
                Bot? victim = null;
                if (!stop)
                {
                    foreach (var b in Bots)
                    {
                        if (!b.Alive) continue;
                        double dx = p.X - b.Pos.X, dy = p.Y - b.Pos.Y;
                        if (dx * dx + dy * dy > R * R) continue;
                        double hz = Arena.Height(b.Pos.X, b.Pos.Y);
                        if (p.Z < hz || p.Z > hz + H) continue;
                        victim = b; break;
                    }
                }
                if (victim != null)
                {
                    var dir = new Vec2(p.Dx, p.Dy).Normalized();
                    if (p.Kind == 0)
                    {
                        Damage(victim, p.Owner, Weapon.Pistol, Rules.PistolDamage, false, dir);
                        p.Alive = false;
                    }
                    else
                    {
                        // as on the server: the explosion (splash to everyone, the victim included) comes first,
                        // then the direct hit (event and OnHit order: boom, splash hits, direct hit)
                        Explode(p);
                        Damage(victim, p.Owner, Weapon.Rocket, Rules.RocketDirectDamage, false, dir);
                    }
                    break;
                }
                if (stop) { if (p.Kind == 1) Explode(p); else p.Alive = false; break; }
                if (reachedEnd) { Explode(p); break; }
            }
            if (p.Alive && p.Kind == 0 && p.Traveled >= p.MaxTravel - 1e-9) p.Alive = false;
        }
        _projectiles.RemoveAll(p => !p.Alive);
    }

    private void Explode(Projectile p)
    {
        p.Alive = false;
        _rec?.Event(Tick, "boom", ("x", p.X), ("y", p.Y), ("z", p.Z), ("by", p.Owner));
        var c = new Vec2(p.X, p.Y);
        foreach (var b in Bots)
        {
            if (!b.Alive) continue;
            double d = b.Pos.DistanceTo(c);
            if (d >= Rules.RocketSplashRadius) continue;
            double dmg = Rules.RocketSplashDamage * (1 - d / Rules.RocketSplashRadius);
            Damage(b, p.Owner, Weapon.Rocket, dmg, true, (b.Pos - c).Normalized());
        }
    }

    private void Damage(Bot victim, int by, Weapon w, double dmg, bool splash, Vec2 dir)
    {
        victim.Hp -= dmg;
        victim.NextHits.Add(new HitEvent(dmg, w, splash, dir));
        victim.LastDamager = by;
        victim.LastCause = w == Weapon.Pistol ? "pistol" : "rocket";
        _stats[victim.Slot].DamageTaken += dmg;
        var shooter = Bots[by];
        if (shooter.Slot != victim.Slot) _stats[shooter.Slot].DamageDealt += dmg;
        if (by != victim.Gid) _stats[shooter.Slot].Hits++;
        _rec?.Event(Tick, "hit", ("s", victim.Gid), ("by", by), ("w", (int)w), ("d", dmg), ("splash", splash));
    }

    // ------------------------------------------------------------------ zone, loot, deaths

    /// <summary>The zone as fleets see it at a tick.</summary>
    public Zone ZoneAt(int t) => ZoneAt(_setup, Rules, t);

    /// <summary>The zone of a setup at a tick, as fleets see it.</summary>
    public static Zone ZoneAt(MatchSetup setup, SimRules Rules, int t)
    {
        var st = setup.ZoneStages;
        int period = Rules.ZoneHoldTicks + Rules.ZoneShrinkTicks;
        int k = t / period, r = t % period;
        double dmg = Rules.ZoneDamagePerStage * Math.Min(Rules.ZoneStages, k + 1);
        if (k >= st.Count - 1)
        {
            var last = st[^1];
            return new Zone(last.Center, last.Radius, last.Center, last.Radius, 0, 0, dmg);
        }
        var a = st[k]; var b = st[k + 1];
        if (r < Rules.ZoneHoldTicks)
            return new Zone(a.Center, a.Radius, b.Center, b.Radius, Rules.ZoneHoldTicks - r, period - r, dmg);
        double f = (r - Rules.ZoneHoldTicks) / (double)Rules.ZoneShrinkTicks;
        return new Zone(Vec2.Lerp(a.Center, b.Center, f), a.Radius + (b.Radius - a.Radius) * f, b.Center, b.Radius, 0, period - r, dmg);
    }

    private void ZoneDamage()
    {
        var z = ZoneAt(Tick);
        foreach (var b in Bots)
        {
            if (!b.Alive || z.Contains(b.Pos)) continue;
            b.Hp -= z.DamagePerTick;
            _stats[b.Slot].DamageTaken += z.DamagePerTick;
            if (b.Hp <= 0) { b.LastDamager = -1; b.LastCause = "zone"; }
        }
    }

    private void Pickups()
    {
        double r2 = Rules.PickupRadius * Rules.PickupRadius;
        foreach (var b in Bots)
        {
            if (!b.Alive) continue;
            for (int i = 0; i < _boxes.Count; i++)
            {
                var box = _boxes[i];
                if ((box.Pos - b.Pos).LengthSquared > r2) continue;
                bool useful = box.Kind switch
                {
                    LootKind.Ammo => b.Ammo[0] < Rules.PistolMaxAmmo,
                    LootKind.Rockets => b.Ammo[1] < Rules.RocketMaxAmmo,
                    LootKind.Repair => b.Hp < Rules.MaxHealth,
                    _ => false,
                };
                if (!useful) continue;
                switch (box.Kind)
                {
                    case LootKind.Ammo: b.Ammo[0] = Math.Min(Rules.PistolMaxAmmo, b.Ammo[0] + Rules.LootAmmoAmount); break;
                    case LootKind.Rockets: b.Ammo[1] = Math.Min(Rules.RocketMaxAmmo, b.Ammo[1] + Rules.LootRocketAmount); break;
                    case LootKind.Repair: b.Hp = Math.Min(Rules.MaxHealth, b.Hp + Rules.LootRepairAmount); break;
                }
                _stats[b.Slot].Loot++;
                _rec?.Event(Tick, "pickup", ("s", b.Gid), ("loot", (int)box.Kind), ("id", box.Id));
                _boxes.RemoveAt(i); i--;
            }
        }
    }

    /// <summary>Removes the dead, drops their boxes, eliminates fleets. Returns true when the match is over.</summary>
    private bool Deaths()
    {
        var eliminatedNow = new List<int>();
        foreach (var b in Bots)
        {
            if (!b.Alive || b.Hp > 0) continue;
            b.Alive = false; b.DeathTick = Tick; b.Hp = 0;
            int by = b.LastCause == "zone" ? -1 : b.LastDamager;
            _rec?.Event(Tick, "death", ("s", b.Gid), ("by", by), ("cause", b.LastCause));
            if (by >= 0 && Bots[by].Slot != b.Slot) _stats[Bots[by].Slot].Kills++;
            _boxes.Add(new Box { Id = _nextBoxId++, Kind = b.Ammo[1] > 0 ? LootKind.Rockets : LootKind.Ammo, Pos = b.Pos });
        }
        for (int s = 0; s < _fleets.Count; s++)
        {
            if (!_fleetAlive[s]) continue;
            bool any = false;
            for (int i = 0; i < Rules.FleetSize; i++) any |= Bots[s * Rules.FleetSize + i].Alive;
            if (!any) { _fleetAlive[s] = false; eliminatedNow.Add(s); _stats[s].SurvivedTicks = Tick + 1; }
        }
        if (eliminatedNow.Count > 0)
        {
            // eliminated together: ranked by remaining health (zero) and then by damage dealt
            eliminatedNow.Sort((x, y) => _stats[x].DamageDealt.CompareTo(_stats[y].DamageDealt));
            int stillAlive = _fleetAlive.Count(a => a);
            for (int k = 0; k < eliminatedNow.Count; k++)
            {
                int place = stillAlive + eliminatedNow.Count - k;
                _stats[eliminatedNow[k]].Place = place;
                _placementsFromLast.Add(eliminatedNow[k]);
                _rec?.Event(Tick, "eliminated", ("f", eliminatedNow[k]), ("place", place));
            }
        }
        return _fleetAlive.Count(a => a) <= 1;
    }

    private void SpawnLoot()
    {
        if (Tick == 0 || Tick % Rules.LootSpawnInterval != 0) return;
        int count = Math.Min(Rules.LootSpawnCount, Rules.LootMaxBoxes - _boxes.Count);
        if (count <= 0) return;
        _setup.ScheduledLoot.TryGetValue(Tick, out var scheduled);
        var z = ZoneAt(Tick);
        for (int i = 0; i < count; i++)
        {
            LootSeed seed;
            if (scheduled != null && i < scheduled.Count) seed = scheduled[i];
            else
            {
                // As on the server (calib/latelot3.py): a spawned box keeps 4 m from every other box and 1.5 m from
                // rocks; a box that finds no such place in its tries is not spawned. In a small zone (R < 2.2,
                // R = 0 after t = 2700) at most one box fits, so late spawns are 0-1 instead of 3.
                Vec2? found = null;
                double sp2 = Rules.LootSpawnSpacing * Rules.LootSpawnSpacing;
                for (int tries = 0; tries < Rules.LootSpawnTries && found == null; tries++)
                {
                    var p = z.Center + _rng.InDisk(z.Radius * Rules.LootSpawnZoneFraction);
                    if (p.X < 1 || p.Y < 1 || p.X > Rules.ArenaSize - 1 || p.Y > Rules.ArenaSize - 1) continue;
                    if (Arena.InsideRock(p.X, p.Y, Rules.LootRockClearance) >= 0) continue;
                    bool near = false;
                    foreach (var o in _boxes) if ((o.Pos - p).LengthSquared < sp2) { near = true; break; }
                    if (!near) found = p;
                }
                if (found == null) continue;
                seed = new LootSeed(MatchSetup.PickKind(_rng, Rules), found.Value);
            }
            _boxes.Add(new Box { Id = _nextBoxId++, Kind = seed.Kind, Pos = seed.Position });
        }
    }

    private void EndOfTick()
    {
        foreach (var b in Bots)
        {
            for (int w = 0; w < 2; w++) if (b.Cd[w] > 0) b.Cd[w]--;
            (b.Inbox, b.NextInbox) = (b.NextInbox, b.Inbox); b.NextInbox.Clear();
            (b.Hits, b.NextHits) = (b.NextHits, b.Hits); b.NextHits.Clear();
            (b.Collisions, b.NextCollisions) = (b.NextCollisions, b.Collisions); b.NextCollisions.Clear();
            if (!b.Alive) { b.Inbox.Clear(); b.Hits.Clear(); b.Collisions.Clear(); }
        }
    }

    private void RecordFrame()
    {
        var z = ZoneAt(Tick);
        _rec!.Frame(Tick, z, Bots.Select(b => b.Alive ? (b.Pos.X, b.Pos.Y, b.Look, b.Hp, b.Energy) : ((double, double, double, double, double)?)null),
            _projectiles.Where(p => p.Alive).Select(p => (p.Id, p.Kind, p.X, p.Y, p.Z, p.Owner)),
            _boxes.Select(x => (x.Id, (int)x.Kind, x.Pos.X, x.Pos.Y)));
    }

    // ------------------------------------------------------------------ result

    private MatchResult Finish(int totalTicks)
    {
        // fleets alive at the end: ranked by remaining health, then damage dealt
        var alive = Enumerable.Range(0, _fleets.Count).Where(s => _fleetAlive[s]).ToList();
        double Health(int s) => Enumerable.Range(0, Rules.FleetSize).Sum(i => Math.Max(0, Bots[s * Rules.FleetSize + i].Hp) * (Bots[s * Rules.FleetSize + i].Alive ? 1 : 0));
        alive.Sort((x, y) => Health(y) != Health(x) ? Health(y).CompareTo(Health(x)) : _stats[y].DamageDealt.CompareTo(_stats[x].DamageDealt));
        for (int k = 0; k < alive.Count; k++) { _stats[alive[k]].Place = k + 1; _stats[alive[k]].SurvivedTicks = totalTicks; }
        var placements = alive.Concat(Enumerable.Reverse(_placementsFromLast)).ToList();
        var result = new MatchResult
        {
            Seed = _setup.Seed, Origin = _setup.Origin, TotalTicks = totalTicks,
            FleetNames = _fleets.Select(f => f.Name).ToList(),
            Placements = placements, Stats = _stats.ToList(),
            SurvivorHealth = Bots.Select(b => b.Alive ? b.Hp : 0).ToList(),
            DeathTicks = Bots.Select(b => b.DeathTick).ToList(),
        };
        if (_rec != null) result.Replay = _rec.Finish(totalTicks, placements, _stats);
        foreach (var (slot, tr) in _tel) result.Telemetry[slot] = tr.Finish(_stats[slot]);
        return result;
    }
}

public sealed class MatchResult
{
    public ulong Seed { get; init; }
    public string Origin { get; init; } = "";
    public int TotalTicks { get; init; }
    public required List<string> FleetNames { get; init; }
    /// <summary>Fleet slots from the winner to the last place.</summary>
    public required List<int> Placements { get; init; }
    public required List<FleetStats> Stats { get; init; }
    public required List<double> SurvivorHealth { get; init; }
    public required List<int> DeathTicks { get; init; }
    /// <summary>Replay v1 JSON (UTF-8), when recorded.</summary>
    public byte[]? Replay { get; set; }
    /// <summary>Telemetry v1 (gzip JSON Lines) per recorded slot.</summary>
    public Dictionary<int, byte[]> Telemetry { get; } = new();
}
