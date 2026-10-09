# What happens in a tick with a collision (coll reported at t+1 for tick t).
import numpy as np, os
from common import *

DT = 0.05
out = {0: [], 1: [], 2: []}
for f in sorted(os.listdir(TEL)):
    if not f.endswith('.jsonl.gz'): continue
    mid = int(f.split('.')[0]); rep = rep_for(mid)
    if rep is None: continue
    T = Terrain(rep['arena']); obs = rep['arena']['obstacles']
    hdr, ticks, res = load_telemetry(f'{TEL}/{f}')
    by = {(tk['t'], s['idx']): s for tk in ticks for s in tk['i']}
    for (t, i), s in sorted(by.items()):
        n = by.get((t + 1, i))
        if n is None or 'coll' not in n: continue
        th = np.array(s['cmd'].get('thrust', [0, 0]), float); L = np.linalg.norm(th)
        if L > 1: th /= L
        v = np.array(s['vel']); p = np.array(s['pos']); g = np.array(T.grad(*p))
        pv = v + (12 * th - 1.5 * v - 6 * g) * DT
        pp = p + pv * DT
        for c in n['coll']:
            kind, nx, ny = c
            nrm = np.array([nx, ny])
            info = dict(mid=mid, t=t, i=i, p=p, pp=pp, np_=np.array(n['pos']), v=v, pv=pv, nv=np.array(n['vel']), n=nrm, ncoll=len(n['coll']))
            if kind == 1:
                d = [(np.hypot(*(np.array(n['pos']) - o[:2])) - o[2], o) for o in obs]
                info['obs'] = min(d, key=lambda x: x[0])
            out[kind].append(info)
for k, name in ((0, 'wall'), (1, 'obstacle'), (2, 'shtemer')):
    L = out[k]
    print('==', name, len(L))
    if not L: continue
    for x in L[:8]:
        vn_pred = x['pv'] @ x['n']; vn_obs = x['nv'] @ x['n']
        vt_pred = x['pv'] - vn_pred * x['n']; vt_obs = x['nv'] - vn_obs * x['n']
        extra = ''
        if k == 1: extra = 'gap=%.3f r=%.2f' % (x['obs'][0], x['obs'][1][2])
        if k == 0: extra = 'pos=%s' % x['np_']
        print(' t%d i%d n=%s v_pred=%s v_obs=%s  vn_pred %.2f vn_obs %.2f |vt| %.2f/%.2f %s' % (x['t'], x['i'], x['n'], x['pv'].round(2), x['nv'], vn_pred, vn_obs, np.linalg.norm(vt_pred), np.linalg.norm(vt_obs), extra))
    if k == 1:
        gaps = np.array([x['obs'][0] for x in L]); print(' obstacle gap (center dist - r - R): pct', np.percentile(gaps, [1, 50, 99]).round(3))
    if k == 0:
        P = np.array([x['np_'] for x in L]); m = np.minimum(P, 100 - P).min(1); print(' wall dist pct', np.percentile(m, [1, 50, 99]).round(3))
    # velocity normal component after collision
    vn = np.array([x['nv'] @ x['n'] for x in L]); vnp = np.array([x['pv'] @ x['n'] for x in L])
    print(' vn_obs pct', np.percentile(vn, [1, 50, 99]).round(3), ' vn_pred pct', np.percentile(vnp, [1, 50, 99]).round(3))
import pickle; pickle.dump(out, open('coll.pkl', 'wb'))
