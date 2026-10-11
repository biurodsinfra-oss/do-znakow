using System.Text.Json.Nodes;
using ZnakiAI.Core.Catalog;
using ZnakiAI.Core.Geometry;
using ZnakiAI.Core.Planning;

namespace ZnakiAI.Core.Bridge;

/// <summary>
/// Odpowiedzi narzędzi MCP liczone bez BricsCAD (oś już odczytana z rysunku).
/// Wyniki są zaokrąglone do 1 mm – tyle wystarcza modelowi, a JSON jest krótszy.
/// </summary>
public static class PlanTools
{
    public static double R(double v) => Math.Round(v, 3);

    public static JsonObject PointInfo(Alignment axis, double x, double y)
    {
        var (s, off, dist) = axis.Project(new Vec2(x, y));
        return new JsonObject
        {
            ["km"] = R(s),
            ["odsuniecie"] = R(off),
            ["strona"] = off >= 0 ? "Lewa" : "Prawa",
            ["odleglosc_od_osi"] = R(dist),
        };
    }

    public static JsonObject PointAt(Alignment axis, double km, double odsuniecie)
    {
        if (km < 0 || km > axis.Length)
            throw new BridgeException($"km {km:0.###} poza osią (0–{axis.Length:0.###}).");
        var p = axis.OffsetPoint(km, odsuniecie);
        var t = axis.TangentAt(km);
        return new JsonObject
        {
            ["x"] = R(p.X),
            ["y"] = R(p.Y),
            ["kierunek_osi_stopnie"] = Math.Round(t.Angle * 180 / Math.PI, 2),
        };
    }

    /// <summary>Walidacja planu i wynik geometrii bez rysowania.</summary>
    public static JsonObject Check(PlacementPlan plan, Alignment axis, SignCatalog catalog, DrawingSettings settings)
    {
        var res = new PlanResolver(catalog, settings).Resolve(plan, axis);
        var znaki = new JsonArray();
        foreach (var s in res.Signs.OrderBy(s => s.Source.Km))
            znaki.Add(new JsonObject
            {
                ["kod"] = s.Def.Kod,
                ["nazwa"] = s.Def.Nazwa,
                ["km"] = R(s.Source.Km),
                ["strona"] = s.Source.Strona.ToString(),
                ["status"] = s.Source.Status.ToString(),
                ["slupek_x"] = R(s.Post.X),
                ["slupek_y"] = R(s.Post.Y),
            });
        var linie = new JsonArray();
        foreach (var m in res.Markings)
            linie.Add(new JsonObject
            {
                ["kod"] = m.Def.Kod,
                ["km_od"] = R(m.Source.KmOd),
                ["km_do"] = R(m.Source.KmDo),
                ["odsuniecie"] = R(m.Source.Odsuniecie),
                ["elementow"] = m.Strokes.Count,
            });
        var rows = new JsonArray();
        foreach (var r in Summary.ToRows(Summary.Build(res)))
            rows.Add(string.Join(" | ", r));
        return new JsonObject
        {
            ["dlugosc_osi"] = R(axis.Length),
            ["bledy"] = ToArray(res.Errors),
            ["ostrzezenia"] = ToArray(res.Warnings.Distinct()),
            ["znaki"] = znaki,
            ["linie"] = linie,
            ["zestawienie"] = rows,
        };
    }

    public static JsonArray ToArray(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var i in items) a.Add(i);
        return a;
    }
}
