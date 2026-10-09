using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Shtemeri.Api;

namespace Shtemeri.Sim;

/// <summary>
/// Writes a fleet's telemetry in the server's telemetry v1 layout (gzip JSON Lines: header, one line per tick, result),
/// so a local match can be compared field by field with a server one. Rounding as on the server (2 decimals).
/// </summary>
internal sealed class TelemetryRecorder
{
    private readonly MemoryStream _ms = new();
    private readonly Utf8JsonWriter _w;
    private readonly int _slot;
    private bool _tickOpen;

    public int Slot => _slot;

    public TelemetryRecorder(int slot, ulong seed, SimRules rules)
    {
        _slot = slot;
        _w = new Utf8JsonWriter(_ms);
        _w.WriteStartObject();
        _w.WriteString("type", "header"); _w.WriteNumber("fleetId", slot); _w.WriteNumber("fleetSize", rules.FleetSize);
        _w.WriteNumber("seed", seed); _w.WriteString("simulator", "shtemeri-sim");
        _w.WriteEndObject(); _w.Flush(); _ms.WriteByte((byte)'\n');
    }

    private static double R(double v) => Math.Round(v, 2);

    public void BeginTick(int t, Zone z)
    {
        _w.Reset(_ms);
        _w.WriteStartObject();
        _w.WriteNumber("t", t);
        _w.WriteStartArray("zone"); _w.WriteNumberValue(R(z.Center.X)); _w.WriteNumberValue(R(z.Center.Y)); _w.WriteNumberValue(R(z.Radius)); _w.WriteEndArray();
        _w.WriteStartArray("i");
        _tickOpen = true;
    }

