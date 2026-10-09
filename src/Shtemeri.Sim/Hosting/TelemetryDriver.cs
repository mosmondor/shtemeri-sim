using System.IO.Compression;
using System.Text.Json;
using Shtemeri.Api;

namespace Shtemeri.Sim.Hosting;

/// <summary>
/// Open-loop check of API semantics: feeds a fleet, tick by tick, exactly what its shtemers received on the server
/// (state, blips, contacts, messages, hits, collisions from a telemetry v1 file) and compares the commands the local
/// instance issues with the commands recorded on the server. The world does not evolve locally, so any systematic
/// mismatch comes from how the local API presents the inputs or handles the commands, not from diverging physics.
/// </summary>
public static class TelemetryDriver
{
    public sealed class Report
    {
        public int Ticks;
        public readonly Dictionary<string, int> Mismatch = new();
        public readonly Dictionary<string, int> Compared = new();
        public readonly List<string> Examples = new();
        public int Errors;
    }

    private sealed class Rec
    {
        public int T, Idx; public JsonElement E;
    }

    public static Report Run(FleetProgram program, string telemetryPath, MatchSetup setup, SimRules rules, int maxExamples = 40,
                             Func<int, Zone>? zoneAt = null, Options? options = null)
    {
        options ??= new Options();
        var lines = ReadLines(telemetryPath);
        var header = JsonDocument.Parse(lines[0]).RootElement;
        int fleetId = header.GetProperty("fleetId").GetInt32();
        int n = header.GetProperty("fleetSize").GetInt32();
        var byTick = new SortedDictionary<int, Dictionary<int, JsonElement>>();
        foreach (var ln in lines.Skip(1))
        {
            var o = JsonDocument.Parse(ln).RootElement;
            if (!o.TryGetProperty("t", out var tt) || !o.TryGetProperty("i", out var arr)) continue;
            var d = new Dictionary<int, JsonElement>();
            foreach (var s in arr.EnumerateArray()) d[s.GetProperty("idx").GetInt32()] = s;
            byTick[tt.GetInt32()] = d;
        }
        var arena = new MeteredArenaFree(setup.Arena);
        var inst = new Shtemer[n];
        var rnd = new Rng[n];
        for (int i = 0; i < n; i++) { inst[i] = program.Create(); rnd[i] = Rng.Derive(setup.Seed, 1000 + (ulong)(fleetId * n + i)); }
        var started = new bool[n];
        var lastThrust = new Vec2[n]; var lastLook = new double?[n];
        var energy = Enumerable.Repeat(rules.MaxEnergy, n).ToArray();   // exact energy, advanced with the server's zooms
        var rep = new Report();
        foreach (var (t, recs) in byTick)
        {
            rep.Ticks++;
            int zoff = int.Parse(Environment.GetEnvironmentVariable("DRIVE_ZONE_OFFSET") ?? "0");
            var zone = zoneAt != null ? zoneAt(t) : Match.ZoneAt(setup, rules, Math.Max(0, t + zoff));
            foreach (var (idx, s) in recs)
            {
                var self = new DrivenSelf(t, idx, n, s, recs, arena, rules, rnd[idx], options);
                self.PrevThrust = lastThrust[idx]; self.PrevLook = lastLook[idx] ?? s.GetProperty("look").GetDouble();
                if (options.ExactEnergy && Math.Abs(energy[idx] - s.GetProperty("en").GetDouble()) < 0.06) self.SetEnergy(energy[idx]);
                var senses = new DrivenSenses(s, self.Position, zone);
                try
                {
                    if (!started[idx]) { started[idx] = true; inst[idx].OnStart(self); }
                    if (s.TryGetProperty("msgIn", out var mi))
                        foreach (var m in mi.EnumerateArray())
                            inst[idx].OnMessage(self, new Message(m[0].GetInt32(), m[1].GetInt32(), m[2].EnumerateArray().Select(x => x.GetDouble()).ToArray()));
                    if (s.TryGetProperty("hits", out var hs))
                        foreach (var h in hs.EnumerateArray())
                            inst[idx].OnHit(self, new HitEvent(h[0].GetDouble(), (Weapon)h[1].GetInt32(), h[2].GetBoolean(), new Vec2(h[3].GetDouble(), h[4].GetDouble())));
                    if (s.TryGetProperty("coll", out var cs))
                        foreach (var c in cs.EnumerateArray())
                            inst[idx].OnCollision(self, new CollisionEvent((CollisionKind)c[0].GetInt32(), new Vec2(c[1].GetDouble(), c[2].GetDouble())));
                    inst[idx].OnTick(self, senses);
                }
                catch (Exception e) { rep.Errors++; if (rep.Examples.Count < maxExamples) rep.Examples.Add($"t{t} #{idx} exception {e.GetType().Name}: {e.Message}"); }
                Compare(rep, t, idx, s.GetProperty("cmd"), self, maxExamples);
                lastThrust[idx] = self.ThrustCmd ?? self.PrevThrust; lastLook[idx] = self.LookCmd ?? self.PrevLook;
                {
                    var cmdE = s.GetProperty("cmd");
                    int zs = cmdE.TryGetProperty("zoom", out var zz) ? zz.GetArrayLength() : 0;
                    double e0 = Math.Abs(energy[idx] - s.GetProperty("en").GetDouble()) < 0.06 ? energy[idx] : s.GetProperty("en").GetDouble();
                    energy[idx] = Math.Min(rules.MaxEnergy, e0 - rules.ZoomCost * zs + rules.EnergyRegen);
                }
            }
        }
        return rep;
    }

