using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Shtemeri.Sim;
using Shtemeri.Sim.Hosting;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

const string Usage = """
shtemeri-sim: local Shtemeri arena. Runs C# fleets (the same source the server takes) without a server.

  shtemeri-sim [options]

Fleets (2 to 8):
  --fleet Name=path.cs        add a fleet (repeat); the same file or name may appear twice (shown as Name#2)
  --fleets file.txt           lines "Name=path.cs" (# comments); relative paths are relative to the file

Arena, zone and boxes:
  (default)                   generated from --seed with the simulator's generator
  --arenas DIR                terrain and rocks from a server replay in DIR (picked by seed), rest generated
  --replay FILE               arena, zone circles, boxes and start slots of that server match (.json or .json.gz)

Running:
  --seed N                    first seed (default 1)
  --games N                   number of matches (seed, seed+1, ...; default 1)
  --threads N                 matches in parallel (default: 3/4 of the logical processors)
  --rotate                    rotate the fleets through the slots from game to game
  --out PATH                  replay JSON of a single match (.json or .json.gz)
  --out-dir DIR               replay of every match as DIR/<seed>.json.gz
  --results FILE              one JSON line per match (placements, stats; "entries": input index of each slot)
  --telemetry DIR             telemetry v1 of every fleet as DIR/<seed>.slot<k>.jsonl.gz (server layout)
  --drive TEL.jsonl.gz        semantics check: feed one fleet (--fleet, --replay) the inputs recorded in a server
                              telemetry file and compare its commands with the recorded ones
  A fleet path ending in .jsonl.gz is a scripted fleet: it replays the commands of a server telemetry file.
  --log FILE                  me.Log lines of a single match as "tick gid fleet#index text"
  --no-budget                 do not meter the instruction budget
  --max-ticks N               stop matches early
  --quiet                     only the summary
""";

var fleetSpecs = new List<(string Name, string Path)>();
ulong seed = 1; int games = 1, threads = Math.Max(1, Environment.ProcessorCount * 3 / 4); int? maxTicks = null;
string? replay = null, arenasDir = null, outPath = null, outDir = null, resultsPath = null, logPath = null, telDir = null, drivePath = null;
bool zoomLate = false, fireLate = false;
bool budget = true, rotate = false, quiet = false;

for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value after " + args[i]);
    switch (args[i])
    {
        case "--fleet": fleetSpecs.Add(ParseFleet(Next(), Environment.CurrentDirectory)); break;
        case "--fleets":
            string file = Next();
            foreach (var line in File.ReadAllLines(file))
            {
                var l = line.Trim();
                if (l.Length == 0 || l.StartsWith('#')) continue;
                fleetSpecs.Add(ParseFleet(l, Path.GetDirectoryName(Path.GetFullPath(file))!));
            }
            break;
        case "--seed": seed = ulong.Parse(Next()); break;
        case "--games": games = int.Parse(Next()); break;
        case "--threads": threads = int.Parse(Next()); break;
        case "--replay": replay = Next(); break;
        case "--arenas": arenasDir = Next(); break;
        case "--out": outPath = Next(); break;
        case "--out-dir": outDir = Next(); break;
        case "--results": resultsPath = Next(); break;
        case "--log": logPath = Next(); break;
        case "--telemetry": telDir = Next(); break;
        case "--drive": drivePath = Next(); break;
        case "--zoom-late": zoomLate = true; break;
        case "--fire-late": fireLate = true; break;
        case "--no-budget": budget = false; break;
        case "--max-ticks": maxTicks = int.Parse(Next()); break;
        case "--rotate": rotate = true; break;
        case "--quiet": quiet = true; break;
        case "-h": case "--help": Console.WriteLine(Usage); return 0;
        default: Console.Error.WriteLine("unknown option " + args[i]); Console.WriteLine(Usage); return 2;
    }
}
if (drivePath != null)
{
    if (fleetSpecs.Count != 1 || replay == null) { Console.Error.WriteLine("--drive needs exactly one --fleet and --replay"); return 2; }
    var prog = FleetCompiler.Load(fleetSpecs[0].Name, fleetSpecs[0].Path);
    var setup0 = MatchSetup.FromReplay(replay, SimRules.Season);
    var rep = TelemetryDriver.Run(prog, drivePath, setup0, SimRules.Season, int.Parse(Environment.GetEnvironmentVariable("DRIVE_EXAMPLES") ?? "30"), null,
        new TelemetryDriver.Options { ZoomDeductsImmediately = !zoomLate, FireUpdatesImmediately = !fireLate });
    Console.WriteLine($"driven {rep.Ticks} ticks, exceptions {rep.Errors}");
    foreach (var (k, c) in rep.Compared) Console.WriteLine($"  {k,-8} compared {c,6}  mismatches {rep.Mismatch.GetValueOrDefault(k),5} ({100.0 * rep.Mismatch.GetValueOrDefault(k) / c:0.00} %)");
    foreach (var e in rep.Examples) Console.WriteLine("    " + e);
    return 0;
}
if (fleetSpecs.Count < 2 || fleetSpecs.Count > 8) { Console.Error.WriteLine("need 2 to 8 fleets"); Console.WriteLine(Usage); return 2; }

