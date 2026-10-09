# Splash damage vs distance from explosion (ranked replays).
import numpy as np, glob, random
from common import *
files = sorted(glob.glob(REPLAYS + '/*.json.gz')); random.seed(2); files = random.sample(files, 120)
rows = []
for f in files:
    d = load_replay(f); T = Terrain(d['arena']); fr = {x['t']: x for x in d['frames']}
    ev = d['events']
    booms = [e for e in ev if e['k'] == 'boom']
    for b in booms:
        F = fr.get(b['t']) if b['t'] % 2 == 0 else None
        Fp = fr.get(b['t'] - 2) if b['t'] % 2 == 0 else None
        if F is None: continue
        hits = [e for e in ev if e['t'] == b['t'] and e['k'] == 'hit' and e['w'] == 1 and e['by'] == b['by']]
        for e in hits:
            q = F['s'][e['s']]
            if q is None: continue
            # position at start of tick t ~ frame t-1 (not stored); position after tick = frame t; physics: projectile moves before/after shtemers?
            hz = T.height(q[0], q[1])
            d2 = np.hypot(q[0] - b['x'], q[1] - b['y'])
            dz = b['z'] - (hz + 1.0)
            # distance to cylinder [hz, hz+2] radius 1
            zc = min(max(b['z'], hz), hz + 2)
            dcyl = np.hypot(max(d2 - 1, 0), b['z'] - zc)
            rows.append((e['d'], e['splash'], d2, np.hypot(d2, dz), dcyl, np.hypot(d2, b['z'] - hz - 1)))
R = np.array(rows, float)
sp = R[R[:, 1] == 1]
print('splash hits', len(sp), 'direct hits', (R[:, 1] == 0).sum(), 'direct dmg values', np.unique(R[R[:, 1] == 0][:, 0])[:10])
pred_frac = 1 - sp[:, 0] / 35  # implied distance/5
for k, name in ((2, '2D centre'), (3, '3D centre(h+1)'), (4, 'cylinder surface')):
    implied = pred_frac * 5
    e = sp[:, k] - implied
    print('%-18s implied-vs-measured: mean %.3f sd %.3f p10 %.3f p90 %.3f' % (name, e.mean(), e.std(), *np.percentile(e, [10, 90])))
print('max splash', sp[:, 0].max(), 'sample', sp[:12].round(2).tolist())
