# Projectile kinematics from replay: per projectile id, its positions; relate to fire events.
import numpy as np
from common import *
rep = load_replay(f'{TEL}/27427.replay.json'); T = Terrain(rep['arena'])
fr = rep['frames']; frm = {f['t']: f for f in fr}
tracks = {}
for f in fr:
    for p in f.get('p', []):
        tracks.setdefault(p[0], []).append((f['t'], p[1], p[2], p[3], p[4], p[5]))
fires = [e for e in rep['events'] if e['k'] == 'fire']
print('n tracks', len(tracks), 'fires', len(fires))
# match: fire event at tick tf by shooter s -> first track of owner s appearing at tf (frame tf if even) or tf+1
ids = sorted(tracks)
shown = 0
for e in fires[:400]:
    cand = [i for i in ids if tracks[i][0][5] == e['s'] and tracks[i][0][1] == e['w'] and tracks[i][0][0] in (e['t'], e['t'] + 1, e['t'] + 2)]
    if not cand: continue
    tr = tracks[cand[0]]
    sh = frm.get(e['t'] - 1 if e['t'] % 2 == 1 else e['t'] - 2)
    if sh is None: continue
    if shown < 6 and (e['t'] % 2 == 0):
        # shooter position at start of fire tick = frame t-1 (odd, not stored) ~ use frame t (after move) and t-2
        s_now = frm[e['t']]['s'][e['s']]; s_prev = frm[e['t'] - 2]['s'][e['s']] if e['t'] >= 2 else None
        print('fire t=%d s=%d w=%d target=(%.2f,%.2f) shooter@t=%s @t-2=%s' % (e['t'], e['s'], e['w'], e['x'], e['y'], s_now[:2], s_prev[:2] if s_prev else None))
        for q in tr[:6]: print('    ', q)
        shown += 1
