using System.IO.Compression;
using System.Text.Json;
using Shtemeri.Api;

namespace Shtemeri.Sim.Hosting;

/// <summary>
/// A fleet that replays the thrust, look and fire commands recorded in a server telemetry file instead of running code.
/// Running all fleets of a server match this way checks the engine's rules (movement, hits, damage, pickups, zone)
/// over a whole match without any decision feedback: the commands are the server's, only the world is local.
/// </summary>
public static class ScriptedFleet
{
    public sealed class Stats { public int FiresRequested, FiresRefused; }

    private sealed record Cmd(Vec2 Thrust, double Look, List<(int W, Vec2 P)> Fires);

    public static FleetProgram Load(string name, string telemetryPath, Stats? stats = null)
    {
        var table = new Dictionary<(int, int), Cmd>();
        using (var fs = File.OpenRead(telemetryPath))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var sr = new StreamReader(gz))
        {
            string? ln;
            while ((ln = sr.ReadLine()) != null)
            {
                if (ln.Length == 0) continue;
                using var doc = JsonDocument.Parse(ln);
                var o = doc.RootElement;
                if (!o.TryGetProperty("t", out var tt) || !o.TryGetProperty("i", out var arr)) continue;
                foreach (var s in arr.EnumerateArray())
                {
                    if (!s.TryGetProperty("cmd", out var c) || !c.TryGetProperty("thrust", out var th)) continue;
                    var fires = new List<(int, Vec2)>();
                    if (c.TryGetProperty("fire", out var f))
                        foreach (var x in f.EnumerateArray()) fires.Add((x[0].GetInt32(), new Vec2(x[1].GetDouble(), x[2].GetDouble())));
                    table[(tt.GetInt32(), s.GetProperty("idx").GetInt32())] =
                        new Cmd(new Vec2(th[0].GetDouble(), th[1].GetDouble()), c.GetProperty("look").GetDouble(), fires);
                }
            }
        }
        stats ??= new Stats();
        return new FleetProgram(name, telemetryPath, () => new Player(table, stats));
    }

    private sealed class Player : Shtemer
    {
        private readonly Dictionary<(int, int), Cmd> _t; private readonly Stats _s;
        public Player(Dictionary<(int, int), Cmd> t, Stats s) { _t = t; _s = s; }
        public override void OnTick(ISelf me, ISenses senses)
        {
            if (!_t.TryGetValue((me.Tick, me.Index), out var c)) return;
            me.Thrust(c.Thrust);
            me.Look(c.Look);
            foreach (var (w, p) in c.Fires)
            {
                _s.FiresRequested++;
                if (!me.Fire((Weapon)w, p)) _s.FiresRefused++;
            }
        }
    }
}