    /// <summary>State at the start of the tick (what the fleet saw) and the commands it issued.</summary>
    public void Shtemer(Bot b, double altitude, IReadOnlyList<Blip> blips, IReadOnlyList<Contact> contacts,
                        IReadOnlyList<Message> msgIn, IReadOnlyList<HitEvent> hits, IReadOnlyList<CollisionEvent> colls,
                        TelemetryCommands cmd, long budget)
    {
        var w = _w;
        w.WriteStartObject();
        w.WriteNumber("idx", b.Index);
        Vec(w, "pos", cmd.Pos); w.WriteNumber("alt", R(altitude)); Vec(w, "vel", cmd.Vel);
        w.WriteNumber("hp", Math.Round(cmd.Hp, 1)); w.WriteNumber("en", Math.Round(cmd.Energy, 1)); w.WriteNumber("look", R(cmd.Look));
        w.WriteStartArray("ammo"); w.WriteNumberValue(cmd.Ammo0); w.WriteNumberValue(cmd.Ammo1); w.WriteEndArray();
        w.WriteStartArray("cd"); w.WriteNumberValue(cmd.Cd0); w.WriteNumberValue(cmd.Cd1); w.WriteEndArray();
        w.WriteStartArray("blips");
        foreach (var bl in blips)
        {
            w.WriteStartArray(); w.WriteNumberValue(bl.Id); w.WriteNumberValue(R(bl.Position.X)); w.WriteNumberValue(R(bl.Position.Y)); w.WriteNumberValue((int)bl.Size); w.WriteEndArray();
        }
        w.WriteEndArray();
        if (contacts.Count > 0)
        {
            w.WriteStartArray("contacts");
            foreach (var c in contacts)
            {
                w.WriteStartObject();
                w.WriteNumber("blip", c.BlipId); w.WriteNumber("kind", (int)c.Kind); Vec(w, "pos", c.Position); Vec(w, "vel", c.Velocity);
                if (c.Kind == ContactKind.Shtemer) { w.WriteBoolean("ally", c.IsAlly); w.WriteNumber("fleet", c.FleetId); w.WriteNumber("index", c.Index); w.WriteNumber("hp", Math.Round(c.Health, 1)); }
                if (c.Kind == ContactKind.Loot) w.WriteNumber("loot", (int)c.Loot);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (msgIn.Count > 0)
        {
            w.WriteStartArray("msgIn");
            foreach (var m in msgIn) { w.WriteStartArray(); w.WriteNumberValue(m.From); w.WriteNumberValue(m.Type); Nums(w, m.Data); w.WriteEndArray(); }
            w.WriteEndArray();
        }
        if (hits.Count > 0)
        {
            w.WriteStartArray("hits");
            foreach (var h in hits) { w.WriteStartArray(); w.WriteNumberValue(Math.Round(h.Damage, 1)); w.WriteNumberValue((int)h.Weapon); w.WriteBooleanValue(h.Splash); w.WriteNumberValue(R(h.Direction.X)); w.WriteNumberValue(R(h.Direction.Y)); w.WriteEndArray(); }
            w.WriteEndArray();
        }
        if (colls.Count > 0)
        {
            w.WriteStartArray("coll");
            foreach (var c in colls) { w.WriteStartArray(); w.WriteNumberValue((int)c.Kind); w.WriteNumberValue(R(c.Normal.X)); w.WriteNumberValue(R(c.Normal.Y)); w.WriteEndArray(); }
            w.WriteEndArray();
        }
        w.WriteStartObject("cmd");
        Vec(w, "thrust", cmd.Thrust); w.WriteNumber("look", R(cmd.LookTarget));
        if (cmd.Zooms.Count > 0) { w.WriteStartArray("zoom"); foreach (var z in cmd.Zooms) w.WriteNumberValue(z); w.WriteEndArray(); }
        if (cmd.Fires.Count > 0)
        {
            w.WriteStartArray("fire");
            foreach (var (wp, p) in cmd.Fires) { w.WriteStartArray(); w.WriteNumberValue(wp); w.WriteNumberValue(R(p.X)); w.WriteNumberValue(R(p.Y)); w.WriteEndArray(); }
            w.WriteEndArray();
        }
        if (cmd.MsgOut.Count > 0)
        {
            w.WriteStartArray("msgOut");
            foreach (var (to, type, data) in cmd.MsgOut) { w.WriteStartArray(); w.WriteNumberValue(to); w.WriteNumberValue(type); Nums(w, data); w.WriteEndArray(); }
            w.WriteEndArray();
        }
        w.WriteEndObject();
        if (cmd.Log.Count > 0) { w.WriteStartArray("log"); foreach (var l in cmd.Log) w.WriteStringValue(l); w.WriteEndArray(); }
        w.WriteNumber("budget", budget);
        w.WriteEndObject();
    }

    public void EndTick()
    {
        if (!_tickOpen) return;
        _w.WriteEndArray(); _w.WriteEndObject(); _w.Flush(); _ms.WriteByte((byte)'\n');
        _tickOpen = false;
    }

    public byte[] Finish(FleetStats stats)
    {
        EndTick();
        _w.Reset(_ms);
        _w.WriteStartObject(); _w.WriteString("type", "result"); _w.WriteNumber("place", stats.Place); _w.WriteEndObject(); _w.Flush();
        _ms.WriteByte((byte)'\n');
        using var outMs = new MemoryStream();
        using (var gz = new GZipStream(outMs, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(_ms.ToArray());
        return outMs.ToArray();
    }

    private static void Vec(Utf8JsonWriter w, string name, Vec2 v) { w.WriteStartArray(name); w.WriteNumberValue(R(v.X)); w.WriteNumberValue(R(v.Y)); w.WriteEndArray(); }
    private static void Nums(Utf8JsonWriter w, IReadOnlyList<double> d) { w.WriteStartArray(); foreach (var x in d) w.WriteNumberValue(Math.Round(x, 4)); w.WriteEndArray(); }
}

/// <summary>What one shtemer did during its callbacks of one tick (filled by <see cref="Agent"/> when telemetry is on).</summary>
internal sealed class TelemetryCommands
{
    public Vec2 Pos, Vel, Thrust; public double Hp, Energy, Look, LookTarget; public int Ammo0, Ammo1, Cd0, Cd1;
    public readonly List<int> Zooms = new();
    public readonly List<(int W, Vec2 P)> Fires = new();
    public readonly List<(int To, int Type, double[] Data)> MsgOut = new();
    public readonly List<string> Log = new();
    public void Clear() { Zooms.Clear(); Fires.Clear(); MsgOut.Clear(); Log.Clear(); }
}
