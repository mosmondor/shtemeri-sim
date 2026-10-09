import sys, gzip, json, numpy as np
from collections import defaultdict
def go(p):
    agg = defaultdict(lambda: [0, 0, 0, 0, 0])
    for ln in gzip.open(p, 'rt', encoding='utf-8'):
        o = json.loads(ln)
        if 'i' not in o: continue
        ph = o['t'] // 300
        for s in o['i']:
            a = agg[ph]; a[0] += 1; a[1] += len(s.get('coll', [])); a[2] += len(s.get('blips', []))
            a[3] += sum(1 for b in s.get('blips', []) if b[3] == 1); a[4] += 'coll' in s
    return agg
for p in sys.argv[1:]:
    agg = go(p)
    print(p.split('/')[-1][:30], ' '.join('t%d: coll %.2f(%.2f) bl %.1f M %.1f' % (k * 300, v[1] / v[0], v[4] / v[0], v[2] / v[0], v[3] / v[0]) for k, v in sorted(agg.items())))
