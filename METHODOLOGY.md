# How the simulator was built and checked

This document is for people who want to run, check or improve the simulator. It lists every mechanic, the number
the simulator uses, where that number comes from, how well it matches the server, and how to measure it again.

The approach in one paragraph: the season guide gives the API and most constants. Everything the guide describes
only in words was **measured** from the server: from **telemetry** (one's own fleet: exact state and commands every
tick) and from **replays** (all fleets, every second tick). Each mechanic has a script in `calib/` that fits it and
reports the error. The engine was then checked in three ways that separate the *world* from the *fleet's decisions*
(section 4). Anything that could not be measured is listed as an assumption (section 5).

## 1. Data

| Source | What it contains | How to get it |
|---|---|---|
| Season guide | API (chapter 9), constants (chapter 8), mechanics in words | the `get_guide` tool of the Shtemeri MCP server |
| Ranked replays | all fleets, positions/health/energy every 2nd tick, projectiles, boxes, events (fire, hit, pickup, death, zoom, boom), zone, final stats | `https://shtemeri.informacija.hr/api/matches/{id}/replay?k=x` (public for ranked matches; may be gzipped) |
| Test match replays | the same, for your own test matches | the link `run_test_match` returns |
| Telemetry | one's own fleet, every tick: state at the start of the tick, blips, contacts, messages, hits, collisions, commands, budget, log | `get_telemetry` / `get_telemetry_download` |

The scripts read ranked replays from `$SHTEMERI_REPLAYS` (default `data/replays/{id}.json.gz`), telemetry from
`$SHTEMERI_TELEMETRY` (default `calib/telemetry/`), and server test match replays from `validation/server/`.
The original calibration used about 9 300 ranked replays (the whole history at the time), 9 telemetries and
12 test matches. None of that data is in this repository; download your own.

**Time alignment.** Telemetry at tick `t` is the state at the *start* of tick `t` plus the commands issued in `t`.
A replay frame `t` is the state *after* tick `t`. Telemetry `t+1` equals replay frame `t` (difference 0.000 m).
Every script relies on this.

## 2. Constants from the guide

Taken as published, and repeated in the telemetry header (`header.rules`), which also adds a few that `IRules`
does not expose (parking). They were identical in all telemetries checked.

| Group | Values |
|---|---|
| time | 20 ticks/s, at most 3600 ticks |
| arena | 100 × 100 m, height grid 101 × 101 (1 m), bilinear interpolation |
| fleets | 4 shtemers, start ring radius 40 |
| body | radius 1, height 2, eye 1.6, gun 1.2, aim point 1.0 m |
| movement | thrust 12 m/s², drag 1.5 /s, top speed 8 m/s (an equilibrium, not a cap), slope 6 m/s² |
| parking | speed 0.5 m/s, slope force 3 m/s² (telemetry header only) |
| health | 250 |
| vision | range 32 m, half angle 0.87 rad, proximity 6 m, look turn 0.21 rad/tick, blip noise 0.03 |
| energy | 100, regeneration 0.4/tick, zoom 8, at most 3 zooms/tick |
| pistol | 40 m/s, 4 damage, cooldown 8, range 30 m, ammo 40 / max 120 |
| rocket | 18 m/s, 25 direct + 35 splash, radius 5, cooldown 50, range 45 m, 1 / max 6 |
| boxes | pickup 1.8 m; ammo 30, rockets 2, repair 50; 10 at the start, 3 every 200 ticks, at most 16 |
| zone | start radius 50, factor 0.62, 6 stages, hold 200, shrink 250 ticks, damage 0.3 per stage |
| messages | 8 numbers per message, 8 messages per tick, log 20 lines × 200 chars |
| war cry | 48 fixed messages, cooldown 400 ticks |
| budget | 50 000 instructions/tick; line of sight 60, height 5, log 20 |

## 3. Mechanics measured from data

Each line gives the model, the evidence and the error. Script names are in `calib/`.

