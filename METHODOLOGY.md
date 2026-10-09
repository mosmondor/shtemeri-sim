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
a  = 12·thrust − 1.5·v − 6·∇h      thrust clamped to length 1
∇h = ((h(x+0.5, y) − h(x−0.5, y)), (h(x, y+0.5) − h(x, y−0.5)))   central difference of the interpolated height, step 0.5
v' = v + a·dt
x' = x + v'·dt
parking: thrust exactly 0, |v| < 0.5 at the start of the tick and 6·|∇h| < 3  →  v' = 0
```

A free regression gives 12.006 / 1.500 / 5.976 with the analytic gradient of the bilinear cell. The slope
coefficient fitted alone (24 097 moving samples) is 5.978 with the cell gradient and 5.998 with the central
difference above; on 67 315 free-flight samples of 15 telemetries the cell gradient leaves 399 samples (0.59 %)
outside the rounding bound, central differences with a step of 0.45–0.55 leave 0–1 (section 7). The same gradient
is used for parking. Speed error: mean 0.0055 m/s, p99 0.014. Every large residual
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
- Order within a tick: rocks and walls (with events), shtemer pairs (one pass, with events), rocks and walls again
  without events. In a shtemer's collision list the obstacle/wall events come before the shtemer events (137 of 137
  mixed lists in server telemetry). Whether this order also moves positions differently could not be shown with
  rounded telemetry (53 usable cases, none above the noise).

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
  97.7 % (1.6), 96.5 % (2.0). Boxes: 99.9 % with 1.0. The terrain is sampled at k/n of the ray, k = 1..n−1,
  n = ⌈L/0.5⌉: of 58 472 observer–shtemer pairs (in cone and range, clear of rocks) this rule gets 22 wrong, the
  earlier max(2, ⌊L/0.25⌋) 35 and a fine 0.02 m sampling 43.
- Blip ids count per observer (1, 2, 3, …); an object that leaves the view and comes back gets a new id.
- Zoom answer: position and velocity at the start of the next tick (error 0.000), only if the object is still
  visible (94–100 % of zooms answered).

**Projectiles** (`proj2.py`, `proj3.py`, `splash.py`, `pistolhits3.py`).
- Created from the shooter's position at the **start** of the tick (perpendicular error 0.003 m, against 0.08 for
  the moved position) and moved one step in the same tick. The muzzle is 1.1 m from the centre **horizontally**
  towards the aim, at ground(centre) + 1.2. Along the line: pistol 3.095 m after the first step (sd 0.012), 5.090
  after the second; rocket 1.997 / 2.895.
- Flight: a 3D straight line from the muzzle towards (aim, ground(aim) + 1.0) and onwards with the same slope.
  Projectiles in their first frame (2 moves; `regression.py`, 200 ranked replays, 115 732 projectiles): 3D error
  median / p95 0.006 / 0.010 m in every pitch band; a muzzle 1.1 m along the sloped line from the centre is off by
  0.186 / 0.270 m when |pitch| > 0.2 (section 7).
- Range counts from the muzzle (1.1 m from the centre). A pistol bullet makes at most 15 moves: the 15th takes it
  from 29.1 to 31.1 m from the shooter's centre, it is checked for hits along the whole move and then removed
  (`range.py`; corrected, see section 7). Many bullets end in the terrain before that.
- A rocket explodes at the aim point, at the first body, in the terrain, or at the end of its range: 46.1 m from the
  shooter's centre, on its 50th move (corrected, section 7).
- Hit test: a point of the path inside the body cylinder (2D ≤ 1 m, between the ground and ground + 2), against
  targets **after** they moved, in sub-steps of 0.50 m. A shtemer whose health already fell to 0 in this tick is no
  longer a target: later bullets fly through it and later explosions do not touch it. The one exception is the
  direct hit of a rocket after its own splash was lethal (section 7). Replaying 15 840 bullets of 12 server matches through the
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
- 10 boxes at the start anywhere in the arena (3.0–97.0), at least 8.0 m from each other; then 3 every 200 ticks
  inside the current zone (distance/R ≤ 0.90, (r/R)² mean 0.418 against 0.405 for uniform), at least 1.5 m from a
  rock edge and at least 4.0 m from every box already on the map. A box that finds no such place (60 random tries)
  is not spawned, so in a small zone fewer than 3 appear (`latelot*.py`; corrected, see section 7).
- Kinds (387 428 boxes, initial and spawned alike): ammo 45.1 %, rockets 29.8 %, repair 25.1 %; the simulator uses
  0.45 / 0.30 / 0.25 (chi² 5.0, df 2; the earlier 0.457 / 0.298 / 0.245 from 11 246 boxes gives 85.9).
- Tries per spawned box: 60. Monte Carlo log-likelihood of the spawn counts on 7 896 server spawn contexts where the
  place matters: K = 40 −874, 50 −844, **60 −838**, 70 −841, 80 −851, 100 −882.
- Initial boxes are at least 5.15 m from every shtemer at the start (10 404 arenas); the generator uses 5.2 m.
- A dead shtemer drops one box where it died: rockets if it had at least one, otherwise ammo (33/33), with the
  standard amount (27/27). Dropped boxes count towards the limit of 16 (spawn 14 → 2, 15 → 1, 16 → 0); the cap is
  16 minus the boxes left after the pickups and the drops of weapon deaths of that tick (0 of 101 402 server batches
  break it). Box ids: drops of weapon deaths < spawned boxes < drops of zone deaths of the same tick (953/953, 57/57).
- Pickup: ≤ 1.8 m from the centre after movement; a box the shtemer does not need stays (a shtemer at full health
  stood within 1.2 m of a repair box 5 247 times and it stayed).

**End of the tick: deaths, pickups, zone, ranking** (`regression.py`; 10 404 ranked replays for the evidence).
- Order: projectiles → weapon deaths (body-index order) and their drops → pickups (living shtemers only) → spawned
  boxes → zone damage (living shtemers only) → zone deaths and their drops → eliminations, once per tick.
- A shtemer killed by a weapon takes no box in that tick (0 pickups by such shtemers; 23 repair boxes within 1.6 m of
  a weapon death stayed). Repair is taken before zone damage: a capped repair outside the zone ends at
  250 − zone damage (350/350).
- Cause and killer are those of the hit that first takes health to 0 and never change: a weapon death outside the
  zone stays pistol/rocket with the shooter (1 051/1 051); "zone" only when the zone damage itself is lethal.
- Fleets eliminated in the same tick are ranked by their health at the **start** of the tick (174 groups: 166
  explained, 0 contradicted, 8 within 0.15), then by damage dealt, then by slot (the server's second key is unknown).
- `hits` in the result counts direct (non-splash) hits on other fleets (62 336 / 62 336 fleet entries).

**Start.** Six fleets on a ring of radius 40 around (50, 50), 60° apart, random slot order and start angle.
Members in a diamond around the fleet centre: index 0 radially −2.83, 1 tangentially −2.83, 2 radially +2.83,
3 tangentially +2.83. Initial look towards the arena centre.

**Instruction budget.** The server counts IL instructions. The simulator rewrites fleet code with Roslyn: at the
start of every block it inserts `Meter.Step(n)`, n = the number of syntax nodes of the block's own statements
(method call × 3, unbraced branch half), scaled by 0.65. Loops without braces are wrapped, `catch` blocks rethrow
the meter's stop. Expression bodies (`=> expr` methods, local functions, properties, accessors, operators and
lambdas) are turned into blocks first, so LINQ selectors and recursion through `=>` methods pay too; lambdas that
become expression trees are left alone. An exception thrown while the meter is over the limit counts as a budget
overrun even when the BCL wraps it (a `Sort` comparer turns it into `InvalidOperationException`). API call
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
  Each block or expression body still pays its whole estimate on entry.
- The server's random generator is not public. Generated arenas, zones and boxes have the server's statistics, not
  its values for a seed; use `--replay` to play on a real setup. Generated terrain is a sum of Gaussian hills
  shifted to start at 0 and cut to [0, 8] (200 generated arenas: maximum 8 in 76 %, on the server 58 %; nodes at 0
  515 on average, server median 392); 8–14 rocks, edges at least 2.5 m from the walls and from each other and 6 m from
  each fleet's spawn centre, as measured on 10 404 server arenas. `--arenas DIR` takes terrain and rocks from real
  replays instead; the start slots are still generated there, so a real rock can sit near a generated spawn.
- `--replay` mode uses only the boxes the server spawned, up to the local cap; the local field differs from the
  server's, so a server box may sit closer than 4 m to a local one.
- The simulator's random streams (blip noise per observer, generated boxes with a fixed number of draws per spawn
  tick, `me.Random` per shtemer) are a design for paired comparisons, not a property of the server.
- Rocks are assumed infinitely tall; wall contact is assumed (no data). The collision order (section 3) is
  confirmed for events; its effect on positions needs exact (unrounded) states.
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

The tick order in `Match.cs`: fleet health remembered → senses (blips from the state at the start of the tick, zoom
answers) → `OnStart`, `OnMessage`, `OnHit`, `OnCollision`, `OnTick` under one budget → projectiles from the
shooters' start positions → energy, look, movement, collisions (rocks and walls, pairs, rocks and walls) →
projectile flight (hits on moved targets that still have health) → weapon deaths and drops → pickups → box spawn →
zone damage → zone deaths and drops → eliminations → cooldowns. `calib/regression.py` checks these rules on local
replays (section 7).

## 7. Corrections

### 2026-10-09 (second): end of the tick, ranking, slope, muzzle, collisions, boxes, budget

Several of these corrections were prompted by an external review by gburazer (9 October 2026); each was re-measured
on our own data before changing the engine. Data for the evidence: all 10 404 ranked replays available on that day
(read one at a time) and 15 server telemetries. "Before" is 4c26b24, "after" this version.

| Rule | Evidence on server data | Before → after |
|---|---|---|
| A shtemer whose health fell to 0 is no longer a target (bullets fly through, splash skips it) | hits on a shtemer already below −0.15: 0 of 207 373 weapon deaths; bullets crossing the cylinder of a body in its death tick and flying on: 11 801 (control, one tick earlier: 2) | hits after a clearly lethal one in 200 local matches: 271 → 0 (rocket direct hit after its own lethal splash: allowed, 175) |
| Weapon deaths before pickups; zone only on the living; cause and killer from the first lethal hit | pickups by a shtemer dying from a weapon: 0; cause of weapon deaths outside the zone: 1 051/1 051 pistol/rocket; killer = shooter of the lethal hit 199 009/199 009 | wrong killer 225 → 0, "zone" deaths with lethal hits 19 → 0, pickups by the dying 7 → 0, order of deaths/pickups 83 → 0 (200 matches) |
| Cap after pickups and weapon drops; id order weapon drop < spawn < zone drop | cap 0/101 402 violations; ids 953/953 and 57/57 | id order in `--replay` runs (192 matches): 6 → 0 violations |
| Same-tick eliminations by fleet health at the start of the tick | 174 groups: 166 explained, 0 contradicted (damage dealt: 101 contradicted); the old key picked the wrong winner in 0.29 % of matches | 200 + 192 local matches: 1 pair contradicted → 0 (7 pairs in the right order, 3 within 0.3 HP) |
| Slope gradient: central difference, step 0.5 | 67 315 free-flight samples: outside the rounding bound 399 (cell) vs 0–1 (step 0.45–0.55); slope coefficient 5.978 (cell) vs 5.998 (`movement2.py`) | local telemetry (6 matches, ~133 000 samples): the engine now follows the central difference (residual p99 0.235 vs 0.251 for the cell; before the reverse) |
| Horizontal muzzle | first-frame error, pistol and rocket, every pitch band: 0.006 / 0.010 m (median / p95) | local first frames off the server model by more than 0.03 m: 58 156 of 95 064 → 0 |
| Collision order: rocks and walls, pairs, rocks and walls silently | obstacle before shtemer in 137/137 mixed event lists | local telemetry: 1 670/1 670 mixed lists in the wrong order → 0/1 000 |
| 60 tries per spawned box | log-likelihood −838 (60) vs −874 (40), 7 896 contexts | spacing below 3.98 m: 0 before and after |
| Box kinds 0.45 / 0.30 / 0.25 | 387 428 boxes: 0.451 / 0.298 / 0.251 | local shares (8 681 boxes) 0.453 / 0.298 / 0.249, chi² 0.29 |
| Line of sight: n = ⌈L/0.5⌉ terrain samples | wrong pairs 22 vs 35 of 58 472 | half the terrain samples |
| `hits` = direct hits on other fleets | 62 336 / 62 336 | result `hits` matching that definition: 12 of 1 200 → 1 200 of 1 200 |
| Generated arenas (8–14 rocks, gaps 2.5 m, 6 m from spawn centres, terrain cut to [0, 8], initial boxes 5.2 m from shtemers) | 10 404 server arenas | 200 generated setups: 456 violations (72 with 6–7 rocks, 202 boxes too close to a shtemer, 129 terrains without a 0) → 0 |
| `--replay` never generates boxes | the server spawned fewer than min(3, room) in 1 669 batches (T 2600: 70 %) | local boxes not among the server's: 45 → 0; boxes per batch at T 2600: server 0.82, before 0.97, after 0.74 |
| Budget: expression bodies metered; overrun classified by the meter | (code) | probe fleet, 40 ticks: LINQ and `=>` recursion now overrun (0 → 120 overruns), a `Sort` comparer overrun counted as an error before (40 errors → 0) |
| Fleets with the same name | (code) | summary and `--results` keep them apart (`Name#2`; `entries` gives the input index per slot) |

