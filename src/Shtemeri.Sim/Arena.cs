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

    public Arena(double size, int gridN, double[] heights, IEnumerable<Obstacle> obstacles, double losStep = 0.25)
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

    /// <summary>Gradient of the bilinear patch under the point (metres of height per metre).</summary>
    public Vec2 Gradient(double x, double y)
    {
        double fx = x / _cell, fy = y / _cell, lim = GridN - 1 - 1e-9;
        if (fx < 0) fx = 0; else if (fx > lim) fx = lim;
        if (fy < 0) fy = 0; else if (fy > lim) fy = lim;
        int ix = (int)fx, iy = (int)fy;
        double tx = fx - ix, ty = fy - iy;
        int i = iy * GridN + ix;
        double h00 = Heights[i], h10 = Heights[i + 1], h01 = Heights[i + GridN], h11 = Heights[i + GridN + 1];
        double gx = ((h10 - h00) * (1 - ty) + (h11 - h01) * ty) / _cell;
        double gy = ((h01 - h00) * (1 - tx) + (h11 - h10) * tx) / _cell;
        return new Vec2(gx, gy);
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
        double len = Math.Sqrt(l2);
        int n = Math.Max(2, (int)(len / _losStep));
        double inv = 1.0 / n;
        for (int k = 1; k < n; k++)
        {
            double f = k * inv;
            double z = az + (bz - az) * f;
            if (z > _maxHeight) continue;
            if (Height(ax + dx * f, ay + dy * f) > z) return false;
        }
        return true;
    }

    /// <summary>A plain generated arena: smooth hills from a sum of Gaussian bumps (0..8 m) and 6 to 10 rocks.</summary>
    public static Arena Generate(Rng rng, SimRules rules)
    {
        int n = 101; double size = rules.ArenaSize;
        var h = new double[n * n];
        int bumps = 6 + rng.Next(6);
        var bx = new double[bumps]; var by = new double[bumps]; var bs = new double[bumps]; var ba = new double[bumps];
        for (int b = 0; b < bumps; b++) { bx[b] = rng.Range(0, size); by[b] = rng.Range(0, size); bs[b] = rng.Range(8, 22); ba[b] = rng.Range(1.5, 6); }
        double max = 0;
        for (int iy = 0; iy < n; iy++)
            for (int ix = 0; ix < n; ix++)
            {
                double x = ix * size / (n - 1), y = iy * size / (n - 1), v = 0;
                for (int b = 0; b < bumps; b++)
                {
                    double ddx = x - bx[b], ddy = y - by[b];
                    v += ba[b] * Math.Exp(-(ddx * ddx + ddy * ddy) / (2 * bs[b] * bs[b]));
                }
                h[iy * n + ix] = v; max = Math.Max(max, v);
            }
        double scale = max > 0 ? 8.0 / max : 0;
        for (int i = 0; i < h.Length; i++) h[i] = Math.Round(h[i] * scale, 2);
        var rocks = new List<Obstacle>();
        int count = 6 + rng.Next(5);
        for (int tries = 0; rocks.Count < count && tries < 500; tries++)
        {
            var c = new Vec2(rng.Range(5, size - 5), rng.Range(5, size - 5));
            double r = rng.Range(1.2, 3.0);
            if (Math.Abs(c.DistanceTo(new Vec2(size / 2, size / 2)) - rules.SpawnRingRadius) < r + 6) continue;   // keep the spawn ring free
            if (rocks.Any(o => o.Center.DistanceTo(c) < o.Radius + r + 3)) continue;
            rocks.Add(new Obstacle(new Vec2(Math.Round(c.X, 2), Math.Round(c.Y, 2)), Math.Round(r, 2)));
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
