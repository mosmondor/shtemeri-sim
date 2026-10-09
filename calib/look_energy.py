# Look turning and energy bookkeeping from telemetry.
import numpy as np, os
from common import *
lk_err, en_err, en_cases = [], [], []
bad_en = []
for f in sorted(os.listdir(TEL)):
    if not f.endswith('.jsonl.gz'): continue
    hdr, ticks, res = load_telemetry(f'{TEL}/{f}')
    by = {(tk['t'], s['idx']): s for tk in ticks for s in tk['i']}
    target = {}
    for (t, i), s in sorted(by.items()):
        if 'look' in s['cmd']: target[i] = s['cmd']['look']
        n = by.get((t + 1, i))
        if n is None: continue
        if i in target:
            d = (target[i] - s['look'] + np.pi) % (2 * np.pi) - np.pi
            pred = s['look'] + np.clip(d, -0.21, 0.21)
            e = (pred - n['look'] + np.pi) % (2 * np.pi) - np.pi
            lk_err.append(abs(e))
        z = len(s['cmd'].get('zoom', []))
        pe = min(100.0, s['en'] - 8 * z + 0.4)
        en_err.append(abs(pe - n['en']))
        if abs(pe - n['en']) > 0.06: bad_en.append((f, t, i, s['en'], z, n['en'], s['cmd'].get('zoom')))
lk_err = np.array(lk_err); en_err = np.array(en_err)
print('look err pct50/99/max', np.percentile(lk_err, [50, 99]).round(4), lk_err.max().round(3), 'n>0.02:', (lk_err > 0.02).sum(), 'of', len(lk_err))
print('energy err pct50/99/max', np.percentile(en_err, [50, 99]).round(4), en_err.max().round(3), 'bad', len(bad_en))
for b in bad_en[:15]: print(b)