**Terrain** (telemetry `alt`). Bilinear interpolation of `arena.heights`. Mean error 0.0025 m, max 0.010 m
(rounding to 2 decimals), 3 200 samples.

**Movement** (`movement2.py`; 39 406 tick pairs, collision ticks excluded). Semi-implicit Euler, dt = 0.05:

```
a  = 12·thrust − 1.5·v − 6·∇h      thrust clamped to length 1, ∇h = gradient of the bilinear surface
v' = v + a·dt
x' = x + v'·dt
parking: thrust exactly 0, |v| < 0.5 at the start of the tick and 6·|∇h| < 3  →  v' = 0
```

A free regression gives 12.006 / 1.500 / 5.976. Speed error: mean 0.0055 m/s, p99 0.014. Every large residual
was a tick where the telemetry rounded a small non-zero thrust to 0.00, so parking needs a thrust of exactly zero.
Rejected variants: implicit drag (error 0.020), exponential drag (0.010), position from the old speed (0.014 m).
There is no hard speed cap (the highest speed measured was 7.78 m/s).

**Collisions** (`collisions.py`).
- Rock: the shtemer is pushed out to rock radius + 1.000 (measured 0.999, p1–p99 0.989–1.008); the normal speed
  component towards the rock is removed, the tangential component stays. 537 cases, median error 0.017 m/s.
- Shtemer with shtemer: perfectly inelastic along the normal (both get the mean normal speed), tangential stays,
  distance restored to 2 m, **one** resolution pass per tick. Measured mostly in formations with several contacts
  at once, so the per-collision fit is rough (median 0.48 m/s). The pass count was chosen on a statistic: pairs
  closer than 2.3 m that are below 1.99 m are 20.9 % on the server and 19.7 % locally with one pass (three passes
  were too strict).
- Wall: no wall contact appears in any telemetry. Assumed: clamp to [1, 99] and remove the speed into the wall.

**Look and energy** (`look_energy.py`, 45 044 ticks). `look' = look + clamp(target − look, ±0.21)`, error 0.
`energy' = min(100, energy − 8·zooms + 0.4)`, error 0 on every tick. The server keeps energy as a double, takes it
at the moment of `Zoom`, and regenerates at the end of the tick. Cooldowns drop at the end of the tick.

**Vision and blips** (`blips.py`, `vision.py`; 120 125 blips, 386 016 observer–target pairs).
- Noise: blip = true position + a point uniform in a disk of radius 0.03 × distance. The ratio error/distance has
  mean 0.0200 in every distance band (theory for a uniform disk: 0.0200), max 0.030. Radial and tangential the same.
  Fresh every tick (correlation of the same blip at t and t+2: 0.004) and independent between observers (−0.003).
- Sizes: shtemer Medium, box Small, rocket in flight Small.
- Proximity: everything within 6 m is sensed (99.99 %), also through rocks and hills; the edge is sharp.
- Cone: angle ≤ 0.87 rad from the look direction at the start of the tick, range 32 m.
- Line of sight: from the eye (ground + 1.6) to ground + **1.0** above the target, blocked by terrain under the ray
  and by rocks (2D circle, infinitely tall). Agreement with telemetry: 99.95 % with 1.0, against 88.8 % (0.0),
  97.7 % (1.6), 96.5 % (2.0). Boxes: 99.9 % with 1.0.
- Blip ids count per observer (1, 2, 3, …); an object that leaves the view and comes back gets a new id.
- Zoom answer: position and velocity at the start of the next tick (error 0.000), only if the object is still
  visible (94–100 % of zooms answered).

**Projectiles** (`proj2.py`, `proj3.py`, `splash.py`, `pistolhits3.py`).
- Created from the shooter's position at the **start** of the tick (perpendicular error 0.003 m, against 0.08 for
  the moved position), 1.1 m from the centre towards the aim, at ground + 1.2, and moved one step in the same tick.
  Along the line: pistol 3.095 m after the first step (sd 0.012), 5.090 after the second; rocket 1.997 / 2.895.
