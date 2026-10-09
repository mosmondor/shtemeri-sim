import glob, numpy as np
from common import load_replay, Terrain
from collections import Counter
def classify(files, limit=None):
    c = Counter(); ex = []
    for f in files:
        d = load_replay(f); T = Terrain(d['arena']); obs = d['arena']['obstacles']; fr = {x['t']: x for x in d['frames']}
        ev = d['events']
        fires = [e for e in ev if e['k'] == 'fire' and e['w'] == 1]
        booms = [e for e in ev if e['k'] == 'boom']; used = set()
        for e in fires:
            F = fr.get(e['t'] - e['t'] % 2)
            if not F or not F['s'][e['s']]: continue
            o = F['s'][e['s']]
            cand = [(b['t'], i) for i, b in enumerate(booms) if b['by'] == e['s'] and b['t'] >= e['t'] and i not in used]
            if not cand: continue
            t, i = min(cand); used.add(i); b = booms[i]
            dt = np.hypot(e['x'] - o[0], e['y'] - o[1]); db = np.hypot(b['x'] - o[0], b['y'] - o[1])
            if dt < 1 or db / dt >= 0.9: continue
            h = T.height(b['x'], b['y'])
            rock = min([np.hypot(b['x'] - q[0], b['y'] - q[1]) - q[2] for q in obs] + [99])
            G = fr.get(b['t'] - b['t'] % 2)
            body = min([np.hypot(b['x'] - q[0], b['y'] - q[1]) for q in G['s'] if q] + [99]) if G else 99
            kind = 'terrain' if b['z'] - h < 0.15 else 'rock' if rock < 0.3 else 'body' if body < 1.3 else 'other'
            c[kind] += 1
            if kind in ('terrain', 'other') and len(ex) < 6: ex.append((f[-20:], e['t'], b['t'], round(db, 2), round(dt, 2), round(b['z'] - h, 2), round(rock, 2), round(body, 2)))
    return c, ex
for name, files in (('server', sorted(glob.glob('../validation/server/*.json'))), ('local', sorted(glob.glob('../validation/local/*/*.json.gz'))[:40])):
    c, ex = classify(files); print(name, dict(c)); [print('   ', x) for x in ex]
