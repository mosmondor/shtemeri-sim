# Movement incl. parking, across all telemetry files that have a replay (terrain).
import numpy as np, os
from common import *
DT = 0.05
def rep_for(mid):
    p = f'{TEL}/{mid}.replay.json'
    if os.path.exists(p): return load_replay(p)
    p = f'{REPLAYS}/{mid}.json.gz'
    if os.path.exists(p): return load_replay(p)
rows = []
for f in sorted(os.listdir(TEL)):
    if not f.endswith('.jsonl.gz'): continue
    mid = int(f.split('.')[0]); rep = rep_for(mid)
    if rep is None: continue
    T = Terrain(rep['arena'])
    hdr, ticks, res = load_telemetry(f'{TEL}/{f}')
    by = {(tk['t'], s['idx']): s for tk in ticks for s in tk['i']}
    last_th = {}
    for (t, i), s in sorted(by.items()):
        if 'thrust' in s['cmd']: last_th[i] = s['cmd']['thrust']
        n = by.get((t + 1, i))
        if n is None or 'coll' in n: continue
        th = np.array(last_th.get(i, [0, 0]), float); L = np.linalg.norm(th)
        if L > 1: th /= L
        rows.append((mid, t, i, *s['pos'], *s['vel'], *th, *T.grad(*s['pos']), *n['pos'], *n['vel'], *T.grad_cd(*s['pos'])))
R = np.array(rows)
v = R[:, 5:7]; th = R[:, 7:9]; g = R[:, 9:11]; nv = R[:, 13:15]
base = v + (12 * th - 1.5 * v - 6 * g) * DT
def park(pred, rule):
    out = pred.copy()
    sp = np.linalg.norm(pred, axis=1); thl = np.linalg.norm(th, axis=1)
    net = np.linalg.norm(12 * th - 6 * g, axis=1); sl = 6 * np.linalg.norm(g, axis=1)
    if rule == 'none': m = np.zeros(len(pred), bool)
    if rule == 'H1 zero-thrust & slope<3': m = (thl < 1e-9) & (sp < 0.5) & (sl < 3)
    if rule == 'H1b zero-thrust & slope<3 (old speed)': m = (thl < 1e-9) & (np.linalg.norm(v, axis=1) < 0.5) & (sl < 3)
    if rule == 'H2 net<3': m = (sp < 0.5) & (net < 3)
    out[m] = 0
    return out, m
for rule in ('none', 'H1 zero-thrust & slope<3', 'H1b zero-thrust & slope<3 (old speed)', 'H2 net<3'):
    p, m = park(base, rule)
    e = np.linalg.norm(p - nv, axis=1)
    print('%-40s n=%d parked=%d err mean %.4f p99 %.3f max %.3f  n(err>0.05)=%d' % (rule, len(e), m.sum(), e.mean(), np.percentile(e, 99), e.max(), (e > 0.05).sum()))
p, m = park(base, 'H1 zero-thrust & slope<3')
e = np.linalg.norm(p - nv, axis=1)
for k in np.argsort(-e)[:25]:
    print(int(R[k,0]), int(R[k,1]), int(R[k,2]), 'v', v[k], 'th', th[k].round(2), 'slope6', round(6*np.linalg.norm(g[k]),2), 'nv', nv[k], 'pred', p[k].round(3))
np.save('movement_rows_all.npy', R)
# Slope coefficient (guide: 6): least squares of 12*thrust - 1.5*v - dv/dt = k * gradient on moving samples,
# with the analytic gradient of the bilinear cell and with central differences, h = 0.5 (server, review F5).
moving = (np.linalg.norm(th, axis=1) > 1e-9) | (np.linalg.norm(v, axis=1) >= 0.5)
y = 12 * th - 1.5 * v - (nv - v) / DT
for name, G in (('cell', g), ('cd h=0.5', R[:, 15:17])):
    m = moving & (np.linalg.norm(G, axis=1) > 0.05)
    k = (y[m] * G[m]).sum() / (G[m] ** 2).sum()
    r = np.linalg.norm(y[m] - 6 * G[m], axis=1)
    print('slope coefficient with %-9s gradient: %.4f (n %d), |12th-1.5v-dv/dt-6g| median %.4f p99 %.3f' % (name, k, m.sum(), np.median(r), np.percentile(r, 99)))
