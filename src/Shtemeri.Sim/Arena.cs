using Shtemeri.Api;

namespace Shtemeri.Sim;

/// <summary>The static map: a height grid (bilinear between grid points) and round rocks.</summary>
public sealed class Arena : IArena
{
    public double Size { get; }
    public int GridN { get; }
    /// <summary>Heights, row by row: height at (ix, iy) is Heights[iy * GridN + ix] (same layout as replay v1).</summary>
    public double[] Heights { get; }
    public IReadOnlyList<Obstacle> Obstacles => _obstacles;

    private readonly Obstacle[] _obstacles;
    private readonly double[] _ox, _oy, _or;
    private readonly double _cell;
    private readonly double _maxHeight;
    private readonly double _losStep;

    public Arena(double size, int gridN, double[] heights, IEnumerable<Obstacle> obstacles, double losStep = 0.5)
    {
        if (heights.Length != gridN * gridN) throw new ArgumentException("heights must have gridN*gridN values");
        Size = size; GridN = gridN; Heights = heights;
        _obstacles = obstacles.ToArray();
        _ox = _obstacles.Select(o => o.Center.X).ToArray();
        _oy = _obstacles.Select(o => o.Center.Y).ToArray();
        _or = _obstacles.Select(o => o.Radius).ToArray();
        _cell = size / (gridN - 1);
        _maxHeight = heights.Length > 0 ? heights.Max() : 0;
        _losStep = losStep;
    }

    public double HeightAt(Vec2 p) => Height(p.X, p.Y);

    public double Height(double x, double y)
    {
        double fx = x / _cell, fy = y / _cell, lim = GridN - 1 - 1e-9;
        if (fx < 0) fx = 0; else if (fx > lim) fx = lim;
        if (fy < 0) fy = 0; else if (fy > lim) fy = lim;
        int ix = (int)fx, iy = (int)fy;
        double tx = fx - ix, ty = fy - iy;
        int i = iy * GridN + ix;
        double a = Heights[i] * (1 - tx) + Heights[i + 1] * tx;
        double b = Heights[i + GridN] * (1 - tx) + Heights[i + GridN + 1] * tx;
        return a * (1 - ty) + b * ty;
    }

    /// <summary>Terrain gradient as the server computes it for the slope force: central differences of the
    /// interpolated height with half step <paramref name="h"/> (review F5: h = 0.5 fits server telemetry, the analytic
    /// gradient of the bilinear cell does not).</summary>
    public Vec2 Gradient(double x, double y, double h = 0.5)
    {
        double inv = 1.0 / (2 * h);
        return new Vec2((Height(x + h, y) - Height(x - h, y)) * inv, (Height(x, y + h) - Height(x, y - h)) * inv);
    }

    public bool IsBlocked(Vec2 p)
    {
        if (p.X < 0 || p.Y < 0 || p.X > Size || p.Y > Size) return true;
        return InsideRock(p.X, p.Y, 0) >= 0;
    }

    /// <summary>Index of the rock whose disk (grown by <paramref name="pad"/>) contains the point, or -1.</summary>
    public int InsideRock(double x, double y, double pad)
    {
        for (int k = 0; k < _ox.Length; k++)
        {
            double dx = x - _ox[k], dy = y - _oy[k], r = _or[k] + pad;
            if (dx * dx + dy * dy < r * r) return k;
        }
        return -1;
    }

    public bool HasLineOfSight(Vec2 from, Vec2 to) =>
        LineOfSight(from.X, from.Y, Height(from.X, from.Y) + SimRules.Season.EyeHeight,
                    to.X, to.Y, Height(to.X, to.Y) + SimRules.Season.TargetHeight);

