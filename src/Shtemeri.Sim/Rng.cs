using Shtemeri.Api;

namespace Shtemeri.Sim;

/// <summary>Deterministic xoshiro256** generator. Same seed, same sequence, on every platform.</summary>
public sealed class Rng : IRandom
{
    private ulong _s0, _s1, _s2, _s3;

    public Rng(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix(ref x); _s1 = SplitMix(ref x); _s2 = SplitMix(ref x); _s3 = SplitMix(ref x);
    }

    /// <summary>A generator derived from a seed and a stream number (e.g. one per shtemer).</summary>
    public static Rng Derive(ulong seed, ulong stream) => new(seed ^ (0x9E3779B97F4A7C15UL * (stream + 1)));

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextULong()
    {
        ulong result = Rotl(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);
        return result;
    }

    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public int Next(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(NextDouble() * maxExclusive);

    public double Range(double min, double max) => min + (max - min) * NextDouble();

    /// <summary>Uniform point in a disk of the given radius, centred on zero.</summary>
    public Vec2 InDisk(double radius)
    {
        double r = radius * Math.Sqrt(NextDouble());
        double a = 2 * Math.PI * NextDouble();
        return new Vec2(r * Math.Cos(a), r * Math.Sin(a));
    }
}
