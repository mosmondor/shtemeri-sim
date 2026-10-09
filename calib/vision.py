# Visibility rules: cone, range, proximity, line of sight (terrain + rocks).
import numpy as np, pickle
from common import *
recs = pickle.load(open('blips.pkl', 'rb'))
arenas = {}
def arena(mid):
    if mid not in arenas:
        rep = rep_for(mid); arenas[mid] = (Terrain(rep['arena']), rep['arena']['obstacles'])
    return arenas[mid]
def ang(r):
    a = np.arctan2(r['ty'] - r['oy'], r['tx'] - r['ox'])
    return abs((a - r['look'] + np.pi) % (2 * np.pi) - np.pi)
def rock_block(obs, ax, ay, bx, by, pad=0.0):
    dx, dy = bx - ax, by - ay; L2 = dx * dx + dy * dy
    for o in obs:
        t = ((o[0] - ax) * dx + (o[1] - ay) * dy) / L2 if L2 > 0 else 0
        t = min(max(t, 0), 1)
        px, py = ax + t * dx, ay + t * dy
        if np.hypot(o[0] - px, o[1] - py) < o[2] + pad: return True
    return False
def terrain_block(T, ax, ay, az, bx, by, bz, step=0.25):
    L = np.hypot(bx - ax, by - ay); n = max(2, int(L / step))
    for k in range(1, n):
        f = k / n
        x, y = ax + (bx - ax) * f, ay + (by - ay) * f
        if T.height(x, y) > az + (bz - az) * f: return True
    return False
rows = []
for r in recs:
    if r['kind'] == 'blip' and r.get('second', 99) <= 3 * r['err'] + 0.5: continue
    if r['okind'] != 'S': continue
    seen = r['kind'] == 'blip'
    rows.append((seen, r['dist'], ang(r), r))
print('shtemer pairs', len(rows))
D = np.array([x[1] for x in rows]); A = np.array([x[2] for x in rows]); S = np.array([x[0] for x in rows])
prox = D <= 6
print('within proximity 6: seen frac %.4f (n=%d)' % (S[prox].mean(), prox.sum()))
m = (D > 6.2) & (D < 31.5) & (A < 0.85)
print('in cone & range (margins): seen frac %.4f n=%d' % (S[m].mean(), m.sum()))
m2 = (D > 6.2) & ((D > 32.5) | (A > 0.89))
print('outside cone/range: seen frac %.4f n=%d' % (S[m2].mean(), m2.sum()))
# boundaries
for lo, hi in ((5.5, 5.9), (5.9, 6.1), (6.1, 6.5)):
    k = (D >= lo) & (D < hi) & (A > 0.95); print(' prox ring %.1f-%.1f outside cone: seen %.3f n=%d' % (lo, hi, S[k].mean() if k.sum() else -1, k.sum()))
for lo, hi in ((31, 31.8), (31.8, 32.2), (32.2, 33)):
    k = (D >= lo) & (D < hi) & (A < 0.8); print(' range %.1f-%.1f in cone: seen %.3f n=%d' % (lo, hi, S[k].mean() if k.sum() else -1, k.sum()))
for lo, hi in ((0.8, 0.85), (0.85, 0.89), (0.89, 0.95)):
    k = (D > 7) & (D < 30) & (A >= lo) & (A < hi); print(' angle %.2f-%.2f: seen %.3f n=%d' % (lo, hi, S[k].mean() if k.sum() else -1, k.sum()))
# LOS hypotheses on in-cone set (subsample)
idx = np.where(m)[0]
rng = np.random.default_rng(0); idx = rng.choice(idx, min(6000, len(idx)), replace=False)
for Ht in (0.0, 1.0, 1.6, 2.0):
    for pad in (0.0,):
        agree = 0; conf = np.zeros((2, 2), int)
        for k in idx:
            r = rows[k][3]; T, obs = arena(r['mid'])
            az = T.height(r['ox'], r['oy']) + 1.6; bz = T.height(r['tx'], r['ty']) + Ht
            vis = not rock_block(obs, r['ox'], r['oy'], r['tx'], r['ty'], pad) and not terrain_block(T, r['ox'], r['oy'], az, r['tx'], r['ty'], bz)
            conf[int(vis), int(S[k])] += 1
        print('LOS target height %.1f pad %.1f: agreement %.4f  [pred-invisible: seen %d unseen %d | pred-visible: seen %d unseen %d]' % (Ht, pad, (conf[0, 0] + conf[1, 1]) / conf.sum(), conf[0, 1], conf[0, 0], conf[1, 1], conf[1, 0]))