    public sealed class Options
    {
        /// <summary>Energy drops at the moment of Zoom (local engine) instead of at the end of the tick.</summary>
        public bool ZoomDeductsImmediately { get; init; } = true;
        /// <summary>Ammo and cooldown change at the moment of Fire (local engine).</summary>
        public bool FireUpdatesImmediately { get; init; } = true;
        /// <summary>Blip.Distance/Bearing from the noisy blip position (local engine) or from the true one (unknown here; noisy).</summary>
        public bool AllyAliveFromPresence { get; init; } = true;
        /// <summary>Use energy advanced exactly from the start (telemetry rounds it to 0.1).</summary>
        public bool ExactEnergy { get; init; } = true;
    }

    private static void Compare(Report rep, int t, int idx, JsonElement cmd, DrivenSelf self, int maxEx)
    {
        void Count(string k, bool bad, string detail)
        {
            rep.Compared[k] = rep.Compared.GetValueOrDefault(k) + 1;
            if (!bad) return;
            rep.Mismatch[k] = rep.Mismatch.GetValueOrDefault(k) + 1;
            if (rep.Examples.Count < maxEx) rep.Examples.Add($"t{t} #{idx} {k}: {detail}");
        }
        if (!cmd.TryGetProperty("thrust", out var th)) { rep.Compared["no-cmd"] = rep.Compared.GetValueOrDefault("no-cmd") + 1; return; }
        var sth = new Vec2(th[0].GetDouble(), th[1].GetDouble());
        var lth = self.ThrustCmd ?? self.PrevThrust;
        Count("thrust", (sth - lth).Length > 0.03, $"server {sth} local {lth}");
        double slook = cmd.GetProperty("look").GetDouble();
        double llook = self.LookCmd ?? self.PrevLook;
        Count("look", Math.Abs(Vec2.AngleDiff(slook, llook)) > 0.03, $"server {slook:0.00} local {llook:0.00}");
        var sz = cmd.TryGetProperty("zoom", out var z) ? string.Join(",", z.EnumerateArray().Select(x => x.GetInt32())) : "";
        Count("zoom", sz != string.Join(",", self.Zooms), $"server [{sz}] local [{string.Join(",", self.Zooms)}]");
        var sf = cmd.TryGetProperty("fire", out var f) ? f.EnumerateArray().Select(x => (x[0].GetInt32(), new Vec2(x[1].GetDouble(), x[2].GetDouble()))).ToList() : new();
        bool fbad = sf.Count != self.Fires.Count || sf.Zip(self.Fires).Any(p => p.First.Item1 != p.Second.W || (p.First.Item2 - p.Second.P).Length > 0.1);
        Count("fire", fbad, $"server [{string.Join(" ", sf.Select(x => $"{x.Item1}@{x.Item2}"))}] local [{string.Join(" ", self.Fires.Select(x => $"{x.W}@{x.P}"))}]");
        var sm = cmd.TryGetProperty("msgOut", out var mo) ? mo.EnumerateArray().Select(x => $"{x[0].GetInt32()}:{x[1].GetInt32()}").ToList() : new();
        var lm = self.MsgOut.Select(x => $"{x.To}:{x.Type}").ToList();
        Count("msgOut", !sm.SequenceEqual(lm), $"server [{string.Join(" ", sm)}] local [{string.Join(" ", lm)}]");
    }

