"""Per-fleet behaviour metrics from replay v1 files, to compare local matches with ranked server matches.

python compare.py --local DIR [--names A=B ...]      local replays (names mapped to server fleet names)
Ranked replays containing the same fleet names are sampled from the archive automatically.
"""
import argparse, glob, gzip, json, os, random, sys
import numpy as np
from collections import defaultdict
from common import load_replay, REPLAYS

def metrics(d, want):
    n = d['rules']['fleetSize']; fr = d['frames']
    names = {f['id']: f['name'].split('#')[0] for f in d['fleets']}
    stats = {s['fleet']: s for s in d['result']['stats']}
    out = {}
    T = len(fr)
    for slot, name in names.items():
        if name not in want: continue
        gids = range(slot * n, slot * n + n)
        spd, edge, mate = [], [], []
        for a, b in zip(fr[:-1], fr[1:]):
            dt = (b['t'] - a['t']) / 20.0
            if dt <= 0: continue
            z = b['z']
            pts = []
            for g in gids:
                qa, qb = a['s'][g], b['s'][g]
                if qa is None or qb is None: continue
                spd.append(np.hypot(qb[0] - qa[0], qb[1] - qa[1]) / dt)
                edge.append(z[2] - np.hypot(qb[0] - z[0], qb[1] - z[1]))
                pts.append(qb[:2])
            if len(pts) >= 2:
                P = np.array(pts); D = np.hypot(*(P[:, None, :] - P[None, :, :]).transpose(2, 0, 1))
                mate.append(D[np.triu_indices(len(P), 1)].mean())
        ev = d['events']
        fires = [e for e in ev if e['k'] == 'fire' and e['s'] in gids]
        hits_direct_pistol = [e for e in ev if e['k'] == 'hit' and e['by'] in gids and e['w'] == 0 and e['s'] not in gids]
        friendly = sum(e['d'] for e in ev if e['k'] == 'hit' and e['by'] in gids and e['s'] in gids)
        deaths = [e for e in ev if e['k'] == 'death' and e['s'] in gids]
        st = stats[slot]
        pist = sum(1 for e in fires if e['w'] == 0)
        out[name] = dict(
            speed=np.mean(spd) if spd else np.nan, moving=np.mean(np.array(spd) > 1.0) if spd else np.nan,
            edge=np.median(edge) if edge else np.nan, outside=np.mean(np.array(edge) < 0) if edge else np.nan,
            spread=np.mean(mate) if mate else np.nan,
            dealt=st['damageDealt'], taken=st['damageTaken'], shots=pist, rockets=st['shotsRocket'],
            acc=len(hits_direct_pistol) / pist if pist else np.nan, loot=st['loot'], surv=st['survivedTicks'],
            place=st['place'], friendly=friendly, kills=st['kills'],
            zone_deaths=sum(1 for e in deaths if e['cause'] == 'zone'), deaths=len(deaths),
            ticks=d['totalTicks'])
    return out

def collect(files, want, mapping=None):
    rows = defaultdict(list)
    for f in files:
        try: d = load_replay(f)
        except Exception as e: print('skip', f, e); continue
        if mapping:
            for fl in d['fleets']:
                base = fl['name'].split('#')[0]
                fl['name'] = mapping.get(base, base)
        for name, m in metrics(d, want).items(): rows[name].append(m)
    return rows

def table(rows, label):
    keys = ['ticks', 'place', 'surv', 'speed', 'moving', 'edge', 'outside', 'spread', 'shots', 'acc', 'rockets', 'dealt', 'taken', 'friendly', 'kills', 'loot', 'deaths', 'zone_deaths']
    print(f'== {label}')
    print('%-10s %4s ' % ('fleet', 'n') + ' '.join('%8s' % k[:8] for k in keys))
    for name in sorted(rows):
        R = rows[name]
        print('%-10s %4d ' % (name, len(R)) + ' '.join('%8.2f' % np.nanmean([r[k] for r in R]) for k in keys))

if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--local', required=True)
    ap.add_argument('--names', nargs='*', default=[], help='local=server name mapping')
    ap.add_argument('--ranked', type=int, default=300, help='ranked replays per fleet name')
    a = ap.parse_args()
    mapping = dict(x.split('=') for x in a.names)
    want = set(mapping.values()) if mapping else None
    local_files = sorted(glob.glob(os.path.join(a.local, '*.json*')))
    loc = collect(local_files, want or set(), mapping)
    table(loc, f'local ({len(local_files)} matches)')
    # ranked: index of fleet names per replay (cached)
    idx_path = os.path.join(os.path.dirname(__file__), 'ranked_index.json')
    if os.path.exists(idx_path): idx = json.load(open(idx_path))
    else:
        idx = {}
        for f in glob.glob(REPLAYS + '/*.json.gz'):
            try:
                raw = gzip.decompress(open(f, 'rb').read())
                head = raw[:4000].decode('utf-8', 'ignore')
                d = json.loads(raw)
                idx[os.path.basename(f)] = [fl['name'] for fl in d['fleets']]
            except Exception: pass
        json.dump(idx, open(idx_path, 'w'))
    random.seed(11)
    files = set()
    for name in want:
        cand = [f for f, ns in idx.items() if name in ns]
        files |= set(random.sample(cand, min(a.ranked, len(cand))))
    rk = collect([os.path.join(REPLAYS, f) for f in sorted(files)], want)
    table(rk, f'ranked ({len(files)} matches)')
