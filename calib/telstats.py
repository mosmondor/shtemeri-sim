"""Rates of telemetry fields per shtemer-tick (what the fleet received / did), for server vs local telemetry files.
python telstats.py fileA [fileB ...]  (each file reported separately)"""
import sys, gzip, json, numpy as np
from collections import Counter
def stats(p):
    c = Counter(); n = 0; collk = Counter(); nblips = []; hitsn = 0; msgin = 0
    for ln in gzip.open(p, 'rt', encoding='utf-8'):
        o = json.loads(ln)
        if 'i' not in o: continue
        for s in o['i']:
            n += 1
            for k in ('coll', 'hits', 'contacts', 'msgIn', 'log'):
                if k in s: c[k] += 1
            for k in ('fire', 'zoom', 'msgOut'):
                if k in s['cmd']: c['cmd.' + k] += 1
            for x in s.get('coll', []): collk[x[0]] += 1
            nblips.append(len(s.get('blips', [])))
            msgin += len(s.get('msgIn', []))
    return n, {k: round(v / n, 4) for k, v in sorted(c.items())}, {k: round(v / n, 4) for k, v in sorted(collk.items())}, round(np.mean(nblips), 2), round(msgin / n, 2)
for p in sys.argv[1:]:
    n, r, ck, nb, mi = stats(p)
    print(p.split('/')[-1][:40], 'n', n, r, 'coll kinds/tick', ck, 'blips/tick', nb, 'msgIn/tick', mi)
