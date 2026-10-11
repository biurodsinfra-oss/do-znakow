using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using ZnakiAI.Core.Bridge;
using ZnakiAI.Core.Planning;

namespace ZnakiAI.Mcp;

/// <summary>Narzędzia MCP: każde przekazuje wywołanie do wtyczki ZnakiAI w BricsCAD.</summary>
[McpServerToolType]
public sealed class BricscadTools(BridgeClient bridge)
{
    public const string Instructions = """
        Narzędzia sterują wtyczką ZnakiAI w otwartym BricsCAD (oznakowanie pionowe i poziome dróg, przepisy polskie).
        Kolejność pracy:
        1. zasady – przeczytaj raz: reguły projektowania i katalog dozwolonych kodów znaków i linii.
        2. wskaz_os – projektant wskazuje w BricsCAD oś odniesienia (zwykle krawędź jezdni); km 0 = początek osi.
        3. kontekst – długość osi i istniejące znaki w pobliżu.
        4. Ułóż plan w kilometrażu (km, strona, odsunięcie). Nigdy nie licz współrzędnych sam – robi to wtyczka.
        5. sprawdz_plan – popraw wszystkie błędy, zanim przejdziesz dalej.
        6. wstaw_plan – wtyczka pokazuje podgląd w BricsCAD, a projektant zatwierdza go w linii poleceń.
        km_punktu i punkt_na_km służą do kontroli położeń.
        """;

    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Human = TimeSpan.FromMinutes(10);

    [McpServerTool(Name = "stan", ReadOnly = true), Description("Czy BricsCAD z wtyczką jest dostępny, nazwa aktywnego rysunku i wskazana oś.")]
    public Task<string> Stan(CancellationToken ct) => Call("stan", null, Quick, ct);

    [McpServerTool(Name = "zasady", ReadOnly = true), Description("Reguły projektowania oznakowania i katalog kodów znaków pionowych i poziomych (z wymiarami linii).")]
    public async Task<string> Zasady(CancellationToken ct)
    {
        var r = await Invoke("zasady", null, Quick, ct);
        return r?["tekst"]?.GetValue<string>() ?? "";
    }

    [McpServerTool(Name = "wskaz_os"),
     Description("Ustawia oś odniesienia. Bez uchwytu prosi projektanta o wskazanie krzywej w BricsCAD i czeka (do 10 min). " +
                 "Z uchwytem (hex, np. \"2A7F\") ustawia oś bez pytania.")]
    public Task<string> WskazOs(
        [Description("Uchwyt obiektu osi w rysunku (opcjonalnie).")] string? uchwyt = null,
        CancellationToken ct = default) =>
        Call("wskaz_os", new { uchwyt }, uchwyt is null ? Human : Quick, ct);

    [McpServerTool(Name = "kontekst", ReadOnly = true), Description("Długość i końce osi oraz istniejące znaki (bloki z katalogu) w pobliżu osi: kod, km, odsunięcie (+ lewo, − prawo), status.")]
    public Task<string> Kontekst(CancellationToken ct) => Call("kontekst", null, Quick, ct);

    [McpServerTool(Name = "km_punktu", ReadOnly = true), Description("Kilometraż i odsunięcie punktu (współrzędne rysunku) względem osi.")]
    public Task<string> KmPunktu(double x, double y, CancellationToken ct) => Call("km_punktu", new { x, y }, Quick, ct);

    [McpServerTool(Name = "punkt_na_km", ReadOnly = true), Description("Współrzędne punktu na danym km osi z odsunięciem (+ lewo, − prawo) i kierunek osi.")]
    public Task<string> PunktNaKm(double km, double odsuniecie = 0, CancellationToken ct = default) =>
        Call("punkt_na_km", new { km, odsuniecie }, Quick, ct);

    [McpServerTool(Name = "sprawdz_plan", ReadOnly = true),
     Description("Waliduje plan bez rysowania: błędy, ostrzeżenia, położenia słupków i zestawienie ilości.")]
    public Task<string> SprawdzPlan([Description(PlanDescription)] PlacementPlan plan, CancellationToken ct) =>
        Call("sprawdz_plan", new { plan }, Quick, ct);

    [McpServerTool(Name = "wstaw_plan", Destructive = false),
     Description("Wstawia plan do rysunku. Wtyczka pokazuje podgląd, a projektant zatwierdza go w BricsCAD (Tak/Nie); " +
                 "narzędzie czeka na decyzję do 10 min. Wynik mówi, czy wstawiono i ile elementów.")]
    public Task<string> WstawPlan([Description(PlanDescription)] PlacementPlan plan, CancellationToken ct) =>
        Call("wstaw_plan", new { plan }, Human, ct);

    private const string PlanDescription =
        "Plan w kilometrażu osi. znaki: kod, km, strona (Prawa/Lewa względem kierunku osi), odsuniecie słupka [m] ≥ 0, " +
        "status (Proj/Tymczas/Ist/DoUsuniecia), tekst (tabliczka), przeciwny (dla ruchu przeciwnego). " +
        "linie: kod, km_od, km_do, odsuniecie [m] ze znakiem (+ lewo, − prawo), status. uwagi: krótkie uzasadnienie.";

    private async Task<string> Call(string metoda, object? parametry, TimeSpan timeout, CancellationToken ct)
    {
        var r = await Invoke(metoda, parametry, timeout, ct);
        return r?.ToJsonString(new JsonSerializerOptions(BridgeProtocol.Json) { WriteIndented = true }) ?? "{}";
    }

    private async Task<JsonNode?> Invoke(string metoda, object? parametry, TimeSpan timeout, CancellationToken ct)
    {
        try { return await bridge.CallAsync(metoda, parametry, timeout, ct); }
        catch (BridgeException ex) { throw new McpException(ex.Message); }
    }
}
