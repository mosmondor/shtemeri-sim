"""Kasni loot: broj kutija na karti i spawnovi po spawn ticku (200, 400, ...), ranked prema lokalnim replayima.

  python latelot.py ranked [N]          # N nasumičnih ranked replaya (default 3000)
  python latelot.py DIR [DIR ...]       # svi *.json.gz u lokalnim folderima

Po spawn ticku T: koliko partija još traje, kutija na karti u frameu T-2 (prije spawna), novih spawnova u T
(nove kutije koje nisu ispale iz mrtvog), kutija u frameu T; radijus zone u T. Plus 'kutije u frameu' po
pojasevima tickova (prosjek po partijama koje još traju).
"""
import glob, os, random, sys
import numpy as np
from multiprocessing import Pool
from collections import defaultdict
from common import *

TS = list(range(200, 3600, 200))
BANDS = [(0, 450), (450, 900), (900, 1350), (1350, 1800), (1800, 2250), (2250, 2450), (2450, 2700), (2700, 2900), (2900, 3600)]


def one(path):
    d = load_replay(path)
    fr = d['frames']
    byt = {F['t']: F for F in fr}
    deaths = [e for e in d['events'] if e['k'] == 'death']
    seen = set()
    out = {'T': {}, 'bands': defaultdict(list), 'total': d['totalTicks'], 'drops': 0, 'spawn_rocks': []}
    obs = d['arena']['obstacles']
    first = {}
    prev = None
    for F in fr:
        ids = {l[0]: l for l in F.get('l', [])}
        new = [l for k, l in ids.items() if k not in seen]
        seen.update(ids)
        t = F['t']
        for b0, b1 in BANDS:
            if b0 <= t < b1: out['bands'][(b0, b1)].append(len(ids))
        if t in TS:
            # drop = nova kutija blizu mrtvog čija je smrt u (t-2, t]
            spawn = []
            for l in new:
                isdrop = False
                for e in deaths:
                    if t - 2 < e['t'] <= t and prev is not None and prev['s'][e['s']] is not None:
                        q = prev['s'][e['s']]
                        if np.hypot(q[0] - l[2], q[1] - l[3]) < 1.5: isdrop = True
                if not isdrop: spawn.append(l)
            before = len(prev.get('l', [])) if prev else 0
            z = F['z']
            out['T'][t] = (before, len(spawn), len(ids), z[2])
            for l in spawn:
                out['spawn_rocks'].append((t, min([np.hypot(l[2] - o[0], l[3] - o[1]) - o[2] for o in d['arena']['obstacles']] or [99]),
                                           np.hypot(l[2] - z[0], l[3] - z[1]), z[2]))
        prev = F
    out['bands'] = dict(out['bands'])
    return out


def main():
    if sys.argv[1] == 'ranked':
        n = int(sys.argv[2]) if len(sys.argv) > 2 else 3000
        files = sorted(glob.glob(REPLAYS + '/*.json.gz')); random.seed(11); files = random.sample(files, min(n, len(files)))
        label = f'ranked ({len(files)})'
    else:
        files = sum([sorted(glob.glob(os.path.join(p, '*.json.gz')) + glob.glob(os.path.join(p, '*.json'))) for p in sys.argv[1:]], [])
        label = f'lokalno ({len(files)}) ' + ' '.join(sys.argv[1:])
    with Pool(2) as pool:
        res = pool.map(one, files, chunksize=8)
    print(label)
    print(f'{"T":>5} {"partija":>7} {"R":>6} {"prije":>6} {"spawn":>6} {"poslije":>7}  raspodjela spawna 0/1/2/3')
    for T in TS:
        rows = [r['T'][T] for r in res if T in r['T']]
        if not rows: continue
        a = np.array(rows, float)
        c = [int((a[:, 1] == k).sum()) for k in range(4)]
        print(f'{T:5d} {len(rows):7d} {np.mean(a[:, 3]):6.2f} {a[:, 0].mean():6.2f} {a[:, 1].mean():6.2f} {a[:, 2].mean():7.2f}  {c}')
    print('kutija na karti po pojasu tickova (partije koje još traju):')
    for b in BANDS:
        v = [np.mean(r['bands'][b]) for r in res if b in r['bands']]
        if v: print(f'  {b[0]:5d}-{b[1]:5d}: {np.mean(v):6.2f}  (partija {len(v)})')
    sr = np.array([x for r in res for x in r['spawn_rocks']], float)
    if len(sr):
        late = sr[sr[:, 0] >= 2400]
        print('spawn: min udaljenost od ruba stijene p0/p1/p5 %s; kasni (T>=2400) n=%d, r/R p50/p90/max %s, |r| p50/max %s' % (
            np.percentile(sr[:, 1], [0, 1, 5]).round(2), len(late),
            np.percentile(late[:, 2] / np.maximum(late[:, 3], 1e-9), [50, 90, 100]).round(2) if len(late) else '-',
            np.percentile(late[:, 2], [50, 100]).round(2) if len(late) else '-'))


if __name__ == '__main__':
    main()
