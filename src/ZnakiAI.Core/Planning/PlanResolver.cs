using ZnakiAI.Core.Catalog;
using ZnakiAI.Core.Geometry;

namespace ZnakiAI.Core.Planning;

/// <summary>Ustawienia graficzne rysowania (w jednostkach rysunku = metry).</summary>
public sealed record DrawingSettings
{
    /// <summary>Skala symbolu znaku (1.0 = blok w skali bibliotecznej).</summary>
    public double SymbolScale { get; init; } = 1.0;
    /// <summary>Odległość symbolu od słupka (na zewnątrz jezdni) – odnośnik jak na planach COR.</summary>
    public double SymbolDistance { get; init; } = 4.0;
    /// <summary>Minimalny odstęp między słupkami po tej samej stronie, poniżej którego zgłaszamy kolizję.</summary>
    public double MinPostSpacing { get; init; } = 0.5;
    /// <summary>Maks. odległość słupka od osi odniesienia uznawana za sensowną.</summary>
    public double MaxLateralOffset { get; init; } = 15.0;
}

public sealed record ResolvedSign(
    SignPlacement Source,
    SignDef Def,
    Vec2 Post,
    Vec2 Symbol,
    /// <summary>Obrót bloku [rad]: lokalna oś Y bloku skierowana od jezdni.</summary>
    double Rotation);

public sealed record ResolvedMarking(
    MarkingPlacement Source,
    MarkingDef Def,
    /// <summary>Każdy element to jedna kreska / odcinek linii ciągłej (oś linii).</summary>
    List<List<Vec2>> Strokes,
    double Width);

public sealed record ResolveResult(
    List<ResolvedSign> Signs,
    List<ResolvedMarking> Markings,
    List<string> Errors,
    List<string> Warnings);

/// <summary>
/// Deterministyczna zamiana planu (kilometraż) na geometrię (współrzędne).
/// Waliduje kody, zakresy i kolizje – nic nie trafia do rysunku bez przejścia przez ten etap.
/// </summary>
public sealed class PlanResolver
{
    private readonly SignCatalog _catalog;
    private readonly DrawingSettings _set;

    public PlanResolver(SignCatalog catalog, DrawingSettings? settings = null)
    {
        _catalog = catalog;
        _set = settings ?? new DrawingSettings();
    }

    public ResolveResult Resolve(PlacementPlan plan, Alignment axis)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var signs = new List<ResolvedSign>();
        var marks = new List<ResolvedMarking>();

        foreach (var p in plan.Znaki)
        {
            string tag = $"znak {p.Kod} km {p.Km:0.00}";
            if (!_catalog.TryGetSign(p.Kod, out var def)) { errors.Add($"{tag}: brak kodu w katalogu."); continue; }
            if (p.Km < 0 || p.Km > axis.Length) { errors.Add($"{tag}: poza osią (0–{axis.Length:0.00} m)."); continue; }
            if (p.Odsuniecie < 0 || p.Odsuniecie > _set.MaxLateralOffset)
            { errors.Add($"{tag}: odsunięcie {p.Odsuniecie:0.00} m poza zakresem 0–{_set.MaxLateralOffset} m."); continue; }

            double sideSign = p.Strona == Side.Lewa ? 1 : -1;
            var t = axis.TangentAt(p.Km);
            var away = t.Left * sideSign;                       // kierunek od jezdni
            var post = axis.OffsetPoint(p.Km, sideSign * p.Odsuniecie);
            var symbol = post + away * _set.SymbolDistance;
            // lokalne Y bloku = away  →  rotacja θ spełnia (−sinθ, cosθ) = away
            double rot = Math.Atan2(-away.X, away.Y);
            signs.Add(new ResolvedSign(p, def, post, symbol, NormalizeAngle(rot)));
        }

        // kolizje słupków po tej samej stronie
        foreach (var grp in signs.GroupBy(s => s.Source.Strona))
        {
            var ordered = grp.OrderBy(s => s.Source.Km).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                double d = Vec2.Distance(ordered[i - 1].Post, ordered[i].Post);
                if (d > 1e-6 && d < _set.MinPostSpacing)
                    warnings.Add($"Słupki {ordered[i - 1].Source.Kod} i {ordered[i].Source.Kod} są {d:0.00} m od siebie – rozważ jeden słupek.");
            }
        }

        foreach (var m in plan.Linie)
        {
            string tag = $"linia {m.Kod} km {m.KmOd:0.00}–{m.KmDo:0.00}";
            if (!_catalog.TryGetMarking(m.Kod, out var def)) { errors.Add($"{tag}: brak kodu w katalogu."); continue; }
            double a = Math.Min(m.KmOd, m.KmDo), b = Math.Max(m.KmOd, m.KmDo);
            if (a < 0 || b > axis.Length) { errors.Add($"{tag}: poza osią (0–{axis.Length:0.00} m)."); continue; }
            if (b - a < 1e-3) { errors.Add($"{tag}: zerowa długość."); continue; }
            if (!def.Zweryfikowane) warnings.Add($"{def.Kod}: wymiary wzoru nie są oznaczone jako zweryfikowane w katalogu.");

            var strokes = new List<List<Vec2>>();
            if (def.Rozstaw > 0)
            {
                // linia podwójna: dwie linie symetrycznie względem zadanego odsunięcia
                AddStrokes(axis, a, b, m.Odsuniecie + def.Rozstaw / 2, def.Kreska, def.Przerwa, strokes);
                bool secondSolid = def.DrugaLinia?.StartsWith("ciag", StringComparison.OrdinalIgnoreCase) == true;
                AddStrokes(axis, a, b, m.Odsuniecie - def.Rozstaw / 2,
                    secondSolid ? 0 : def.Kreska, secondSolid ? 0 : def.Przerwa, strokes);
            }
            else
            {
                AddStrokes(axis, a, b, m.Odsuniecie, def.Kreska, def.Przerwa, strokes);
            }
            marks.Add(new ResolvedMarking(m, def, strokes, def.Szerokosc));
        }

        return new ResolveResult(signs, marks, errors, warnings);
    }

    private static void AddStrokes(Alignment axis, double a, double b, double offset, double dash, double gap,
        List<List<Vec2>> output)
    {
        // długości kresek mierzone po linii równoległej, nie po osi bazowej
        var path = axis.OffsetAlignment(a, b, offset);
        if (dash <= 0 || gap <= 0)
        {
            output.Add(path.Points.ToList());
            return;
        }
        foreach (var (from, to) in Alignment.DashRanges(path.Length, dash, gap))
            output.Add(path.Slice(from, to));
    }

    private static double NormalizeAngle(double a)
    {
        a %= 2 * Math.PI;
        return a < 0 ? a + 2 * Math.PI : a;
    }
}
