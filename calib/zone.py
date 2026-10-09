# Zone schedule and damage.
import numpy as np, glob, random
from common import *
files = sorted(glob.glob(REPLAYS + '/*.json.gz')); random.seed(3); files = random.sample(files, 300)
def zone_at(st, t, hold=200, shrink=250):
    # stage k (1..6): hold [ (k-1)*450, (k-1)*450+200 ), shrink to stage k over next 250
    k = t // (hold + shrink); r = t % (hold + shrink)
    if k >= len(st) - 1: return st[-1]
    a, b = np.array(st[k], float), np.array(st[k + 1], float)
    if r < hold: return a
    f = (r - hold) / shrink
    return a + (b - a) * f
errs = []; offs = []; radii = []
for f in files:
    d = load_replay(f); st = d['zoneStages']
    radii.append([s[2] for s in st])
    for fr in d['frames'][::5]:
        z = zone_at(st, fr['t'])
        errs.append(np.abs(np.array(fr['z']) - z).max())
    for k in range(1, len(st)):
        dc = np.hypot(st[k][0] - st[k - 1][0], st[k][1] - st[k - 1][1]); room = st[k - 1][2] - st[k][2]
        offs.append((k, dc / room if room > 0 else 0, dc, room))
errs = np.array(errs)
print('zone schedule err: max %.3f p99 %.3f' % (errs.max(), np.percentile(errs, 99)))
for t in (199, 200, 201, 450, 451):
    pass
R = np.array(radii); print('radii unique per stage:', [np.unique(R[:, k]).tolist()[:4] for k in range(R.shape[1])])
O = np.array(offs)
for k in range(1, 7):
    m = O[:, 0] == k
    print('stage %d offset/room pct' % k, np.percentile(O[m, 1], [5, 25, 50, 75, 95, 100]).round(3), ' sqrt-uniform check: mean of (off/room)^2 = %.3f (0.5 if uniform in disk)' % (O[m, 1] ** 2).mean())
st0 = [load_replay(f)['zoneStages'][0] for f in files[:20]]; print('stage0', set(map(tuple, st0)))
