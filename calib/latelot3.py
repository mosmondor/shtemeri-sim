"""Model spawna s razmakom: za svaki spawn tick ranked partije (stanje iz framea T-2) simuliraj spawn
3 kutije, svaka s K pokušaja (točka uniformno u 0.9R, ≥ 1.5 od ruba stijene, ≥ D od svake kutije),
i usporedi raspodjelu broja spawnova po pojasevima R s izmjerenom."""
import glob, random, sys
import numpy as np
from multiprocessing import Pool
from common import *

TS = list(range(200, 3600, 200))


def one(path):
    d = load_replay(path)
    fr = d['frames']; deaths = [e for e in d['events'] if e['k'] == 'death']
    obs = np.array(d['arena']['obstacles'], float).reshape(-1, 3)
    init = [(l[2], l[3]) for l in fr[0].get('l', [])] if fr[0]['t'] == 0 else []
    seen = set(); prev = None; out = []
    for F in fr:
        ids = {l[0]: l for l in F.get('l', [])}
        new = [l for k, l in ids.items() if k not in seen]; seen.update(ids)
        t = F['t']
        if t in TS and prev is not None:
            ns = 0
            for l in new:
                if not any(t - 2 < e['t'] <= t and prev['s'][e['s']] is not None and
                           np.hypot(prev['s'][e['s']][0] - l[2], prev['s'][e['s']][1] - l[3]) < 1.5 for e in deaths): ns += 1
            old = [(l[2], l[3]) for l in prev.get('l', [])]
            out.append((t, F['z'], old, ns))
        prev = F
    return obs, out, init


def sim(obs, z, old, K, D, rng, n=3, cap=16):
    boxes = list(old); placed = 0
    for i in range(min(n, cap - len(old))):
        for k in range(K):
            r = 0.9 * z[2] * np.sqrt(rng.random()); a = 2 * np.pi * rng.random()
            x, y = z[0] + r * np.cos(a), z[1] + r * np.sin(a)
            if len(obs) and (np.hypot(obs[:, 0] - x, obs[:, 1] - y) - obs[:, 2]).min() < 1.5: continue
            if boxes and min(np.hypot(bx - x, by - y) for bx, by in boxes) < D: continue
            boxes.append((x, y)); placed += 1; break
    return placed


if __name__ == '__main__':
    files = sorted(glob.glob(REPLAYS + '/*.json.gz')); random.seed(11); files = random.sample(files, int(sys.argv[1]))
    with Pool(2) as pool: res = pool.map(one, files, chunksize=8)
    inits = [i for _, _, i in res]
    dd = [min(np.hypot(a[0] - b[0], a[1] - b[1]) for j, b in enumerate(i) if j != k) for i in inits for k, a in enumerate(i) if len(i) > 1]
    print('početne kutije: min udaljenost do druge početne p0/p1/p5/p50', np.percentile(dd, [0, 1, 5, 50]).round(2))
    bands = [(0.01, 2), (2, 4), (4, 5), (5, 8), (8, 60)]
    obsd = {b: np.zeros(4) for b in bands}
    for obs, rows, _ in res:
        for t, z, old, ns in rows:
            if len(old) + 3 > 16: continue
            for b in bands:
                if b[0] <= z[2] < b[1]: obsd[b][min(ns, 3)] += 1
    print('izmjereno:', {f'{b}': obsd[b].astype(int).tolist() for b in bands})
    for K, D in [(1, 4), (3, 4), (10, 4), (30, 4), (100, 4), (10, 3.5), (30, 4.5), (100, 0)]:
        rng = np.random.default_rng(1); pr = {b: np.zeros(4) for b in bands}
        for obs, rows, _ in res:
            for t, z, old, ns in rows:
                if len(old) + 3 > 16: continue
                for b in bands:
                    if b[0] <= z[2] < b[1]: pr[b][sim(obs, z, old, K, D, rng)] += 1
        print(f'K={K:3d} D={D}:', '  '.join(f'{b}: ' + '/'.join(f'{x:.0f}' for x in pr[b]) for b in bands))
