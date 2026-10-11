namespace ZnakiAI.Core.Geometry;

/// <summary>Punkt / wektor 2D w jednostkach rysunku (metry).</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public static Vec2 operator *(double k, Vec2 a) => new(a.X * k, a.Y * k);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double Dot(Vec2 o) => X * o.X + Y * o.Y;
    public double Cross(Vec2 o) => X * o.Y - Y * o.X;

    public Vec2 Normalized()
    {
        var l = Length;
        if (l < 1e-12) throw new InvalidOperationException("Wektor zerowy nie ma kierunku.");
        return new Vec2(X / l, Y / l);
    }

    /// <summary>Normalna lewa (obrót o +90°).</summary>
    public Vec2 Left => new(-Y, X);

    /// <summary>Kąt kierunku w radianach (atan2).</summary>
    public double Angle => Math.Atan2(Y, X);

    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;

    public override string ToString() => $"({X:F3}; {Y:F3})";
}