**`calib/regression.py`** checks these rules on local replays (and works on server replays too: 0 violations on 200
ranked and 12 test-match replays). On 200 local matches (6 fleets, `--arenas`): before 59 950 violations, after 0;
on the 12 test-match setups × 16 (`--replay`): before 60 544, after 0. `--determinism` plays 3 seeds with 1 and 2
threads, twice, in generated, `--arenas` and `--replay` mode: identical md5 after the change.

**Separate random streams.** Blip noise now has one stream per observer, generated boxes one stream with a fixed
number of draws per spawn tick. This does not change faithfulness; it makes paired comparisons less noisy. On 300
paired seeds (one fleet against a variant of it, same 5 opponents), the variance of the paired difference in place
went from 0.708 to 0.555 (ratio 0.78, bootstrap 95 % 0.53–1.15), survival time 0.90, damage dealt 1.01: in the right
direction, not significant at this sample size.

**Effect on the same-setup check** (12 server test matches, each setup played 16 times locally with the same seeds
before and after):

| | server | before | after |
|---|---|---|---|
| deaths per match, pistol / rocket / zone | 14.08 / 1.33 / 6.75 | 14.10 / 1.81 / 6.41 (se 0.26 / 0.11 / 0.31) | 14.06 / 1.96 / 6.24 (se 0.27 / 0.13 / 0.33) |
| pickups per match, ammo / rockets / repair | 30.8 / 18.2 / 9.9 | 30.2 / 19.0 / 10.0 | 29.8 / 19.1 / 10.0 |
| match length, ticks | 2 648 ± 39 | 2 628 ± 116 | 2 619 ± 147 |
| server place as a quantile of the local places (0.5 typical), six fleets | | 0.64 0.43 0.50 0.39 0.47 0.48 | 0.65 0.42 0.50 0.40 0.53 0.51 |

