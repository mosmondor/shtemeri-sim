"""Regression checks of the local engine against server rules measured on server replays (METHODOLOGY.md, section 7).

Replays are read one at a time (streamed; with --workers N, N replays in memory at once).

  python regression.py DIR [--server DIR] [--tel DIR] [--workers N]
      DIR       local replays (*.json.gz / *.json) written with --out-dir
      --server  folder with the server replays a --replay run was set up from (checks that no box is generated)
      --tel     folder with local telemetry (--telemetry) of the same run (collision event order, slope gradient)
  python regression.py --determinism EXE FLEETS [ARENAS_DIR] [REPLAY_FILE]
      plays the same seeds with 1 and 2 threads, twice, and compares md5 of the replays
      (generated setups, --arenas and --replay; one simulator process at a time)

Checks on replays (each line prints counts; "viol" must be 0):
  spawn-pickup   a spawned box picked up in the tick it appears
  spacing        a spawned box closer than 3.98 m to a box on the field (generated loot only)
  kinds          kind shares of initial + spawned boxes, chi^2 against 0.45 / 0.30 / 0.25 (generated loot only)
  splash-first   a direct rocket hit without the same rocket's splash on the victim before it
  after-lethal   hits on a shtemer whose health is already clearly below 0 in that tick
                 (except the direct hit of a rocket after its own lethal splash)
  zone-cause     a "zone" death whose hits in that tick were lethal on their own
  killer         a weapon death whose "by" is not the shooter of the lethal hit
  death-order    hits after a death in the same tick; deaths not in body-index order; weapon deaths after pickups;
                 zone deaths before pickups; a pickup by a shtemer that dies from a weapon in that tick
  box-ids        same tick: weapon drop id < spawned box id < zone drop id
  cap            spawned boxes > min(3, 16 - boxes after pickups and weapon drops)
  same-tick      fleets eliminated together: ranked by fleet health at the start of the tick
  hits           result hits == direct (non-splash) hits on other fleets
  replay-boxes   a spawned box that is not one of the server's (--replay runs, with --server)
  muzzle         first-frame projectile position, horizontal muzzle model vs the centre model (by pitch)
  arena          generated arenas: 8-14 rocks, edges >= 2.5 m from walls and rocks, >= 6 m from spawn centres,
                 terrain in [0, 8] touching 0, 10 initial boxes 8 m apart, >= 3 m from walls, >= 5.15 m from shtemers
"""
import gzip, hashlib, json, math, os, subprocess, sys, tempfile, shutil
from collections import Counter, defaultdict
from multiprocessing import Pool

SPAWN_EVERY, SPAWN_COUNT, MAX_BOXES = 200, 3, 16
KIND_P = {1: 0.45, 2: 0.30, 3: 0.25}


def load(path):
    raw = open(path, 'rb').read()
    if raw[:2] == b'\x1f\x8b':
        raw = gzip.decompress(raw)
    return json.loads(raw)


def height_fn(arena):
    n = arena['gridN']; cell = arena['size'] / (n - 1); h = arena['heights']; lim = n - 1 - 1e-9

    def H(x, y):
        fx = min(max(x / cell, 0), lim); fy = min(max(y / cell, 0), lim)
        ix, iy = int(fx), int(fy); tx, ty = fx - ix, fy - iy; i = iy * n + ix
        a = h[i] * (1 - tx) + h[i + 1] * tx
        b = h[i + n] * (1 - tx) + h[i + n + 1] * tx
        return a * (1 - ty) + b * ty

    def g_cell(x, y):
        fx = min(max(x / cell, 0), lim); fy = min(max(y / cell, 0), lim)
        ix, iy = int(fx), int(fy); tx, ty = fx - ix, fy - iy; i = iy * n + ix
        h00, h10, h01, h11 = h[i], h[i + 1], h[i + n], h[i + n + 1]
        return (((h10 - h00) * (1 - ty) + (h11 - h01) * ty) / cell, ((h01 - h00) * (1 - tx) + (h11 - h10) * tx) / cell)

    def g_cd(x, y, s=0.5):
        return ((H(x + s, y) - H(x - s, y)) / (2 * s), (H(x, y + s) - H(x, y - s)) / (2 * s))
    return H, g_cell, g_cd


