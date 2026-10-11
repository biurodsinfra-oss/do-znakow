namespace ZnakiAI.Core.Geometry;

/// <summary>
/// Oś odniesienia (krawędź jezdni lub oś drogi) z kilometrażem.
/// Łuki polilinii (bulge) są zamieniane na cięciwy z kontrolą strzałki,
/// więc odchyłka od łuku nie przekracza <see cref="DefaultArcTolerance"/>.
/// Konwencja: odsunięcie dodatnie = po lewej stronie kierunku osi, ujemne = po prawej.
/// </summary>
public sealed class Alignment
{
    /// <summary>Maksymalna strzałka cięciwy przy dyskretyzacji łuków [m].</summary>
    public const double DefaultArcTolerance = 0.0005;

    private readonly Vec2[] _pts;
    private readonly double[] _ch; // kilometraż wierzchołków

    public IReadOnlyList<Vec2> Points => _pts;
    public double Length => _ch[^1];

    public Alignment(IEnumerable<Vec2> points)
    {
        var list = new List<Vec2>();
        foreach (var p in points)
        {
            if (list.Count == 0 || Vec2.Distance(list[^1], p) > 1e-9) list.Add(p);
        }
        if (list.Count < 2) throw new ArgumentException("Oś musi mieć co najmniej dwa różne punkty.");
        _pts = list.ToArray();
        _ch = new double[_pts.Length];
        for (int i = 1; i < _pts.Length; i++) _ch[i] = _ch[i - 1] + Vec2.Distance(_pts[i - 1], _pts[i]);
    }

    /// <summary>
    /// Buduje oś z wierzchołków polilinii i współczynników bulge (jak w LWPOLYLINE).
    /// bulges[i] dotyczy odcinka vertices[i] → vertices[i+1].
    /// </summary>
    public static Alignment FromPolyline(IReadOnlyList<Vec2> vertices, IReadOnlyList<double>? bulges = null,
        bool closed = false, double arcTolerance = DefaultArcTolerance)
    {
        var pts = new List<Vec2> { vertices[0] };
        int segCount = closed ? vertices.Count : vertices.Count - 1;
        for (int i = 0; i < segCount; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            double bulge = bulges is null || i >= bulges.Count ? 0 : bulges[i];
            if (Math.Abs(bulge) < 1e-12)
            {
                pts.Add(b);
                continue;
            }
            AppendArc(pts, a, b, bulge, arcTolerance);
        }
        return new Alignment(pts);
    }

