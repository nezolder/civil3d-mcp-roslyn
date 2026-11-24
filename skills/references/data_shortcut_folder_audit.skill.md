---
name: data_shortcut_folder_audit
category: references
description: Audit a data shortcuts folder (_Shortcuts) for duplicate keys, keys left behind after a rename, two keys on one object and missing sources, optionally checking each key against its source drawing read in a side database
requires_write: false
aliases: ["adatparancsikon mappa ellenőrzése régi és ütköző kulcsokra", "data shortcut folder audit stale and duplicate keys", "adatkapcsolat kulcsok ellenőrzése átnevezés után", "shortcuts folder key collision check"]
workflow_tags: ["audit", "source_state"]
tested_civil_version: "2025"
validation_summary: "Offline: compiles against the Civil 3D 2025 assemblies. Not yet run in Civil 3D; the shortcut XML fields are read by name and the first file's fields are reported for review."
parameters:
  - name: shortcutsFolder
    type: string
    required: false
    description: Full path of the project's _Shortcuts folder or of the project folder that holds it; empty searches upward from the active drawing and from the source drawings of its data references (default "")
  - name: checkSources
    type: bool
    required: false
    description: Read each source drawing in a side database and check that the key's handle still points to an object of that name (default true)
  - name: useAbsolutePaths
    type: bool
    required: false
    description: When a key's relative path finds no file, use the absolute path stored in the key; false keeps every read inside the folder tree of the project, e.g. for a copied project (default true)
  - name: maxSourceDrawings
    type: int
    required: false
    description: Maximum source drawings read when checkSources is true, 1 to 50 (default 20)
  - name: onlyProblems
    type: bool
    required: false
    description: List only keys with a finding (default true)
  - name: limit
    type: int
    required: false
    description: Maximum keys listed, 1 to 500 (default 200)
---

## Code Template

