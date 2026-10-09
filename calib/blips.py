# Blip noise and visibility: telemetry blips at tick t vs replay frame t-1 (state at start of tick t).
import numpy as np, os, pickle
from common import *
recs = []   # per (observer, object): dict
for f in sorted(os.listdir(TEL)):
    if not f.endswith('.jsonl.gz'): continue
    mid = int(f.split('.')[0]); rep = rep_for(mid)
    if rep is None: continue
    hdr, ticks, res = load_telemetry(f'{TEL}/{f}')
    fid = hdr['fleetId']; n = hdr['fleetSize']
    fr = {x['t']: x for x in rep['frames']}
    for tk in ticks:
        t = tk['t']
        F = fr.get(t - 1)
        if F is None: continue
        objs = []
        for g, q in enumerate(F['s']):
            if q is not None and q[3] > 0: objs.append(('S', g, q[0], q[1]))
        for l in F.get('l', []): objs.append(('L', l[0], l[2], l[3]))
        for p in F.get('p', []):
            if p[1] == 1: objs.append(('R', p[0], p[2], p[3]))
        for s in tk['i']:
            me = fid * n + s['idx']
            ox, oy = s['pos']
            cand = [o for o in objs if not (o[0] == 'S' and o[1] == me)]
            if not cand: continue
            P = np.array([[o[2], o[3]] for o in cand])
            used = set()
            for b in s.get('blips', []):
                bid, bx, by_, size = b
                d = np.hypot(P[:, 0] - bx, P[:, 1] - by_)
                order = np.argsort(d)
                k = order[0]
                dist = np.hypot(P[k, 0] - ox, P[k, 1] - oy)
                recs.append(dict(kind='blip', mid=mid, t=t, obs=me, size=size, okind=cand[k][0], oid=cand[k][1], bid=bid,
                                 err=d[k], second=d[order[1]] if len(order) > 1 else 99, dist=dist,
                                 dx=bx - P[k, 0], dy=by_ - P[k, 1], ox=ox, oy=oy, look=s['look'], tx=P[k, 0], ty=P[k, 1]))
                used.add(k)
            for k, o in enumerate(cand):
                if k in used: continue
                recs.append(dict(kind='miss', mid=mid, t=t, obs=me, okind=o[0], oid=o[1], dist=np.hypot(o[2] - ox, o[3] - oy),
                                 ox=ox, oy=oy, look=s['look'], tx=o[2], ty=o[3]))
pickle.dump(recs, open('blips.pkl', 'wb'))
B = [r for r in recs if r['kind'] == 'blip' and r['second'] > 3 * r['err'] + 0.5]
err = np.array([r['err'] for r in B]); dist = np.array([r['dist'] for r in B])
ratio = err / np.maximum(dist, 1e-6)
print('matched blips', len(B), 'of', sum(r['kind'] == 'blip' for r in recs))
print('err/dist pct', np.percentile(ratio, [10, 50, 90, 99, 99.9]).round(4), 'max', ratio.max().round(4))
for lo, hi in ((0, 3), (3, 6), (6, 12), (12, 20), (20, 32)):
    m = (dist >= lo) & (dist < hi)
    print(' dist %2d-%2d n=%5d err mean %.3f max %.3f ratio mean %.4f max %.4f' % (lo, hi, m.sum(), err[m].mean(), err[m].max(), ratio[m].mean(), ratio[m].max()))
# radial / tangential components
dx = np.array([r['dx'] for r in B]); dy = np.array([r['dy'] for r in B])
ux = np.array([r['tx'] - r['ox'] for r in B]); uy = np.array([r['ty'] - r['oy'] for r in B]); L = np.hypot(ux, uy); ux /= L; uy /= L
rad = (dx * ux + dy * uy) / dist; tan = (-dx * uy + dy * ux) / dist
print('radial/dist pct', np.percentile(rad, [1, 25, 50, 75, 99]).round(4), 'tangential/dist pct', np.percentile(tan, [1, 25, 50, 75, 99]).round(4))
print('x/dist pct', np.percentile(dx / dist, [1, 25, 50, 75, 99]).round(4))
