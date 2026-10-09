"""Do blips cover all objects in view? For each shtemer-tick: true shtemers in proximity / in cone (from the replay,
state at the start of the tick) vs Medium blips. Server vs local telemetry, same setup."""
import sys, gzip, json, math, numpy as np
sys.path.insert(0, 'calib')
from common import Terrain
from collections import defaultdict
def go(tel, rep, slot):
    R = json.load(open(rep, encoding='utf-8')); fr = {f['t']: f for f in R['frames']}; T = Terrain(R['arena'])
    out = defaultdict(lambda: [0, 0])
    for ln in gzip.open(tel, 'rt', encoding='utf-8'):
        o = json.loads(ln)
        if 'i' not in o or (o['t'] - 1) not in fr: continue
        F = fr[o['t'] - 1]
        for s in o['i']:
            g = slot * 4 + s['idx']; px, py = s['pos']
            others = [q for h, q in enumerate(F['s']) if q and h != g]
            near = [q for q in others if math.hypot(q[0] - px, q[1] - py) <= 5.8]
            med = [b for b in s.get('blips', []) if b[3] == 1]
            mnear = [b for b in med if math.hypot(b[1] - px, b[2] - py) <= 6.2]
            k = min(len(near), 8)
            out[k][0] += 1; out[k][1] += len(mnear)
    return out
for tel, rep, slot, name in ((sys.argv[1], sys.argv[2], int(sys.argv[3]), 'A'), (sys.argv[4], sys.argv[5], int(sys.argv[6]), 'B')):
    out = go(tel, rep, slot)
    print(name, ' '.join('%d near: n%d medium-blips-in-6m %.2f' % (k, v[0], v[1] / v[0]) for k, v in sorted(out.items())))
