using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using ZnakiAI.Core.Catalog;
using ZnakiAI.Core.Planning;

namespace ZnakiAI.Core.Ai;

/// <summary>Obiekt istniejący w rysunku, opisany względem osi (wysyłany do AI jako kontekst).</summary>
public sealed record ExistingObject(string Kod, double Km, double Odsuniecie, string? Status = null);

/// <summary>Kontekst rysunku przekazywany do AI. Same liczby względem osi – bez współrzędnych globalnych.</summary>
public sealed record DrawingContext
{
    public double DlugoscOsi { get; init; }
    /// <summary>Opis osi odniesienia, np. "prawa krawędź jezdni ul. Grunwaldzkiej, kierunek do centrum".</summary>
    public string OpisOsi { get; init; } = "krawędź jezdni";
    public bool ObszarZabudowany { get; init; } = true;
    public int? PredkoscDopuszczalna { get; init; }
    /// <summary>Szerokość jezdni [m], jeśli znana.</summary>
    public double? SzerokoscJezdni { get; init; }
    /// <summary>Zakres zajęcia / robót (km od–do), jeśli wskazano.</summary>
    public (double Od, double Do)? StrefaRobot { get; init; }
    public List<ExistingObject> Istniejace { get; init; } = new();
}

public sealed record AiSettings
{
    public string Model { get; init; } = "claude-opus-5-5";
    /// <summary>low = najszybciej; medium = domyślnie; high = trudne przypadki.</summary>
    public string Effort { get; init; } = "medium";
    public int MaxTokens { get; init; } = 16000;
    /// <summary>Serwerowy fallback przy odmowie modelu (beta server-side-fallback).</summary>
    public bool ServerFallback { get; init; } = true;
}

public sealed record AiPlanResult(PlacementPlan Plan, string RawJson, TimeSpan Elapsed, long InputTokens,
    long OutputTokens, long CacheReadTokens);

/// <summary>
/// Warstwa AI: jedno wywołanie modelu, odpowiedź wymuszona schematem JSON (structured output).
/// Brak pętli narzędzi = jedna podróż sieciowa = szybko. Model zwraca wyłącznie kilometraże
/// i kody; geometrię liczy <see cref="PlanResolver"/>.
/// </summary>
public sealed class AiPlanner
{
    private readonly AnthropicClient _client;
    private readonly SignCatalog _catalog;
    private readonly AiSettings _settings;
    private readonly string _systemPrompt;

    public AiPlanner(SignCatalog catalog, AiSettings? settings = null, AnthropicClient? client = null)
    {
        _catalog = catalog;
        _settings = settings ?? new AiSettings();
        _client = client ?? new AnthropicClient(); // klucz z ANTHROPIC_API_KEY
        _systemPrompt = BuildSystemPrompt(catalog);
    }

    public async Task<AiPlanResult> PlanAsync(string instruction, DrawingContext ctx, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var p = new MessageCreateParams
        {
            Model = _settings.Model,
            MaxTokens = _settings.MaxTokens,
            // Stały prompt systemowy + katalog na początku i z cache_control:
            // kolejne wywołania czytają go z cache (szybciej i taniej).
            System = new List<BetaTextBlockParam>
            {
                new() { Text = _systemPrompt, CacheControl = new BetaCacheControlEphemeral() },
            },
            OutputConfig = new BetaOutputConfig
            {
                Effort = ParseEffort(_settings.Effort),
                Format = new BetaJsonOutputFormat { Schema = PlanSchema() },
            },
            Messages = [new() { Role = Role.User, Content = BuildUserMessage(instruction, ctx) }],
        };
        if (_settings.ServerFallback)
        {
            p = p with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
        }

        BetaMessage response = await _client.Beta.Messages.Create(p, ct);
        sw.Stop();

        if (response.StopReason is { } sr && sr.Raw() == "refusal")
            throw new InvalidOperationException("Model odmówił wygenerowania planu (refusal). Zmień polecenie.");
        if (response.StopReason is { } sr2 && sr2.Raw() == "max_tokens")
            throw new InvalidOperationException("Odpowiedź przerwana limitem tokenów – zwiększ MaxTokens lub zawęź zakres.");

        var json = string.Concat(response.Content
            .Select(b => b.TryPickText(out var t) ? t.Text : null)
            .Where(t => t is not null));
        var plan = PlacementPlan.FromJson(json);

        var u = response.Usage;
        return new AiPlanResult(plan, json, sw.Elapsed, u.InputTokens, u.OutputTokens,
            u.CacheReadInputTokens ?? 0);
    }

