using System.IO.Compression;
using System.Text.Json;
using Shtemeri.Api;

namespace Shtemeri.Sim;

/// <summary>A box that exists at the start or appears on a schedule.</summary>
public readonly record struct LootSeed(LootKind Kind, Vec2 Position);

/// <summary>
/// Everything about a match that does not depend on the fleets: the map, the zone circles, the boxes and where the
/// fleets start. It is either generated from a seed with the simulator's own generator, or read from a server replay
/// (same arena, zone and box schedule as that match; the server's generator itself is not public).
/// </summary>
public sealed class MatchSetup
{
    public ulong Seed { get; init; }
    public required Arena Arena { get; init; }
    /// <summary>Zone circles: [0] is the start circle, [i] the circle stage i shrinks to (replay v1 zoneStages).</summary>
    public required IReadOnlyList<(Vec2 Center, double Radius)> ZoneStages { get; init; }
    public required IReadOnlyList<LootSeed> InitialLoot { get; init; }
    /// <summary>Boxes the server spawned at a tick (replay mode). Ticks without an entry get no boxes.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<LootSeed>> ScheduledLoot { get; init; } = new Dictionary<int, IReadOnlyList<LootSeed>>();
    /// <summary>Replay mode: spawned boxes come only from <see cref="ScheduledLoot"/>; nothing is generated (review H3).</summary>
    public bool LootFromSchedule { get; init; }
    /// <summary>Angle (from the arena centre) of each slot's fleet centre. Slot i plays at SlotAngles[i].</summary>
    public required IReadOnlyList<double> SlotAngles { get; init; }
    /// <summary>Fleet names per slot in the source replay (empty when generated).</summary>
    public IReadOnlyList<string> FleetNames { get; init; } = Array.Empty<string>();
    /// <summary>Where this setup came from (for the log).</summary>
    public string Origin { get; init; } = "generated";

