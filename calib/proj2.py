# Spawn geometry of projectiles: telemetry fire commands (exact shooter state) + replay tracks.
import numpy as np, os
from common import *
res = []
for mid in (27427, 27426, 27425):
    hdr, ticks, _ = load_telemetry(f'{TEL}/{mid}.jsonl.gz'); rep = load_replay(f'{TEL}/{mid}.replay.json')
    T = Terrain(rep['arena']); fid = hdr['fleetId']; n = hdr['fleetSize']
    by = {(tk['t'], s['idx']): s for tk in ticks for s in tk['i']}
    tracks = {}
    for f in rep['frames']:
        for p in f.get('p', []): tracks.setdefault(p[0], []).append((f['t'], *p[1:]))
    starts = {}
    for pid, tr in tracks.items(): starts.setdefault((tr[0][5], tr[0][1]), []).append((tr[0][0], pid))
    for (t, i), s in by.items():
        for w, tx, ty in s['cmd'].get('fire', []):
            g = fid * n + i
            c = [pid for (t0, pid) in starts.get((g, w), []) if t0 == t + (t % 2)]
            if len(c) != 1: continue
            tr = tracks[c[0]]
            nx = by.get((t + 1, i))
            if nx is None: continue
            p0 = np.array(s['pos']); p1 = np.array(nx['pos'])
            tgt = np.array([tx, ty])
            q = np.array(tr[0][2:4]); k = (tr[0][0] - t) + 1   # number of moves done (1 if fired on even tick)
            # muzzle candidates
            for name, base in (('start', p0), ('after', p1)):
                u = (tgt - base); u /= np.linalg.norm(u)
                along = (q - base) @ u; perp = abs(np.cross(u, q - base))
                res.append((mid, t, i, w, k, name, along, perp, tr[0][4], T.height(*base), T.height(*tgt)))
import collections
for w in (0, 1):
    for name in ('start', 'after'):
        for k in (1, 2):
            R = [r for r in res if r[3] == w and r[5] == name and r[4] == k]
            if not R: continue
            al = np.array([r[6] for r in R]); pe = np.array([r[7] for r in R])
            print('w%d base=%-5s moves=%d n=%4d along mean %.3f sd %.3f | perp mean %.3f p90 %.3f' % (w, name, k, len(R), al.mean(), al.std(), pe.mean(), np.percentile(pe, 90)))
