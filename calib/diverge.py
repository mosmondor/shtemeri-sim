# How fast does a local match drift away from the server match with the same setup?
import sys, numpy as np, json
from common import load_replay
a = load_replay(sys.argv[1]); b = load_replay(sys.argv[2])
fa = {f['t']: f for f in a['frames']}; fb = {f['t']: f for f in b['frames']}
for t in [0, 2, 4, 6, 10, 20, 30, 40, 60, 80, 100, 150, 200, 300, 400, 600, 800]:
    if t not in fa or t not in fb: continue
    e = [np.hypot(p[0] - q[0], p[1] - q[1]) for p, q in zip(fa[t]['s'], fb[t]['s']) if p and q]
    lk = [abs((p[2] - q[2] + np.pi) % (2 * np.pi) - np.pi) for p, q in zip(fa[t]['s'], fb[t]['s']) if p and q]
    print('t=%4d pos err median %.3f max %.3f | look err median %.3f | alive %d/%d' % (t, np.median(e), np.max(e), np.median(lk), sum(1 for p in fa[t]['s'] if p), sum(1 for q in fb[t]['s'] if q)))