The totals moved within their standard errors: these corrections fix rare events (about one per match each) that
the whole-match statistics of 12 setups cannot resolve; `regression.py` is the check that sees them. In a 6-fleet
gauntlet the places did move (200 matches, same seeds: one fleet 2.24 → 2.53, another 2.84 → 2.75).

Speed: 200 matches, 2 threads, no replay recording: 11 600–13 600 ticks/s before, 13 600–14 600 after (shared
machine; fewer terrain samples in line of sight, a few more in the slope gradient).

### 2026-10-09: late boxes, pistol and rocket range

An outside comparison reported that the simulator made about 3× more boxes than the server near tick 2600. Checking
it found two loot rules and one range rule that were missing. All numbers below can be repeated with the scripts
named.

**Box spacing** (`latelot.py`, `latelot2.py`, `latelot3.py`; 3 000 ranked replays, 81 608 spawned boxes).
- What was wrong: the simulator placed 3 boxes every 200 ticks wherever they fell in 0.9·R. When all 100 tries hit
  a rock, it kept the last try, so a few boxes landed inside rocks (clearance down to −0.8 m).
- Evidence: a spawned box is never closer than 4.0 m to another box (p0.1 4.01 m; fewer than 0.1 % are closer,
  down to 2.06 m, not explained). Initial boxes are never closer than 8.0 m to each other (p0 8.00 m).
  In the last shrink (tick 2600, R = 1.83, so the spawn disk has radius 1.65 m) the server spawned 0 or 1 box,
  never 2 or 3 (491 matches: 164 × 0, 327 × 1; mean 0.67).
