# Stationary shtemers in ranked replays: how steep is the terrain where they stand perfectly still?
import numpy as np, glob, os, random
from common import *
files = sorted(glob.glob(REPLAYS + '/*.json.gz'))
random.seed(1); files = random.sample(files, 150)
still_g, moving_slow_g = [], []
for f in files:
    d = load_replay(f); T = Terrain(d['arena'])
    fr = d['frames']
    for i in range(1, len(fr) - 3):
        for s, st in enumerate(fr[i]['s']):
            if st is None: continue
            seq = [fr[i + k]['s'][s] for k in range(-1, 3)]
            if any(q is None for q in seq): continue
            same = all(abs(q[0] - st[0]) < 1e-9 and abs(q[1] - st[1]) < 1e-9 for q in seq[1:])
            g = np.hypot(*T.grad(st[0], st[1]))
            if same: still_g.append(g)
            else:
                d1 = np.hypot(seq[2][0] - seq[1][0], seq[2][1] - seq[1][1]) / 0.1
                if d1 < 0.3: moving_slow_g.append(g)
still_g = np.array(still_g); ms = np.array(moving_slow_g)
print('still samples', len(still_g), 'slope pct', np.percentile(still_g, [50, 90, 99, 99.9]).round(3), 'max', still_g.max().round(3))
print('6*slope max', (6 * still_g).max().round(3))
print('slow-moving samples', len(ms), 'slope pct', np.percentile(ms, [50, 90, 99]).round(3))
