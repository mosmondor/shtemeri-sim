# What is inside a box dropped by a destroyed shtemer? Ammo delta at pickup (own telemetry) for drop vs spawned boxes.
import os, math, gzip, json
from common import *
pairs = [(f'{TEL}/{m}.jsonl.gz', None) for m in (27425, 27426, 27427, 26909, 26918, 26921, 26936, 26939)]
pairs += [('../validation/server/27538.slot0.jsonl.gz', '../validation/server/27538.json'), ('../validation/server/27538.slot1.jsonl.gz', '../validation/server/27538.json')]
rows = []
for tp, rp in pairs:
    h, ticks, _ = load_telemetry(tp)
    rep = load_replay(rp) if rp else rep_for(int(os.path.basename(tp).split('.')[0]))
    if rep is None: continue
    fid = h['fleetId']; by = {(tk['t'], s['idx']): s for tk in ticks for s in tk['i']}
    first = {}; lastpos = {}; drops = {}
    for F in rep['frames']:
        for l in F['l']:
            if l[0] not in first: first[l[0]] = (F['t'], l[1], l[2], l[3])
    deaths = [e for e in rep['events'] if e['k'] == 'death']
    for bid, (t, k, x, y) in first.items():
        for e in deaths:
            if 0 <= t - e['t'] <= 2:
                # victim position near death from the frame before
                fr = [F for F in rep['frames'] if e['t'] - 3 <= F['t'] <= e['t']]
                q = next((F['s'][e['s']] for F in reversed(fr) if F['s'][e['s']]), None)
                if q and math.hypot(q[0] - x, q[1] - y) < 3: drops[bid] = e['s']
    for e in rep['events']:
        if e['k'] != 'pickup' or e['s'] // 4 != fid: continue
        i = e['s'] % 4; a, b = by.get((e['t'], i)), by.get((e['t'] + 1, i))
        if not a or not b: continue
        dA = b['ammo'][0] - a['ammo'][0]; dR = b['ammo'][1] - a['ammo'][1]; dH = b['hp'] - a['hp']
        victim = drops.get(e['id'])
        rows.append(('drop' if victim is not None else 'spawn', e['loot'], dA, dR, round(dH, 1), a['ammo'], victim))
from collections import Counter
for r in rows:
    if r[0] == 'drop': print(r)
print(Counter((r[0], r[1], r[2], r[3]) for r in rows if r[0] == 'spawn'))
