# Movement model fit from telemetry (exact commands) + replay terrain.
# Record at tick t: pos/vel at start of tick t, cmd issued in tick t. Coll at t+1 = collision during tick t.
import numpy as np
from common import *
DT = 0.05
rows = []
for mid in (27427, 27426, 27425):
    hdr, ticks, res = load_telemetry(f'{TEL}/{mid}.jsonl.gz')
    T = Terrain(load_replay(f'{TEL}/{mid}.replay.json')['arena'])
    by = {}
    for tk in ticks:
        for s in tk['i']:
            by[(tk['t'], s['idx'])] = s
    for (t, i), s in by.items():
        n = by.get((t + 1, i))
        if n is None or 'coll' in n: continue
        th = np.array(s['cmd']['thrust'], float)
        L = np.linalg.norm(th)
        if L > 1: th /= L
        gx, gy = T.grad(*s['pos'])
        rows.append((mid, t, i, *s['pos'], *s['vel'], *th, gx, gy, *n['pos'], *n['vel']))
R = np.array(rows, float)
px, py, vx, vy, tx, ty, gx, gy, nx, ny, nvx, nvy = R[:, 3:15].T
v = np.stack([vx, vy], 1); nv = np.stack([nvx, nvy], 1)
th = np.stack([tx, ty], 1); g = np.stack([gx, gy], 1)
acc = (nv - v) / DT
# Regression per axis stacked: acc = A*th - D*v - S*g
X = np.concatenate([np.stack([th[:, 0], v[:, 0], g[:, 0]], 1), np.stack([th[:, 1], v[:, 1], g[:, 1]], 1)])
Y = np.concatenate([acc[:, 0], acc[:, 1]])
coef, *_ = np.linalg.lstsq(X, Y, rcond=None)
print('n', len(R), 'fit acc = %.3f*thrust + %.3f*v + %.3f*grad' % tuple(coef))
# model with guide constants
def step(v, th, g, A=12, D=1.5, S=6):
    a = A * th - D * v - S * g
    return v + a * DT
pv = step(v, th, g)
e = np.linalg.norm(pv - nv, axis=1)
print('guide model vel err: mean %.3f p50 %.3f p90 %.3f p99 %.3f' % (e.mean(), *np.percentile(e, [50, 90, 99])))
# implicit drag variant: v' = (v + (A th - S g) dt)/(1+D dt)
pv2 = (v + (12 * th - 6 * g) * DT) / (1 + 1.5 * DT)
e2 = np.linalg.norm(pv2 - nv, axis=1)
print('implicit drag err: mean %.3f p90 %.3f' % (e2.mean(), np.percentile(e2, 90)))
# exponential drag variant
pv3 = v * np.exp(-1.5 * DT) + (12 * th - 6 * g) * DT
e3 = np.linalg.norm(pv3 - nv, axis=1)
print('exp drag err: mean %.3f p90 %.3f' % (e3.mean(), np.percentile(e3, 90)))
# position update: semi-implicit (new vel) vs explicit (old vel)
p = np.stack([px, py], 1); npos = np.stack([nx, ny], 1)
for name, vv in (('new vel', nv), ('old vel', v), ('avg', (v + nv) / 2)):
    ep = np.linalg.norm(p + vv * DT - npos, axis=1)
    print('pos update with', name, 'mean %.4f p90 %.4f' % (ep.mean(), np.percentile(ep, 90)))
# big residuals: look at them
bad = np.argsort(-e)[:15]
spd = np.linalg.norm(v, axis=1); nspd = np.linalg.norm(nv, axis=1); thl = np.linalg.norm(th, axis=1); gl = np.linalg.norm(g, axis=1)
for k in bad:
    print('mid %d t %d i %d v=%s th=%s g=%s nv=%s pred=%s' % (R[k,0], R[k,1], R[k,2], v[k].round(2), th[k].round(2), g[k].round(3), nv[k], pv[k].round(2)))
np.save('movement_rows.npy', R)
