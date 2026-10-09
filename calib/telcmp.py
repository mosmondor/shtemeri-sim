"""Tick-by-tick comparison of a server telemetry file with a local one (same setup).
python telcmp.py server.jsonl.gz local.jsonl.gz [max_tick] [fields...]"""
import sys, gzip, json, math
def load(p):
    out = {}
    for ln in gzip.open(p, 'rt', encoding='utf-8'):
        o = json.loads(ln)
        if 't' in o and 'i' in o:
            for s in o['i']: out[(o['t'], s['idx'])] = s
    return out
S, L = load(sys.argv[1]), load(sys.argv[2])
T = int(sys.argv[3]) if len(sys.argv) > 3 else 80
def close(a, b, tol):
    if isinstance(a, (int, float)) and isinstance(b, (int, float)): return abs(a - b) <= tol
    if isinstance(a, list) and isinstance(b, list): return len(a) == len(b) and all(close(x, y, tol) for x, y in zip(a, b))
    if isinstance(a, dict) and isinstance(b, dict): return set(a) == set(b) and all(close(a[k], b[k], tol) for k in a)
    return a == b
first = {}
for t in range(T):
    for i in range(4):
        s, l = S.get((t, i)), L.get((t, i))
        if s is None or l is None: continue
        checks = {
            'pos': (s['pos'], l['pos'], 0.03), 'vel': (s['vel'], l['vel'], 0.03), 'look': (s['look'], l['look'], 0.02),
            'hp': (s['hp'], l['hp'], 0.05), 'en': (s['en'], l['en'], 0.05), 'ammo': (s['ammo'], l['ammo'], 0), 'cd': (s['cd'], l['cd'], 0),
            'blip_ids': ([b[0] for b in s.get('blips', [])], [b[0] for b in l.get('blips', [])], 0),
            'blip_sizes': (sorted(b[3] for b in s.get('blips', [])), sorted(b[3] for b in l.get('blips', [])), 0),
            'contacts': ([(c['blip'], c['kind']) for c in s.get('contacts', [])], [(c['blip'], c['kind']) for c in l.get('contacts', [])], 0),
            'contact_pos': ([c['pos'] for c in s.get('contacts', [])], [c['pos'] for c in l.get('contacts', [])], 0.03),
            'msgIn_types': ([(m[0], m[1]) for m in s.get('msgIn', [])], [(m[0], m[1]) for m in l.get('msgIn', [])], 0),
            'hits': (s.get('hits', []), l.get('hits', []), 0.05), 'coll': ([c[0] for c in s.get('coll', [])], [c[0] for c in l.get('coll', [])], 0),
            'thrust': (s['cmd']['thrust'], l['cmd']['thrust'], 0.03), 'cmd_look': (s['cmd']['look'], l['cmd']['look'], 0.03),
            'zoom': (s['cmd'].get('zoom', []), l['cmd'].get('zoom', []), 0), 'fire_w': ([f[0] for f in s['cmd'].get('fire', [])], [f[0] for f in l['cmd'].get('fire', [])], 0),
            'msgOut_types': ([(m[0], m[1]) for m in s['cmd'].get('msgOut', [])], [(m[0], m[1]) for m in l['cmd'].get('msgOut', [])], 0),
            'log': (s.get('log', []), l.get('log', []), 0),
        }
        for k, (a, b, tol) in checks.items():
            if k not in first and not close(a, b, tol): first[k] = (t, i, a, b)
for k, (t, i, a, b) in sorted(first.items(), key=lambda x: x[1][0]):
    print('%-13s first differs at t=%d #%d  server=%s  local=%s' % (k, t, i, str(a)[:150], str(b)[:150]))
