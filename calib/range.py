"""Doseg i timing projektila na server replayima (metak po metak).

Pištolj: za svaki metak modelom (pod-korak 0.5, pogodak na pozicijama nakon pomaka) produljenim do 34 m
nađi predviđeni pogodak i prijeđeni put; po pojasevima puta: koliko predviđenih pogodaka server potvrdi.
Iz toga se vidi gdje metak stvarno nestaje (29.6 = pod-korak prije 30, 31.1 = cijeli zadnji korak...).
Raketa: boom događaj prema modelu (eksplozija u točki cilja, domet 45): udaljenost i tick.
Cooldown: razmak između uzastopnih Fire istog strijelca (min i raspodjela).

  python range.py N            # 12 server probnih + N ranked
  python range.py N DIR        # prvih N lokalnih replaya iz DIR
"""
import sys, glob, random, numpy as np
from collections import Counter
from multiprocessing import Pool
from common import load_replay, Terrain, REPLAYS

SUB = 0.5


def pos_at(fr, t, g):
    if t in fr:
        q = fr[t]['s'][g]; return None if q is None else (q[0], q[1])
    a, b = fr.get(t - 1), fr.get(t + 1)
    if a is None or b is None: return None
    qa, qb = a['s'][g], b['s'][g]
    if qa is None or qb is None: return None
    return ((qa[0] + qb[0]) / 2, (qa[1] + qb[1]) / 2)


def analyse(path):
    d = load_replay(path)
    T = Terrain(d['arena']); obs = d['arena']['obstacles']; fr = {f['t']: f for f in d['frames']}; N = len(d['frames'][0]['s'])
    hitset = {}
    for e in d['events']:
        if e['k'] == 'hit' and e['w'] == 0: hitset.setdefault(e['by'], []).append([e['t'], e['s'], False])
    out = {'pist': [], 'cd': Counter(), 'rk': [], 'pk_last': []}
    last = {}
    for e in d['events']:
        if e['k'] == 'fire':
            k = (e['s'], e['w'])
            if k in last: out['cd'][(e['w'], e['t'] - last[k])] += 1
            last[k] = e['t']
    tracks = {}
    for f in d['frames']:
        for p in f.get('p', []): tracks.setdefault(p[0], []).append((f['t'], p))
    for e in d['events']:
        if e['k'] != 'fire': continue
        t = e['t']; start = pos_at(fr, t - 1, e['s'])
        if start is None: continue
        mx, my = start; mz = T.height(mx, my) + 1.2; tz = T.height(e['x'], e['y']) + 1.0
        dv = np.array([e['x'] - mx, e['y'] - my, tz - mz]); L = np.linalg.norm(dv)
        if L < 1e-6: continue
        u = dv / L
        if e['w'] == 1:
            out['rk'].append((e['s'], t, mx, my, mz, u, L, e['x'], e['y']))
            continue
        p = np.array([mx, my, mz]) + u * 1.1; trav = 1.1
        pred = None; tk = t; path = []
        while pred is None and trav <= 34 and tk < t + 18:
            n = int(np.ceil(2.0 / SUB)); sub = 2.0 / n
            P = {g: pos_at(fr, tk, g) for g in range(N)}
            a = p.copy()
            for _ in range(n):
                p = p + u * sub; trav += sub
                if trav > 34 + 1e-9: break
                if p[2] < T.height(p[0], p[1]) or not (0 <= p[0] <= 100 and 0 <= p[1] <= 100): pred = ('stop', tk, trav); break
                if any(np.hypot(p[0] - o[0], p[1] - o[1]) < o[2] for o in obs): pred = ('stop', tk, trav); break
                for g, q in P.items():
                    if q is None or g == e['s']: continue
                    if np.hypot(p[0] - q[0], p[1] - q[1]) <= 1.0:
                        hz = T.height(q[0], q[1])
                        if hz <= p[2] <= hz + 2: pred = (g, tk, trav); break
                if pred: break
            path.append((tk, a[:2].copy(), p[:2].copy()))
            tk += 1
        act = None
        for h in hitset.get(e['s'], []):
            if h[2]: continue
            for (tk2, a, b) in path:
                if tk2 != h[0]: continue
                q = pos_at(fr, tk2, h[1])
                if q is None: continue
                ab = b - a; w = np.array(q) - a; s = np.clip(w @ ab / max(ab @ ab, 1e-9), 0, 1)
                if np.linalg.norm(a + s * ab - q) < 1.6: act = (h[1], h[0]); h[2] = True; break
            if act: break
        out['pist'].append((None if pred is None else (pred[0] if isinstance(pred[0], str) else 'hit'), None if pred is None else pred[2],
                            None if pred is None else pred[1] - t, act is not None, None if act is None else act[1] - t))
    # zadnji frame u kojem se metak vidi: k = tick framea - Fire tick, put od starta (3D i 2D), visina iznad terena
    fires = {}
    for e in d['events']:
        if e['k'] == 'fire' and e['w'] == 0: fires.setdefault(e['s'], []).append(e['t'])
    for pid, tr in tracks.items():
        if tr[0][1][1] != 0: continue
        owner = tr[0][1][5]; t0 = tr[0][0]
        ft = [x for x in fires.get(owner, []) if t0 - 1 <= x <= t0]
        if len(ft) != 1: continue
        st = pos_at(fr, ft[0] - 1, owner)
        if st is None: continue
        tl, pl = tr[-1]
        mz = T.height(*st) + 1.2
        d2 = np.hypot(pl[2] - st[0], pl[3] - st[1]); d3 = np.sqrt(d2 ** 2 + (pl[4] - mz) ** 2)
        out['pk_last'].append((tl - ft[0], d2, d3, pl[4] - T.height(pl[2], pl[3])))
    # rakete: sljedeći boom istog strijelca
    booms = [e for e in d['events'] if e['k'] == 'boom']
    direct = {(e['t'], e['by']) for e in d['events'] if e['k'] == 'hit' and e['w'] == 1 and not e.get('splash')}
    rk = []
    for s, t, mx, my, mz, u, L, tx, ty in out['rk']:
        b = [x for x in booms if x['by'] == s and x['t'] >= t]
        if not b: continue
        b = b[0]
        if b['t'] > t + 60: continue
        db3 = np.sqrt((b['x'] - mx) ** 2 + (b['y'] - my) ** 2 + (b['z'] - mz) ** 2)
        # model: nakon ticka t+k prijeđeno 1.1 + 0.9*(k+1); kraj = min(L, 45)
        end = min(L, 45.0)
        kmod = int(np.ceil((end - 1.1) / 0.9 - 1e-9)) - 1
        rk.append((L, db3, b['t'] - t, kmod, (b['t'], s) in direct, b['z'] - T.height(b['x'], b['y'])))
    out['rk'] = rk
    return out


