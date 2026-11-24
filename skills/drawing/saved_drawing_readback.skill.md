---
name: saved_drawing_readback
category: drawing
description: Read the saved DWG file itself in a separate database, without opening it in the editor, and compare chosen objects and counts with the open drawing; the independent readback after a save
requires_write: false
aliases: ["mentett rajzfájl független visszaolvasása megnyitás nélkül", "read back the saved DWG file in a side database", "mentés utáni ellenőrzés a lemezen lévő fájlból", "saved file readback compare with open drawing"]
workflow_tags: ["source_state", "readback"]
tested_civil_version: "2025"
validation_summary: "Offline: 2025 API compilation. Live: not yet tested; verify on a throwaway copy before use."
parameters:
  - name: filePath
    type: string
    required: false
    description: Full path of the saved DWG to read; empty reads the saved file of the active drawing (default "")
  - name: handles
    type: array
    required: false
    description: Up to 200 object handles (hex text, as in a receipt) to look up in the saved file (default none)
  - name: compareWithOpen
    type: bool
    required: false
    description: Also look up the same handles and counts in the open active drawing and flag differences (default true)
  - name: includeCivil
    type: bool
    required: false
    description: List surfaces, alignments and corridors of the saved file by name and handle (default true)
  - name: limit
    type: int
    required: false
    description: Maximum Civil objects listed per kind, 1 to 500 (default 100)
---

## Code Template