def zone(stages, t):
    period = 450; k, r = divmod(t, period); dmg = 0.3 * min(6, k + 1)
    if k >= len(stages) - 1:
        c = stages[-1]; return c[0], c[1], c[2], dmg
    a, b = stages[k], stages[k + 1]
    if r < 200: return a[0], a[1], a[2], dmg
    f = (r - 200) / 250
    return a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f, dmg


class Match:
    def __init__(self, d):
        self.d = d; self.fs = d['rules']['fleetSize']; self.nf = len(d['fleets'])
        self.frames = {f['t']: f for f in d['frames']}
        self.ft = sorted(self.frames)
        self.ev = defaultdict(list)
        for e in d['events']: self.ev[e['t']].append(e)
        self.death = {}
        for e in d['events']:
            if e['k'] == 'death': self.death[e['s']] = e
        self.stages = d['zoneStages']

    def body(self, t, g):
        f = self.frames.get(t)
        if f is None or g >= len(f['s']): return None
        return f['s'][g]

    def end_pos(self, g, t):
        """Position of g at the end of tick t (it may have died in t): the frame, or extrapolated from two frames."""
        b = self.body(t, g)
        if b is not None: return b[0], b[1]
        a = t - 1 if (t - 1) in self.frames else t - 2
        pa = self.body(a, g)
        if pa is None: return None
        pb = self.body(a - 2, g)
        vx, vy = ((pa[0] - pb[0]) / 2, (pa[1] - pb[1]) / 2) if pb is not None else (0.0, 0.0)
        return pa[0] + vx * (t - a), pa[1] + vy * (t - a)

    def start_hp(self, g, t):
        """(health of g at the start of tick t, exact?). Odd t: the frame of t-1. Even t: frame t-2 advanced by t-1."""
        if (t - 1) in self.frames:
            b = self.body(t - 1, g); return (b[3] if b else 0.0), True
        b = self.body(t - 2, g)
        if b is None: return 0.0, True
        if g in self.death and self.death[g]['t'] == t - 1: return 0.0, True
        hp = b[3]
        for e in self.ev.get(t - 1, ()):
            if e['k'] == 'hit' and e['s'] == g: hp -= e['d']
            if e['k'] == 'pickup' and e['s'] == g and e['loot'] == 3: hp = min(250.0, hp + 50)
        p = self.end_pos(g, t - 1)
        cx, cy, r, dmg = zone(self.stages, t - 1)
        dz = math.hypot(p[0] - cx, p[1] - cy) - r
        if dz > 0: hp -= dmg
        return hp, abs(dz) > 0.15


def classify_boxes(M):
    """Where each box came from: initial, spawn (tick), wdrop / zdrop (tick), unknown. From frames and deaths."""
    seen = {}; origin = {}
    prev_pos = {}
    for t in M.ft:
        f = M.frames[t]
        new = [l for l in f['l'] if l[0] not in seen]
        for l in f['l']: seen.setdefault(l[0], (t, l[1], l[2], l[3]))
        if t == 0:
            for l in new: origin[l[0]] = ('initial', 0)
        else:
            deaths = [e for tt in (t - 1, t) for e in M.ev.get(tt, ()) if e['k'] == 'death']
            pairs = []
            for e in deaths:
                p = prev_pos.get(e['s'])
                if p is None: continue
                for l in new: pairs.append((math.hypot(l[2] - p[0], l[3] - p[1]), e, l))
            pairs.sort(key=lambda x: x[0])
            used_d, used_l = set(), set()
            for dist, e, l in pairs:
                if dist > 2.5 or id(e) in used_d or l[0] in used_l: continue
                used_d.add(id(e)); used_l.add(l[0])
                origin[l[0]] = ('zdrop' if e['cause'] == 'zone' else 'wdrop', e['t'])
            for l in new:
                if l[0] in used_l: continue
                origin[l[0]] = ('spawn', t) if t % SPAWN_EVERY == 0 else ('unknown', t)
        for g, b in enumerate(f['s']):
            if b is not None: prev_pos[g] = (b[0], b[1])
    return seen, origin