    /// <summary>Generated setup. <paramref name="arena"/> overrides the generated terrain (e.g. one taken from a replay).</summary>
    public static MatchSetup Generate(ulong seed, int fleets, SimRules rules, Arena? arena = null, string origin = "generated")
    {
        var rng = Rng.Derive(seed, 0xA11CE);
        // slots first: generated rocks keep clear of the fleet spawn centres
        double start = rng.Range(-Math.PI, Math.PI);
        var order = Enumerable.Range(0, fleets).ToArray();
        for (int i = order.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
        var angles = order.Select(k => Vec2.NormalizeAngle(start + 2 * Math.PI * k / fleets)).ToArray();
        var centre = new Vec2(rules.ArenaSize / 2, rules.ArenaSize / 2);
        arena ??= Arena.Generate(rng, rules, angles.Select(a => centre + Vec2.FromAngle(a) * rules.SpawnRingRadius).ToList());
        var stages = new List<(Vec2, double)> { (centre, rules.ZoneStartRadius) };
        double r = rules.ZoneStartRadius;
        for (int k = 1; k <= rules.ZoneStages; k++)
        {
            double nr = k == rules.ZoneStages ? 0 : Math.Round(r * rules.ZoneShrinkFactor, 2);
            var prev = stages[^1].Item1;
            var c = prev + rng.InDisk(r - nr);
            stages.Add((new Vec2(Math.Round(c.X, 2), Math.Round(c.Y, 2)), nr));
            r = nr;
        }
        // initial boxes (server: always 10, 3 m from the walls, 8 m apart, at least 5.15 m from every shtemer)
        var bodies = angles.SelectMany(a => SpawnPositions(a, rules)).ToList();
        var loot = new List<LootSeed>();
        double s2 = rules.LootInitialSpacing * rules.LootInitialSpacing, b2 = rules.LootInitialBodyClearance * rules.LootInitialBodyClearance;
        for (int i = 0; i < rules.LootInitialBoxes; i++)
        {
            for (int tries = 0; tries < 10000; tries++)
            {
                var p = new Vec2(rng.Range(rules.LootWallMargin, rules.ArenaSize - rules.LootWallMargin),
                                 rng.Range(rules.LootWallMargin, rules.ArenaSize - rules.LootWallMargin));
                if (arena.InsideRock(p.X, p.Y, rules.LootRockClearance) >= 0) continue;
                if (bodies.Any(q => (q - p).LengthSquared < b2)) continue;
                if (loot.Any(l => (l.Position - p).LengthSquared < s2)) continue;
                loot.Add(new LootSeed(PickKind(rng, rules), p));
                break;
            }
        }
        return new MatchSetup { Seed = seed, Arena = arena, ZoneStages = stages, InitialLoot = loot, SlotAngles = angles, Origin = origin };
    }

    /// <summary>Start positions of a fleet's shtemers: around the fleet centre on the spawn ring, radially in/out and
    /// tangentially (index i at offset (1 + i / 4) times the member offset).</summary>
    public static Vec2[] SpawnPositions(double angle, SimRules rules)
    {
        var centre = new Vec2(rules.ArenaSize / 2, rules.ArenaSize / 2);
        var u = Vec2.FromAngle(angle); var v = new Vec2(-u.Y, u.X);
        var fc = centre + u * rules.SpawnRingRadius;
        double o = rules.SpawnMemberOffset;
        var offsets = new[] { -u * o, -v * o, u * o, v * o };
        var pos = new Vec2[rules.FleetSize];
        for (int i = 0; i < pos.Length; i++) pos[i] = fc + offsets[i % 4] * (1 + i / 4);
        return pos;
    }

    public static LootKind PickKind(Rng rng, SimRules rules)
    {
        double u = rng.NextDouble() * rules.LootKindWeights.Sum();
        for (int k = 0; k < rules.LootKindWeights.Length; k++)
        {
            u -= rules.LootKindWeights[k];
            if (u < 0) return (LootKind)(k + 1);
        }
        return LootKind.Repair;
    }

    // ------------------------------------------------------------------ replay input

    public static JsonDocument ReadReplayJson(string path)
    {
        using var fs = File.OpenRead(path);
        Stream s = fs;
        var head = new byte[2];
        int n = fs.Read(head, 0, 2);
        fs.Position = 0;
        if (n == 2 && head[0] == 0x1f && head[1] == 0x8b) s = new GZipStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return JsonDocument.Parse(ms.ToArray());
    }

    public static Arena ArenaFromReplay(JsonElement root, SimRules rules)
    {
        var a = root.GetProperty("arena");
        var heights = a.GetProperty("heights").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        var obs = a.GetProperty("obstacles").EnumerateArray()
            .Select(o => new Obstacle(new Vec2(o[0].GetDouble(), o[1].GetDouble()), o[2].GetDouble())).ToList();
        return new Arena(a.GetProperty("size").GetDouble(), a.GetProperty("gridN").GetInt32(), heights, obs, rules.LosStep);
    }

    /// <summary>
    /// The setup of a server match: arena, zone circles, the initial boxes and the boxes the server spawned (box drops
    /// of destroyed shtemers are left out: locally they come from local deaths). Slot angles come from frame 0.
    /// </summary>
    public static MatchSetup FromReplay(string path, SimRules rules)
    {
        using var doc = ReadReplayJson(path);
        var root = doc.RootElement;
        var arena = ArenaFromReplay(root, rules);
        var stages = root.GetProperty("zoneStages").EnumerateArray()
            .Select(z => (new Vec2(z[0].GetDouble(), z[1].GetDouble()), z[2].GetDouble())).ToList();
        int fleetSize = root.GetProperty("rules").GetProperty("fleetSize").GetInt32();
        var frames = root.GetProperty("frames");
        var deaths = root.GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("k").GetString() == "death")
            .Select(e => (T: e.GetProperty("t").GetInt32(), S: e.GetProperty("s").GetInt32())).ToList();

        var first = new Dictionary<int, (int T, LootKind K, Vec2 P)>();
        var lastPos = new Dictionary<int, Vec2>();   // last known position of every shtemer, to recognise drops
        var dropIds = new HashSet<int>();
        foreach (var f in frames.EnumerateArray())
        {
            int t = f.GetProperty("t").GetInt32();
            var s = f.GetProperty("s");
            foreach (var l in f.GetProperty("l").EnumerateArray())
            {
                int id = l[0].GetInt32();
                if (first.ContainsKey(id)) continue;
                var p = new Vec2(l[2].GetDouble(), l[3].GetDouble());
                first[id] = (t, (LootKind)l[1].GetInt32(), p);
                if (t > 0 && deaths.Any(d => t - d.T >= 0 && t - d.T <= 2 && lastPos.TryGetValue(d.S, out var q) && q.DistanceTo(p) < 3))
                    dropIds.Add(id);
            }
            int gid = 0;
            foreach (var q in s.EnumerateArray())
            {
                if (q.ValueKind == JsonValueKind.Array) lastPos[gid] = new Vec2(q[0].GetDouble(), q[1].GetDouble());
                gid++;
            }
        }
        var initial = first.Where(kv => kv.Value.T == 0).OrderBy(kv => kv.Key).Select(kv => new LootSeed(kv.Value.K, kv.Value.P)).ToList();
        var scheduled = first.Where(kv => kv.Value.T > 0 && !dropIds.Contains(kv.Key))
            .GroupBy(kv => kv.Value.T)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LootSeed>)g.OrderBy(kv => kv.Key).Select(kv => new LootSeed(kv.Value.K, kv.Value.P)).ToList());

        // fleet centres in frame 0 (one tick after the start; the shift is a few centimetres)
        var f0 = frames[0].GetProperty("s");
        int nFleets = root.GetProperty("fleets").GetArrayLength();
        var angles = new double[nFleets];
        for (int k = 0; k < nFleets; k++)
        {
            double cx = 0, cy = 0; int m = 0;
            for (int i = 0; i < fleetSize; i++)
            {
                var q = f0[k * fleetSize + i];
                if (q.ValueKind != JsonValueKind.Array) continue;
                cx += q[0].GetDouble(); cy += q[1].GetDouble(); m++;
            }
            angles[k] = Math.Atan2(cy / m - rules.ArenaSize / 2, cx / m - rules.ArenaSize / 2);
        }
        return new MatchSetup
        {
            Seed = (ulong)root.GetProperty("seed").GetInt64(),
            Arena = arena, ZoneStages = stages, InitialLoot = initial, ScheduledLoot = scheduled, LootFromSchedule = true, SlotAngles = angles,
            Origin = "replay " + Path.GetFileName(path),
            FleetNames = root.GetProperty("fleets").EnumerateArray().Select(f => f.GetProperty("name").GetString() ?? "?").ToList(),
        };
    }
}