    /// <summary>True when the straight 3D segment is not cut by a rock (infinitely tall disk) or by the terrain.</summary>
    public bool LineOfSight(double ax, double ay, double az, double bx, double by, double bz)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        for (int k = 0; k < _ox.Length; k++)
        {
            double t = l2 > 0 ? ((_ox[k] - ax) * dx + (_oy[k] - ay) * dy) / l2 : 0;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double px = ax + t * dx - _ox[k], py = ay + t * dy - _oy[k];
            if (px * px + py * py < _or[k] * _or[k]) return false;
        }
        if (az > _maxHeight && bz > _maxHeight) return true;
        // n = ceil(L / step) intervals, terrain sampled at k/n, k = 1..n-1 (review F8)
        double len = Math.Sqrt(l2);
        int n = (int)Math.Ceiling(len / _losStep);
        double inv = n > 0 ? 1.0 / n : 0;
        for (int k = 1; k < n; k++)
        {
            double f = k * inv;
            double z = az + (bz - az) * f;
            if (z > _maxHeight) continue;
            if (Height(ax + dx * f, ay + dy * f) > z) return false;
        }
        return true;
    }

    /// <summary>
    /// A generated arena, shaped after the server's arenas (review F11, 10 404 ranked arenas): terrain is a sum of
    /// Gaussian bumps shifted to start at 0 and cut to [0, 8] (not rescaled); 8 to 14 rocks (uniform), radius 1.2-3.0,
    /// every rock edge at least 2.5 m from the walls and from other rocks and at least 6 m from each fleet's spawn
    /// centre (<paramref name="spawnCentres"/>; no other excluded band).
    /// </summary>
    public static Arena Generate(Rng rng, SimRules rules, IReadOnlyList<Vec2>? spawnCentres = null)
    {
        int n = 101; double size = rules.ArenaSize;
        var h = new double[n * n];
        int bumps = 15 + rng.Next(7);
        var bx = new double[bumps]; var by = new double[bumps]; var bs = new double[bumps]; var ba = new double[bumps];
        for (int b = 0; b < bumps; b++) { bx[b] = rng.Range(0, size); by[b] = rng.Range(0, size); bs[b] = rng.Range(5.2, 13.3); ba[b] = rng.Range(1.7, 4.5); }
        double min = double.MaxValue;
        for (int iy = 0; iy < n; iy++)
            for (int ix = 0; ix < n; ix++)
            {
                double x = ix * size / (n - 1), y = iy * size / (n - 1), v = 0;
                for (int b = 0; b < bumps; b++)
                {
                    double ddx = x - bx[b], ddy = y - by[b];
                    v += ba[b] * Math.Exp(-(ddx * ddx + ddy * ddy) / (2 * bs[b] * bs[b]));
                }
                h[iy * n + ix] = v; min = Math.Min(min, v);
            }
        double offset = min + rng.Range(0.05, 0.10);
        for (int i = 0; i < h.Length; i++) h[i] = Math.Round(Math.Clamp(h[i] - offset, 0, 8), 2);

        const double Gap = 2.5, SpawnClear = 6;
        var rocks = new List<Obstacle>();
        int count = 8 + rng.Next(7);
        for (int tries = 0; rocks.Count < count && tries < 5000; tries++)
        {
            double r = Math.Round(rng.Range(1.2, 3.0), 2);
            var c = new Vec2(Math.Round(rng.Range(r + Gap, size - r - Gap), 2), Math.Round(rng.Range(r + Gap, size - r - Gap), 2));
            if (rocks.Any(o => o.Center.DistanceTo(c) < o.Radius + r + Gap)) continue;
            if (spawnCentres != null && spawnCentres.Any(sc => sc.DistanceTo(c) - r < SpawnClear)) continue;
            rocks.Add(new Obstacle(c, r));
        }
        return new Arena(size, n, h, rocks, rules.LosStep);
    }
}

/// <summary>The arena as a fleet sees it: same answers, but every call is charged to the instruction budget.</summary>
internal sealed class MeteredArena : IArena
{
    private readonly Arena _a;
    private readonly SimRules _r;
    public MeteredArena(Arena a, SimRules r) { _a = a; _r = r; }
    public double Size => _a.Size;
    public IReadOnlyList<Obstacle> Obstacles => _a.Obstacles;
    public double HeightAt(Vec2 point) { Runtime.Meter.Charge(_r.CostHeightAt); return _a.HeightAt(point); }
    public bool IsBlocked(Vec2 point) { Runtime.Meter.Charge(1); return _a.IsBlocked(point); }
    public bool HasLineOfSight(Vec2 from, Vec2 to) { Runtime.Meter.Charge(_r.CostLineOfSight); return _a.HasLineOfSight(from, to); }
}
