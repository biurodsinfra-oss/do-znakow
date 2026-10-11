using System.Diagnostics;
using System.Reflection;
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using Teigha.DatabaseServices;
using Teigha.Runtime;
using ZnakiAI.Core;
using ZnakiAI.Core.Ai;
using ZnakiAI.Core.Catalog;
using ZnakiAI.Core.Geometry;
using ZnakiAI.Core.Planning;
using CadApp = Bricscad.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(ZnakiAI.Bricscad.Plugin))]
[assembly: CommandClass(typeof(ZnakiAI.Bricscad.Commands))]

namespace ZnakiAI.Bricscad;

public sealed class Plugin : IExtensionApplication
{
    internal static PluginConfig Config { get; private set; } = new();
    internal static SignCatalog Catalog { get; private set; } = null!;
    private static AiPlanner? _planner;

    /// <summary>Planer tworzony leniwie – brak klucza API nie blokuje komend bez AI.</summary>
    internal static AiPlanner Planner => _planner ??= new AiPlanner(Catalog, Config.ToAiSettings());

    public void Initialize()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        Reload(Path.Combine(dir, "znakiai.config.json"));
        CadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
            "\nZnakiAI załadowane. Komendy: ZNAKIAI, ZNAKIPLAN, ZNAKIKM, ZNAKIKATALOG.\n");
    }

    internal static void Reload(string configPath)
    {
        Config = PluginConfig.Load(configPath);
        Catalog = SignCatalog.Load(Config.Resolve(Config.KatalogZnakow), Config.Resolve(Config.KatalogLinii));
        _planner = null;
    }

    public void Terminate() { }
}

public sealed class Commands
{
    /// <summary>AI: polecenie słowne → plan → walidacja → wstawienie.</summary>
    [CommandMethod("ZNAKIAI", CommandFlags.Modal)]
    public void ZnakiAi()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;
        if (!TryPickAxis(ed, doc.Database, out var axis, out var axisId)) return;

        var ctx = new DrawingContext
        {
            DlugoscOsi = axis.Length,
            OpisOsi = "wskazana oś odniesienia (krawędź jezdni)",
            Istniejace = CollectExisting(doc.Database, axis, axisId),
        };

        // opcjonalna strefa robót
        var z1 = ed.GetPoint(new PromptPointOptions("\nPoczątek strefy robót <pomiń>: ") { AllowNone = true });
        if (z1.Status == PromptStatus.OK)
        {
            var z2 = ed.GetPoint(new PromptPointOptions("\nKoniec strefy robót: ") { BasePoint = z1.Value, UseBasePoint = true });
            if (z2.Status == PromptStatus.OK)
            {
                double a = axis.Project(new Vec2(z1.Value.X, z1.Value.Y)).Chainage;
                double b = axis.Project(new Vec2(z2.Value.X, z2.Value.Y)).Chainage;
                ctx = ctx with { StrefaRobot = (Math.Min(a, b), Math.Max(a, b)) };
            }
        }

        var zab = new PromptKeywordOptions("\nObszar zabudowany? [Tak/Nie] <Tak>: ") { AllowNone = true };
        zab.Keywords.Add("Tak");
        zab.Keywords.Add("Nie");
        var zr = ed.GetKeywords(zab);
        if (zr.Status == PromptStatus.Cancel) return;
        ctx = ctx with { ObszarZabudowany = zr.StringResult != "Nie" };

        var instr = ed.GetString(new PromptStringOptions("\nCo zaprojektować? ") { AllowSpaces = true });
        if (instr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(instr.StringResult)) return;