    private static List<string> ReadLines(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(gz);
        var list = new List<string>();
        string? l;
        while ((l = sr.ReadLine()) != null) if (l.Length > 0) list.Add(l);
        return list;
    }

    private sealed class MeteredArenaFree : IArena
    {
        private readonly Arena _a;
        public MeteredArenaFree(Arena a) { _a = a; }
        public double Size => _a.Size;
        public IReadOnlyList<Obstacle> Obstacles => _a.Obstacles;
        public double HeightAt(Vec2 p) => _a.HeightAt(p);
        public bool IsBlocked(Vec2 p) => _a.IsBlocked(p);
        private static readonly double Eh = double.Parse(Environment.GetEnvironmentVariable("DRIVE_LOS_EYE") ?? "1.6", System.Globalization.CultureInfo.InvariantCulture);
        private static readonly double Th = double.Parse(Environment.GetEnvironmentVariable("DRIVE_LOS_TARGET") ?? "1.0", System.Globalization.CultureInfo.InvariantCulture);
        public bool HasLineOfSight(Vec2 a, Vec2 b) =>
            _a.LineOfSight(a.X, a.Y, _a.Height(a.X, a.Y) + Eh, b.X, b.Y, _a.Height(b.X, b.Y) + Th);
    }

    private sealed class DrivenSenses : ISenses
    {
        public IReadOnlyList<Blip> Blips { get; }
        public IReadOnlyList<Contact> Contacts { get; }
        public Zone Zone { get; }
        public DrivenSenses(JsonElement s, Vec2 me, Zone zone)
        {
            Zone = zone;
            var bl = new List<Blip>();
            if (s.TryGetProperty("blips", out var b))
                foreach (var x in b.EnumerateArray())
                {
                    var p = new Vec2(x[1].GetDouble(), x[2].GetDouble()); var rel = p - me;
                    bl.Add(new Blip(x[0].GetInt32(), p, rel.Length, rel.Angle, (BlipSize)x[3].GetInt32()));
                }
            Blips = bl;
            var cl = new List<Contact>();
            if (s.TryGetProperty("contacts", out var c))
                foreach (var x in c.EnumerateArray())
                {
                    var kind = (ContactKind)x.GetProperty("kind").GetInt32();
                    var pos = V(x.GetProperty("pos")); var vel = V(x.GetProperty("vel"));
                    bool ally = x.TryGetProperty("ally", out var a) && a.GetBoolean();
                    int fleet = x.TryGetProperty("fleet", out var fl) ? fl.GetInt32() : -1;
                    int index = x.TryGetProperty("index", out var ix) ? ix.GetInt32() : -1;
                    double hp = x.TryGetProperty("hp", out var h) ? h.GetDouble() : 0;
                    var loot = x.TryGetProperty("loot", out var lt) ? (LootKind)lt.GetInt32() : LootKind.None;
                    cl.Add(new Contact(x.GetProperty("blip").GetInt32(), kind, pos, vel, ally, fleet, index, hp, loot));
                }
            Contacts = cl;
        }
    }

    internal static Vec2 V(JsonElement e) => new(e[0].GetDouble(), e[1].GetDouble());

