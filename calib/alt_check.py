# Does telemetry 'alt' equal bilinear interpolation of replay heights?
import numpy as np
from common import *
for mid in (27427, 27426, 27425):
    hdr, ticks, res = load_telemetry(f'{TEL}/{mid}.jsonl.gz')
    rep = load_replay(f'{TEL}/{mid}.replay.json')
    T = Terrain(rep['arena'])
    err = []
    for tk in ticks[::7]:
        for s in tk['i']:
            if 'pos' in s and 'alt' in s:
                err.append(T.height(*s['pos']) - s['alt'])
    err = np.abs(err)
    print(mid, 'n', len(err), 'mean', err.mean(), 'p99', np.percentile(err, 99), 'max', err.max())
    print(' heights sample', rep['arena']['heights'][:8], 'min/max', min(rep['arena']['heights']), max(rep['arena']['heights']))