if __name__ == '__main__':
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 40
    if len(sys.argv) > 2:
        files = sorted(glob.glob(sys.argv[2] + '/*.json.gz'))[:n]
    else:
        files = sorted(glob.glob('../validation/server/*.json'))
        random.seed(3); files += random.sample(sorted(glob.glob(REPLAYS + '/*.json.gz')), n)
    with Pool(2) as pool: res = pool.map(analyse, files)
    P = [x for r in res for x in r['pist']]
    print('metaka', len(P), 'datoteka', len(files))
    print('pištolj: model (produljen do 34 m) predviđa pogodak na prijeđenom putu (3D od centra); koliko server potvrdi')
    for lo, hi in [(0, 25), (25, 28.1), (28.1, 29.1), (29.1, 29.6), (29.6, 30.1), (30.1, 30.6), (30.6, 31.1), (31.1, 32.1), (32.1, 34.1)]:
        m = [x for x in P if x[0] == 'hit' and lo < x[1] <= hi + 1e-9]
        if m: print(f'  put ({lo:5.1f},{hi:5.1f}]: predviđeno {len(m):6d}, server pogodak {sum(x[3] for x in m):6d} ({np.mean([x[3] for x in m]):.3f})')
    srvk = Counter(x[4] for x in P if x[3])
    print('  server pogodak: tick nakon Fire (k), rep:', sorted(srvk.items())[-5:])
    L = np.array([x for r in res for x in r['pk_last']], float)
    print('pištolj, zadnji frame s metkom: k = tick framea - Fire tick (vrh):', Counter(L[:, 0].astype(int)).most_common(8))
    m = L[:, 3] > 0.3
    for k in (12, 13, 14, 15):
        mk = m & (L[:, 0] == k)
        if mk.sum(): print(f'  k={k}: n={mk.sum()} put 3D p50/max {np.percentile(L[mk, 2], [50, 100]).round(2)}, 2D p50/max {np.percentile(L[mk, 1], [50, 100]).round(2)}')
    print('  najveći viđeni put 3D p99/p99.9/max:', np.percentile(L[:, 2], [99, 99.9, 100]).round(2), '2D:', np.percentile(L[:, 1], [99, 99.9, 100]).round(2))
    cd = Counter(); [cd.update(r['cd']) for r in res]
    for w in (0, 1):
        v = sorted((k[1], c) for k, c in cd.items() if k[0] == w)
        print(f'razmak Fire (w={w}) najmanjih 5:', v[:5])
    R = np.array([x for r in res for x in r['rk']], float)
    print('rakete', len(R))
    free = (R[:, 4] == 0) & (R[:, 5] > 0.3)
    for lo, hi in [(0, 20), (20, 40), (40, 44.5), (44.5, 45.5), (45.5, 100)]:
        mm = free & (R[:, 0] >= lo) & (R[:, 0] < hi)
        if mm.sum(): print(f'  cilj 3D [{lo},{hi}): n={mm.sum():4d} boom udalj. - min(L,45) p5/p50/p95 {np.percentile(R[mm, 1] - np.minimum(R[mm, 0], 45), [5, 50, 95]).round(3)}, '
                           f'boom tick - model {Counter((R[mm, 2] - R[mm, 3]).astype(int)).most_common(4)}')
