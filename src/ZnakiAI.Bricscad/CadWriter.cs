using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using ZnakiAI.Core;
using ZnakiAI.Core.Catalog;
using ZnakiAI.Core.Planning;
using static ZnakiAI.Bricscad.AxisReader;

namespace ZnakiAI.Bricscad;

/// <summary>
/// Zapis wyniku do rysunku. Wszystko w jednej transakcji – jedno polecenie Cofnij (U) wycofuje całość.
/// </summary>
internal sealed class CadWriter
{
    private readonly Database _db;
    private readonly PluginConfig _cfg;

    public CadWriter(Database db, PluginConfig cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    public (int Signs, int Strokes) Write(ResolveResult res)
    {
        int strokes = 0;
        EnsureBlocks(res.Signs.Select(s => s.Def).DistinctBy(d => d.BlockName));

        using var tr = _db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

        foreach (var s in res.Signs)
        {
            string layer = EnsureLayer(tr, _cfg.SignLayer(s.Source.Status), s.Source.Status == SignStatus.Tymczas ? (short)30 : (short)1);

            // słupek + odnośnik do symbolu
            var post = new Circle(P3(s.Post), Vector3d.ZAxis, 0.15) { Layer = layer };
            Add(ms, tr, post);
            var leader = new Line(P3(s.Post), P3(s.Symbol)) { Layer = layer };
            Add(ms, tr, leader);

            var btrId = bt[s.Def.BlockName];
            var br = new BlockReference(P3(s.Symbol), btrId)
            {
                Rotation = s.Rotation,
                ScaleFactors = new Scale3d(_cfg.SkalaSymbolu),
                Layer = layer,
            };
            Add(ms, tr, br);
            FillAttributes(tr, br, btrId, s);
        }

        foreach (var m in res.Markings)
        {
            string layer = EnsureLayer(tr, _cfg.MarkingLayer(m.Source.Status), m.Source.Status == SignStatus.Tymczas ? (short)30 : (short)7);
            foreach (var stroke in m.Strokes)
            {
                // polilinia o stałej szerokości = rzeczywista szerokość linii; drukuje się 1:1
                var pl = new Polyline { Layer = layer };
                for (int i = 0; i < stroke.Count; i++) pl.AddVertexAt(i, P2(stroke[i]), 0, m.Width, m.Width);
                pl.ConstantWidth = m.Width;
                Add(ms, tr, pl);
                strokes++;
            }
        }

        tr.Commit();
        return (res.Signs.Count, strokes);
    }

    private static void Add(BlockTableRecord ms, Transaction tr, Entity e)
    {
        ms.AppendEntity(e);
        tr.AddNewlyCreatedDBObject(e, true);
    }

    private static string StatusLabel(SignStatus s) => s switch
    {
        SignStatus.Tymczas => "tymczas",
        SignStatus.Ist => "ist",
        SignStatus.DoUsuniecia => "do usunięcia",
        _ => "proj",
    };

    /// <summary>
    /// Wypełnia atrybuty po tagu. Obsługuje bloki zastępcze (KOD/STATUS/TEKST) oraz typowe
    /// tagi bibliotek znaków (ID, ADDTEXT, SIZE...). Nieznane tagi zostają z wartością domyślną.
    /// </summary>
    private static void FillAttributes(Transaction tr, BlockReference br, ObjectId btrId, ResolvedSign s)
    {
        var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
        if (!btr.HasAttributeDefinitions) return;
        foreach (ObjectId id in btr)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not AttributeDefinition ad || ad.Constant) continue;
            var ar = new AttributeReference();
            ar.SetAttributeFromBlock(ad, br.BlockTransform);
            ar.TextString = ad.Tag.ToUpperInvariant() switch
            {
                "KOD" or "ID" or "ITEMNO" => s.Def.Kod,
                "STATUS" => StatusLabel(s.Source.Status),
                "TEKST" or "ADDTEXT" => s.Source.Tekst ?? "",
                "KM" or "POSITION" => s.Source.Km.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                _ => ad.TextString,
            };
            br.AttributeCollection.AppendAttribute(ar);
            tr.AddNewlyCreatedDBObject(ar, true);
        }
    }

    private string EnsureLayer(Transaction tr, string name, short aci)
    {
        var lt = (LayerTable)tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(name)) return name;
        lt.UpgradeOpen();
        var ltr = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, aci) };
        lt.Add(ltr);
        tr.AddNewlyCreatedDBObject(ltr, true);
        return name;
    }

    /// <summary>Import brakujących bloków z biblioteki DWG; gdy brak – tworzy blok zastępczy.</summary>
    private void EnsureBlocks(IEnumerable<SignDef> defs)
    {
        var missing = new List<SignDef>();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var bt = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
            missing.AddRange(defs.Where(d => !bt.Has(d.BlockName)));
            tr.Commit();
        }
        if (missing.Count == 0) return;

        var lib = _cfg.Resolve(_cfg.BibliotekaDwg);
        if (!string.IsNullOrWhiteSpace(lib) && File.Exists(lib))
            ImportFromLibrary(lib, missing);
        else if (!string.IsNullOrWhiteSpace(lib) && Directory.Exists(lib))
            ImportFromFolder(lib, missing);

        using (var tr = _db.TransactionManager.StartTransaction())
        {
            var bt = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForWrite);
            foreach (var d in missing.Where(d => !bt.Has(d.BlockName)))
                CreatePlaceholderBlock(tr, bt, d);
            tr.Commit();
        }
    }

    /// <summary>Komunikaty o źródle bloków (do wypisania w linii komend).</summary>
    public List<string> Log { get; } = new();

    /// <summary>Indeks plików DWG w folderze biblioteki: nazwa pliku bez rozszerzenia → ścieżka. Budowany raz na sesję.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> FolderIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nazwy bloków zdefiniowanych w danym pliku DWG (cache na sesję).</summary>
    private static readonly Dictionary<string, HashSet<string>> FileBlocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Klucz porównania nazw: bez wielkości liter, spacji i separatorów ("C-13/16" = "C-13_16" = "c 13-16").</summary>
    private static string Key(string name) =>
        new string(name.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Biblioteka jako folder (np. C:\ProgramData\GACAD):
    /// 1) plik DWG nazwany kodem znaku (A-14.dwg) wstawiany jako blok,
    /// 2) pozostałe szukane jako definicje bloków wewnątrz plików DWG z folderu.
    /// </summary>
    private void ImportFromFolder(string folder, List<SignDef> wanted)
    {
        if (!FolderIndex.TryGetValue(folder, out var index))
        {
            index = new Dictionary<string, string>();
            foreach (var f in Directory.EnumerateFiles(folder, "*.dwg", new EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                index.TryAdd(Key(Path.GetFileNameWithoutExtension(f)), f);
            FolderIndex[folder] = index;
            Log.Add($"Biblioteka: {index.Count} plików DWG w {folder}.");
        }

        var rest = new List<SignDef>();
        foreach (var d in wanted)
        {
            if (index.TryGetValue(Key(d.BlockName), out var file) || index.TryGetValue(Key(d.Kod), out file))
            {
                try
                {
                    using var src = new Database(false, true);
                    src.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
                    _db.Insert(d.BlockName, src, true);
                    Log.Add($"{d.Kod}: blok z pliku {file}");
                    continue;
                }
                catch (System.Exception ex)
                {
                    Log.Add($"{d.Kod}: nie wczytano {file} ({ex.Message})");
                }
            }
            rest.Add(d);
        }

        foreach (var file in index.Values)
        {
            if (rest.Count == 0) break;
            if (!FileBlocks.TryGetValue(file, out var names))
            {
                names = new HashSet<string>();
                try
                {
                    using var src = new Database(false, true);
                    src.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
                    using var tr = src.TransactionManager.StartTransaction();
                    var bt = (BlockTable)tr.GetObject(src.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId id in bt)
                        names.Add(Key(((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name));
                    tr.Commit();
                }
                catch (System.Exception) { /* plik uszkodzony / zablokowany – pomijamy */ }
                FileBlocks[file] = names;
            }
            var hits = rest.Where(d => names.Contains(Key(d.BlockName)) || names.Contains(Key(d.Kod))).ToList();
            if (hits.Count == 0) continue;
            ImportFromLibrary(file, hits);
            foreach (var h in hits) Log.Add($"{h.Kod}: blok z biblioteki {file}");
            rest.RemoveAll(hits.Contains);
        }

        foreach (var d in rest) Log.Add($"{d.Kod}: brak w bibliotece – blok zastępczy.");
    }

    private void ImportFromLibrary(string libPath, List<SignDef> wanted)
    {
        using var src = new Database(false, true);
        src.ReadDwgFile(libPath, FileOpenMode.OpenForReadAndAllShare, true, "");
        var ids = new ObjectIdCollection();
        var renames = new Dictionary<ObjectId, string>(); // blok w bibliotece ma inną pisownię niż kod
        using (var tr = src.TransactionManager.StartTransaction())
        {
            var bt = (BlockTable)tr.GetObject(src.BlockTableId, OpenMode.ForRead);
            var byKey = new Dictionary<string, ObjectId>();
            foreach (ObjectId id in bt)
                byKey.TryAdd(Key(((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name), id);
            foreach (var d in wanted)
            {
                if (bt.Has(d.BlockName)) ids.Add(bt[d.BlockName]);
                else if (byKey.TryGetValue(Key(d.BlockName), out var id) || byKey.TryGetValue(Key(d.Kod), out id))
                {
                    ids.Add(id);
                    renames[id] = d.BlockName;
                }
            }
            tr.Commit();
        }
        if (ids.Count == 0) return;
        var map = new IdMapping();
        src.WblockCloneObjects(ids, _db.BlockTableId, map, DuplicateRecordCloning.Ignore, false);
        if (renames.Count == 0) return;
        // ujednolicenie nazwy, by wstawianie po BlockName znalazło blok
        using var tr2 = _db.TransactionManager.StartTransaction();
        var dbt = (BlockTable)tr2.GetObject(_db.BlockTableId, OpenMode.ForRead);
        foreach (var (srcId, name) in renames)
        {
            if (dbt.Has(name)) continue;
            var btr = (BlockTableRecord)tr2.GetObject(map[srcId].Value, OpenMode.ForWrite);
            btr.Name = name;
        }
        tr2.Commit();
    }

    /// <summary>Prosty blok: kształt wg grupy znaku + atrybuty KOD/STATUS/TEKST. Wymiar ~1 m przy skali 1.</summary>
    private static void CreatePlaceholderBlock(Transaction tr, BlockTable bt, SignDef d)
    {
        var btr = new BlockTableRecord { Name = d.BlockName, Origin = Point3d.Origin };
        bt.Add(btr);
        tr.AddNewlyCreatedDBObject(btr, true);

        void Add(Entity e)
        {
            btr.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
        }

        const double r = 0.5;
        switch (d.Ksztalt)
        {
            case "trojkat":
            case "trojkat_odwrocony":
            {
                double k = d.Ksztalt == "trojkat" ? 1 : -1;
                var pl = new Polyline();
                pl.AddVertexAt(0, new Point2d(-r, -k * r * 0.5), 0, 0, 0);
                pl.AddVertexAt(1, new Point2d(r, -k * r * 0.5), 0, 0, 0);
                pl.AddVertexAt(2, new Point2d(0, k * r), 0, 0, 0);
                pl.Closed = true;
                Add(pl);
                break;
            }
            case "kolo":
                Add(new Circle(Point3d.Origin, Vector3d.ZAxis, r));
                break;
            default:
            {
                var pl = new Polyline();
                pl.AddVertexAt(0, new Point2d(-r, -r), 0, 0, 0);
                pl.AddVertexAt(1, new Point2d(r, -r), 0, 0, 0);
                pl.AddVertexAt(2, new Point2d(r, r), 0, 0, 0);
                pl.AddVertexAt(3, new Point2d(-r, r), 0, 0, 0);
                pl.Closed = true;
                Add(pl);
                break;
            }
        }

        Add(new AttributeDefinition(new Point3d(-r, -r - 0.35, 0), d.Kod, "KOD", "Kod znaku", ObjectId.Null) { Height = 0.25 });
        Add(new AttributeDefinition(new Point3d(-r, -r - 0.70, 0), "proj", "STATUS", "Status", ObjectId.Null) { Height = 0.20 });
        Add(new AttributeDefinition(new Point3d(-r, -r - 1.00, 0), "", "TEKST", "Tekst dodatkowy", ObjectId.Null) { Height = 0.20 });
    }
}
