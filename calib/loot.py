# Loot: initial boxes, spawn schedule, kinds, positions, death drops, pickup radius.
import numpy as np, glob, random
from common import *
from collections import Counter, defaultdict
files = sorted(glob.glob(REPLAYS + '/*.json.gz')); random.seed(5); files = random.sample(files, 300)
init_n = Counter(); init_kind = Counter(); spawn_kind = Counter(); drop_kind = Counter()
spawn_ticks = Counter(); spawn_counts = Counter(); dropdist = []; zone_rel = []; init_pos = []
pick_d = []; max_boxes = 0; obstacle_gap = []; per_spawn = []
seeds = defaultdict(list)
for f in files:
    d = load_replay(f); fr = d['frames']; seeds[d['seed']].append(f)
    obs = d['arena']['obstacles']
    first = {}
    for F in fr:
        max_boxes = max(max_boxes, len(F.get('l', [])))
        for l in F.get('l', []):
            if l[0] not in first: first[l[0]] = (F['t'], l[1], l[2], l[3], F['z'])
    deaths = [e for e in d['events'] if e['k'] == 'death']
    init = [k for k, v in first.items() if v[0] == 0]
    init_n[len(init)] += 1
    for k in init: init_kind[first[k][1]] += 1; init_pos.append(first[k][2:4])
    byt = defaultdict(list)
    for k, v in first.items():
        if v[0] == 0: continue
        # death drop? a death at t-? near position
        dd = [e for e in deaths if 0 <= v[0] - e['t'] <= 2]
        isdrop = False
        for e in dd:
            q = None
            for F in fr:
                if F['t'] >= e['t'] - 2 and F['s'][e['s']] is not None: q = F['s'][e['s']]
                if F['t'] >= e['t']: break
            if q and np.hypot(q[0] - v[2], q[1] - v[3]) < 3: isdrop = True; dropdist.append(np.hypot(q[0] - v[2], q[1] - v[3]))
        if isdrop: drop_kind[v[1]] += 1
        else:
            spawn_kind[v[1]] += 1; byt[v[0]].append(k)
            z = v[4]; zone_rel.append(np.hypot(v[2] - z[0], v[3] - z[1]) / max(z[2], 1e-6))
            obstacle_gap.append(min(np.hypot(v[2] - o[0], v[3] - o[1]) - o[2] for o in obs))
    for t, ks in byt.items(): spawn_ticks[t] += 1; per_spawn.append(len(ks))
    for e in d['events']:
        if e['k'] == 'pickup':
            pass
print('initial box count', init_n.most_common(5))
print('initial kinds', init_kind, ' spawn kinds', spawn_kind, ' drop kinds', drop_kind)
print('spawn ticks (top)', sorted(spawn_ticks.items())[:20])
print('boxes per spawn', Counter(per_spawn))
print('max boxes at once', max_boxes)
print('spawn pos rel to zone radius pct', np.percentile(zone_rel, [50, 90, 99, 100]).round(3), 'mean (r/R)^2 %.3f' % (np.array(zone_rel) ** 2).mean())
print('spawn min gap to rock edge pct', np.percentile(obstacle_gap, [0, 1, 5]).round(2))
ip = np.array(init_pos); print('initial box pos: dist from centre pct', np.percentile(np.hypot(ip[:, 0] - 50, ip[:, 1] - 50), [0, 50, 90, 100]).round(1), 'min coord', ip.min().round(2), 'max', ip.max().round(2))
print('drop dist from victim pct', np.percentile(dropdist, [50, 99]).round(3) if dropdist else None)
print('repeated seeds:', sum(1 for s, v in seeds.items() if len(v) > 1))