```csharp
var shortcutsFolder = "";
var checkSources = true;
var maxSourceDrawings = 20;
var useAbsolutePaths = true;
var onlyProblems = true;
var limit = 200;

if (maxSourceDrawings < 1 || maxSourceDrawings > 50) return new { success = false, error = "maxSourceDrawings must be between 1 and 50." };
if (limit < 1 || limit > 500) return new { success = false, error = "limit must be between 1 and 500." };

// Find the _Shortcuts folder: given, or upward from the drawing and from its data reference sources.
string ShortcutsIn(string folder)
{
    if (string.IsNullOrWhiteSpace(folder) || !System.IO.Directory.Exists(folder)) return null;
    if (string.Equals(System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')), "_Shortcuts", StringComparison.OrdinalIgnoreCase)) return folder;
    var inside = System.IO.Path.Combine(folder, "_Shortcuts");
    return System.IO.Directory.Exists(inside) ? inside : null;
}
string SearchUp(string start)
{
    for (var dir = start; !string.IsNullOrEmpty(dir); dir = System.IO.Path.GetDirectoryName(dir))
    {
        var found = ShortcutsIn(dir);
        if (found != null) return found;
    }
    return null;
}

var searched = new List<string>();
string folderPath = null;
string foundBy = null;
if (!string.IsNullOrWhiteSpace(shortcutsFolder))
{
    var given = shortcutsFolder.Trim();
    if (!System.IO.Path.IsPathRooted(given)) return new { success = false, error = "shortcutsFolder must be a full path." };
    folderPath = ShortcutsIn(given);
    foundBy = "parameter";
    if (folderPath == null) return new { success = false, error = $"No _Shortcuts folder at or inside {given}." };
}
else
{
    var starts = new List<string>();
    if (!string.IsNullOrWhiteSpace(Database.Filename) && System.IO.Path.IsPathRooted(Database.Filename))
        starts.Add(System.IO.Path.GetDirectoryName(Database.Filename));
    var model = (BlockTableRecord)Transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(Database), OpenMode.ForRead);
    var sourceDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var civilClass = RXObject.GetClass(typeof(Autodesk.Civil.DatabaseServices.Entity));
    foreach (ObjectId id in model)
    {
        if (sourceDirs.Count >= 20) break;
        if (!id.ObjectClass.IsDerivedFrom(civilClass)) continue;
        var civil = Transaction.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Entity;
        if (civil == null || !civil.IsReferenceObject) continue;
        try
        {
            var source = civil.GetReferenceInfo()?.SourceDrawing;
            if (!string.IsNullOrWhiteSpace(source) && System.IO.Path.IsPathRooted(source)) sourceDirs.Add(System.IO.Path.GetDirectoryName(source));
        }
        catch (System.Exception) { }
    }
    starts.AddRange(sourceDirs);
    foreach (var start in starts)
    {
        searched.Add(start);
        folderPath = SearchUp(start);
        if (folderPath != null) { foundBy = sourceDirs.Contains(start) ? "referenceSource" : "activeDrawingFolder"; break; }
    }
    if (folderPath == null)
        return new { success = false, error = "No _Shortcuts folder found above the active drawing or its reference sources; pass shortcutsFolder.", searched };
}
var projectFolder = System.IO.Path.GetDirectoryName(folderPath.TrimEnd('\\', '/'));

// Read every shortcut XML. Civil 3D 2025 writes one <Shortcut> per file: its source as File@name (absolute)
// and DwgRelPath@path (relative), the object as Object@name, @type, @handleLow, @handleHigh, @parentAlignmentShortcutName.
// Elements and attributes are matched by local name, whatever the namespace or letter case.
string Attr(System.Xml.XmlNode node, string name) => node?.Attributes?.Cast<System.Xml.XmlAttribute>()
    .FirstOrDefault(a => string.Equals(a.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
Dictionary<string, string> ReadFields(string file, out string problem)
{
    problem = null;
    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        var xml = new System.Xml.XmlDocument();
        xml.XmlResolver = null;
        xml.Load(file);
        var elements = xml.SelectNodes("//*").Cast<System.Xml.XmlNode>().ToList();
        System.Xml.XmlNode First(string localName) => elements.FirstOrDefault(e => string.Equals(e.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        void Put(string key, string value) { if (!string.IsNullOrEmpty(value)) fields[key] = value; }
        var shortcut = First("Shortcut");
        var obj = First("Object");
        Put("shortcutName", Attr(shortcut, "name"));
        Put("sourceFile", Attr(First("File"), "name"));
        Put("relativePath", Attr(First("DwgRelPath"), "path"));
        Put("objectName", Attr(obj, "name"));
        Put("objectType", Attr(obj, "type"));
        Put("handleLow", Attr(obj, "handleLow"));
        Put("handleHigh", Attr(obj, "handleHigh"));
        Put("parentAlignment", Attr(obj, "parentAlignmentShortcutName"));
        var count = elements.Count(e => string.Equals(e.LocalName, "Shortcut", StringComparison.OrdinalIgnoreCase));
        if (count != 1) fields["shortcutCount"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    catch (System.Exception error) { problem = "UNREADABLE_XML: " + error.GetType().Name; }
    return fields;
}

long? HandleOf(Dictionary<string, string> f)
{
    // handleLow and handleHigh are decimal numbers in the files.
    var styles = System.Globalization.NumberStyles.Integer;
    var culture = System.Globalization.CultureInfo.InvariantCulture;
    if (!f.TryGetValue("handleLow", out var lowText) || !long.TryParse(lowText, styles, culture, out var low)) return null;
    long high = 0;
    if (f.TryGetValue("handleHigh", out var highText)) long.TryParse(highText, styles, culture, out high);
    return (high << 32) | (low & 0xFFFFFFFFL);
}

// The relative path first (it follows a copied project), against the project folder and its parent;
// then the absolute path stored in the key, unless useAbsolutePaths is false.
string ResolveSource(Dictionary<string, string> f, out string basis)
{
    basis = null;
    f.TryGetValue("relativePath", out var relative);
    f.TryGetValue("sourceFile", out var absolute);
    var candidates = new List<(string basis, string path)>();
    if (!string.IsNullOrWhiteSpace(relative) && !System.IO.Path.IsPathRooted(relative))
    {
        candidates.Add(("relativeToProjectFolder", System.IO.Path.Combine(projectFolder, relative)));
        var parent = System.IO.Path.GetDirectoryName(projectFolder);
        if (!string.IsNullOrEmpty(parent)) candidates.Add(("relativeToProjectParent", System.IO.Path.Combine(parent, relative)));
    }
    if (useAbsolutePaths && !string.IsNullOrWhiteSpace(absolute) && System.IO.Path.IsPathRooted(absolute)) candidates.Add(("absolute", absolute));
    if (candidates.Count == 0) return null;
    foreach (var c in candidates)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(c.path); } catch (System.Exception) { continue; }
        if (System.IO.File.Exists(full)) { basis = c.basis; return full; }
    }
    basis = "notFound";
    try { return System.IO.Path.GetFullPath(candidates[0].path); } catch (System.Exception) { return candidates[0].path; }
}

var keys = new List<Dictionary<string, object>>();
Dictionary<string, string> firstFields = null;
string firstFile = null;
foreach (var typeDir in System.IO.Directory.GetDirectories(folderPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
{
    var typeName = System.IO.Path.GetFileName(typeDir);
    foreach (var file in System.IO.Directory.GetFiles(typeDir, "*.xml", System.IO.SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
    {
        var fields = ReadFields(file, out var problem);
        if (firstFields == null && problem == null) { firstFields = fields; firstFile = file; }
        var fileKey = System.IO.Path.GetFileNameWithoutExtension(file);
        fields.TryGetValue("objectName", out var keyName);
        string basis = null;
        var source = problem == null ? ResolveSource(fields, out basis) : null;
        var row = new Dictionary<string, object>
        {
            ["type"] = typeName,
            ["file"] = System.IO.Path.GetRelativePath(folderPath, file),
            ["name"] = string.IsNullOrEmpty(keyName) ? fileKey : keyName,
            ["shortcutName"] = fields.TryGetValue("shortcutName", out var shortcutName) ? shortcutName : null,
            ["objectType"] = fields.TryGetValue("objectType", out var objectType) ? objectType : null,
            ["sourceDrawing"] = source,
            ["sourceBasis"] = basis,
            ["sourceExists"] = source != null && System.IO.File.Exists(source),
            ["parentAlignment"] = fields.TryGetValue("parentAlignment", out var parentAlignment) ? parentAlignment : null,
            ["handle"] = HandleOf(fields)?.ToString("X"),
            ["findings"] = new List<string>()
        };
        var findings = (List<string>)row["findings"];
        if (problem != null) findings.Add(problem);
        else
        {
            if (fields.ContainsKey("shortcutCount")) findings.Add("SHORTCUT_COUNT_" + fields["shortcutCount"]);
            if (string.IsNullOrEmpty(keyName)) findings.Add("NO_NAME_FIELD");
            else if (!fileKey.StartsWith(keyName, StringComparison.OrdinalIgnoreCase)) findings.Add("FILE_NAME_DIFFERS_FROM_KEY_NAME");
            if (source == null) findings.Add("NO_SOURCE_FIELD");
            else if (!(bool)row["sourceExists"]) findings.Add("SOURCE_MISSING");
            if (row["handle"] == null) findings.Add("NO_HANDLE_FIELD");
        }
        keys.Add(row);
    }
}

// A profile key whose parent alignment key is not in the folder.
var alignmentKeys = new HashSet<string>(keys.Where(k => string.Equals((string)k["type"], "Alignments", StringComparison.OrdinalIgnoreCase))
    .SelectMany(k => new[] { (string)k["name"], (string)k["shortcutName"] }).Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);
foreach (var k in keys)
{
    var parent = (string)k["parentAlignment"];
    if (string.IsNullOrEmpty(parent) || alignmentKeys.Contains(parent)) continue;
    var colon = parent.LastIndexOf(':');
    if (colon > 1 && alignmentKeys.Contains(parent.Substring(colon + 1))) continue;
    ((List<string>)k["findings"]).Add("PARENT_ALIGNMENT_KEY_MISSING");
}

// Collisions between keys of one type folder.
foreach (var group in keys.GroupBy(k => ((string)k["type"]).ToUpperInvariant() + "|" + ((string)k["name"]).ToUpperInvariant()).Where(g => g.Count() > 1))
    foreach (var k in group) ((List<string>)k["findings"]).Add("DUPLICATE_KEY");
foreach (var group in keys.Where(k => k["handle"] != null && k["sourceDrawing"] != null)
    .GroupBy(k => ((string)k["type"]).ToUpperInvariant() + "|" + ((string)k["sourceDrawing"]).ToUpperInvariant() + "|" + k["handle"]).Where(g => g.Count() > 1))
    foreach (var k in group) ((List<string>)k["findings"]).Add("SAME_OBJECT_HAS_SEVERAL_KEYS");

// Each key against its source drawing, read once per drawing with shared access.
var sourcesRead = new List<object>();
var sourcesSkipped = 0;
if (checkSources)
{
    foreach (var group in keys.Where(k => (bool)k["sourceExists"] && k["handle"] != null).GroupBy(k => (string)k["sourceDrawing"], StringComparer.OrdinalIgnoreCase))
    {
        if (sourcesRead.Count >= maxSourceDrawings) { sourcesSkipped++; foreach (var k in group) ((List<string>)k["findings"]).Add("SOURCE_NOT_CHECKED_LIMIT"); continue; }
        try
        {
            using (var side = new Autodesk.AutoCAD.DatabaseServices.Database(false, true))
            {
                side.ReadDwgFile(group.Key, FileOpenMode.OpenForReadAndAllShare, true, null);
                side.CloseInput(true);
                using (var tr = side.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (var k in group)
                    {
                        var findings = (List<string>)k["findings"];
                        var handle = new Handle(Convert.ToInt64((string)k["handle"], 16));
                        if (!side.TryGetObjectId(handle, out var id) || id.IsNull || id.IsErased)
                        {
                            findings.Add("HANDLE_NOT_FOUND_IN_SOURCE");
                            continue;
                        }
                        var obj = tr.GetObject(id, OpenMode.ForRead, false);
                        var civil = obj as Autodesk.Civil.DatabaseServices.Entity;
                        k["sourceObjectType"] = obj.GetType().Name;
                        k["sourceObjectName"] = civil?.Name;
                        if (civil == null) findings.Add("HANDLE_IS_NOT_A_CIVIL_OBJECT");
                        else if (!string.Equals(civil.Name, (string)k["name"], StringComparison.OrdinalIgnoreCase)) findings.Add("SOURCE_OBJECT_HAS_OTHER_NAME");
                    }
                    tr.Commit();
                }
            }
            sourcesRead.Add(new { path = group.Key, keys = group.Count() });
        }
        catch (System.Exception error)
        {
            sourcesRead.Add(new { path = group.Key, keys = group.Count(), error = error.GetType().Name + ": " + error.Message });
            foreach (var k in group) ((List<string>)k["findings"]).Add("SOURCE_UNREADABLE");
        }
    }
}

var findingCounts = keys.SelectMany(k => (List<string>)k["findings"]).GroupBy(f => f.Split(':')[0]).ToDictionary(g => g.Key, g => g.Count());
var listed = keys.Where(k => !onlyProblems || ((List<string>)k["findings"]).Count > 0).ToList();
return new
{
    success = true,
    shortcutsFolder = folderPath,
    foundBy,
    keyCount = keys.Count,
    keysWithFindings = keys.Count(k => ((List<string>)k["findings"]).Count > 0),
    byType = keys.GroupBy(k => (string)k["type"]).ToDictionary(g => g.Key, g => g.Count()),
    findingCounts,
    sourceBasis = keys.GroupBy(k => (string)k["sourceBasis"] ?? "none").ToDictionary(g => g.Key, g => g.Count()),
    sourcesChecked = checkSources,
    sourcesRead,
    sourcesSkipped,
    listedCount = Math.Min(listed.Count, limit),
    listedTotal = listed.Count,
    keys = listed.Take(limit).ToList(),
    firstFile = firstFile == null ? null : System.IO.Path.GetRelativePath(folderPath, firstFile),
    firstFileFields = firstFields
};
```

