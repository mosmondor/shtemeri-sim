// Copied verbatim from the season guide (chapter 9, API). Do not edit: fleets compile against this.
using System.Globalization;

namespace Shtemeri.Api;

/// <summary>2D vector in arena coordinates (metres). X grows east, Y grows north. Angles are radians, 0 = +X, counter-clockwise.</summary>
public readonly struct Vec2 : IEquatable<Vec2>
{
    public readonly double X;
    public readonly double Y;

    public Vec2(double x, double y) { X = x; Y = y; }

    public static readonly Vec2 Zero = new(0, 0);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;

    /// <summary>Angle of this vector in radians (-π..π].</summary>
    public double Angle => Math.Atan2(Y, X);

    /// <summary>Unit vector in the same direction; Zero if this vector is (almost) zero.</summary>
    public Vec2 Normalized()
    {
        double len = Length;
        return len < 1e-9 ? Zero : new Vec2(X / len, Y / len);
    }

    /// <summary>Same direction, length limited to <paramref name="max"/>.</summary>
    public Vec2 ClampLength(double max)
    {
        double len = Length;
        return len <= max || len < 1e-9 ? this : new Vec2(X / len * max, Y / len * max);
    }

    public double DistanceTo(Vec2 other) => (other - this).Length;
    public double Dot(Vec2 other) => X * other.X + Y * other.Y;
    public double Cross(Vec2 other) => X * other.Y - Y * other.X;

    /// <summary>This vector rotated counter-clockwise by <paramref name="radians"/>.</summary>
    public Vec2 Rotated(double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new Vec2(X * c - Y * s, X * s + Y * c);
    }

    public static Vec2 FromAngle(double radians, double length = 1.0) =>
        new(Math.Cos(radians) * length, Math.Sin(radians) * length);

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    /// <summary>Normalizes an angle to (-π..π].</summary>
    public static double NormalizeAngle(double radians)
    {
        radians %= 2 * Math.PI;
        if (radians > Math.PI) radians -= 2 * Math.PI;
        else if (radians <= -Math.PI) radians += 2 * Math.PI;
        return radians;
    }

    /// <summary>Signed shortest difference to - from, in (-π..π].</summary>
    public static double AngleDiff(double from, double to) => NormalizeAngle(to - from);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public static Vec2 operator *(double k, Vec2 a) => new(a.X * k, a.Y * k);
    public static Vec2 operator /(Vec2 a, double k) => new(a.X / k, a.Y / k);
    public static bool operator ==(Vec2 a, Vec2 b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Vec2 a, Vec2 b) => !(a == b);

    public bool Equals(Vec2 other) => this == other;
    public override bool Equals(object? obj) => obj is Vec2 v && this == v;
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:0.0}, {1:0.0})", X, Y);
}
