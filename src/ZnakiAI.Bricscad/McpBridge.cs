using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;
using ZnakiAI.Core.Ai;
using ZnakiAI.Core.Bridge;
using ZnakiAI.Core.Geometry;
using ZnakiAI.Core.Planning;
using CadApp = Bricscad.ApplicationServices.Application;

namespace ZnakiAI.Bricscad;

/// <summary>
/// Strona wtyczki mostu MCP. Żądania przychodzą z serwera ZnakiAI.Mcp przez named pipe
/// i są wykonywane w wątku BricsCAD: odczyty w zdarzeniu Idle, a wskazanie osi i wstawianie
/// jako zwykłe komendy (ZNAKIOS, ZNAKIMCPWSTAW). Dzięki temu każda zmiana w rysunku
/// przechodzi przez podgląd i potwierdzenie projektanta w linii poleceń, a cofa ją U.
/// </summary>
internal static class McpBridge
{
    private static BridgeServer? _server;
    private static bool _idleHooked;
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static readonly SemaphoreSlim Interactive = new(1, 1);
    private static TaskCompletionSource<string?>? _interactiveDone;
    private static PlacementPlan? _pendingPlan;
    private static Commands.ApplyOutcome? _lastOutcome;
    private static (string Doc, string Handle)? _axis;

    private static readonly TimeSpan InteractiveTimeout = TimeSpan.FromMinutes(10);

    public static bool Running => _server?.Running == true;

    public static void Start()
    {
        if (!_idleHooked)
        {
            CadApp.Idle += OnIdle;
            _idleHooked = true;
        }
        _server ??= new BridgeServer(Handle);
        _server.Start();
    }

    public static void Stop()
    {
        _server?.Dispose();
        _server = null;
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        while (Queue.TryDequeue(out var a)) a();
    }

    /// <summary>Wykonanie w wątku BricsCAD (najbliższe Idle).</summary>
    private static Task<T> OnMain<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Enqueue(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    internal static void RememberAxis(Database db, ObjectId id)
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        _axis = (doc?.Name ?? "", id.Handle.ToString());
    }

    /// <summary>Wywoływane przez ZNAKIOS / ZNAKIMCPWSTAW po zakończeniu; null = sukces.</summary>
    internal static void CompleteInteractive(string? error) => _interactiveDone?.TrySetResult(error);

    private static async Task<JsonNode?> Handle(BridgeRequest req, CancellationToken ct)
    {
        var p = req.Parametry ?? new JsonObject();
        switch (req.Metoda)
        {
            case "stan":
                return await OnMain(() =>
                {
                    var doc = CadApp.DocumentManager.MdiActiveDocument;
                    JsonNode? os = null;
                    if (_axis is { } a && doc is not null && a.Doc == doc.Name)
                    {
                        try { os = AxisInfo(ReadAxis(doc, out _)); } catch (BridgeException) { }
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["rysunek"] = doc?.Name,
                        ["os"] = os,
                    };
                });

            case "zasady":
                return new JsonObject { ["tekst"] = AiPlanner.BuildSystemPrompt(Plugin.Catalog) };

            case "wskaz_os":
                if (p["uchwyt"]?.GetValue<string>() is { Length: > 0 } h)
                {
                    return await OnMain(() =>
                    {
                        var doc = Active();
                        var previous = _axis;
                        _axis = (doc.Name, h.Trim().ToUpperInvariant());
                        try { return AxisInfo(ReadAxis(doc, out _)); }
                        catch { _axis = previous; throw; }
                    });
                }
                await RunInteractive("ZNAKIOS", null, ct);
                return await OnMain(() => AxisInfo(ReadAxis(Active(), out _)));

            case "kontekst":
                return await OnMain(() =>
                {
                    var doc = Active();
                    var axis = ReadAxis(doc, out var id);
                    var ist = new JsonArray();
                    foreach (var o in Commands.CollectExisting(doc.Database, axis, id).OrderBy(o => o.Km))
                        ist.Add(new JsonObject { ["kod"] = o.Kod, ["km"] = o.Km, ["odsuniecie"] = o.Odsuniecie, ["status"] = o.Status });
                    var r = AxisInfo(axis);
                    r["promien_szukania"] = Plugin.Config.PromienSzukaniaIstniejacych;
                    r["istniejace"] = ist;
                    return r;
                });

            case "km_punktu":
            {
                double x = Num(p, "x"), y = Num(p, "y");
                var axis = await OnMain(() => ReadAxis(Active(), out _));
                return PlanTools.PointInfo(axis, x, y);
            }

            case "punkt_na_km":
            {
                double km = Num(p, "km"), off = p["odsuniecie"]?.GetValue<double>() ?? 0;
                var axis = await OnMain(() => ReadAxis(Active(), out _));
                return PlanTools.PointAt(axis, km, off);
            }

            case "sprawdz_plan":
            {
                var plan = ParsePlan(p);
                var axis = await OnMain(() => ReadAxis(Active(), out _));
                return PlanTools.Check(plan, axis, Plugin.Catalog, Plugin.Config.ToDrawingSettings());
            }

            case "wstaw_plan":
            {
                var plan = ParsePlan(p);
                await OnMain(() => ReadAxis(Active(), out _)); // szybki błąd, gdy brak osi
                _lastOutcome = null;
                await RunInteractive("ZNAKIMCPWSTAW", () => _pendingPlan = plan, ct);
                return _lastOutcome is { } o
                    ? JsonSerializer.SerializeToNode(o, BridgeProtocol.Json)
                    : throw new BridgeException("Wstawianie przerwane w BricsCAD.");
            }

            default:
                throw new BridgeException($"Nieznana metoda: {req.Metoda}");
        }
    }