## Usage Notes

- Read-only: the XML files are only read, and each source drawing is read once in a separate database with shared access, so a drawing open in another Civil 3D instance can still be checked. Nothing in the folder or in any drawing changes.
- Findings per key: `DUPLICATE_KEY` (two files of one type folder with the same key name; creating a reference then fails), `SAME_OBJECT_HAS_SEVERAL_KEYS` (two keys point to the same object of the same source drawing, typically a key left behind after the object was renamed), `SOURCE_OBJECT_HAS_OTHER_NAME` (the object behind the handle now has another name), `HANDLE_NOT_FOUND_IN_SOURCE`, `SOURCE_MISSING`, `FILE_NAME_DIFFERS_FROM_KEY_NAME`, and `NO_..._FIELD` or `UNREADABLE_XML` when a file could not be read as expected.
- The files are read as Civil 3D 2025 writes them: `Shortcut@name`, `File@name` (absolute source path), `DwgRelPath@path` (relative source path) and `Object@name`, `@type`, `@handleLow`, `@handleHigh` (decimal), `@parentAlignmentShortcutName`. `firstFileFields` shows what was read from the first file; when every key reports `NO_HANDLE_FIELD` or `NO_SOURCE_FIELD`, compare it with a file in the folder before trusting the result.
- The source is found by the relative path first, against the project folder (the one holding `_Shortcuts`) and its parent, then by the absolute path; `sourceBasis` says which one was used. For a copied project pass `useAbsolutePaths: false`, so that no drawing of the original project is read.
- The file name of a key is the object name plus an identifier, so `FILE_NAME_DIFFERS_FROM_KEY_NAME` means the file name does not start with the object name. `PARENT_ALIGNMENT_KEY_MISSING` marks a profile key whose parent alignment key is not in the folder.
- The recipe only reports. Removing or renaming a stale key is a decision for the user, done in the Civil 3D user interface or the file system after a backup; synchronising references is a user action too (usage guide topic `civil`).
- Found in field use (Civil 3D 2025): after a published object was renamed, the old key stayed in the folder and pointed to another object, and duplicate keys made creating references fail.