```csharp
var filePath = "";
var handles = new string[] { };
var compareWithOpen = true;
var includeCivil = true;
var limit = 100;

handles ??= new string[] { };
if (handles.Length > 200) return new { success = false, error = "handles must hold at most 200 items." };
if (limit < 1 || limit > 500) return new { success = false, error = "limit must be between 1 and 500." };
var path = string.IsNullOrWhiteSpace(filePath) ? Database.Filename : filePath.Trim();
if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path))
    return new { success = false, error = "No saved file: give a full filePath or save the active drawing first." };
if (!path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
    return new { success = false, error = "filePath must be a .dwg file." };
var info = new System.IO.FileInfo(path);
if (!info.Exists) return new { success = false, error = $"File not found: {path}" };
var isActiveFile = string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(Database.Filename ?? ""), StringComparison.OrdinalIgnoreCase);

var parsedHandles = new List<(string text, Handle handle)>();
foreach (var text in handles)
{
    var clean = (text ?? "").Trim();
    if (!long.TryParse(clean, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
        return new { success = false, error = $"'{text}' is not a hexadecimal handle." };
    parsedHandles.Add((clean.ToUpperInvariant(), new Handle(value)));
}

string NameOf(Autodesk.AutoCAD.DatabaseServices.DBObject obj) => obj switch
{
    Autodesk.Civil.DatabaseServices.Entity civil => civil.Name,
    BlockReference block => block.Name,
    Layout layout => layout.LayoutName,
    SymbolTableRecord record => record.Name,
    _ => null
};

Dictionary<string, object> Describe(Database db, Transaction tr, Handle handle)
{
    var row = new Dictionary<string, object> { ["found"] = false };
    if (!db.TryGetObjectId(handle, out var id) || id.IsNull) return row;
    row["found"] = true;
    row["erased"] = id.IsErased;
    if (id.IsErased) return row;
    var obj = tr.GetObject(id, OpenMode.ForRead, false);
    row["type"] = obj.GetType().Name;
    row["name"] = NameOf(obj);
    row["layer"] = (obj as Autodesk.AutoCAD.DatabaseServices.Entity)?.Layer;
    if (obj is Autodesk.AutoCAD.DatabaseServices.Entity entity)
    {
        try
        {
            var e = entity.GeometricExtents;
            row["extents"] = new[] { Math.Round(e.MinPoint.X, 6), Math.Round(e.MinPoint.Y, 6), Math.Round(e.MaxPoint.X, 6), Math.Round(e.MaxPoint.Y, 6) };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { row["extents"] = null; }
    }
    return row;
}

string Signature(Dictionary<string, object> row) => string.Join("|", row.OrderBy(p => p.Key).Select(p =>
    p.Key + "=" + (p.Value is double[] values
        ? string.Join(",", values.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))
        : Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture))));

Dictionary<string, int> ModelSpaceCounts(Database db, Transaction tr)
{
    var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
    var model = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
    var counts = new Dictionary<string, int>();
    foreach (ObjectId id in model)
    {
        var key = id.ObjectClass.Name;
        counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
    }
    return counts;
}

List<Dictionary<string, object>> savedRows;
Dictionary<string, int> savedCounts;
string savedFingerprint;
object civil = null;

using (var side = new Autodesk.AutoCAD.DatabaseServices.Database(false, true))
{
    side.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
    side.CloseInput(true);
    savedFingerprint = side.FingerprintGuid.ToString();
    using (var tr = side.TransactionManager.StartOpenCloseTransaction())
    {
        savedRows = parsedHandles.Select(h => Describe(side, tr, h.handle)).ToList();
        savedCounts = ModelSpaceCounts(side, tr);
        if (includeCivil)
        {
            var doc = CivilDocument.GetCivilDocument(side);
            List<object> Names(IEnumerable<ObjectId> ids) => ids
                .Where(id => !id.IsErased)
                .Select(id => tr.GetObject(id, OpenMode.ForRead, false) as Autodesk.Civil.DatabaseServices.Entity)
                .Where(e => e != null)
                .Take(limit)
                .Select(e => (object)new { name = e.Name, handle = e.Handle.ToString() })
                .ToList();
            var alignmentIds = doc.GetAlignmentIds().Cast<ObjectId>().Concat(doc.GetSitelessAlignmentIds().Cast<ObjectId>()).Distinct().ToList();
            var corridorIds = new List<ObjectId>();
            for (var i = 0; i < doc.CorridorCollection.Count; i++) corridorIds.Add(doc.CorridorCollection[i]);
            civil = new
            {
                surfaces = new { count = doc.GetSurfaceIds().Count, items = Names(doc.GetSurfaceIds().Cast<ObjectId>()) },
                alignments = new { count = alignmentIds.Count, items = Names(alignmentIds) },
                corridors = new { count = corridorIds.Count, items = Names(corridorIds) }
            };
        }
        tr.Commit();
    }
}

object comparison = null;
if (compareWithOpen)
{
    var openRows = parsedHandles.Select(h => Describe(Database, Transaction, h.handle)).ToList();
    var openCounts = ModelSpaceCounts(Database, Transaction);
    var countDifferences = savedCounts.Keys.Union(openCounts.Keys)
        .Select(k => new { type = k, saved = savedCounts.TryGetValue(k, out var s) ? s : 0, open = openCounts.TryGetValue(k, out var o) ? o : 0 })
        .Where(d => d.saved != d.open)
        .ToList();
    var handleDifferences = Enumerable.Range(0, parsedHandles.Count)
        .Where(i => Signature(savedRows[i]) != Signature(openRows[i]))
        .Select(i => parsedHandles[i].text)
        .ToList();
    comparison = new
    {
        openDrawing = Database.Filename,
        isActiveFile,
        fingerprintMatches = string.Equals(savedFingerprint, Database.FingerprintGuid.ToString(), StringComparison.OrdinalIgnoreCase),
        dbmodOpen = Convert.ToInt32(Application.GetSystemVariable("DBMOD")),
        sameModelSpaceCounts = countDifferences.Count == 0,
        countDifferences,
        handlesDiffering = handleDifferences,
        open = parsedHandles.Select((h, i) => new { handle = h.text, open = openRows[i] }).ToList()
    };
}

return new
{
    success = true,
    file = new { path, bytes = info.Length, lastWriteUtc = info.LastWriteTimeUtc.ToString("o"), fingerprintGuid = savedFingerprint },
    objects = parsedHandles.Select((h, i) => new { handle = h.text, saved = savedRows[i] }).ToList(),
    modelSpaceCounts = savedCounts,
    civil,
    comparison
};
```

## Usage Notes
- This reads the file on disk, not the editor's memory. Use it after a write with `saveDrawing: true` to check that the change reached the file; a query alone reads the open drawing, including unsaved changes.
- Take the handles from the write's receipt (`changes`) or from your own result. `handlesDiffering` lists handles whose type, name, layer or extents differ between the file and the open drawing; after a clean save it should be empty and `sameModelSpaceCounts` true.
- With `dbmodOpen` 1 the open drawing has unsaved changes, so differences are expected.
- `filePath` can name another drawing (a backup copy, an xref source) to read it without opening it; `comparison` then compares it with the active drawing.
- Nothing is changed: the separate database is read with shared access and closed at the end. Extents of some Civil objects are not available in a database that is not open in the editor; they come back as null.
- If this read hangs or times out, do not repeat it automatically; report the readback as missing.
