import numpy as np, pickle
from vision import recs, arena, ang, rock_block, terrain_block
rows = []
for r in recs:
    if r['kind'] == 'blip' and r.get('second', 99) <= 3 * r['err'] + 0.5: continue
    if r['okind'] != 'L': continue
    rows.append((r['kind'] == 'blip', r['dist'], ang(r), r))
D = np.array([x[1] for x in rows]); A = np.array([x[2] for x in rows]); S = np.array([x[0] for x in rows])
print('loot: prox seen %.3f n=%d' % (S[D <= 5.9].mean(), (D <= 5.9).sum()))
m = (D > 6.2) & (D < 31.5) & (A < 0.85)
print('loot in cone seen %.3f n=%d; outside seen %.4f' % (S[m].mean(), m.sum(), S[(D > 6.2) & ((D > 32.5) | (A > 0.89))].mean()))
idx = np.where(m)[0]; rng = np.random.default_rng(0); idx = rng.choice(idx, min(4000, len(idx)), replace=False)
for Ht in (0.0, 0.3, 0.5, 1.0):
    conf = np.zeros((2, 2), int)
    for k in idx:
        r = rows[k][3]; T, obs = arena(r['mid'])
        az = T.height(r['ox'], r['oy']) + 1.6; bz = T.height(r['tx'], r['ty']) + Ht
        vis = not rock_block(obs, r['ox'], r['oy'], r['tx'], r['ty']) and not terrain_block(T, r['ox'], r['oy'], az, r['tx'], r['ty'], bz)
        conf[int(vis), int(S[k])] += 1
    print('loot Ht %.1f agreement %.4f conf %s' % (Ht, (conf[0, 0] + conf[1, 1]) / conf.sum(), conf.tolist()))
