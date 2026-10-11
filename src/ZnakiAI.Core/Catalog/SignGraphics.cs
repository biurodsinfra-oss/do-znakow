using ZnakiAI.Core.Geometry;

namespace ZnakiAI.Core.Catalog;

/// <summary>
/// Element grafiki bloku znaku: zamknięty kontur (wierzchołki + bulge na odcinek),
/// opcjonalne wypełnienie i kolor obrysu (kolory ACI).
/// </summary>
public sealed record SignShape(IReadOnlyList<Vec2> Vertices, IReadOnlyList<double> Bulges, short? FillAci, short? OutlineAci);

/// <summary>
/// Wbudowana biblioteka symboli: kształt i barwy wg grupy znaku (A żółty z czerwonym obrzeżem,
/// B biały z czerwonym obrzeżem, C niebieski, D niebieski, T biała tabliczka, U urządzenie).
/// Używana, gdy znaku nie ma w bibliotece DWG. Symbol mieści się w kwadracie ~1×1 m, środek w (0,0),
/// góra symbolu = +Y bloku.
/// </summary>
public static class SignGraphics
{
    public const short Red = 1, Yellow = 2, Blue = 5, Black = 7, White = 255;

    public static List<SignShape> Build(SignDef d)
    {
        var s = new List<SignShape>();
        switch (d.Ksztalt)
        {
            case "trojkat":
                s.Add(Poly(Triangle(1.0, up: true), Red, Red));
                s.Add(Poly(Triangle(0.70, up: true), Yellow, null));
                break;
            case "trojkat_odwrocony":
                s.Add(Poly(Triangle(1.0, up: false), Red, Red));
                s.Add(Poly(Triangle(0.70, up: false), White, null));
                break;
            case "osmiokat":
                s.Add(Poly(Regular(8, 0.5, Math.PI / 8), Red, Red));
                break;
            case "kolo" when d.Grupa == "C":
                s.Add(Disc(0.5, Blue, Blue));
                break;
            case "kolo":
                s.Add(Disc(0.5, Red, Red));
                s.Add(Disc(0.38, White, null));
                break;
            case "romb":
                s.Add(Poly(Regular(4, 0.5, 0), White, Black));
                s.Add(Poly(Regular(4, 0.33, 0), Yellow, null));
                break;
            case "kwadrat":
                s.Add(Poly(Rect(1.0, 1.0), Blue, Blue));
                s.Add(Poly(Rect(0.6, 0.6), White, null));
                break;
            case "tabliczka":
                s.Add(Poly(Rect(1.0, 0.4), White, Black));
                break;
            default: // urządzenia BRD i nieznane
                s.Add(Poly(Rect(1.0, 0.3), White, Red));
                for (int i = 0; i < 3; i++) // ukośne pasy czerwone
                {
                    double x = -0.4 + i * 0.3;
                    s.Add(Poly([new Vec2(x, -0.15), new Vec2(x + 0.12, -0.15), new Vec2(x + 0.22, 0.15), new Vec2(x + 0.10, 0.15)], Red, null));
                }
                break;
        }
        return s;
    }

    /// <summary>Trójkąt równoboczny o boku a, środek ciężkości w (0,0).</summary>
    public static List<Vec2> Triangle(double a, bool up)
    {
        double h = a * Math.Sqrt(3) / 2, k = up ? 1 : -1;
        return [new Vec2(-a / 2, -k * h / 3), new Vec2(a / 2, -k * h / 3), new Vec2(0, k * 2 * h / 3)];
    }

    /// <summary>Wielokąt foremny wpisany w okrąg o promieniu r, obrócony o rot.</summary>
    public static List<Vec2> Regular(int n, double r, double rot) =>
        Enumerable.Range(0, n).Select(i => new Vec2(r * Math.Cos(rot + 2 * Math.PI * i / n + Math.PI / 2),
                                                    r * Math.Sin(rot + 2 * Math.PI * i / n + Math.PI / 2))).ToList();

    public static List<Vec2> Rect(double w, double h) =>
        [new Vec2(-w / 2, -h / 2), new Vec2(w / 2, -h / 2), new Vec2(w / 2, h / 2), new Vec2(-w / 2, h / 2)];

    private static SignShape Poly(List<Vec2> v, short? fill, short? outline) =>
        new(v, Enumerable.Repeat(0.0, v.Count).ToList(), fill, outline);

    /// <summary>Koło jako dwa półokręgi (bulge = 1).</summary>
    private static SignShape Disc(double r, short? fill, short? outline) =>
        new([new Vec2(r, 0), new Vec2(-r, 0)], [1.0, 1.0], fill, outline);
}
