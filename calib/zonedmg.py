import numpy as np, glob, random
from common import *
files = sorted(glob.glob(REPLAYS + '/*.json.gz')); random.seed(4); files = random.sample(files, 200)
from collections import defaultdict
acc = defaultdict(list)
for f in files:
    d = load_replay(f); fr = d['frames']
    hit_ticks = defaultdict(set)
    for e in d['events']:
        if e['k'] in ('hit', 'pickup'): hit_ticks[e['s']].add(e['t'])
    for a, b in zip(fr[:-1], fr[1:]):
        if b['t'] - a['t'] != 2: continue
        for s, (qa, qb) in enumerate(zip(a['s'], b['s'])):
            if qa is None or qb is None: continue
            if any(t in hit_ticks[s] for t in (b['t'] - 1, b['t'])): continue
            # position at start of tick b.t-1 is qa (after tick a.t); outside both
            za, zb = a['z'], b['z']
            da = np.hypot(qa[0] - za[0], qa[1] - za[1]) - za[2]
            db = np.hypot(qb[0] - zb[0], qb[1] - zb[1]) - zb[2]
            if da > 0.3 and db > 0.3:
                acc[b['t'] // 50 * 50].append((qa[3] - qb[3]) / 2)
            elif da < -0.3 and db < -0.3 and qa[3] - qb[3] > 0:
                acc[-1].append(qa[3] - qb[3])
for t in sorted(acc):
    v = np.array(acc[t])
    if t >= 0 and len(v) > 5: print('t~%4d n=%4d dmg/tick median %.3f mean %.3f' % (t, len(v), np.median(v), v.mean()))
print('inside zone, unexplained hp drops:', len(acc[-1]))