def check(path, server_dir=None):
    d = load(path); M = Match(d); fs = M.fs
    sim = d.get('simulator', '')
    replay_mode = ' replay ' in sim + ' '
    generated_loot = not replay_mode
    R = Counter(); out = {'kinds': Counter(), 'notes': []}
    seen, origin = classify_boxes(M)
    R['games'] += 1; R['unknown boxes'] += sum(1 for o in origin.values() if o[0] == 'unknown')

    # boxes per spawn tick
    by_tick = defaultdict(lambda: defaultdict(list))
    for bid, (kind, t) in origin.items(): by_tick[t][kind].append(bid)
    for bid, (kind, t) in origin.items():
        if kind in ('initial', 'spawn') and generated_loot: out['kinds'][seen[bid][1]] += 1
    pickups = defaultdict(list)
    for t, L in M.ev.items():
        for e in L:
            if e['k'] == 'pickup': pickups[t].append(e)
    max_seen_before = {}
    running = -1
    for t in M.ft:
        max_seen_before[t] = running
        running = max([running] + [l[0] for l in M.frames[t]['l']])

    for t in range(SPAWN_EVERY, d['totalTicks'], SPAWN_EVERY):   # every spawn tick, also those with no box
        sp = sorted(by_tick[t]['spawn']); zd = by_tick[t]['zdrop']; wd = by_tick[t]['wdrop']
        f = M.frames.get(t)
        if f is None: continue
        R['spawn ticks'] += 1; R['spawned boxes'] += len(sp)
        out.setdefault('per_T', Counter())[t] += len(sp)
        # cap
        room = MAX_BOXES - (len(f['l']) - len(sp) - len(zd))
        R['cap viol'] += len(sp) > max(0, min(SPAWN_COUNT, room))
        # spacing (generated loot)
        if generated_loot:
            field = [l for l in f['l'] if l[0] not in set(zd)]
            for b in sp:
                p = seen[b]
                dmin = min((math.hypot(l[2] - p[2], l[3] - p[3]) for l in field if l[0] != b), default=99)
                R['spacing checked'] += 1; R['spacing viol'] += dmin < 3.98
        # ids within the tick
        same_w = [x for x in wd if origin[x][1] == t]
        same_z = [x for x in zd if origin[x][1] == t]
        if sp and (same_w or same_z):
            R['box-ids ticks'] += 1
            R['box-ids viol'] += (bool(same_w) and max(same_w) > min(sp)) or (bool(same_z) and min(same_z) < max(sp))
        # spawned box picked up in its own tick: an unseen id above anything drops could explain
        n_drops = sum(1 for tt in (t - 1, t) for e in M.ev.get(tt, ()) if e['k'] == 'death')
        for e in pickups.get(t, ()):
            if e['id'] not in seen and e['id'] > max_seen_before.get(t, -1) + n_drops:
                R['spawn-pickup viol'] += 1

    # events tick by tick
    for t, L in M.ev.items():
        died = False; picked = False; w_after_pick = False; last_w = -1; last_z = -1; zone_seen = False
        boom_by = set(); splash_on = set()
        wdead = {e['s'] for e in L if e['k'] == 'death' and e['cause'] != 'zone'}
        for e in L:
            k = e['k']
            if k == 'boom': boom_by.add(e['by'])
            if k in ('hit', 'boom') and died: R['death-order viol'] += 1
            if k == 'hit':
                if e['w'] == 1 and e['splash']: splash_on.add((e['by'], e['s']))
                if e['w'] == 1 and not e['splash']:
                    R['direct rocket hits'] += 1
                    R['splash-first viol'] += e['by'] not in boom_by or (e['by'], e['s']) not in splash_on
            if k == 'pickup':
                picked = True
                if e['s'] in wdead: R['pickup by dying viol'] += 1
                if zone_seen: R['death-order viol'] += 1
            if k == 'death':
                died = True; R['deaths ' + e['cause']] += 1
                if e['cause'] == 'zone':
                    zone_seen = True
                    if e['s'] < last_z: R['death-order viol'] += 1
                    last_z = e['s']
                else:
                    if picked or zone_seen or e['s'] < last_w: R['death-order viol'] += 1
                    last_w = e['s']

    # hits after the lethal one, cause and killer, on death ticks
    for g, de in M.death.items():
        t = de['t']
        hits = [e for e in M.ev.get(t, ()) if e['k'] == 'hit' and e['s'] == g]
        hp0, exact = M.start_hp(g, t)
        key = 'exact' if exact else 'recon'
        rem = hp0; lethal = None
        for i, e in enumerate(hits):
            before = rem; rem -= e['d']
            if lethal is None and rem <= 0.05: lethal = i
            if lethal is not None and i > lethal and before < -0.15:
                lh = hits[lethal]
                if lh['w'] == 1 and lh['splash'] and e['w'] == 1 and not e['splash'] and e['by'] == lh['by']:
                    R['after-lethal own direct'] += 1
                else:
                    R[f'after-lethal viol ({key})'] += 1
        R[f'deaths checked ({key})'] += 1
        if de['cause'] == 'zone':
            if lethal is not None and rem < -0.15: R[f'zone-cause viol ({key})'] += 1
        elif lethal is not None and (hp0 - sum(h['d'] for h in hits[:lethal])) > 0.1 \
                and (hp0 - sum(h['d'] for h in hits[:lethal + 1])) < -0.1:   # lethal hit clear of the 0.1 rounding
            R['killer checked'] += 1
            R[f'killer viol ({key})'] += de['by'] != hits[lethal]['by']

    # same-tick eliminations
    elim = defaultdict(list)
    for e in d['events']:
        if e['k'] == 'eliminated': elim[e['t']].append(e)
    for t, G in elim.items():
        if len(G) < 2: continue
        hp = {}
        for e in G:
            s = 0.0
            for g in range(e['f'] * fs, e['f'] * fs + fs):
                if g in M.death and M.death[g]['t'] >= t: s += max(0.0, M.start_hp(g, t)[0])
            hp[e['f']] = s
        for a in G:
            for b in G:
                if a['place'] >= b['place']: continue   # a placed better than b
                diff = hp[a['f']] - hp[b['f']]
                if abs(diff) <= 0.3: R['same-tick close'] += 1
                elif diff > 0: R['same-tick ok'] += 1
                else: R['same-tick viol'] += 1

    # hits definition
    hits_by = Counter()
    for e in d['events']:
        if e['k'] == 'hit' and not e['splash'] and e['by'] // fs != e['s'] // fs: hits_by[e['by'] // fs] += 1
    for st in d['result']['stats']:
        R['hits fleets'] += 1; R['hits viol'] += st['hits'] != hits_by[st['fleet']]

    # replay mode: every spawned box must be one of the server's
    if replay_mode and server_dir:
        name = sim.split(' replay ')[-1].strip()
        sp_srv = server_spawns(os.path.join(server_dir, name))
        for bid, (kind, t) in origin.items():
            if kind != 'spawn': continue
            p = seen[bid]
            R['replay-boxes checked'] += 1
            R['replay-boxes viol'] += not any(abs(q[0] - p[2]) < 0.011 and abs(q[1] - p[3]) < 0.011 for q in sp_srv.get(t, ()))
        R['replay-boxes local'] += sum(1 for o in origin.values() if o[0] == 'spawn')
        R['replay-boxes server'] += sum(len(v) for v in sp_srv.values())

    muzzle_check(d, M, R, out)
    if sim.endswith('generated'): arena_checks(d, M, R)
    out['R'] = R
    return out