    private static Effort ParseEffort(string e) => e.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };

    internal static string BuildUserMessage(string instruction, DrawingContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("KONTEKST RYSUNKU");
        sb.AppendLine($"Oś odniesienia: {ctx.OpisOsi}; długość {ctx.DlugoscOsi:0.00} m (km 0 = początek osi).");
        sb.AppendLine($"Obszar zabudowany: {(ctx.ObszarZabudowany ? "tak" : "nie")}");
        if (ctx.PredkoscDopuszczalna is { } v) sb.AppendLine($"Prędkość dopuszczalna: {v} km/h");
        if (ctx.SzerokoscJezdni is { } w) sb.AppendLine($"Szerokość jezdni: {w:0.00} m");
        if (ctx.StrefaRobot is { } z) sb.AppendLine($"Strefa robót / zajęcia: km {z.Od:0.00} – {z.Do:0.00}");
        if (ctx.Istniejace.Count > 0)
        {
            sb.AppendLine("Oznakowanie istniejące (kod | km | odsunięcie m, + lewo / − prawo | status):");
            foreach (var o in ctx.Istniejace.OrderBy(o => o.Km))
                sb.AppendLine($"{o.Kod} | {o.Km:0.00} | {o.Odsuniecie:0.00} | {o.Status ?? "ist"}");
        }
        sb.AppendLine();
        sb.AppendLine("POLECENIE PROJEKTANTA");
        sb.AppendLine(instruction.Trim());
        return sb.ToString();
    }

    internal static string BuildSystemPrompt(SignCatalog catalog) => $$"""
        Jesteś asystentem projektanta organizacji ruchu w Polsce. Na podstawie polecenia i kontekstu
        rysunku zwracasz plan rozmieszczenia oznakowania pionowego i poziomego w formacie JSON.

        Zasady planu:
        - Wszystkie położenia podajesz w kilometrażu [m] wzdłuż osi odniesienia (km 0 = początek osi)
          i odsunięciu [m]. Nie podajesz współrzędnych – liczy je program.
        - Znaki pionowe: "strona" = Prawa/Lewa względem kierunku osi, "odsuniecie" = odległość słupka
          od osi odniesienia (≥ 0). Gdy oś to krawędź jezdni, typowo 0,5–2,0 m (zalecane ok. 1,0 m).
          "przeciwny" = true dla znaku skierowanego do ruchu jadącego przeciwnie do kierunku osi.
        - Znaki poziome: "odsuniecie" ze znakiem (+ lewo, − prawo) – położenie osi linii.
        - Status: Proj (projektowany), Tymczas (tymczasowy – organizacja na czas robót),
          Ist (istniejący, tylko gdy polecenie każe go dorysować), DoUsuniecia.
        - Używaj wyłącznie kodów z katalogu poniżej. Jeśli potrzebny znak nie istnieje w katalogu,
          nie wymyślaj kodu – opisz to w "uwagi".
        - Znaki ostrzegawcze (A) ustawiaj przed miejscem niebezpiecznym: w obszarze zabudowanym
          20–50 m, poza obszarem zabudowanym 150–300 m, na drogach ekspresowych i autostradach 300–500 m.
          Jeśli odległość jest inna, dodaj tabliczkę T-1 z odległością.
        - Oznakowanie istniejące uwzględniaj: nie dubluj znaków, a sprzeczne istniejące znaki
          na czas robót wskaż w "uwagi" (do zakrycia/demontażu).
        - Nie wychodź poza zakres osi 0–długość. Zaokrąglaj kilometraż do 0,1 m.
        - W "uwagi" krótko (maks. kilka zdań) uzasadnij układ i wypisz założenia.

        KATALOG
        {{catalog.ToPromptText()}}
        """;

    /// <summary>Schemat JSON odpowiedzi (structured output) – odpowiada <see cref="PlacementPlan"/>.</summary>
    internal static Dictionary<string, JsonElement> PlanSchema()
    {
        var statusEnum = new[] { "Proj", "Tymczas", "Ist", "DoUsuniecia" };
        var schema = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "znaki", "linie", "uwagi" },
            properties = new Dictionary<string, object>
            {
                ["znaki"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "kod", "km", "strona", "odsuniecie", "status", "tekst", "przeciwny" },
                        properties = new Dictionary<string, object>
                        {
                            ["kod"] = new { type = "string" },
                            ["km"] = new { type = "number" },
                            ["strona"] = new { type = "string", @enum = new[] { "Prawa", "Lewa" } },
                            ["odsuniecie"] = new { type = "number" },
                            ["status"] = new { type = "string", @enum = statusEnum },
                            ["tekst"] = new { type = new[] { "string", "null" } },
                            ["przeciwny"] = new { type = "boolean" },
                        },
                    },
                },
                ["linie"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "kod", "km_od", "km_do", "odsuniecie", "status" },
                        properties = new Dictionary<string, object>
                        {
                            ["kod"] = new { type = "string" },
                            ["km_od"] = new { type = "number" },
                            ["km_do"] = new { type = "number" },
                            ["odsuniecie"] = new { type = "number" },
                            ["status"] = new { type = "string", @enum = statusEnum },
                        },
                    },
                },
                ["uwagi"] = new { type = "string" },
            },
        };
        var el = JsonSerializer.SerializeToElement(schema);
        return el.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}
