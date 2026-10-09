# Replays every pistol bullet of a server replay through the hit model (positions from frames, odd ticks
# interpolated) and compares predicted outcome (hit whom, when) with the server's hit events.
import sys, glob, random, numpy as np
from common import load_replay, Terrain, REPLAYS
SUB = float(sys.argv[1]) if len(sys.argv) > 1 else 0.45
RAD = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0
def pos_at(fr, t, g):
    # state after tick t (frame t); odd ticks interpolated
    if t in fr:
        q = fr[t]['s'][g]; return None if q is None else (q[0], q[1])
    a, b = fr.get(t - 1), fr.get(t + 1)
    if a is None or b is None: return None
    qa, qb = a['s'][g], b['s'][g]
    if qa is None or qb is None: return None
    return ((qa[0] + qb[0]) / 2, (qa[1] + qb[1]) / 2)
def run(d):
    T = Terrain(d['arena']); obs = d['arena']['obstacles']
    fr = {f['t']: f for f in d['frames']}; N = len(d['frames'][0]['s'])
    hits = {}
    for e in d['events']:
        if e['k'] == 'hit' and e['w'] == 0: hits.setdefault(e['by'], []).append((e['t'], e['s']))
    out = []
    for e in d['events']:
        if e['k'] != 'fire' or e['w'] != 0: continue
        t, s = e['t'], e['k']
        start = pos_at(fr, t - 1, e['s'])
        if start is None: continue
        mx, my = start; mz = T.height(mx, my) + 1.2
        tx, ty = e['x'], e['y']; tz = T.height(tx, ty) + 1.0
        dv = np.array([tx - mx, ty - my, tz - mz]); L = np.linalg.norm(dv)
        if L < 1e-6: continue
        u = dv / L; p = np.array([mx, my, mz]) + u * 1.1; trav = 1.1
        pred = None; tk = t
        while pred is None and trav <= 30 and tk < t + 16:
            n = int(np.ceil(2.0 / SUB)); sub = 2.0 / n
            P = {g: pos_at(fr, tk, g) for g in range(N)}
            for _ in range(n):
                p = p + u * sub; trav += sub
                if trav > 30 + 1e-9: break
                if p[2] < T.height(p[0], p[1]) or p[0] < 0 or p[1] < 0 or p[0] > 100 or p[1] > 100: pred = ('wall', tk); break
                if any(np.hypot(p[0] - o[0], p[1] - o[1]) < o[2] for o in obs): pred = ('rock', tk); break
                for g, q in P.items():
                    if q is None: continue
                    if np.hypot(p[0] - q[0], p[1] - q[1]) <= RAD:
                        hz = T.height(q[0], q[1])
                        if hz <= p[2] <= hz + 2: pred = (g, tk); break
                if pred: break
            tk += 1
        actual = [h for h in hits.get(e['s'], []) if t <= h[0] <= t + 16]
        hit_now = actual[0] if actual else None
        out.append((pred, hit_now))
    return out
files = sorted(glob.glob('../validation/server/*.json'))
random.seed(3); files += random.sample(sorted(glob.glob(REPLAYS + '/*.json.gz')), 6)
tp = fp = fn = tn = same = 0
for f in files:
    for pred, act in run(load_replay(f)):
        ph = pred is not None and isinstance(pred[0], int)
        if ph and act: tp += 1; same += (pred[0] == act[1] and abs(pred[1] - act[0]) <= 1)
        elif ph and not act: fp += 1
        elif not ph and act: fn += 1
        else: tn += 1
tot = tp + fp + fn + tn
print('substep %.2f radius %.2f: bullets %d | both hit %d (same victim+tick %d) | model hit, server miss %d | model miss, server hit %d | both miss %d | agreement %.3f'
      % (SUB, RAD, tot, tp, same, fp, fn, tn, (tp + tn) / tot))
