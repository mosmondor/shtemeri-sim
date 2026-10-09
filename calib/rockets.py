# Where do rockets explode relative to their target? server vs local
import glob, sys, numpy as np
from common import load_replay
def stats(files):
    fr_ratio = []; self_d = []; early = 0; tot = 0; direct = 0
    for f in files:
        d = load_replay(f); fr = {x['t']: x for x in d['frames']}
        ev = d['events']
        fires = [e for e in ev if e['k'] == 'fire' and e['w'] == 1]
        booms = [e for e in ev if e['k'] == 'boom']
        used = set()
        for e in fires:
            F = fr.get(e['t'] - e['t'] % 2) or fr.get(e['t'] - 1)
            if not F or not F['s'][e['s']]: continue
            o = F['s'][e['s']]
            cand = [(b['t'], i) for i, b in enumerate(booms) if b['by'] == e['s'] and b['t'] >= e['t'] and i not in used]
            if not cand: continue
            t, i = min(cand); used.add(i); b = booms[i]
            dt = np.hypot(e['x'] - o[0], e['y'] - o[1]); db = np.hypot(b['x'] - o[0], b['y'] - o[1])
            if dt < 1: continue
            tot += 1; r = db / dt; fr_ratio.append(r)
            if r < 0.9: early += 1
            if any(h['k'] == 'hit' and h['t'] == b['t'] and h['by'] == b['by'] and not h['splash'] and h['w'] == 1 for h in ev): direct += 1
    fr_ratio = np.array(fr_ratio)
    return tot, early / max(tot, 1), direct / max(tot, 1), np.percentile(fr_ratio, [5, 25, 50, 75]).round(2)
srv = sorted(glob.glob('../validation/server/*.json'))
loc = sorted(glob.glob('../validation/local/*/*.json.gz'))[:40]
print('server: n %d, early(<0.9 of target) %.2f, direct hits %.2f, ratio pct %s' % stats(srv))
print('local : n %d, early(<0.9 of target) %.2f, direct hits %.2f, ratio pct %s' % stats(loc))
