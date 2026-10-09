# Bullet-by-bullet check of the pistol hit model with geometric matching of server hit events to bullets.
import sys, glob, random, numpy as np
from common import load_replay, Terrain, REPLAYS
SUB = float(sys.argv[1]) if len(sys.argv) > 1 else 0.45
RAD = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0
def pos_at(fr, t, g):
    if t in fr:
        q = fr[t]['s'][g]; return None if q is None else (q[0], q[1])
    a, b = fr.get(t - 1), fr.get(t + 1)
    if a is None or b is None: return None
    qa, qb = a['s'][g], b['s'][g]
    if qa is None or qb is None: return None
    return ((qa[0] + qb[0]) / 2, (qa[1] + qb[1]) / 2)
def bullets(d):
    T = Terrain(d['arena']); obs = d['arena']['obstacles']; fr = {f['t']: f for f in d['frames']}; N = len(d['frames'][0]['s'])
    hitset = {}
    for e in d['events']:
        if e['k'] == 'hit' and e['w'] == 0: hitset.setdefault(e['by'], []).append([e['t'], e['s'], False])
    res = []
    for e in d['events']:
        if e['k'] != 'fire' or e['w'] != 0: continue
        t = e['t']; start = pos_at(fr, t - 1, e['s'])
        if start is None: continue
        mx, my = start; mz = T.height(mx, my) + 1.2; tz = T.height(e['x'], e['y']) + 1.0
        dv = np.array([e['x'] - mx, e['y'] - my, tz - mz]); L = np.linalg.norm(dv)
        if L < 1e-6: continue
        u = dv / L; p = np.array([mx, my, mz]) + u * 1.1; trav = 1.1
        pred = None; tk = t; path = []
        while pred is None and trav <= 30 and tk < t + 16:
            n = int(np.ceil(2.0 / SUB)); sub = 2.0 / n
            P = {g: pos_at(fr, tk, g) for g in range(N)}
            a = p.copy()
            for _ in range(n):
                p = p + u * sub; trav += sub
                if trav > 30 + 1e-9: break
                if p[2] < T.height(p[0], p[1]) or not (0 <= p[0] <= 100 and 0 <= p[1] <= 100): pred = ('stop', tk); break
                if any(np.hypot(p[0] - o[0], p[1] - o[1]) < o[2] for o in obs): pred = ('stop', tk); break
                for g, q in P.items():
                    if q is None: continue
                    if np.hypot(p[0] - q[0], p[1] - q[1]) <= RAD:
                        hz = T.height(q[0], q[1])
                        if hz <= p[2] <= hz + 2: pred = (g, tk); break
                if pred: break
            path.append((tk, a[:2].copy(), p[:2].copy()))
            tk += 1
        # server: a hit by this shooter whose victim is within 1.6 m of this bullet's segment at that tick
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
        res.append((pred, act))
    unexplained = sum(1 for v in hitset.values() for h in v if not h[2])
    return res, unexplained
files = sorted(glob.glob('../validation/server/*.json'))
random.seed(3); files += random.sample(sorted(glob.glob(REPLAYS + '/*.json.gz')), 6)
tp = fp = fn = tn = same = unexp = 0
for f in files:
    r, u = bullets(load_replay(f)); unexp += u
    for pred, act in r:
        ph = pred is not None and isinstance(pred[0], int)
        if ph and act: tp += 1; same += (pred[0] == act[0] and pred[1] == act[1])
        elif ph: fp += 1
        elif act: fn += 1
        else: tn += 1
tot = tp + fp + fn + tn
print('substep %.2f radius %.2f: bullets %d | both hit %d (same victim and tick %d) | model hit/server miss %d | model miss/server hit %d | both miss %d | agreement %.4f | server hits not matched to any bullet %d'
      % (SUB, RAD, tot, tp, same, fp, fn, tn, (tp + tn) / tot, unexp))
