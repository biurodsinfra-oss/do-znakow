using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ZnakiAI.Core.Bridge;

/// <summary>
/// Most między serwerem MCP (osobny proces uruchamiany przez Claude Desktop / Claude Code)
/// a wtyczką w BricsCAD. Lokalny named pipe dostępny tylko dla bieżącego użytkownika Windows.
/// Jedno połączenie = jedno żądanie i jedna odpowiedź (linia JSON).
/// </summary>
public static class BridgeProtocol
{
    public const string PipeName = "ZnakiAI.Bricscad";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed record BridgeRequest(
    [property: JsonPropertyName("metoda")] string Metoda,
    [property: JsonPropertyName("parametry")] JsonObject? Parametry);

public sealed record BridgeResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("wynik")] JsonNode? Wynik,
    [property: JsonPropertyName("blad")] string? Blad);

/// <summary>Błąd zgłoszony przez wtyczkę (np. brak osi) – treść jest dla użytkownika.</summary>
public sealed class BridgeException(string message) : Exception(message);

/// <summary>Strona wtyczki: przyjmuje żądania i przekazuje je do obsługi.</summary>
public sealed class BridgeServer : IDisposable
{
    private readonly Func<BridgeRequest, CancellationToken, Task<JsonNode?>> _handler;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public BridgeServer(Func<BridgeRequest, CancellationToken, Task<JsonNode?>> handler, string pipeName = BridgeProtocol.PipeName)
    {
        _handler = handler;
        _pipeName = pipeName;
    }

    public bool Running => _loop is { IsCompleted: false };

    public void Start() => _loop ??= Task.Run(() => AcceptLoop(_cts.Token));

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            _ = Task.Run(() => Serve(pipe, ct), CancellationToken.None);
        }
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        BridgeResponse resp;
        try
        {
            var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            var req = JsonSerializer.Deserialize<BridgeRequest>(line ?? "", BridgeProtocol.Json)
                      ?? throw new BridgeException("Puste żądanie.");
            resp = new BridgeResponse(true, await _handler(req, ct).ConfigureAwait(false), null);
        }
        catch (BridgeException ex) { resp = new BridgeResponse(false, null, ex.Message); }
        catch (Exception ex) { resp = new BridgeResponse(false, null, $"{ex.GetType().Name}: {ex.Message}"); }
        try
        {
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(resp, BridgeProtocol.Json)).ConfigureAwait(false);
            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException) { /* klient się rozłączył */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(1000); } catch { }
        _cts.Dispose();
    }
}

/// <summary>Strona serwera MCP: wywołuje metodę we wtyczce.</summary>
public sealed class BridgeClient(string pipeName = BridgeProtocol.PipeName)
{
    public async Task<JsonNode?> CallAsync(string metoda, object? parametry, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(3000, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            throw new BridgeException("Brak połączenia z BricsCAD. Uruchom BricsCAD, wczytaj wtyczkę ZnakiAI (NETLOAD) " +
                                      "i sprawdź, czy most jest włączony (komenda ZNAKIMCP).");
        }

        var obj = parametry is null ? null : JsonSerializer.SerializeToNode(parametry, BridgeProtocol.Json) as JsonObject;
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new BridgeRequest(metoda, obj), BridgeProtocol.Json)).ConfigureAwait(false);
        await writer.FlushAsync(cts.Token).ConfigureAwait(false);

        var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        string? line;
        try { line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new BridgeException($"BricsCAD nie odpowiedział w {timeout.TotalSeconds:0} s. " +
                                      "Jeśli trwa inna komenda albo czeka pytanie w linii poleceń, dokończ ją.");
        }
        var resp = JsonSerializer.Deserialize<BridgeResponse>(line ?? "", BridgeProtocol.Json)
                   ?? throw new BridgeException("Pusta odpowiedź z BricsCAD.");
        if (!resp.Ok) throw new BridgeException(resp.Blad ?? "Nieznany błąd wtyczki.");
        return resp.Wynik;
    }
}
