using System.Text.Json;
using ZnakiAI.Core.Ai;
using ZnakiAI.Core.Catalog;
using ZnakiAI.Core.Geometry;
using ZnakiAI.Core.Planning;

namespace ZnakiAI.Core.Tests;

public class PrecisionTests
{
    private const double Mm = 0.001;

    private static SignCatalog LoadCatalog()
    {
        var dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "catalog"))) dir = Path.GetDirectoryName(dir)!;
        return SignCatalog.Load(Path.Combine(dir, "catalog", "znaki_pionowe.json"),
            Path.Combine(dir, "catalog", "znaki_poziome.json"));
    }

    [Fact]
    public void Straight_axis_chainage_and_offset_are_exact()
    {
        var ax = new Alignment([new Vec2(0, 0), new Vec2(100, 0)]);
        var p = ax.OffsetPoint(37.25, -1.5); // prawa strona
        Assert.Equal(37.25, p.X, 9);
        Assert.Equal(-1.5, p.Y, 9);
        var (s, off, _) = ax.Project(p);
        Assert.Equal(37.25, s, 9);
        Assert.Equal(-1.5, off, 9);
    }

    [Fact]
    public void Arc_from_bulge_stays_on_radius_within_tolerance()
    {
        // półokrąg R = 50: (0,0) → (100,0), bulge = 1 (180°, CCW)
        var ax = Alignment.FromPolyline([new Vec2(0, 0), new Vec2(100, 0)], [1.0]);
        var c = new Vec2(50, 0);
        Assert.Equal(Math.PI * 50, ax.Length, 2); // długość łuku do 1 cm
        for (double s = 0; s <= ax.Length; s += 0.37)
        {
            var p = ax.PointAt(s);
            Assert.InRange(Vec2.Distance(p, c), 50 - Alignment.DefaultArcTolerance - 1e-9, 50 + 1e-9);
        }
        // CCW od (0,0) do (100,0) przez dół → punkt środkowy (50,-50)
        var mid = ax.PointAt(ax.Length / 2);
        Assert.Equal(50, mid.X, 2);
        Assert.Equal(-50, mid.Y, 2);
    }

    [Fact]
    public void Offset_line_on_arc_is_parallel()
    {
        var ax = Alignment.FromPolyline([new Vec2(0, 0), new Vec2(100, 0)], [1.0]);
        var c = new Vec2(50, 0);
        // lewa strona przy łuku CCW = do środka → promień 50 − 3
        var line = ax.Slice(10, 120, 3.0);
        foreach (var p in line) Assert.Equal(47, Vec2.Distance(p, c), 2);
    }

    [Fact]
    public void Dash_pattern_is_measured_along_the_marking_line()
    {
        var r = Alignment.DashRanges(30, 4, 8);
        Assert.Equal(3, r.Count);
        Assert.Equal((0, 4), r[0]);
        Assert.Equal((12, 16), r[1]);
        Assert.Equal((24, 28), r[2]);
    }

    [Fact]
    public void Resolver_places_sign_post_and_rotates_symbol_away_from_road()
    {
        var cat = LoadCatalog();
        var ax = new Alignment([new Vec2(0, 0), new Vec2(200, 0)]);
        var plan = new PlacementPlan
        {
            Znaki = [new SignPlacement { Kod = "A-14", Km = 50, Strona = Side.Prawa, Odsuniecie = 1.0 }],
        };
        var res = new PlanResolver(cat, new DrawingSettings { SymbolDistance = 4 }).Resolve(plan, ax);
        Assert.Empty(res.Errors);
        var s = Assert.Single(res.Signs);
        Assert.Equal(50, s.Post.X, 9);
        Assert.Equal(-1, s.Post.Y, 9);
        Assert.Equal(-5, s.Symbol.Y, 9);
        // oś Y bloku = (−sinθ, cosθ) ma wskazywać od jezdni (0,−1) → θ = π
        Assert.Equal(Math.PI, s.Rotation, 9);
    }

    [Fact]
    public void Resolver_rejects_unknown_code_and_out_of_range()
    {
        var cat = LoadCatalog();
        var ax = new Alignment([new Vec2(0, 0), new Vec2(100, 0)]);
        var plan = new PlacementPlan
        {
            Znaki = [new SignPlacement { Kod = "X-99", Km = 10 }, new SignPlacement { Kod = "B-33", Km = 150 }],
            Linie = [new MarkingPlacement { Kod = "P-1a", KmOd = 0, KmDo = 50, Odsuniecie = 3.5 }],
        };
        var res = new PlanResolver(cat).Resolve(plan, ax);
        Assert.Equal(2, res.Errors.Count);
        var m = Assert.Single(res.Markings);
        Assert.Equal(5, m.Strokes.Count); // 0-4, 12-16, 24-28, 36-40, 48-50
        Assert.Equal(3.5, m.Strokes[0][0].Y, 9);
    }

    [Fact]
    public void Plan_json_roundtrip_matches_ai_schema_names()
    {
        const string json = """
            {"znaki":[{"kod":"A-14","km":12.5,"strona":"Lewa","odsuniecie":1,"status":"Tymczas","tekst":null,"przeciwny":true}],
             "linie":[{"kod":"P-4","km_od":0,"km_do":20,"odsuniecie":0,"status":"Proj"}],"uwagi":"ok"}
            """;
        var p = PlacementPlan.FromJson(json);
        Assert.Equal(Side.Lewa, p.Znaki[0].Strona);
        Assert.Equal(SignStatus.Tymczas, p.Znaki[0].Status);
        Assert.True(p.Znaki[0].Przeciwny);
        Assert.Equal(20, p.Linie[0].KmDo);
    }

    [Fact]
    public void Schema_is_valid_json_object()
    {
        var s = AiPlanner.PlanSchema();
        Assert.Equal("object", s["type"].GetString());
        Assert.Contains("znaki", s["properties"].EnumerateObject().Select(p => p.Name));
        var prompt = AiPlanner.BuildSystemPrompt(LoadCatalog());
        Assert.Contains("A-14 | roboty na drodze", prompt);
    }

    [Fact]
    public void Example_plan_resolves_without_errors()
    {
        var dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "examples"))) dir = Path.GetDirectoryName(dir)!;
        var plan = PlacementPlan.FromJson(File.ReadAllText(Path.Combine(dir, "examples", "plan_przyklad.json")));
        var ax = Alignment.FromPolyline([new Vec2(0, 0), new Vec2(60, 0), new Vec2(100, 20)], [0.0, 0.2]);
        var res = new PlanResolver(LoadCatalog()).Resolve(plan, ax);
        Assert.Empty(res.Errors);
        Assert.Equal(3, res.Signs.Count);
        Assert.Single(res.Markings);
    }

    [Fact]
    public void Every_catalog_sign_gets_builtin_graphics_within_one_metre()
    {
        foreach (var d in LoadCatalog().Signs)
        {
            var shapes = SignGraphics.Build(d);
            Assert.NotEmpty(shapes);
            foreach (var sh in shapes)
            {
                Assert.Equal(sh.Vertices.Count, sh.Bulges.Count);
                Assert.All(sh.Vertices, v => Assert.InRange(v.Length, 0, 0.71));
            }
        }
        var tri = SignGraphics.Triangle(1.0, up: true);
        Assert.Equal(1.0, Vec2.Distance(tri[0], tri[1]), 9);
        Assert.Equal(1.0, Vec2.Distance(tri[1], tri[2]), 9);
        Assert.Equal(0, (tri[0] + tri[1] + tri[2]).Y, 9); // środek ciężkości w (0,0)
    }

    [Fact]
    public void Summary_counts_signs_and_painted_area()
    {
        var ax = new Alignment([new Vec2(0, 0), new Vec2(100, 0)]);
        var plan = new PlacementPlan
        {
            Znaki =
            [
                new SignPlacement { Kod = "A-14", Km = 10, Status = SignStatus.Tymczas },
                new SignPlacement { Kod = "A-14", Km = 60, Strona = Side.Lewa, Status = SignStatus.Tymczas },
                new SignPlacement { Kod = "B-36", Km = 20 },
            ],
            Linie = [new MarkingPlacement { Kod = "P-1a", KmOd = 0, KmDo = 30, Odsuniecie = 3 }],
        };
        var sum = Summary.Build(new PlanResolver(LoadCatalog()).Resolve(plan, ax));
        Assert.Equal(2, sum.Znaki.Single(z => z.Kod == "A-14").Sztuk);
        var l = Assert.Single(sum.Linie);
        Assert.Equal(30, l.DlugoscOdcinka, 6);
        Assert.Equal(12, l.DlugoscMalowana, 6);      // kreski 0-4, 12-16, 24-28
        Assert.Equal(12 * 0.12, l.Powierzchnia, 6);
        Assert.Equal(1 + 2 + 1, Summary.ToRows(sum).Count);
    }
}