var rules = SimRules.Season;
var sw = Stopwatch.StartNew();
var programs = new List<FleetEntry>();
var scriptStats = new ScriptedFleet.Stats();
var seenNames = new Dictionary<string, int>();
foreach (var (specName, path) in fleetSpecs)
{
    // a fleet is identified by its input index; a repeated name is shown as Name#2, Name#3, ... (as in replays)
    seenNames[specName] = seenNames.GetValueOrDefault(specName) + 1;
    string name = seenNames[specName] > 1 ? $"{specName}#{seenNames[specName]}" : specName;
    try
    {
        var prog = path.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase)
            ? ScriptedFleet.Load(name, path, scriptStats)
            : FleetCompiler.Load(name, path);
        programs.Add(new FleetEntry { Name = name, Program = prog });
    }
    catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
}
if (!quiet) Console.WriteLine($"compiled {programs.Count} fleets in {sw.Elapsed.TotalSeconds:0.0} s");

string[] arenaFiles = arenasDir != null
    ? Directory.GetFiles(arenasDir, "*.json*").OrderBy(f => f, StringComparer.Ordinal).ToArray()
    : Array.Empty<string>();
MatchSetup? fixedSetup = replay != null ? MatchSetup.FromReplay(replay, rules) : null;
if (fixedSetup != null && !quiet)
    Console.WriteLine($"setup from {fixedSetup.Origin}: slots in the server match: {string.Join(", ", fixedSetup.FleetNames)}");

MatchSetup SetupFor(ulong s)
{
    if (fixedSetup != null) return fixedSetup;
    if (arenaFiles.Length > 0)
    {
        string f = arenaFiles[(int)(s % (ulong)arenaFiles.Length)];
        using var doc = MatchSetup.ReadReplayJson(f);
        return MatchSetup.Generate(s, programs.Count, rules, MatchSetup.ArenaFromReplay(doc.RootElement, rules), "arena of " + Path.GetFileName(f));
    }
    return MatchSetup.Generate(s, programs.Count, rules);
}

var results = new MatchResult[games];
var entries = new int[games][];   // per game: input index of the fleet in each slot
sw.Restart();
long ticksTotal = 0;
Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) }, g =>
{
    ulong s = seed + (ulong)g;
    var order = Enumerable.Range(0, programs.Count).Select(k => rotate ? (k + g) % programs.Count : k).ToList();
    var fleets = order.Select(k => programs[k]).ToList();
    entries[g] = order.ToArray();
    var match = new Match(SetupFor(s), fleets, rules, new MatchOptions
    {
        RecordReplay = outPath != null || outDir != null, EnforceBudget = budget, MaxTicks = maxTicks, RngSeed = s,
        TelemetrySlots = telDir != null ? Enumerable.Range(0, fleets.Count).ToList() : Array.Empty<int>(),
    });
    StreamWriter? log = null;
    if (logPath != null && games == 1)
    {
        log = new StreamWriter(logPath, false, new UTF8Encoding(false));
        match.LogLine = (gid, tick, text) => log.WriteLine($"{tick} {gid} {fleets[gid / rules.FleetSize].Name}#{gid % rules.FleetSize} {text}");
    }
    var r = match.Run();
    log?.Dispose();
    if (telDir != null)
    {
        Directory.CreateDirectory(telDir);
        foreach (var (slot, bytes) in r.Telemetry) File.WriteAllBytes(Path.Combine(telDir, $"{s}.slot{slot}.jsonl.gz"), bytes);
    }
    results[g] = r;
    Interlocked.Add(ref ticksTotal, r.TotalTicks);
    if (r.Replay != null)
    {
        string target = outPath ?? Path.Combine(outDir!, $"{s}.json.gz");
        if (games > 1 && outPath != null) target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, $"{Path.GetFileNameWithoutExtension(outPath)}.{s}.json");
        WriteReplay(target, r.Replay);
    }
    if (!quiet && games > 1) Console.WriteLine($"  game {g + 1}/{games} seed {s}: {r.TotalTicks} ticks, winner {r.FleetNames[r.Placements[0]]}");
});
double secs = sw.Elapsed.TotalSeconds;