- Number of tries: replaying every ranked spawn tick (state from the frame before) through the model with K tries
  per box. For R 4–5 (spawn counts 0/1/2/3 when the cap of 16 does not bind): server 1/2/40/177, K = 30 gives
  1/3/42/174, K = 100 gives 1/2/28/189; for R 5–8 the server has 19 spawns of 2, K = 30 gives 34, K = 100 gives 10.
  The simulator used 40 (60 since the second correction of that day, above).
- After R reaches 0 (tick 2700) the spawn point is the zone centre, so at most one box can sit there.

| spawned boxes per spawn tick (mean) | server | before | after |
|---|---|---|---|
| tick 2400 (R 4.58), ranked vs 200 local matches with other fleets | 2.81 (cap not binding) | 3.00 | 2.75 |
| tick 2600 (R 1.83), same | 0.67 | 2.96 | 0.83 |
| tick 2600, 12 server test matches vs the same setups played 16× locally | 0.82 | 2.89 | 0.96 |

In `--replay` mode the simulator also used to top up the server's 0–1 late boxes with generated ones up to 3; the
generated ones now follow the same spacing, so they are rejected where the server had no room either.

**Pistol range** (`range.py`; 70 567 bullets of the 12 test matches and 40 ranked replays).
- What was wrong: the simulator removed a bullet as soon as its path passed 30 m from the shooter's centre, in the
  middle of the move. Hits were possible only up to 29.6 m.
