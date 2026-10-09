import numpy as np, glob, random
from common import *
from collections import Counter
files = sorted(glob.glob(REPLAYS + '/*.json.gz')); random.seed(6); files = random.sample(files, 200)
kc = Counter(); angs = []; sq = []; nfl = Counter(); slotgap = []
for f in files:
    d = load_replay(f)
    l0 = d['frames'][0]['l']; kc[tuple(sorted(Counter(x[1] for x in l0).items()))] += 1
    n = d['rules']['fleetSize']; nf = len(d['fleets']); nfl[nf] += 1
    s0 = d['frames'][0]['s']
    cs = []
    for k in range(nf):
        P = np.array([s0[k * n + i][:2] for i in range(n)])
        c = P.mean(0); cs.append(np.arctan2(c[1] - 50, c[0] - 50))
        sq.append(np.hypot(c[0] - 50, c[1] - 50))
        # member offsets in fleet frame (radial out, tangential)
        u = (c - 50) / np.linalg.norm(c - 50); v = np.array([-u[1], u[0]])
        if len(angs) < 3: print('fleet', k, 'centre', c.round(2), 'offsets(radial,tangential)', [(round((p - c) @ u, 2), round((p - c) @ v, 2)) for p in P])
        angs.append(1)
    cs = np.array(cs)
    gaps = np.diff(np.unwrap(cs)); slotgap.extend(((gaps + np.pi) % (2 * np.pi) - np.pi).round(3))
print('initial kind mixes (top)', kc.most_common(6))
print('fleet counts', nfl, 'centre dist pct', np.percentile(sq, [0, 50, 100]).round(2))
print('angle step between consecutive slots', Counter(slotgap).most_common(6))