def muzzle_check(d, M, R, out):
    """Projectiles in their first frame (shot in an odd tick t, frame t+1 = two moves), against two muzzle models:
    horizontal (1.1 m towards the aim at ground + 1.2, flying to (aim, ground(aim) + 1.0)) and the centre model
    (from (centre, ground + 1.2) towards the same point, 1.1 m along that line). Errors in 3D, by pitch band."""
    H = height_fn(d['arena'])[0]
    seen = set()
    for t in M.ft:
        f = M.frames[t]
        if t % 2 == 0 and (t - 1) in M.ev and (t - 2) in M.frames:
            fired = [e for e in M.ev[t - 1] if e['k'] == 'fire']
            new = [p for p in f['p'] if p[0] not in seen]
            for e in fired:
                c = M.body(t - 2, e['s'])
                cand = [p for p in new if p[5] == e['s'] and p[1] == e['w']]
                if c is None or len(cand) != 1: continue
                p = cand[0]; step = 2.0 if e['w'] == 0 else 0.9
                cx, cy = c[0], c[1]; tx, ty = e['x'], e['y']; hz = H(cx, cy) + 1.2; tz = H(tx, ty) + 1.0
                hl = math.hypot(tx - cx, ty - cy)
                if hl < 3 + 2 * step: continue
                ux, uy = (tx - cx) / hl, (ty - cy) / hl
                errs = []
                for (sx, sy, off) in ((cx + 1.1 * ux, cy + 1.1 * uy, 0.0), (cx, cy, 1.1)):
                    dx, dy, dz = tx - sx, ty - sy, tz - hz; L = math.sqrt(dx * dx + dy * dy + dz * dz)
                    k = off + 2 * step
                    errs.append(math.dist((sx + dx / L * k, sy + dy / L * k, hz + dz / L * k), (p[2], p[3], p[4])))
                pitch = abs(math.atan2(tz - hz, hl))
                band = 0 if pitch < 0.03 else 1 if pitch < 0.08 else 2 if pitch < 0.2 else 3
                out.setdefault('muzzle', []).append((band, errs[0], errs[1]))
                R['muzzle viol (horizontal model > 0.03 m)'] += errs[0] > 0.03
        seen.update(p[0] for p in f['p'])


