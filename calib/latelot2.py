"""Spawn kutija: najmanja udaljenost nove kutije od drugih kutija (postojećih i iz istog spawna), od shtemera,
od stijena, i od ruba zone; i što razlikuje spawn tickove s 0/1/2 kutije."""
import glob, os, random, sys
import numpy as np
from multiprocessing import Pool
from common import *

TS = list(range(200, 3600, 200))


def one(path):
    d = load_replay(path)
    fr = d['frames']
    deaths = [e for e in d['events'] if e['k'] == 'death']
    obs = d['arena']['obstacles']
    seen = set(); prev = None; rows = []
    for F in fr:
        ids = {l[0]: l for l in F.get('l', [])}
        new = [l for k, l in ids.items() if k not in seen]
        seen.update(ids)
        t = F['t']
        if t in TS and prev is not None:
            spawn = []
            for l in new:
                isdrop = any(t - 2 < e['t'] <= t and prev['s'][e['s']] is not None and
                             np.hypot(prev['s'][e['s']][0] - l[2], prev['s'][e['s']][1] - l[3]) < 1.5 for e in deaths)
                if not isdrop: spawn.append(l)
            z = F['z']
            old = [l for l in ids.values() if l not in spawn]
            alive = [s for s in F['s'] if s is not None]
            for l in spawn:
                others = [o for o in ids.values() if o[0] != l[0]]
                dbox = min([np.hypot(o[2] - l[2], o[3] - l[3]) for o in others] or [99])
                dbox_old = min([np.hypot(o[2] - l[2], o[3] - l[3]) for o in old] or [99])
                dbot = min([np.hypot(s[0] - l[2], s[1] - l[3]) for s in alive] or [99])
                drock = min([np.hypot(l[2] - o[0], l[3] - o[1]) - o[2] for o in obs] or [99])
                rows.append(('s', t, len(spawn), len(prev.get('l', [])), z[2], dbox, dbox_old, dbot, drock,
                             np.hypot(l[2] - z[0], l[3] - z[1])))
            # zona: koliko je slobodnog mjesta (udio diska 0.9R dalje od 1.5 od stijene)
            R = z[2] * 0.9
            if R > 0:
                pts = np.array(z[:2]) + R * np.sqrt(np.random.default_rng(t).random(400))[:, None] * \
                      np.stack([np.cos(a := np.random.default_rng(t + 1).random(400) * 2 * np.pi), np.sin(a)], 1)
                free = np.ones(400, bool)
                for o in obs: free &= np.hypot(pts[:, 0] - o[0], pts[:, 1] - o[1]) - o[2] >= 1.5
                ffree = free.mean()
            else: ffree = 0
            rows.append(('T', t, len(spawn), len(prev.get('l', [])), z[2], ffree))
        prev = F
    return rows


if __name__ == '__main__':
    files = sorted(glob.glob(REPLAYS + '/*.json.gz')); random.seed(11); files = random.sample(files, int(sys.argv[1]) if len(sys.argv) > 1 else 3000)
    with Pool(2) as pool: res = pool.map(one, files, chunksize=8)
    S = np.array([r[1:] for x in res for r in x if r[0] == 's'], float)
    T = np.array([r[1:] for x in res for r in x if r[0] == 'T'], float)
    print('spawnova', len(S))
    print('min udalj. od druge kutije (sve) p0/p0.1/p1/p5/p50:', np.percentile(S[:, 4], [0, .1, 1, 5, 50]).round(2))
    print('min udalj. od stare kutije        p0/p0.1/p1/p5/p50:', np.percentile(S[:, 5], [0, .1, 1, 5, 50]).round(2))
    print('min udalj. od shtemera            p0/p0.1/p1/p5/p50:', np.percentile(S[:, 6], [0, .1, 1, 5, 50]).round(2))
    print('min udalj. od ruba stijene        p0/p0.1/p1/p5/p50:', np.percentile(S[:, 7], [0, .1, 1, 5, 50]).round(2))
    for lo, hi in [(0, 99), (8, 99), (4, 8), (2, 4), (0.01, 2)]:
        m = (S[:, 3] >= lo) & (S[:, 3] < hi)
        if m.sum(): print(f'  R u [{lo},{hi}): n={m.sum()} min kutija-kutija p0/p1/p5 {np.percentile(S[m, 4], [0, 1, 5]).round(2)}')
    print('spawn tickovi: broj spawnova prema slobodnom udjelu diska i R')
    for lo, hi in [(0, .25), (.25, .5), (.5, .75), (.75, .99), (.99, 1.01)]:
        m = (T[:, 4] >= lo) & (T[:, 4] < hi) & (T[:, 2] + 3 <= 16)
        if m.sum(): print(f'  slobodno {lo:.2f}-{hi:.2f}: n={m.sum():5d}  spawn sr. {T[m, 1].mean():.2f}  0/1/2/3 {[int((T[m,1]==k).sum()) for k in range(4)]}')
    for lo, hi in [(0, .1), (.1, 2), (2, 4), (4, 5), (5, 8), (8, 13), (13, 60)]:
        m = (T[:, 3] >= lo) & (T[:, 3] < hi) & (T[:, 2] + 3 <= 16)
        if m.sum(): print(f'  R {lo}-{hi}: n={m.sum():5d}  spawn sr. {T[m, 1].mean():.2f}  0/1/2/3 {[int((T[m,1]==k).sum()) for k in range(4)]}')
    m = T[:, 2] + 3 > 16
    print('  kapa (prije+3>16): n', m.sum(), 'spawn = min(3,16-prije) u %.3f' % np.mean(T[m, 1] == np.minimum(3, 16 - T[m, 2])) if m.sum() else '')
