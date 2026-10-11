using System.Text.Json;
using ZnakiAI.Core.Ai;
using ZnakiAI.Core.Planning;

namespace ZnakiAI.Core;

/// <summary>Konfiguracja wtyczki (znakiai.config.json obok DLL).</summary>
public sealed class PluginConfig
{
    public string BibliotekaDwg { get; set; } = "";
    public string KatalogZnakow { get; set; } = "catalog/znaki_pionowe.json";
    public string KatalogLinii { get; set; } = "catalog/znaki_poziome.json";
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "medium";
    public double SkalaSymbolu { get; set; } = 1.0;
    public double OdlegloscSymbolu { get; set; } = 4.0;
    public double PromienSzukaniaIstniejacych { get; set; } = 30.0;
    public double WysokoscTekstuTabeli { get; set; } = 1.25;
    public string WarstwaPionProj { get; set; } = "ZN-PION-PROJ";
    public string WarstwaPionTymczas { get; set; } = "ZN-PION-TYMCZ";
    public string WarstwaPionIst { get; set; } = "ZN-PION-IST";
    public string WarstwaPoz { get; set; } = "ZN-POZ-PROJ";
    public string WarstwaPozTymczas { get; set; } = "ZN-POZ-TYMCZ";

    /// <summary>Folder, względem którego rozwiązywane są ścieżki względne.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string BaseDir { get; private set; } = AppContext.BaseDirectory;

    public static PluginConfig Load(string path)
    {
        var cfg = File.Exists(path)
            ? JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? new PluginConfig()
            : new PluginConfig();
        cfg.BaseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return cfg;
    }

    public string Resolve(string p) => string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) ? p : Path.Combine(BaseDir, p);

    public DrawingSettings ToDrawingSettings() => new()
    {
        SymbolScale = SkalaSymbolu,
        SymbolDistance = OdlegloscSymbolu,
    };

    public AiSettings ToAiSettings() => new() { Model = Model, Effort = Effort };

    public string SignLayer(SignStatus s) => s switch
    {
        SignStatus.Tymczas => WarstwaPionTymczas,
        SignStatus.Ist => WarstwaPionIst,
        _ => WarstwaPionProj,
    };

    public string MarkingLayer(SignStatus s) => s == SignStatus.Tymczas ? WarstwaPozTymczas : WarstwaPoz;
}
