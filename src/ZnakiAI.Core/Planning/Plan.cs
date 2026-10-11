using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZnakiAI.Core.Planning;

/// <summary>
/// Plan oznakowania wyrażony w kilometrażu względem wskazanej osi.
/// To jest jedyny format, który zwraca AI – współrzędne liczy wyłącznie silnik geometrii,
/// dzięki czemu precyzja nie zależy od modelu językowego.
/// </summary>
public sealed record PlacementPlan
{
    [JsonPropertyName("znaki")] public List<SignPlacement> Znaki { get; init; } = new();
    [JsonPropertyName("linie")] public List<MarkingPlacement> Linie { get; init; } = new();
    /// <summary>Krótkie uzasadnienie / uwagi projektanta (wyświetlane, nie rysowane).</summary>
    [JsonPropertyName("uwagi")] public string? Uwagi { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static PlacementPlan FromJson(string json) =>
        JsonSerializer.Deserialize<PlacementPlan>(json, JsonOptions) ?? new PlacementPlan();
}

public enum Side { Prawa, Lewa }

public enum SignStatus { Proj, Tymczas, Ist, DoUsuniecia }

public sealed record SignPlacement
{
    /// <summary>Kod znaku, np. "A-14", "B-33".</summary>
    [JsonPropertyName("kod")] public required string Kod { get; init; }
    /// <summary>Kilometraż słupka na osi [m].</summary>
    [JsonPropertyName("km")] public double Km { get; init; }
    [JsonPropertyName("strona")] public Side Strona { get; init; } = Side.Prawa;
    /// <summary>Odległość słupka od osi odniesienia [m] (≥ 0, strona określa kierunek).</summary>
    [JsonPropertyName("odsuniecie")] public double Odsuniecie { get; init; } = 1.0;
    [JsonPropertyName("status")] public SignStatus Status { get; init; } = SignStatus.Proj;
    /// <summary>Tabliczka / tekst dodatkowy, np. "na odcinku 50 m", "40".</summary>
    [JsonPropertyName("tekst")] public string? Tekst { get; init; }
    /// <summary>true = znak dla ruchu przeciwnego do kierunku osi.</summary>
    [JsonPropertyName("przeciwny")] public bool Przeciwny { get; init; }
}

public sealed record MarkingPlacement
{
    [JsonPropertyName("kod")] public required string Kod { get; init; }
    [JsonPropertyName("km_od")] public double KmOd { get; init; }
    [JsonPropertyName("km_do")] public double KmDo { get; init; }
    /// <summary>Odsunięcie osi linii od osi odniesienia [m], ze znakiem: + lewo, − prawo.</summary>
    [JsonPropertyName("odsuniecie")] public double Odsuniecie { get; init; }
    [JsonPropertyName("status")] public SignStatus Status { get; init; } = SignStatus.Proj;
}
