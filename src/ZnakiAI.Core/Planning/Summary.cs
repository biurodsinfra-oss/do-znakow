using ZnakiAI.Core.Geometry;

namespace ZnakiAI.Core.Planning;

/// <summary>Wiersz zestawienia znaków pionowych.</summary>
public sealed record SignSummaryRow(string Kod, string Nazwa, SignStatus Status, int Sztuk);

/// <summary>
/// Wiersz zestawienia znaków poziomych. DlugoscOdcinka = długość oznakowanego odcinka,
/// DlugoscMalowana = suma długości kresek (dla linii ciągłych równa odcinkowi; dla podwójnych obie linie),
/// Powierzchnia = malowana długość × szerokość [m²].
/// </summary>
public sealed record MarkingSummaryRow(string Kod, string Nazwa, SignStatus Status, double DlugoscOdcinka,
    double DlugoscMalowana, double Powierzchnia);

public sealed record PlanSummary(List<SignSummaryRow> Znaki, List<MarkingSummaryRow> Linie);

public static class Summary
{
    public static PlanSummary Build(ResolveResult res)
    {
        var znaki = res.Signs
            .GroupBy(s => (s.Def.Kod, s.Def.Nazwa, s.Source.Status))
            .Select(g => new SignSummaryRow(g.Key.Kod, g.Key.Nazwa, g.Key.Status, g.Count()))
            .OrderBy(r => r.Status).ThenBy(r => r.Kod, StringComparer.Ordinal)
            .ToList();

        var linie = res.Markings
            .GroupBy(m => (m.Def.Kod, m.Def.Nazwa, m.Source.Status))
            .Select(g =>
            {
                double odcinek = g.Sum(m => Math.Abs(m.Source.KmDo - m.Source.KmOd));
                double malowana = g.Sum(m => m.Strokes.Sum(PathLength));
                double pow = g.Sum(m => m.Strokes.Sum(PathLength) * m.Width);
                return new MarkingSummaryRow(g.Key.Kod, g.Key.Nazwa, g.Key.Status,
                    Math.Round(odcinek, 2), Math.Round(malowana, 2), Math.Round(pow, 2));
            })
            .OrderBy(r => r.Status).ThenBy(r => r.Kod, StringComparer.Ordinal)
            .ToList();

        return new PlanSummary(znaki, linie);
    }

    public static double PathLength(IReadOnlyList<Vec2> pts)
    {
        double l = 0;
        for (int i = 1; i < pts.Count; i++) l += Vec2.Distance(pts[i - 1], pts[i]);
        return l;
    }

    public static string StatusLabel(SignStatus s) => s switch
    {
        SignStatus.Tymczas => "tymczasowe",
        SignStatus.Ist => "istniejące",
        SignStatus.DoUsuniecia => "do usunięcia",
        _ => "projektowane",
    };

    /// <summary>Wiersze tabeli (nagłówek + dane) – wspólne dla tabeli w rysunku i eksportu CSV.</summary>
    public static List<string[]> ToRows(PlanSummary s)
    {
        var rows = new List<string[]> { new[] { "Kod", "Nazwa", "Status", "Ilość", "j.m." } };
        foreach (var z in s.Znaki)
            rows.Add([z.Kod, z.Nazwa, StatusLabel(z.Status), z.Sztuk.ToString(), "szt."]);
        foreach (var l in s.Linie)
            rows.Add([l.Kod, $"{l.Nazwa} (odcinek {l.DlugoscOdcinka:0.00} m, malowane {l.DlugoscMalowana:0.00} m)",
                StatusLabel(l.Status), l.Powierzchnia.ToString("0.00"), "m²"]);
        return rows;
    }
}