- Flight: a 3D straight line towards (aim, ground(aim) + 1.0) and onwards with the same slope.
- A pistol bullet disappears beyond 30 m; many end in the terrain.
- A rocket explodes at the aim point, at the first body, or in the terrain.
- Hit test: a point of the path inside the body cylinder (2D ≤ 1 m, between the ground and ground + 2), against
  targets **after** they moved, in sub-steps of 0.50 m. Replaying 15 840 bullets of 12 server matches through the
  model: 98.9 % agreement (0.45 m 98.7 %, 0.67 m 98.6 %, one full 2 m step 89.1 %).
- Direct rocket hits explode 0.62/0.86/0.98 m (p5/p50/p95) from the target centre on the server, 0.61/0.84/0.99 locally.
- Splash: `35·(1 − d/5)`, d = 2D distance from the explosion to the centre after movement. Implied distance error
  mean 0.000 m, sd 0.006 (3 004 splash hits). Splash hits the shooter and his mates too.
- Event order of a direct rocket hit: explosion, splash to everybody (including the target), then the direct hit
  (26/26 cases). `OnHit` and the replay follow this order.

**Zone** (`zone.py`, `zonedmg.py`; 300 and 200 matches).
- Circles: (50, 50, 50), radii 50, 31, 19.22, 11.92, 7.39, 4.58, 0.
- Schedule: stage k holds circle k−1 from (k−1)·450 to (k−1)·450 + 200, then centre and radius shrink linearly to
  circle k in 250 ticks. Max error against `frames[].z`: 0.010.
- New centre: offset from the old centre uniform in a disk of radius (R_old − R_new) (mean (offset/room)² 0.47–0.54,
  theory 0.5).
- Damage outside: 0.3 × stage per tick, stage = min(6, ⌊t/450⌋ + 1). The median is exactly that value in every
  50-tick band (~30 000 samples).

**Boxes** (`loot.py`, `pickup.py`, `spawn.py`, `dropcontent.py`; 300 matches).
- 10 boxes at the start anywhere in the arena (3.0–97.0); then 3 every 200 ticks inside the current zone
  (distance/R ≤ 0.90, (r/R)² mean 0.418 against 0.405 for uniform), at least 1.49 m from a rock edge.
- Kinds (11 246 boxes): ammo 45.7 %, rockets 29.8 %, repair 24.5 %.
- A dead shtemer drops one box where it died: rockets if it had at least one, otherwise ammo (33/33), with the
  standard amount (27/27). Dropped boxes count towards the limit of 16 (spawn 14 → 2, 15 → 1, 16 → 0).
- Pickup: ≤ 1.8 m from the centre after movement; a box the shtemer does not need stays (a shtemer at full health
  stood within 1.2 m of a repair box 5 247 times and it stayed).

**Start.** Six fleets on a ring of radius 40 around (50, 50), 60° apart, random slot order and start angle.
Members in a diamond around the fleet centre: index 0 radially −2.83, 1 tangentially −2.83, 2 radially +2.83,
3 tangentially +2.83. Initial look towards the arena centre.

**Instruction budget.** The server counts IL instructions. The simulator rewrites fleet code with Roslyn: at the
start of every block it inserts `Meter.Step(n)`, n = the number of syntax nodes of the block's own statements
(method call × 3, unbraced branch half), scaled by 0.65. Loops without braces are wrapped, `catch` blocks rethrow
the meter's stop. Expression-bodied members and expression lambdas are not counted (an underestimate). API call
costs (line of sight 60, height 5, log 20) are charged by the engine. Mean spend per tick (server / local) for three
fleets with very different code: 8 557 / 8 762, 15 819 / 16 153, 1 519 / 1 295.

## 4. How faithfulness is checked

Three tools separate the world from the fleets' decisions, so a difference can be traced to its cause.