_srv_cache = {}


def server_spawns(path):
    if path not in _srv_cache:
        M = Match(load(path)); seen, origin = classify_boxes(M); res = defaultdict(list)
        for bid, (kind, t) in origin.items():
            if kind == 'spawn': res[t].append((seen[bid][2], seen[bid][3]))
        _srv_cache[path] = dict(res)
    return _srv_cache[path]


def arena_checks(d, M, R):
    a = d['arena']; size = a['size']; rocks = a['obstacles']; fs = M.fs
    R['arenas'] += 1
    R['arena viol rock count'] += not (8 <= len(rocks) <= 14)
    R['arena rocks'] += len(rocks)
    f0 = M.frames[0]['s']
    centres = []
    for k in range(M.nf):
        pts = [f0[g] for g in range(k * fs, k * fs + fs) if f0[g] is not None]
        centres.append((sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts)))
    for i, (x, y, r) in enumerate(rocks):
        R['arena viol rock-wall'] += min(x, y, size - x, size - y) - r < 2.5 - 0.011
        for (x2, y2, r2) in rocks[i + 1:]:
            R['arena viol rock-rock'] += math.hypot(x - x2, y - y2) - r - r2 < 2.5 - 0.02
        R['arena viol rock-spawn'] += any(math.hypot(x - cx, y - cy) - r < 6 - 0.1 for cx, cy in centres)
    h = a['heights']
    R['arena viol terrain'] += min(h) != 0 or max(h) > 8
    R['arena max==8'] += max(h) >= 8
    R['arena nodes at 0'] += sum(1 for v in h if v <= 0)
    boxes = M.frames[0]['l']
    R['arena viol box count'] += len(boxes) != 10
    bodies = [b for b in f0 if b is not None]
    for i, l in enumerate(boxes):
        R['arena viol box-wall'] += min(l[2], l[3], size - l[2], size - l[3]) < 3 - 0.011
        R['arena viol box-body'] += any(math.hypot(l[2] - b[0], l[3] - b[1]) < 5.15 - 0.1 for b in bodies)
        for m in boxes[i + 1:]:
            R['arena viol box-box'] += math.hypot(l[2] - m[2], l[3] - m[3]) < 8 - 0.02


