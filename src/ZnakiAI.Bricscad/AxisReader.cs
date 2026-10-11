using Teigha.DatabaseServices;
using Teigha.Geometry;
using ZnakiAI.Core.Geometry;

namespace ZnakiAI.Bricscad;

/// <summary>Zamiana obiektu CAD (polilinia, linia, łuk, splajn) na oś z kilometrażem.</summary>
internal static class AxisReader
{
    /// <summary>Krok próbkowania dla krzywych bez reprezentacji łukowej (splajny, polilinie 3D) [m].</summary>
    private const double SampleStep = 0.10;

    public static Alignment ToAlignment(Entity ent)
    {
        switch (ent)
        {
            case Polyline pl:
            {
                var v = new List<Vec2>();
                var b = new List<double>();
                for (int i = 0; i < pl.NumberOfVertices; i++)
                {
                    var p = pl.GetPoint2dAt(i);
                    v.Add(new Vec2(p.X, p.Y));
                    b.Add(pl.GetBulgeAt(i));
                }
                return Alignment.FromPolyline(v, b, pl.Closed);
            }
            case Line ln:
                return new Alignment([new Vec2(ln.StartPoint.X, ln.StartPoint.Y), new Vec2(ln.EndPoint.X, ln.EndPoint.Y)]);
            case Arc arc:
            {
                // łuk CAD jest zawsze CCW (przy normalnej +Z): bulge = tan(kąt/4)
                double sweep = arc.EndAngle - arc.StartAngle;
                if (sweep <= 0) sweep += 2 * Math.PI;
                double bulge = Math.Tan(sweep / 4) * Math.Sign(arc.Normal.Z == 0 ? 1 : arc.Normal.Z);
                return Alignment.FromPolyline(
                    [new Vec2(arc.StartPoint.X, arc.StartPoint.Y), new Vec2(arc.EndPoint.X, arc.EndPoint.Y)], [bulge]);
            }
            case Curve c:
            {
                double len = c.GetDistanceAtParameter(c.EndParam);
                int n = Math.Max(2, (int)Math.Ceiling(len / SampleStep));
                var pts = new List<Vec2>(n + 1);
                for (int i = 0; i <= n; i++)
                {
                    Point3d p = c.GetPointAtDist(Math.Min(len, len * i / n));
                    pts.Add(new Vec2(p.X, p.Y));
                }
                return new Alignment(pts);
            }
            default:
                throw new ArgumentException($"Obiekt {ent.GetType().Name} nie może być osią. Wskaż polilinię, linię, łuk lub splajn.");
        }
    }

    public static Point3d P3(Vec2 v) => new(v.X, v.Y, 0);
    public static Point2d P2(Vec2 v) => new(v.X, v.Y);
}
