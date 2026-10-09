# Pickup: distance at pickup, and do full shtemers pick up useless boxes?
import numpy as np
from common import *
for mid in (27427, 27426, 27425):
    hdr, ticks, _ = load_telemetry(f'{TEL}/{mid}.jsonl.gz'); rep = load_replay(f'{TEL}/{mid}.replay.json')
    fid = hdr['fleetId']; n = hdr['fleetSize']
    fr = {f['t']: f for f in rep['frames']}
    picks = [e for e in rep['events'] if e['k'] == 'pickup']
    # pickup distance using frame of pickup tick (after move)
    ds = []
    for e in picks:
        F = fr.get(e['t']) or fr.get(e['t'] + 1); Fp = fr.get(e['t'] - 2) or fr.get(e['t'] - 1)
        if F is None or Fp is None: continue
        box = [l for l in Fp['l'] if l[0] == e['id']]
        q = F['s'][e['s']]
        if box and q: ds.append(np.hypot(q[0] - box[0][2], q[1] - box[0][3]))
    print(mid, 'pickups', len(picks), 'dist pct', np.percentile(ds, [50, 90, 100]).round(2) if ds else None)
    # near-miss: own shtemer within 1.5 of a box whose kind is useless to it, and no pickup
    by = {(tk['t'], s['idx']): s for tk in ticks for s in tk['i']}
    useless_near = 0; useless_picked = 0
    for (t, i), s in by.items():
        F = fr.get(t - 1)
        if F is None: continue
        for l in F.get('l', []):
            if np.hypot(l[2] - s['pos'][0], l[3] - s['pos'][1]) < 1.5:
                kind = l[1]
                full = (kind == 1 and s['ammo'][0] >= 120) or (kind == 2 and s['ammo'][1] >= 6) or (kind == 3 and s['hp'] >= 250)
                if full:
                    useless_near += 1
                    if any(e['id'] == l[0] and e['s'] == fid * n + i for e in picks): useless_picked += 1
                    else: print('   full near box not picked: t', t, 'i', i, 'kind', kind, 'ammo', s['ammo'], 'hp', s['hp'])
    print('   useless-near events', useless_near, 'picked', useless_picked)