if (games == 1 && !quiet) PrintMatch(results[0]);
PrintSummary(results, entries, programs.Select(p => p.Name).ToList());
if (scriptStats.FiresRequested > 0) Console.WriteLine($"scripted fires: {scriptStats.FiresRequested}, refused locally {scriptStats.FiresRefused}");
Console.WriteLine($"{games} matches in {secs:0.0} s: {games / secs:0.00} matches/s, {ticksTotal / secs:0} ticks/s ({threads} threads)");

if (resultsPath != null)
{
    using var w = new StreamWriter(resultsPath, false, new UTF8Encoding(false));
    for (int g = 0; g < results.Length; g++)
    {
        var r = results[g];
        w.Write($"{{\"seed\":{r.Seed},\"ticks\":{r.TotalTicks},\"fleets\":[{string.Join(",", r.FleetNames.Select(n => "\"" + n.Replace("\"", "'") + "\""))}],");
        w.Write($"\"entries\":[{string.Join(",", entries[g])}],");
        w.Write($"\"placements\":[{string.Join(",", r.Placements)}],\"stats\":[");
        w.Write(string.Join(",", r.Stats.Select(s =>
            $"{{\"fleet\":{s.Fleet},\"place\":{s.Place},\"kills\":{s.Kills},\"damageDealt\":{s.DamageDealt:0.0},\"damageTaken\":{s.DamageTaken:0.0}," +
            $"\"shotsPistol\":{s.ShotsPistol},\"shotsRocket\":{s.ShotsRocket},\"hits\":{s.Hits},\"loot\":{s.Loot},\"survivedTicks\":{s.SurvivedTicks}," +
            $"\"budgetExceeded\":{s.BudgetExceeded},\"errors\":{s.Errors},\"budgetMean\":{(s.BudgetTicks > 0 ? s.BudgetSum / s.BudgetTicks : 0)},\"budgetMax\":{s.BudgetMax}}}")));
        w.WriteLine("]}");
    }
}
return 0;

static (string, string) ParseFleet(string spec, string baseDir)
{
    int eq = spec.IndexOf('=');
    if (eq <= 0) throw new ArgumentException("fleet must be Name=path.cs: " + spec);
    string path = spec[(eq + 1)..].Trim();
    if (!Path.IsPathRooted(path)) path = Path.Combine(baseDir, path);
    return (spec[..eq].Trim(), path);
}

static void WriteReplay(string path, byte[] json)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        gz.Write(json);
    }
    else File.WriteAllBytes(path, json);
}

static void PrintMatch(MatchResult r)
{
    Console.WriteLine($"seed {r.Seed} ({r.Origin}), {r.TotalTicks} ticks ({r.TotalTicks / 20.0:0.0} s)");
    Console.WriteLine($"{"place",5}  {"fleet",-14} {"kills",5} {"dealt",8} {"taken",8} {"surv s",7} {"pistol",6} {"rkt",4} {"hits",5} {"loot",4} {"budget avg/max",15} {"over",4} {"err",4}");
    foreach (int slot in r.Placements)
    {
        var s = r.Stats[slot];
        Console.WriteLine($"{s.Place,5}  {r.FleetNames[slot],-14} {s.Kills,5} {s.DamageDealt,8:0.0} {s.DamageTaken,8:0.0} {s.SurvivedTicks / 20.0,7:0.0} {s.ShotsPistol,6} {s.ShotsRocket,4} {s.Hits,5} {s.Loot,4} {(s.BudgetTicks > 0 ? s.BudgetSum / s.BudgetTicks : 0),7}/{s.BudgetMax,-7} {s.BudgetExceeded,4} {s.Errors,4}");
        if (s.FirstError != null) Console.WriteLine($"        first error: {s.FirstError}");
    }
}

static void PrintSummary(MatchResult[] results, int[][] entries, List<string> names)
{
    if (results.Length < 2) return;
    var places = names.Select(_ => new List<int>()).ToArray();
    var dealt = new double[names.Count];
    for (int g = 0; g < results.Length; g++)
        for (int slot = 0; slot < results[g].FleetNames.Count; slot++)
        {
            int e = entries[g][slot];
            places[e].Add(results[g].Stats[slot].Place);
            dealt[e] += results[g].Stats[slot].DamageDealt;
        }
    Console.WriteLine($"{"fleet",-14} {"games",5} {"wins",5} {"win%",6} {"avg place",9} {"avg dealt",9}");
    foreach (int e in Enumerable.Range(0, names.Count).OrderBy(e => places[e].Average()))
        Console.WriteLine($"{names[e],-14} {places[e].Count,5} {places[e].Count(p => p == 1),5} {100.0 * places[e].Count(p => p == 1) / places[e].Count,6:0.0} {places[e].Average(),9:0.00} {dealt[e] / places[e].Count,9:0.0}");
}
