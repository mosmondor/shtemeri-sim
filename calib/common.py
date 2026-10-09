"""Shared loaders for calibration scripts (telemetry v1 + replay v1)."""
import gzip, json, os
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
# Point these at your own data (METHODOLOGY.md, "Data"): environment variables, or the defaults below.
TEL = os.environ.get('SHTEMERI_TELEMETRY', os.path.join(HERE, 'telemetry'))
REPLAYS = os.environ.get('SHTEMERI_REPLAYS', os.path.join(HERE, '..', 'data', 'replays'))


def load_replay(path):
    if path.endswith('.gz'):
        return json.loads(gzip.decompress(open(path, 'rb').read()))
    return json.load(open(path, encoding='utf-8'))


def load_ranked(mid):
    return load_replay(os.path.join(REPLAYS, f'{mid}.json.gz'))


def load_telemetry(path):
    lines = gzip.open(path, 'rt', encoding='utf-8').read().splitlines()
    header = json.loads(lines[0])
    ticks, result = [], None
    for ln in lines[1:]:
        o = json.loads(ln)
        if o.get('type') == 'result':
            result = o
        elif 't' in o:
            ticks.append(o)
    return header, ticks, result


class Terrain:
    def __init__(self, arena):
        self.n = arena['gridN']
        self.size = arena['size']
        self.h = np.array(arena['heights'], float).reshape(self.n, self.n)  # [iy, ix]
        self.cell = self.size / (self.n - 1)

    def height(self, x, y):
        fx = min(max(x / self.cell, 0), self.n - 1 - 1e-9)
        fy = min(max(y / self.cell, 0), self.n - 1 - 1e-9)
        ix, iy = int(fx), int(fy)
        tx, ty = fx - ix, fy - iy
        h = self.h
        a = h[iy, ix] * (1 - tx) + h[iy, ix + 1] * tx
        b = h[iy + 1, ix] * (1 - tx) + h[iy + 1, ix + 1] * tx
        return a * (1 - ty) + b * ty

    def grad(self, x, y, eps=None):
        """Analytic gradient of the bilinear patch."""
        fx = min(max(x / self.cell, 0), self.n - 1 - 1e-9)
        fy = min(max(y / self.cell, 0), self.n - 1 - 1e-9)
        ix, iy = int(fx), int(fy)
        tx, ty = fx - ix, fy - iy
        h = self.h
        h00, h10, h01, h11 = h[iy, ix], h[iy, ix + 1], h[iy + 1, ix], h[iy + 1, ix + 1]
        gx = ((h10 - h00) * (1 - ty) + (h11 - h01) * ty) / self.cell
        gy = ((h01 - h00) * (1 - tx) + (h11 - h10) * tx) / self.cell
        return gx, gy

    def grad_cd(self, x, y, h=0.5):
        """Central difference of the interpolated height with half step h (the server's slope gradient, h = 0.5)."""
        return ((self.height(x + h, y) - self.height(x - h, y)) / (2 * h),
                (self.height(x, y + h) - self.height(x, y - h)) / (2 * h))


def rep_for(mid):
    """Replay for a telemetry file: local test-match copy first, then the ranked archive."""
    p = os.path.join(TEL, f'{mid}.replay.json')
    if os.path.exists(p): return load_replay(p)
    p = os.path.join(REPLAYS, f'{mid}.json.gz')
    if os.path.exists(p): return load_replay(p)
    return None