    private sealed class DrivenSelf : ISelf
    {
        private readonly JsonElement _s; private readonly Dictionary<int, JsonElement> _all; private readonly Options _o;
        private double _energy; private readonly int[] _ammo = new int[2]; private readonly int[] _cd = new int[2];
        private bool _firedP, _firedR; private int _zooms, _msgs;
        public Vec2? ThrustCmd; public double? LookCmd; public Vec2 PrevThrust; public double PrevLook;
        public readonly List<int> Zooms = new(); public readonly List<(int W, Vec2 P)> Fires = new();
        public readonly List<(int To, int Type)> MsgOut = new();
        private readonly HashSet<int> _blipIds = new();

        public DrivenSelf(int tick, int index, int fleetSize, JsonElement s, Dictionary<int, JsonElement> all, IArena arena, IRules rules, IRandom rnd, Options o)
        {
            Tick = tick; Index = index; FleetSize = fleetSize; _s = s; _all = all; Arena = arena; Rules = rules; Random = rnd; _o = o;
            Position = V(s.GetProperty("pos")); Velocity = V(s.GetProperty("vel"));
            Altitude = s.GetProperty("alt").GetDouble(); Health = s.GetProperty("hp").GetDouble();
            _energy = s.GetProperty("en").GetDouble(); LookAngle = s.GetProperty("look").GetDouble();
            var am = s.GetProperty("ammo"); _ammo[0] = am[0].GetInt32(); _ammo[1] = am[1].GetInt32();
            var cd = s.GetProperty("cd"); _cd[0] = cd[0].GetInt32(); _cd[1] = cd[1].GetInt32();
            if (s.TryGetProperty("blips", out var b)) foreach (var x in b.EnumerateArray()) _blipIds.Add(x[0].GetInt32());
        }

        public void SetEnergy(double e) => _energy = e;
        public int Index { get; }
        public int FleetSize { get; }
        public bool IsAdmiral => Index == 0;
        public bool IsAllyAlive(int index) => _all.ContainsKey(index);
        public int Tick { get; }
        public Vec2 Position { get; }
        public double Altitude { get; }
        public Vec2 Velocity { get; }
        public double Health { get; }
        public double Energy => _energy;
        public double LookAngle { get; }
        public int Ammo(Weapon w) => _ammo[(int)w & 1];
        public int Cooldown(Weapon w) => _cd[(int)w & 1];
        public IArena Arena { get; }
        public IRules Rules { get; }
        public IRandom Random { get; }
        public void Thrust(Vec2 d) => ThrustCmd = d.ClampLength(1);
        public void Look(double a) => LookCmd = Vec2.NormalizeAngle(a);
        public void LookAt(Vec2 p) { var d = p - Position; if (d.LengthSquared > 1e-12) LookCmd = d.Angle; }
        public bool Fire(Weapon w, Vec2 target)
        {
            int k = (int)w;
            if (_cd[k] > 0 || _ammo[k] <= 0 || (k == 0 ? _firedP : _firedR)) return false;
            if (k == 0) _firedP = true; else _firedR = true;
            if (_o.FireUpdatesImmediately) { _ammo[k]--; _cd[k] = k == 0 ? Rules.PistolCooldown : Rules.RocketCooldown; }
            Fires.Add((k, target)); return true;
        }
        public bool Zoom(int id)
        {
            if (_zooms >= Rules.MaxZoomsPerTick || _energy < Rules.ZoomCost || !_blipIds.Contains(id)) return false;
            _zooms++; if (_o.ZoomDeductsImmediately) _energy -= Rules.ZoomCost; Zooms.Add(id); return true;
        }
        public void Send(int to, int type, params double[] data) { if (_msgs >= Rules.MaxMessagesPerTick) return; _msgs++; if (to >= 0 && to < FleetSize && to != Index) MsgOut.Add((to, type)); }
        public void Broadcast(int type, params double[] data) { if (_msgs >= Rules.MaxMessagesPerTick) return; _msgs++; MsgOut.Add((-1, type)); }
        public void Log(string text) { }
        public bool Shout(int m) => m >= 0 && m < WarCry.Count;
    }
}