        ed.WriteMessage($"\nAI planuje ({Plugin.Config.Model}, effort {Plugin.Config.Effort})...");
        AiPlanResult ai;
        try
        {
            // osobny wątek: brak zakleszczenia na kontekście synchronizacji UI
            ai = Task.Run(() => Plugin.Planner.PlanAsync(instr.StringResult, ctx)).GetAwaiter().GetResult();
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nBłąd AI: {ex.Message}\n");
            return;
        }
        ed.WriteMessage($"\nPlan gotowy w {ai.Elapsed.TotalSeconds:0.0} s " +
                        $"(tokeny: wej. {ai.InputTokens}, z cache {ai.CacheReadTokens}, wyj. {ai.OutputTokens}).");
        SavePlan(doc, ai.Plan);
        ApplyPlan(doc, axis, ai.Plan, confirm: true);
    }

    /// <summary>Bez AI: wczytanie planu JSON (np. poprawionego ręcznie) i wstawienie.</summary>
    [CommandMethod("ZNAKIPLAN", CommandFlags.Modal)]
    public void ZnakiPlan()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;
        if (!TryPickAxis(ed, doc.Database, out var axis, out _)) return;
        var f = ed.GetFileNameForOpen(new PromptOpenFileOptions("Plan oznakowania (JSON)") { Filter = "Plan JSON (*.json)|*.json" });
        if (f.Status != PromptStatus.OK) return;
        PlacementPlan plan;
        try { plan = PlacementPlan.FromJson(File.ReadAllText(f.StringResult)); }
        catch (System.Exception ex) { ed.WriteMessage($"\nNiepoprawny plik planu: {ex.Message}\n"); return; }
        ApplyPlan(doc, axis, plan, confirm: true);
    }

    /// <summary>Kontrola precyzji: kilometraż i odsunięcie wskazanego punktu względem osi.</summary>
    [CommandMethod("ZNAKIKM", CommandFlags.Modal)]
    public void ZnakiKm()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;
        if (!TryPickAxis(ed, doc.Database, out var axis, out _)) return;
        while (true)
        {
            var r = ed.GetPoint(new PromptPointOptions("\nPunkt <koniec>: ") { AllowNone = true });
            if (r.Status != PromptStatus.OK) break;
            var (s, off, _) = axis.Project(new Vec2(r.Value.X, r.Value.Y));
            ed.WriteMessage($"\nkm {s:0.000} m, odsunięcie {Math.Abs(off):0.000} m ({(off >= 0 ? "lewa" : "prawa")})");
        }
    }

    [CommandMethod("ZNAKIKATALOG", CommandFlags.Modal)]
    public void ZnakiKatalog()
    {
        var ed = CadApp.DocumentManager.MdiActiveDocument.Editor;
        ed.WriteMessage("\n" + Plugin.Catalog.ToPromptText());
    }

    // ---------------------------------------------------------------------------------------

    private static bool TryPickAxis(Editor ed, Database db, out Alignment axis, out ObjectId id)
    {
        axis = null!;
        id = ObjectId.Null;
        var peo = new PromptEntityOptions("\nWskaż oś odniesienia (krawędź jezdni) – kierunek wg kierunku rysowania: ");
        peo.SetRejectMessage("\nTo musi być krzywa (polilinia, linia, łuk, splajn).");
        peo.AddAllowedClass(typeof(Curve), false);
        var r = ed.GetEntity(peo);
        if (r.Status != PromptStatus.OK) return false;
        using var tr = db.TransactionManager.StartTransaction();
        try
        {
            axis = AxisReader.ToAlignment((Entity)tr.GetObject(r.ObjectId, OpenMode.ForRead));
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n{ex.Message}\n");
            return false;
        }
        tr.Commit();
        id = r.ObjectId;
        ed.WriteMessage($"\nOś: długość {axis.Length:0.000} m.");
        return true;
    }

    /// <summary>Bloki znaków w pobliżu osi (nazwa bloku = kod z katalogu) jako kontekst dla AI.</summary>
    private static List<ExistingObject> CollectExisting(Database db, Alignment axis, ObjectId axisId)
    {
        var result = new List<ExistingObject>();
        double radius = Plugin.Config.PromienSzukaniaIstniejacych;
        using var tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        foreach (ObjectId oid in ms)
        {
            if (oid == axisId || !oid.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(BlockReference)))) continue;
            var br = (BlockReference)tr.GetObject(oid, OpenMode.ForRead);
            string name = br.IsDynamicBlock
                ? ((BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name
                : br.Name;
            if (!Plugin.Catalog.TryGetSign(name, out var def)) continue;
            var (s, off, dist) = axis.Project(new Vec2(br.Position.X, br.Position.Y));
            if (dist > radius) continue;
            string status = br.Layer.Contains("TYMCZ", StringComparison.OrdinalIgnoreCase) ? "tymczas"
                : br.Layer.Contains("PROJ", StringComparison.OrdinalIgnoreCase) ? "proj" : "ist";
            result.Add(new ExistingObject(def.Kod, Math.Round(s, 2), Math.Round(off, 2), status));
        }
        tr.Commit();
        return result;
    }

    private static void ApplyPlan(Document doc, Alignment axis, PlacementPlan plan, bool confirm)
    {
        var ed = doc.Editor;
        var sw = Stopwatch.StartNew();
        var res = new PlanResolver(Plugin.Catalog, Plugin.Config.ToDrawingSettings()).Resolve(plan, axis);

        ed.WriteMessage($"\n--- Plan: {res.Signs.Count} znaków pionowych, {res.Markings.Count} linii ---");
        foreach (var s in res.Signs.OrderBy(s => s.Source.Km))
            ed.WriteMessage($"\n  {s.Def.Kod,-8} km {s.Source.Km,8:0.00}  {s.Source.Strona,-5} {s.Source.Odsuniecie:0.00} m  {s.Source.Status}" +
                            (string.IsNullOrEmpty(s.Source.Tekst) ? "" : $"  [{s.Source.Tekst}]"));
        foreach (var m in res.Markings)
            ed.WriteMessage($"\n  {m.Def.Kod,-8} km {m.Source.KmOd:0.00}–{m.Source.KmDo:0.00}  odsunięcie {m.Source.Odsuniecie:0.00} m  ({m.Strokes.Count} el.)");
        foreach (var e in res.Errors) ed.WriteMessage($"\n  BŁĄD: {e}");
        foreach (var w in res.Warnings.Distinct()) ed.WriteMessage($"\n  Uwaga: {w}");
        if (!string.IsNullOrWhiteSpace(plan.Uwagi)) ed.WriteMessage($"\n  AI: {plan.Uwagi}");

        if (res.Signs.Count + res.Markings.Count == 0) { ed.WriteMessage("\nNic do wstawienia.\n"); return; }

        bool Ask()
        {
            var k = new PromptKeywordOptions("\nPodgląd na rysunku. Wstawić? [Tak/Nie] <Tak>: ") { AllowNone = true };
            k.Keywords.Add("Tak");
            k.Keywords.Add("Nie");
            var kr = ed.GetKeywords(k);
            return kr.Status != PromptStatus.Cancel && kr.StringResult != "Nie";
        }

        void Refresh()
        {
            // pokazuje obiekty z niezatwierdzonej transakcji (podgląd) albo ich zniknięcie po wycofaniu
            doc.TransactionManager.QueueForGraphicsFlush();
            doc.TransactionManager.FlushGraphics();
            ed.UpdateScreen();
        }

        using (doc.LockDocument())
        {
            var writer = new CadWriter(doc.Database, Plugin.Config);
            var result = writer.Write(res, confirm ? Ask : null, confirm ? Refresh : null);
            foreach (var line in writer.Log) ed.WriteMessage($"\n  {line}");
            if (result is not { } r)
            {
                ed.WriteMessage("\nOdrzucono – rysunek bez zmian. Plan JSON można poprawić i wstawić przez ZNAKIPLAN.\n");
                return;
            }
            ed.WriteMessage($"\nWstawiono {r.Signs} znaków i {r.Strokes} elementów linii w {sw.ElapsedMilliseconds} ms. Cofnij: U.\n");
        }
    }

    /// <summary>Zapis planu obok rysunku – ślad decyzji AI i możliwość poprawki + ZNAKIPLAN.</summary>
    private static void SavePlan(Document doc, PlacementPlan plan)
    {
        try
        {
            string dir = Path.GetDirectoryName(doc.Name) is { Length: > 0 } d && Directory.Exists(d) ? d : Path.GetTempPath();
            string file = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(doc.Name)}_plan_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(file, plan.ToJson());
            doc.Editor.WriteMessage($"\nPlan zapisany: {file}");
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage($"\nNie zapisano planu: {ex.Message}");
        }
    }
}
