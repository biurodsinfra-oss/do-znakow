using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZnakiAI.Core.Catalog;

/// <summary>Znak pionowy z katalogu.</summary>
public sealed record SignDef
{
    public required string Kod { get; init; }
    public required string Nazwa { get; init; }
    /// <summary>A, B, C, D, E, F, G, T, U...</summary>
    public string Grupa { get; init; } = "";
    /// <summary>trojkat, kolo, kwadrat, prostokat, osmiokat, romb, tabliczka, urzadzenie</summary>
    public string Ksztalt { get; init; } = "";
    /// <summary>Nazwa bloku w bibliotece DWG; domyślnie = kod.</summary>
    public string? Blok { get; init; }
    public string BlockName => string.IsNullOrWhiteSpace(Blok) ? Kod : Blok!;
}

/// <summary>Znak poziomy (linia) z katalogu.</summary>
public sealed record MarkingDef
{
    public required string Kod { get; init; }
    public required string Nazwa { get; init; }
    /// <summary>Szerokość linii [m].</summary>
    public double Szerokosc { get; init; } = 0.12;
    /// <summary>Długość kreski [m]; 0 = linia ciągła.</summary>
    public double Kreska { get; init; }
    /// <summary>Długość przerwy [m].</summary>
    public double Przerwa { get; init; }
    /// <summary>Linie podwójne: rozstaw osi linii [m] (0 = pojedyncza).</summary>
    public double Rozstaw { get; init; }
    /// <summary>Dla linii podwójnych: wzór drugiej linii ("ciagla"/"przerywana"); null = jak pierwsza.</summary>
    public string? DrugaLinia { get; init; }
    /// <summary>false = wartości wymagają sprawdzenia z rozporządzeniem przed użyciem produkcyjnym.</summary>
    public bool Zweryfikowane { get; init; }

    public bool IsSolid => Kreska <= 0 || Przerwa <= 0;
}

public sealed class SignCatalog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, SignDef> _signs;
    private readonly Dictionary<string, MarkingDef> _markings;

    public IReadOnlyCollection<SignDef> Signs => _signs.Values;
    public IReadOnlyCollection<MarkingDef> Markings => _markings.Values;

    public SignCatalog(IEnumerable<SignDef> signs, IEnumerable<MarkingDef> markings)
    {
        _signs = signs.ToDictionary(s => Normalize(s.Kod), StringComparer.OrdinalIgnoreCase);
        _markings = markings.ToDictionary(m => Normalize(m.Kod), StringComparer.OrdinalIgnoreCase);
    }

    public static string Normalize(string code) => code.Trim().ToUpperInvariant().Replace(' ', '-');

    public bool TryGetSign(string code, out SignDef sign) => _signs.TryGetValue(Normalize(code), out sign!);
    public bool TryGetMarking(string code, out MarkingDef m) => _markings.TryGetValue(Normalize(code), out m!);

    public static SignCatalog Load(string signsJsonPath, string markingsJsonPath)
    {
        var signs = JsonSerializer.Deserialize<CatalogFile<SignDef>>(File.ReadAllText(signsJsonPath), Json)
                    ?? throw new InvalidDataException(signsJsonPath);
        var marks = JsonSerializer.Deserialize<CatalogFile<MarkingDef>>(File.ReadAllText(markingsJsonPath), Json)
                    ?? throw new InvalidDataException(markingsJsonPath);
        return new SignCatalog(signs.Pozycje, marks.Pozycje);
    }

    /// <summary>Zwięzła lista kodów dla promptu AI (stała treść – dobrze się cache'uje).</summary>
    public string ToPromptText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ZNAKI PIONOWE (kod | nazwa):");
        foreach (var s in _signs.Values.OrderBy(s => s.Kod, StringComparer.Ordinal)) sb.AppendLine($"{s.Kod} | {s.Nazwa}");
        sb.AppendLine();
        sb.AppendLine("ZNAKI POZIOME (kod | nazwa | szerokość m | kreska/przerwa m):");
        foreach (var m in _markings.Values.OrderBy(m => m.Kod, StringComparer.Ordinal))
            sb.AppendLine($"{m.Kod} | {m.Nazwa} | {m.Szerokosc:0.00} | {(m.IsSolid ? "ciągła" : $"{m.Kreska:0.0}/{m.Przerwa:0.0}")}");
        return sb.ToString();
    }

    private sealed class CatalogFile<T>
    {
        [JsonPropertyName("pozycje")] public List<T> Pozycje { get; set; } = new();
    }
}
