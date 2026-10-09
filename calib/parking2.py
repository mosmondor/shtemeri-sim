import numpy as np, glob, random
from common import *
files = sorted(glob.glob(REPLAYS + '/*.json.gz'))
random.seed(1); files = random.sample(files, 150)
out = []
for f in files:
    d = load_replay(f); T = Terrain(d['arena']); obs = d['arena']['obstacles']
    fr = d['frames']
    for i in range(1, len(fr) - 3):
        for s, st in enumerate(fr[i]['s']):
            if st is None: continue
            seq = [fr[i + k]['s'][s] for k in range(-1, 3)]
            if any(q is None for q in seq): continue
            if not all(abs(q[0] - st[0]) < 1e-9 and abs(q[1] - st[1]) < 1e-9 for q in seq[1:]): continue
            g = np.hypot(*T.grad(st[0], st[1]))
            if g < 0.45: continue
            x, y = st[0], st[1]
            dw = min(x, y, 100 - x, 100 - y) - 1
            do = min([np.hypot(x - o[0], y - o[1]) - o[2] - 1 for o in obs] + [99])
            dsh = min([np.hypot(x - q[0], y - q[1]) - 2 for j, q in enumerate(fr[i]['s']) if q and j != s] + [99])
            out.append((g, dw, do, dsh, f[-12:], fr[i]['t'], s))
out.sort()
for o in out[-40:]: print(np.round(o[:4], 2), o[4:])
print(len(out))
