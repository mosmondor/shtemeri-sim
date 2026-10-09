using System.Text.Json;
using Shtemeri.Api;

namespace Shtemeri.Sim;

/// <summary>
/// Writes a match as replay JSON v1 (guide, chapter 12), the format the server publishes, so existing analysis
/// scripts read local matches unchanged. Rounding as on the server: positions and zone 2 decimals, angles 2,
/// health and energy 1, damage 1.
/// </summary>
internal sealed class ReplayRecorder
{
    private static readonly string[] Colors =
        { "#e6194b", "#4363d8", "#3cb44b", "#ffe119", "#911eb4", "#f58231", "#46f0f0", "#f032e6" };

    private readonly MemoryStream _frames = new();
    private readonly Utf8JsonWriter _fw;
    private readonly MemoryStream _events = new();
    private readonly Utf8JsonWriter _ew;
    private readonly MatchSetup _setup;
    private readonly IReadOnlyList<FleetEntry> _fleets;
    private readonly SimRules _rules;

    public ReplayRecorder(MatchSetup setup, IReadOnlyList<FleetEntry> fleets, SimRules rules)
    {
        _setup = setup; _fleets = fleets; _rules = rules;
        _fw = new Utf8JsonWriter(_frames);
        _ew = new Utf8JsonWriter(_events);
        _fw.WriteStartArray();
        _ew.WriteStartArray();
    }

    private static double R2(double v) => Math.Round(v, 2);
    private static double R1(double v) => Math.Round(v, 1);