    private static void AppendArc(List<Vec2> pts, Vec2 a, Vec2 b, double bulge, double tol)
    {
        double theta = 4 * Math.Atan(bulge);               // kąt środkowy ze znakiem (CCW > 0)
        double chord = Vec2.Distance(a, b);
        double r = chord / (2 * Math.Sin(Math.Abs(theta) / 2));
        // środek łuku: od środka cięciwy wzdłuż normalnej lewej o (c/2)(1-b²)/(2b)
        var mid = (a + b) * 0.5;
        var dir = (b - a).Normalized();
        var center = mid + dir.Left * (chord / 2 * (1 - bulge * bulge) / (2 * bulge));

        // liczba podziałów tak, by strzałka ≤ tol
        double maxStep = 2 * Math.Acos(Math.Max(-1, 1 - tol / r));
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(theta) / maxStep));
        double a0 = (a - center).Angle;
        for (int k = 1; k < n; k++)
        {
            double ang = a0 + theta * k / n;
            pts.Add(center + new Vec2(Math.Cos(ang), Math.Sin(ang)) * r);
        }
        pts.Add(b);
    }

    private int SegmentIndex(double s)
    {
        if (s <= 0) return 0;
        if (s >= Length) return _pts.Length - 2;
        int i = Array.BinarySearch(_ch, s);
        if (i >= 0) return Math.Min(i, _pts.Length - 2);
        return Math.Max(0, ~i - 1);
    }

    private void CheckRange(double s)
    {
        if (s < -1e-6 || s > Length + 1e-6)
            throw new ArgumentOutOfRangeException(nameof(s), $"Kilometraż {s:F3} poza osią 0–{Length:F3} m.");
    }

    public Vec2 PointAt(double s)
    {
        CheckRange(s);
        s = Math.Clamp(s, 0, Length);
        int i = SegmentIndex(s);
        double segLen = _ch[i + 1] - _ch[i];
        double t = segLen < 1e-12 ? 0 : (s - _ch[i]) / segLen;
        return _pts[i] + (_pts[i + 1] - _pts[i]) * t;
    }

    /// <summary>Jednostkowy wektor styczny w kilometrażu s.</summary>
    public Vec2 TangentAt(double s)
    {
        CheckRange(s);
        int i = SegmentIndex(Math.Clamp(s, 0, Length));
        return (_pts[i + 1] - _pts[i]).Normalized();
    }

    /// <summary>Punkt odsunięty prostopadle; offset &gt; 0 = lewa strona.</summary>
    public Vec2 OffsetPoint(double s, double offset) => PointAt(s) + TangentAt(s).Left * offset;

    /// <summary>Rzut punktu na oś: kilometraż, odsunięcie ze znakiem (lewo &gt; 0), odległość.</summary>
    public (double Chainage, double Offset, double Distance) Project(Vec2 p)
    {
        double best = double.MaxValue, bestS = 0, bestOff = 0;
        for (int i = 0; i < _pts.Length - 1; i++)
        {
            var a = _pts[i];
            var d = _pts[i + 1] - a;
            double len2 = d.Dot(d);
            double t = len2 < 1e-24 ? 0 : Math.Clamp((p - a).Dot(d) / len2, 0, 1);
            var q = a + d * t;
            double dist = Vec2.Distance(p, q);
            if (dist < best)
            {
                best = dist;
                bestS = _ch[i] + Math.Sqrt(len2) * t;
                bestOff = d.Cross(p - a) >= 0 ? dist : -dist;
            }
        }
        return (bestS, bestOff, best);
    }

    /// <summary>
    /// Fragment osi między kilometrażami s1–s2, odsunięty o offset.
    /// Wierzchołki pośrednie liczone jako przecięcie linii równoległych (miter),
    /// co daje dokładny przebieg linii równoległej.
    /// </summary>
    public List<Vec2> Slice(double s1, double s2, double offset = 0)
    {
        CheckRange(s1);
        CheckRange(s2);
        if (s2 < s1) (s1, s2) = (s2, s1);
        var result = new List<Vec2> { OffsetPoint(s1, offset) };
        int i1 = SegmentIndex(s1), i2 = SegmentIndex(s2);
        for (int v = i1 + 1; v <= i2; v++)
        {
            if (_ch[v] <= s1 + 1e-9 || _ch[v] >= s2 - 1e-9) continue;
            result.Add(OffsetVertex(v, offset));
        }
        result.Add(PointAtOnSegment(s2, i2) + (_pts[i2 + 1] - _pts[i2]).Normalized().Left * offset);
        return result;
    }

    private Vec2 PointAtOnSegment(double s, int i)
    {
        double segLen = _ch[i + 1] - _ch[i];
        double t = segLen < 1e-12 ? 0 : (s - _ch[i]) / segLen;
        return _pts[i] + (_pts[i + 1] - _pts[i]) * t;
    }

    private Vec2 OffsetVertex(int v, double offset)
    {
        var n1 = (_pts[v] - _pts[v - 1]).Normalized().Left;
        var n2 = (_pts[v + 1] - _pts[v]).Normalized().Left;
        var m = n1 + n2;
        if (m.Length < 1e-9) return _pts[v] + n1 * offset; // zawrócenie – nie powinno wystąpić
        m = m.Normalized();
        double cosHalf = m.Dot(n1);
        double k = offset / Math.Max(cosHalf, 0.1);      // ograniczenie miter przy ostrych kątach
        return _pts[v] + m * k;
    }

    /// <summary>
    /// Oś równoległa w zakresie s1–s2. Kreski linii przerywanych należy mierzyć
    /// po tej osi (a nie po osi bazowej), bo na łukach długości się różnią.
    /// </summary>
    public Alignment OffsetAlignment(double s1, double s2, double offset) => new(Slice(s1, s2, offset));

    /// <summary>
    /// Podział na kreski: zwraca zakresy kilometrażu [od, do] kolejnych kresek.
    /// phase przesuwa początek wzoru (np. kontynuacja wzoru z poprzedniego odcinka).
    /// Ostatnia kreska jest przycinana do końca zakresu, chyba że byłaby krótsza od minDash.
    /// </summary>
    public static List<(double From, double To)> DashRanges(double length, double dash, double gap,
        double phase = 0, double minDash = 0.0)
    {
        if (dash <= 0) throw new ArgumentException("Długość kreski musi być > 0.");
        var res = new List<(double, double)>();
        if (gap <= 0)
        {
            res.Add((0, length));
            return res;
        }
        double period = dash + gap;
        double s = -(((phase % period) + period) % period);
        for (; s < length - 1e-9; s += period)
        {
            double a = Math.Max(0, s), b = Math.Min(length, s + dash);
            if (b - a > Math.Max(minDash, 1e-6)) res.Add((a, b));
        }
        return res;
    }
}
