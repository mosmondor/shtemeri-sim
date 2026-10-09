# Diagnose bullets the server counts as hits but the model misses.
import glob, numpy as np
from common import load_replay, Terrain
from pistolhits import pos_at
from collections import Counter
rows = []
for f in sorted(glob.glob('../validation/server/*.json')):
    d = load_replay(f); T = Terrain(d['arena']); fr = {x['t']: x for x in d['frames']}
    hits = {}
    for e in d['events']:
        if e['k'] == 'hit' and e['w'] == 0: hits.setdefault(e['by'], []).append((e['t'], e['s']))
    for e in d['events']:
        if e['k'] != 'fire' or e['w'] != 0: continue
        t = e['t']; start = pos_at(fr, t - 1, e['s'])
        if start is None: continue
        act = [h for h in hits.get(e['s'], []) if t <= h[0] <= t + 16]
        if not act: continue
        th, v = act[0]
        mx, my = start; mz = T.height(mx, my) + 1.2; tz = T.height(e['x'], e['y']) + 1.0
        dv = np.array([e['x'] - mx, e['y'] - my, tz - mz]); u = dv / np.linalg.norm(dv)
        # bullet positions at end of ticks t..th: travelled 1.1 + 2*(k+1)
        q = pos_at(fr, th, v); q0 = pos_at(fr, th - 1, v)
        if q is None: continue
        k = th - t
        p_end = np.array([mx, my, mz]) + u * (1.1 + 2 * (k + 1)); p_beg = np.array([mx, my, mz]) + u * (1.1 + 2 * k)
        # closest approach of the segment to the victim (after-move and before-move positions)
        def seg_d(c):
            a, b = p_beg[:2], p_end[:2]; ab = b - a; w = np.array(c) - a
            s = np.clip(w @ ab / (ab @ ab), 0, 1); return np.linalg.norm(a + s * ab - np.array(c)), s
        dA, sA = seg_d(q); dB, sB = seg_d(q0) if q0 else (99, 0)
        z_at = p_beg[2] + (p_end[2] - p_beg[2]) * sA; hz = T.height(*q)
        rows.append((k, dA, dB, z_at - hz))
R = np.array(rows)
miss = R[R[:, 1] > 1.0]
print('server pistol hits analysed', len(R), '; segment-to-victim(after move) > 1 m:', len(miss))
print(' of those: dist after-move pct', np.percentile(miss[:, 1], [10, 50, 90]).round(2), ' dist before-move pct', np.percentile(miss[:, 2], [10, 50, 90]).round(2))
print(' flight ticks k:', Counter(miss[:, 0].astype(int)).most_common(6))
print(' all hits: z above victim ground pct', np.percentile(R[:, 3], [1, 5, 50, 95, 99]).round(2))
print(' all hits: dist after-move pct', np.percentile(R[:, 1], [50, 90, 95, 99]).round(2), 'before-move', np.percentile(R[:, 2], [50, 90, 95, 99]).round(2))
