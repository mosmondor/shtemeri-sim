# Projectile end of life: how far do they go, and what ends them.
import numpy as np
from common import *
for mid in (27427, 27426):
    hdr, ticks, _ = load_telemetry(f'{TEL}/{mid}.jsonl.gz'); rep = load_replay(f'{TEL}/{mid}.replay.json')
    T = Terrain(rep['arena']); fid = hdr['fleetId']; n = hdr['fleetSize']; obs = rep['arena']['obstacles']
    by = {(tk['t'], s['idx']): s for tk in ticks for s in tk['i']}
    tracks = {}
    for f in rep['frames']:
        for p in f.get('p', []): tracks.setdefault(p[0], []).append((f['t'], *p[1:]))
    starts = {}
    for pid, tr in tracks.items(): starts.setdefault((tr[0][5], tr[0][1]), []).append((tr[0][0], pid))
    hitsby = {}
    for e in rep['events']:
        if e['k'] == 'hit' and not e.get('splash'): hitsby.setdefault((e['by'], e['w']), []).append(e['t'])
    booms = [e for e in rep['events'] if e['k'] == 'boom']
    out = []
    for (t, i), s in by.items():
        for w, tx, ty in s['cmd'].get('fire', []):
            g = fid * n + i
            c = [pid for (t0, pid) in starts.get((g, w), []) if t0 == t + (t % 2)]
            if len(c) != 1: continue
            tr = tracks[c[0]]; last = tr[-1]
            p0 = np.array(s['pos']); tgt = np.array([tx, ty]); u = (tgt - p0) / np.linalg.norm(tgt - p0)
            dl = (np.array(last[2:4]) - p0) @ u
            # terrain at last point
            hl = T.height(last[2], last[3])
            # was there a hit by this shooter shortly after last frame?
            hit = any(last[0] <= th <= last[0] + 2 for th in hitsby.get((g, w), []))
            out.append((w, dl, last[4] - hl, hit, np.linalg.norm(tgt - p0), last[0] - t))
    O = np.array(out, float)
    for w in (0, 1):
        m = (O[:, 0] == w) & (O[:, 3] == 0)
        print(mid, 'w', w, 'no-hit tracks', m.sum(), 'last along dist pct', np.percentile(O[m, 1], [5, 25, 50, 75, 95]).round(2), 'max', O[m, 1].max().round(2))
        if w == 1:
            print('   rocket last-along minus target dist pct', np.percentile(O[m, 1] - O[m, 4], [5, 50, 95]).round(2))
        print('   z above terrain at last frame pct', np.percentile(O[m, 2], [5, 25, 50]).round(2))
