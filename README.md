# shtemeri-sim

A local simulator of the [Shtemeri](https://shtemeri.informacija.hr) arena. It runs the same C# fleet source the
server accepts, with no server and no changes to the fleet, so you can play thousands of matches on your own machine.

- Same API: fleets compile against `Shtemeri.Api`, copied verbatim from the season guide (chapter 9).
- Same numbers: the season table (guide, chapter 8), plus the mechanics the guide describes only in words, measured
  from server telemetry and replays (see `METHODOLOGY.md` for every number, how it was measured and its error).
- Same output: every match can be written as replay JSON v1, the format the server publishes, so analysis scripts
  written for server replays read local matches unchanged.
- Deterministic: the same fleets and the same seed give the same match, byte for byte.

## Requirements

.NET 8 SDK. The first build downloads `Microsoft.CodeAnalysis.CSharp` (Roslyn) from NuGet.

## Quick start

```
dotnet run -c Release --project src/Shtemeri.Sim.Cli -- --fleets fleets/example6.txt --games 100
```

`fleets/example6.txt` lists the fleets (six copies of the small example fleet in `fleets/examples/`), one `Name=path.cs` per line (paths relative to the file). Fleets can also be given
on the command line: `--fleet Mine=../my/Fleet.cs --fleet Old=../my/Fleet.v3.cs`. The same file may appear several times.

Useful options (`--help` lists all):

| Option | What it does |
|---|---|
| `--seed N`, `--games N` | play seeds N, N+1, ... |
| `--rotate` | rotate the fleets through the start slots from game to game |
| `--arenas DIR` | take terrain and rocks from server replays in DIR (one per seed); zone and boxes are generated |
| `--replay FILE` | replay the setup of one server match: arena, zone circles, the boxes the server spawned, start slots. List the fleets in the server's slot order (the program prints it). With `--games N` the same setup is played with N different random streams |
| `--out FILE`, `--out-dir DIR` | write replay JSON v1 (`.json` or `.json.gz`) |
| `--results FILE` | one JSON line per match: placements and per-fleet stats |
| `--log FILE` | the fleets' `me.Log` lines of a single match |
| `--no-budget` | run fleets without the instruction meter |

Example output of a batch:

```
fleet          games  wins   win% avg place avg dealt
MyFleet           32    13   40.6      2.09    1292.7
...
32 matches in 4.2 s: 7.7 matches/s
```

## How faithful is it

Measured against server telemetry (exact commands and states of one's own fleet) and replays (all fleets):

- movement, slope, parking: velocity error 0.006 m/s per tick (rounding of the telemetry);
- look turning, energy and zoom: exact;
- blip noise (uniform in a disk of 3 % of the distance, fresh every tick), cone, proximity, line of sight
  (eye 1.6 m to 1.0 m above the target's ground): 99.95 % agreement on what is seen;
- pistol hits, replayed bullet by bullet through the hit model: 98.9 % agreement with the server;
- rocket splash: 35·(1 − d/5) on the 2D distance, error 0.006 m;
- zone schedule and damage, box spawning, pickups, drops: as on the server (METHODOLOGY.md).

With the same setup as a server match, a local match follows the server's positions to about 1 cm for the first
three seconds, then diverges (the noise of blips and `me.Random` are not the server's). Over six server test matches
the match length, deaths by cause, pickups and the per-fleet averages agree well; `METHODOLOGY.md` lists the known differences.

What is approximate:

- The instruction budget. The server counts IL instructions; the simulator inserts a meter at the start of every
  block of fleet code and estimates the block's cost from its syntax, scaled to match the server's average spend.
  It stops runaway code and puts a fleet in the right range, but a fleet that lives near 50 000 per tick will not
  overrun on exactly the same ticks.
- The server's random generator is not public: generated arenas, zones and boxes have the right statistics, not the
  server's values for a seed. Use `--replay` to play on a server match's setup.
- Code restrictions (no static fields, no `System.Random`, ...) are not enforced; the server's compiler does that.

## Layout

```
src/Shtemeri.Api       the fleet API (from the guide)
src/Shtemeri.Sim       engine: Match (tick loop), Arena (terrain, rocks, line of sight), MatchSetup (generated or
                       from a replay), Agent (ISelf), ReplayRecorder (replay v1), Hosting/FleetCompiler (Roslyn + meter)
src/Shtemeri.Sim.Cli   command line runner
calib/                 Python scripts that measured the mechanics from telemetry and replays (METHODOLOGY.md)
fleets/                fleet lists and a small example fleet
```

## Using the engine from code

```csharp
var fleets = new[] {
    new FleetEntry { Name = "A", Program = FleetCompiler.Load("A", "A.cs") },
    new FleetEntry { Name = "B", Program = FleetCompiler.Load("B", "B.cs") },
};
var setup = MatchSetup.Generate(seed: 7, fleets: fleets.Length, SimRules.Season);
MatchResult r = new Match(setup, fleets).Run();
```

## Contributing

The simulator is only as good as its measurements. `METHODOLOGY.md` explains where every number comes from, how
faithfulness is checked (same-setup runs, the telemetry driver, scripted fleets) and what is still an assumption.
If you measure something better, or find a difference from the server, open an issue or a pull request with the
sample size and the error.

## License

MIT, see `LICENSE`.