def telemetry_checks(tel_dir, rep_dir):
    R = Counter(); num = {'cell': 0.0, 'cd': 0.0}; den = {'cell': 0.0, 'cd': 0.0}; res = {'cell': [], 'cd': []}
    for fn in sorted(os.listdir(tel_dir)):
        if not fn.endswith('.jsonl.gz'): continue
        seed = fn.split('.')[0]
        rp = next((os.path.join(rep_dir, seed + x) for x in ('.json.gz', '.json') if os.path.exists(os.path.join(rep_dir, seed + x))), None)
        grad = height_fn(load(rp)['arena']) if rp else None
        prev = {}
        with gzip.open(os.path.join(tel_dir, fn), 'rt', encoding='utf-8') as fh:
            next(fh)
            for ln in fh:
                o = json.loads(ln)
                if 't' not in o: continue
                cur = {}
                for s in o['i']:
                    cur[s['idx']] = s
                    kinds = [c[0] for c in s.get('coll', ())]
                    if 2 in kinds and (0 in kinds or 1 in kinds):
                        R['coll mixed lists'] += 1
                        R['coll order viol'] += max(i for i, k in enumerate(kinds) if k != 2) > min(i for i, k in enumerate(kinds) if k == 2)
                    p = prev.get(s['idx'])
                    if grad is None or p is None or 'coll' in s or p['_t'] != o['t'] - 1: continue
                    th = p['cmd'].get('thrust', [0, 0]); v = p['vel']; nv = s['vel']
                    if math.hypot(*th) < 1e-9 and math.hypot(*v) < 0.5: continue   # parking
                    y = [12 * th[k] - 1.5 * v[k] - (nv[k] - v[k]) / 0.05 for k in (0, 1)]
                    for name, gf in (('cell', grad[1]), ('cd', grad[2])):
                        g = gf(*p['pos'])
                        if math.hypot(*g) < 0.05: continue
                        num[name] += y[0] * g[0] + y[1] * g[1]; den[name] += g[0] ** 2 + g[1] ** 2
                        res[name].append(math.hypot(y[0] - 6 * g[0], y[1] - 6 * g[1]))
                for s in o['i']: s['_t'] = o['t']
                prev.update(cur)
    for name in ('cell', 'cd'):
        if den[name] > 0:
            r = sorted(res[name])
            print(f'  slope fit with {name:4s} gradient: coefficient {num[name] / den[name]:.4f} (n {len(r)}), '
                  f'|residual| median {r[len(r) // 2]:.4f} p99 {r[int(len(r) * 0.99)]:.4f}')
    for k, v in sorted(R.items()): print(f'  {k}: {v}')


def md5_of(path):
    raw = open(path, 'rb').read()
    if raw[:2] == b'\x1f\x8b': raw = gzip.decompress(raw)
    return hashlib.md5(raw).hexdigest()