- Evidence: each bullet was run through the hit model with the range extended to 34 m, and the predicted hits were
  compared with the server's hit events. Predicted hits on the path 29.6–31.1 m: server confirmed 157 of 168 (93 %,
  the same rate as at 25–29 m); 31.1–34 m: 1 of 119. The last frame in which a bullet is seen is 29.1 m from the
  centre (13 ticks after `Fire`), so the bullet is removed at the end of the move that takes it to 31.1 m.
  The server registers 235 pistol hits 14 ticks after `Fire` in this sample (the 15th move).
- Same check on local replays: before 0 of 72 hits beyond 29.6 m, after 58 of 61.

**Rocket range** (3 000 ranked replays, 240 rockets aimed 40 m or farther).
- What was wrong: a rocket aimed beyond its range exploded 45.0 m from the shooter's centre, on its 49th move.
- Evidence: on the server such rockets explode 46.08–46.10 m from the centre, 49 ticks after `Fire` (the 50th move:
  1.1 + 50 × 0.9). A rocket aimed at 45.2–45.9 m explodes at its aim point. So the range of 45 m counts from the
  muzzle, as for the pistol (1.1 + 30 = 31.1).
- After: a probe fleet firing at points 60 m away explodes at 46.10 m on the 50th move.

**Timing checked, no change.** A projectile is created in the tick of `Fire` and moves once in that tick; the shortest
interval between two shots of one shtemer is 8 ticks for the pistol (55 045 cases) and 50 for the rocket, on the
server and locally. Rockets that hit nothing explode at the aim point (distance error 0.000 m) in the predicted tick
in 96.7 % of cases (1 280 of 1 324; the rest end earlier, for example on a rock).

**Effect on the same-setup check** (12 server test matches, each setup played 16 times locally with the same seeds
before and after):

| | server | before | after |
|---|---|---|---|
| deaths per match, pistol / rocket / zone | 14.08 / 1.33 / 6.75 | 13.76 / 1.81 / 6.64 | 14.10 / 1.81 / 6.41 |
| match length, ticks | 2 648 ± 39 | 2 625 ± 123 | 2 628 ± 116 |
| server place as a quantile of the local places (0.5 typical), six fleets | | 0.66 0.43 0.48 0.40 0.49 0.46 | 0.64 0.43 0.50 0.39 0.47 0.48 |

`pistolhits3.py` now follows the corrected range (15 moves, the last one checked whole); the 98.9 % agreement in
section 3 was measured with the old cut at 30 m and has not been re-run.

Determinism holds after the change (same seed twice: identical md5 for generated, `--arenas` and `--replay` matches).