    /// <summary>Wykonanie komendy w BricsCAD i czekanie, aż projektant ją zakończy.</summary>
    private static async Task RunInteractive(string command, Action? prepare, CancellationToken ct)
    {
        if (!await Interactive.WaitAsync(0, ct))
            throw new BridgeException("W BricsCAD trwa już inna operacja z Claude. Dokończ ją w linii poleceń.");
        try
        {
            var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _interactiveDone = done;
            prepare?.Invoke();
            await OnMain(() =>
            {
                Active().SendStringToExecute($"_.{command} ", true, false, false);
                return true;
            });
            var finished = await Task.WhenAny(done.Task, Task.Delay(InteractiveTimeout, ct));
            if (finished != done.Task)
                throw new BridgeException($"Projektant nie zakończył komendy {command} w BricsCAD.");
            if (await done.Task is { } err) throw new BridgeException(err);
        }
        finally
        {
            _interactiveDone = null;
            _pendingPlan = null;
            Interactive.Release();
        }
    }

    /// <summary>Komenda ZNAKIMCPWSTAW: plan z MCP, podgląd i pytanie Tak/Nie jak w ZNAKIPLAN.</summary>
    internal static void RunPendingInsert()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (_pendingPlan is not { } plan)
        {
            doc.Editor.WriteMessage("\nBrak planu przekazanego przez Claude.\n");
            return;
        }
        try
        {
            var axis = ReadAxis(doc, out _);
            doc.Editor.WriteMessage("\nPlan od Claude (MCP):");
            _lastOutcome = Commands.ApplyPlan(doc, axis, plan, confirm: true);
            CompleteInteractive(null);
        }
        catch (Exception ex)
        {
            doc.Editor.WriteMessage($"\n{ex.Message}\n");
            CompleteInteractive(ex.Message);
        }
    }

    private static Document Active() =>
        CadApp.DocumentManager.MdiActiveDocument ?? throw new BridgeException("W BricsCAD nie ma otwartego rysunku.");

    private static Alignment ReadAxis(Document doc, out ObjectId id)
    {
        if (_axis is not { } a)
            throw new BridgeException("Nie wskazano osi. Użyj narzędzia wskaz_os albo komendy ZNAKIOS w BricsCAD.");
        if (a.Doc != doc.Name)
            throw new BridgeException($"Oś wskazano w innym rysunku ({Path.GetFileName(a.Doc)}). Wskaż ją ponownie.");
        try
        {
            id = doc.Database.GetObjectId(false, new Handle(Convert.ToInt64(a.Handle, 16)), 0);
        }
        catch (Exception)
        {
            throw new BridgeException($"Nie ma obiektu o uchwycie {a.Handle}. Wskaż oś ponownie.");
        }
        if (id.IsNull || id.IsErased)
            throw new BridgeException("Oś została usunięta z rysunku. Wskaż ją ponownie.");
        using var tr = doc.Database.TransactionManager.StartTransaction();
        try
        {
            var axis = AxisReader.ToAlignment((Entity)tr.GetObject(id, OpenMode.ForRead));
            tr.Commit();
            return axis;
        }
        catch (Exception ex) when (ex is not BridgeException)
        {
            throw new BridgeException($"Obiekt {a.Handle} nie nadaje się na oś: {ex.Message}");
        }
    }

    private static JsonObject AxisInfo(Alignment axis)
    {
        var a = axis.PointAt(0);
        var b = axis.PointAt(axis.Length);
        return new JsonObject
        {
            ["uchwyt"] = _axis?.Handle,
            ["dlugosc"] = PlanTools.R(axis.Length),
            ["poczatek"] = new JsonArray(PlanTools.R(a.X), PlanTools.R(a.Y)),
            ["koniec"] = new JsonArray(PlanTools.R(b.X), PlanTools.R(b.Y)),
        };
    }

    private static double Num(JsonObject p, string key) =>
        p[key]?.GetValue<double>() ?? throw new BridgeException($"Brak parametru {key}.");

    private static PlacementPlan ParsePlan(JsonObject p)
    {
        var node = p["plan"] ?? throw new BridgeException("Brak parametru plan.");
        try { return PlacementPlan.FromJson(node.ToJsonString()); }
        catch (JsonException ex) { throw new BridgeException($"Niepoprawny plan: {ex.Message}"); }
    }
}