    public void Frame(int t, Zone z, IEnumerable<(double X, double Y, double Look, double Hp, double En)?> bots,
                      IEnumerable<(int Id, int Kind, double X, double Y, double Z, int Owner)> projectiles,
                      IEnumerable<(int Id, int Kind, double X, double Y)> boxes)
    {
        var w = _fw;
        w.WriteStartObject();
        w.WriteNumber("t", t);
        w.WriteStartArray("z"); w.WriteNumberValue(R2(z.Center.X)); w.WriteNumberValue(R2(z.Center.Y)); w.WriteNumberValue(R2(z.Radius)); w.WriteEndArray();
        w.WriteStartArray("s");
        foreach (var b in bots)
        {
            if (b == null) { w.WriteNullValue(); continue; }
            var v = b.Value;
            w.WriteStartArray();
            w.WriteNumberValue(R2(v.X)); w.WriteNumberValue(R2(v.Y)); w.WriteNumberValue(R2(v.Look));
            w.WriteNumberValue(R1(v.Hp)); w.WriteNumberValue(R1(v.En));
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteStartArray("p");
        foreach (var p in projectiles)
        {
            w.WriteStartArray();
            w.WriteNumberValue(p.Id); w.WriteNumberValue(p.Kind); w.WriteNumberValue(R2(p.X)); w.WriteNumberValue(R2(p.Y));
            w.WriteNumberValue(R2(p.Z)); w.WriteNumberValue(p.Owner);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteStartArray("l");
        foreach (var l in boxes)
        {
            w.WriteStartArray();
            w.WriteNumberValue(l.Id); w.WriteNumberValue(l.Kind); w.WriteNumberValue(R2(l.X)); w.WriteNumberValue(R2(l.Y));
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public void Event(int t, string kind, params (string Key, object Value)[] fields)
    {
        var w = _ew;
        w.WriteStartObject();
        w.WriteNumber("t", t);
        w.WriteString("k", kind);
        foreach (var (k, v) in fields)
        {
            switch (v)
            {
                case int i: w.WriteNumber(k, i); break;
                case double d: w.WriteNumber(k, k == "d" ? R1(d) : R2(d)); break;
                case bool bo: w.WriteBoolean(k, bo); break;
                case string s: w.WriteString(k, s); break;
                default: w.WriteString(k, v?.ToString()); break;
            }
        }
        w.WriteEndObject();
    }

    public byte[] Finish(int totalTicks, IReadOnlyList<int> placements, IReadOnlyList<FleetStats> stats)
    {
        _fw.WriteEndArray(); _fw.Flush();
        _ew.WriteEndArray(); _ew.Flush();
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteNumber("seed", _setup.Seed);
            w.WriteNumber("ticksPerSecond", _rules.TicksPerSecond);
            w.WriteNumber("frameStride", 2);
            w.WriteNumber("totalTicks", totalTicks);
            w.WriteString("simulator", "shtemeri-sim (local), " + _setup.Origin);
            w.WriteStartObject("rules");
            w.WriteNumber("arenaSize", _rules.ArenaSize); w.WriteNumber("fleetSize", _rules.FleetSize);
            w.WriteNumber("shtemerRadius", _rules.ShtemerRadius); w.WriteNumber("visionRange", _rules.VisionRange);
            w.WriteNumber("visionHalfAngle", _rules.VisionHalfAngle); w.WriteNumber("proximityRange", _rules.ProximityRange);
            w.WriteNumber("rocketSplashRadius", _rules.RocketSplashRadius); w.WriteNumber("maxHealth", _rules.MaxHealth);
            w.WriteNumber("pistolSpeed", _rules.PistolSpeed); w.WriteNumber("pistolRange", _rules.PistolRange);
            w.WriteNumber("rocketSpeed", _rules.RocketSpeed); w.WriteNumber("rocketRange", _rules.RocketRange);
            w.WriteEndObject();
            var a = _setup.Arena;
            w.WriteStartObject("arena");
            w.WriteNumber("size", a.Size); w.WriteNumber("gridN", a.GridN);
            w.WriteStartArray("heights"); foreach (var h in a.Heights) w.WriteNumberValue(h); w.WriteEndArray();
            w.WriteStartArray("obstacles");
            foreach (var o in a.Obstacles) { w.WriteStartArray(); w.WriteNumberValue(o.Center.X); w.WriteNumberValue(o.Center.Y); w.WriteNumberValue(o.Radius); w.WriteEndArray(); }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartArray("zoneStages");
            foreach (var (c, r) in _setup.ZoneStages) { w.WriteStartArray(); w.WriteNumberValue(c.X); w.WriteNumberValue(c.Y); w.WriteNumberValue(r); w.WriteEndArray(); }
            w.WriteEndArray();
            w.WriteStartArray("fleets");
            var seen = new Dictionary<string, int>();
            for (int i = 0; i < _fleets.Count; i++)
            {
                string name = _fleets[i].Name;
                seen[name] = seen.TryGetValue(name, out int c) ? c + 1 : 1;
                if (seen[name] > 1) name += "#" + seen[name];
                w.WriteStartObject();
                w.WriteNumber("id", i); w.WriteString("name", name); w.WriteString("owner", _fleets[i].Owner);
                w.WriteString("color", Colors[i % Colors.Length]);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("frames"); w.WriteRawValue(_frames.ToArray(), skipInputValidation: true);
            w.WritePropertyName("events"); w.WriteRawValue(_events.ToArray(), skipInputValidation: true);
            w.WriteStartObject("result");
            w.WriteStartArray("placements"); foreach (var p in placements) w.WriteNumberValue(p); w.WriteEndArray();
            w.WriteStartArray("stats");
            foreach (var s in stats)
            {
                w.WriteStartObject();
                w.WriteNumber("fleet", s.Fleet); w.WriteNumber("place", s.Place); w.WriteNumber("kills", s.Kills);
                w.WriteNumber("damageDealt", R1(s.DamageDealt)); w.WriteNumber("damageTaken", R1(s.DamageTaken));
                w.WriteNumber("shotsPistol", s.ShotsPistol); w.WriteNumber("shotsRocket", s.ShotsRocket);
                w.WriteNumber("hits", s.Hits); w.WriteNumber("loot", s.Loot); w.WriteNumber("survivedTicks", s.SurvivedTicks);
                w.WriteNumber("budgetExceeded", s.BudgetExceeded); w.WriteNumber("errors", s.Errors); w.WriteBoolean("crashed", s.Crashed);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}