1. **Same setup** (`--replay FILE`). Arena, zone circles, the boxes the server spawned and the start slots of a
   server match. The local match follows the server's positions to about 1 cm (median, all 24 shtemers) for the
   first 60 ticks, then diverges, because the blip noise and `me.Random` are not the server's. Compare
   distributions over many local runs of the same setup: match length, deaths by cause, pickups, and the server's
   placement as a quantile of the local placements (0.5 is typical).
2. **Driver** (`--drive TELEMETRY --fleet X=... --replay ...`). The fleet gets exactly what its shtemers received on
   the server (state, blips, contacts, messages, hits, collisions, from the telemetry), and the commands it issues
   locally are compared with the recorded ones. The world is not simulated at all, so any difference comes from the
   API semantics: callback order, when energy and ammo are taken, `IsAllyAlive`, `Zone`, blip distance. On server
   inputs, fire decisions agree 99.98–100 %, thrust 0.1–2 % (more for a fleet that uses `me.Random` or a controller
   sensitive to the rounding of telemetry).
3. **Scripted fleets** (a fleet path ending in `.jsonl.gz`). All six fleets of a server match replay their recorded
   commands (thrust, look, fire) and the local engine computes the world. The decisions have no feedback, so only the
   engine is tested. On one match the total health of all 24 shtemers followed the server (5 391 / 5 399 at tick 200),
   and the first three deaths came at ticks 496, 613, 697 against 492, 610, 616 on the server.

`--telemetry DIR` writes local telemetry in the server's format; `calib/telcmp.py` compares a server and a local
telemetry tick by tick and reports the first tick where each field differs.

Results over 12 server test matches (six fleets each, every setup played 32 times locally): deaths per match by
pistol / rocket / zone 14.1 / 1.3 / 6.8 on the server against 14.1 / 1.9 / 6.3 locally; match length 2 648 ± 39
against 2 621 ± 126 ticks; pickups per match (ammo / rockets / repair) 29.5 / 18.5 / 10.5 against
29.5 / 18.8 / 10.2. The ordering of six versions of one fleet agreed with their ordering in server test matches
(Spearman 0.78–0.93).

## 5. Known gaps and assumptions

- The budget is an estimate (section 3). A fleet near 50 000 will not overrun on the same ticks as on the server.
- The server's random generator is not public. Generated arenas, zones and boxes have the server's statistics, not
  its values for a seed; use `--replay` to play on a real setup. Generated terrain is a sum of Gaussian hills
  (0–8 m) with 6–10 rocks; `--arenas DIR` takes terrain and rocks from real replays instead.
- Rocks are assumed infinitely tall; wall contact is assumed (no data).
- Shtemer collisions in crowds are fitted on a statistic, not per collision (section 3). Fleets that push other
  shtemers on purpose should be checked against the server.
- Rockets fired into a crowd: in some fleets the local rate of rockets fired while an enemy is closer than 4 m was
  about 3× the server's, on a small sample (12 matches). Not resolved.
- Code restrictions of the server's compiler (no static fields, no `System.Random`, …) are not enforced.

## 6. How to improve it

1. Download data (section 1). The more telemetry from your own fleets, the better: it is the only source of exact
   commands.
2. Pick a mechanic, run its script in `calib/`, and compare the printed error with the numbers in section 3.
3. Change the engine (`src/Shtemeri.Sim/Match.cs` holds the tick loop in the server's order; constants live in
   `SimRules.cs`), then check with the tools in section 4: the driver for API semantics, scripted fleets for the
   engine, same-setup runs for the whole.
4. Keep determinism: same fleets and seed must give a byte-identical replay (`--out` twice, compare hashes).
5. Report what you measured with the sample size and the error, as above, so the next person can repeat it.

The tick order in `Match.cs`: senses (blips from the state at the start of the tick, zoom answers) → `OnStart`,
`OnMessage`, `OnHit`, `OnCollision`, `OnTick` under one budget → projectiles from the shooters' start positions →
energy, look, movement, collisions → projectile flight (hits on moved targets) → zone damage → pickups → deaths, box
drops, eliminations → box spawn → cooldowns.