def determinism(exe, fleets, arenas=None, replay=None):
    modes = [('generated', [])]
    if arenas: modes.append(('arenas', ['--arenas', arenas]))
    if replay: modes.append(('replay', ['--replay', replay]))
    tmp = tempfile.mkdtemp(prefix='shtemeri-det-')
    ok = True
    try:
        for name, extra in modes:
            sums = []
            for run, threads in enumerate((1, 2, 2)):
                od = os.path.join(tmp, f'{name}{run}')
                subprocess.run([exe, '--fleets', fleets, '--seed', '7', '--games', '3', '--threads', str(threads), '--quiet',
                                '--out-dir', od] + extra, check=True, capture_output=True)
                sums.append(sorted((f, md5_of(os.path.join(od, f))) for f in os.listdir(od)))
            same = sums[0] == sums[1] == sums[2]
            ok &= same
            print(f'  determinism {name}: {"same md5" if same else "DIFFERENT"} (3 games x 1 thread, 2 threads, 2 threads)')
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    return ok


def chi2_kinds(c):
    n = sum(c.values())
    if n == 0: return None
    x = sum((c[k] - n * p) ** 2 / (n * p) for k, p in KIND_P.items())
    return n, {k: round(c[k] / n, 4) for k in KIND_P}, x, math.exp(-x / 2)


def _check(args):
    try:
        return check(*args)
    except Exception as e:
        return {'error': f'{args[0]}: {e}'}


def main():
    a = sys.argv[1:]
    if a and a[0] == '--determinism':
        ok = determinism(*a[1:])
        sys.exit(0 if ok else 1)
    opts = {'--server': None, '--tel': None, '--workers': '2'}
    pos = []
    i = 0
    while i < len(a):
        if a[i] in opts: opts[a[i]] = a[i + 1]; i += 2
        else: pos.append(a[i]); i += 1
    files = []
    for root in pos:
        for dp, _, fns in os.walk(root):
            files += [os.path.join(dp, f) for f in sorted(fns) if f.endswith('.json.gz') or f.endswith('.json')]
    total = Counter(); kinds = Counter(); perT = Counter(); nT = Counter(); errors = []; muz = []
    with Pool(int(opts['--workers'])) as pool:
        for r in pool.imap_unordered(_check, [(f, opts['--server']) for f in files], chunksize=1):
            if 'error' in r: errors.append(r['error']); continue
            total.update(r['R']); kinds.update(r['kinds'])
            for t, n in r.get('per_T', {}).items(): perT[t] += n; nT[t] += 1
            muz += r.get('muzzle', [])
    print(f'replays: {len(files)}, errors {len(errors)}')
    for e in errors[:5]: print('  ', e)
    for k in sorted(total): print(f'  {k}: {total[k]}')
    ck = chi2_kinds(kinds)
    if ck:
        n, share, x, p = ck
        print(f'  kinds: n {n}, shares {share}, chi2 vs 0.45/0.30/0.25 = {x:.2f} (df 2, p {p:.3f})')
    late = {t: round(perT[t] / nT[t], 3) for t in (2200, 2400, 2600) if nT[t]}
    if late: print(f'  spawned per batch at T 2200/2400/2600: {late}')
    for band, name in enumerate(('|pitch| < 0.03', '0.03-0.08', '0.08-0.2', '> 0.2')):
        e = sorted(x[1:] for x in muz if x[0] == band)
        if e:
            h = sorted(x[0] for x in e); c = sorted(x[1] for x in e)
            print(f'  muzzle {name:14s} n {len(e):6d}  3D error median / p95: horizontal {h[len(h) // 2]:.3f} / '
                  f'{h[int(len(h) * 0.95)]:.3f}, centre {c[len(c) // 2]:.3f} / {c[int(len(c) * 0.95)]:.3f}')
    viol = sum(v for k, v in total.items() if 'viol' in k)
    print(f'  TOTAL violations: {viol}')
    if opts['--tel']:
        print('telemetry:')
        telemetry_checks(opts['--tel'], pos[0])


if __name__ == '__main__':
    main()
