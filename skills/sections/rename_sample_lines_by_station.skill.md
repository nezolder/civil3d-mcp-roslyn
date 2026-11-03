---
name: rename_sample_lines_by_station
category: sections
description: Rename the sample lines of a group after their actual station with a name format parameter, after checking each station against the line's own vertices; previews the new names unless apply is true
requires_write: true
aliases: ["mintavonalak átnevezése szelvény szerint", "rename sample lines by actual station", "mintavonal nevek frissítése a tényleges szelvényre", "sample line names follow station after moving lines"]
workflow_tags: ["modeling", "drawing_view"]
tested_civil_version: "2025"
validation_summary: "Offline compile + live (Civil 3D 2025, disposable copy): 22 lines, 4 moved incl. a name swap; preview kept DBMOD=0, apply renamed 3, saved, reopened, read back. km+m format live, refusal paths and station equations unverified."
parameters:
  - name: sampleLineGroupHandle
    type: string
    required: true
    description: Hex handle of the existing local sample line group
  - name: nameFormat
    type: string
    required: false
    description: New name pattern with {station} (zero padded station), {km} and {m} (kilometre and metre parts, as in 0+014.01); must contain {station} or {m} (default "SL_{station}")
  - name: nameDecimals
    type: int
    required: false
    description: Station decimals in the new names, 0 to 4 (default 2)
  - name: nameIntegerDigits
    type: int
    required: false
    description: Minimum integer digits of {station}, zero padded, 0 to 6 (default 3, so 14.01 becomes 014.01); {m} always has 3
  - name: onlyNamePrefix
    type: string
    required: false
    description: Rename only lines whose current name starts with this text, ignoring case; other lines keep their names; empty renames every line in the range (default "")
  - name: rangeStart
    type: double
    required: false
    description: First station to rename; NaN uses the first sample line (default NaN)
  - name: rangeEnd
    type: double
    required: false
    description: Last station to rename; NaN uses the last sample line (default NaN)
  - name: apply
    type: bool
    required: false
    description: false only lists the planned names and changes nothing; true renames the lines (default false)
---

## Code Template

```csharp
var sampleLineGroupHandle = "SAMPLE_LINE_GROUP_HANDLE";
var nameFormat = "SL_{station}";
var nameDecimals = 2;
var nameIntegerDigits = 3;
var onlyNamePrefix = "";
var rangeStart = double.NaN;
var rangeEnd = double.NaN;
var apply = false;
var tolerance = 1e-3;

var isHandle = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value)
    && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{1,16}$"));
var isName = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 255
    && !value.Any(char.IsControl));
if (!isHandle(sampleLineGroupHandle))
    return new { success = false, error = "sampleLineGroupHandle must be a hexadecimal handle." };
if (nameDecimals < 0 || nameDecimals > 4 || nameIntegerDigits < 0 || nameIntegerDigits > 6)
    return new { success = false, error = "nameDecimals must be 0..4 and nameIntegerDigits 0..6." };
if (onlyNamePrefix == null || onlyNamePrefix.Length > 100 || onlyNamePrefix.Any(char.IsControl))
    return new { success = false, error = "onlyNamePrefix must be at most 100 characters without control characters." };
if (double.IsInfinity(rangeStart) || double.IsInfinity(rangeEnd))
    return new { success = false, error = "rangeStart and rangeEnd must be finite or NaN." };

// The name format may only use known placeholders and must hold the station.
if (nameFormat == null || nameFormat.Length > 200 || nameFormat.Any(char.IsControl))
    return new { success = false, error = "nameFormat must be at most 200 characters without control characters." };
var placeholders = System.Text.RegularExpressions.Regex.Matches(nameFormat, "\\{([^{}]*)\\}")
    .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
var unknownPlaceholders = placeholders.Where(p => p != "station" && p != "km" && p != "m").Distinct().ToList();
var formatRest = System.Text.RegularExpressions.Regex.Replace(nameFormat, "\\{(station|km|m)\\}", "");
if (unknownPlaceholders.Count > 0 || formatRest.Contains('{') || formatRest.Contains('}'))
    return new { success = false, error = "nameFormat may only use the placeholders {station}, {km} and {m}.", unknown = unknownPlaceholders };
if (!placeholders.Contains("station") && !placeholders.Contains("m"))
    return new { success = false, error = "nameFormat must contain {station} or {m}, otherwise every line would get the same name." };
var usesKmParts = placeholders.Contains("km") || placeholders.Contains("m");

ObjectId? ResolveId(string handle)
{
    try { return Database.GetObjectId(false, new Handle(System.Convert.ToInt64(handle, 16)), 0); }
    catch { return null; }
}

var groupId = ResolveId(sampleLineGroupHandle);
var group = groupId.HasValue ? Transaction.GetObject(groupId.Value, OpenMode.ForRead) as SampleLineGroup : null;
if (group == null || group.IsErased || group.IsReferenceObject)
    return new { success = false, error = "sampleLineGroupHandle must identify an existing local sample line group." };
var alignment = Transaction.GetObject(group.ParentAlignmentId, OpenMode.ForRead) as Alignment;
if (alignment == null || alignment.IsErased)
    return new { success = false, error = "The sample line group has no readable parent alignment." };
if (alignment.StationEquations.Count != 0)
    return new { success = false, error = "Alignments with station equations are not supported; raw and displayed stations would differ in the names." };

var lines = new List<SampleLine>();
foreach (ObjectId id in group.GetSampleLineIds())
    if (Transaction.GetObject(id, OpenMode.ForRead) is SampleLine line && !line.IsErased) lines.Add(line);
if (lines.Count == 0)
    return new { success = false, error = "The sample line group has no sample lines." };
if (lines.Count > 1000)
    return new { success = false, error = "At most 1000 sample lines can be renamed in one run.", count = lines.Count };
lines = lines.OrderBy(l => l.Station).ToList();
var firstStation = double.IsNaN(rangeStart) ? lines.First().Station : rangeStart;
var lastStation = double.IsNaN(rangeEnd) ? lines.Last().Station : rangeEnd;
if (lastStation < firstStation)
    return new { success = false, error = "rangeEnd must not be smaller than rangeStart." };

var invariant = System.Globalization.CultureInfo.InvariantCulture;
string FormatStation(double station)
{
    var text = Math.Abs(station).ToString("F" + nameDecimals, invariant);
    var dot = text.IndexOf('.');
    var integerPart = dot < 0 ? text : text.Substring(0, dot);
    text = integerPart.PadLeft(nameIntegerDigits, '0') + (dot < 0 ? "" : text.Substring(dot));
    return (station < 0 ? "-" : "") + text;
}
string BuildName(double station)
{
    var name = nameFormat.Replace("{station}", FormatStation(station));
    if (usesKmParts)
    {
        // Split the already rounded value, so 999.999 with 2 decimals gives 1+000.00.
        var rounded = decimal.Parse(station.ToString("F" + nameDecimals, invariant), invariant);
        var km = decimal.Floor(rounded / 1000m);
        var metres = (rounded - km * 1000m).ToString("000" + (nameDecimals > 0 ? "." + new string('0', nameDecimals) : ""), invariant);
        name = name.Replace("{km}", km.ToString("0", invariant)).Replace("{m}", metres);
    }
    return name;
}

// Distance in plan from a point to the sample line's own vertex polyline.
double DistanceToLine(List<SampleLineVertex> vertices, double x, double y)
{
    var best = double.PositiveInfinity;
    for (var k = 0; k < vertices.Count; k++)
    {
        var ax = vertices[k].Location.X; var ay = vertices[k].Location.Y;
        if (vertices.Count == 1) return Math.Sqrt((x - ax) * (x - ax) + (y - ay) * (y - ay));
        if (k == 0) continue;
        var px = vertices[k - 1].Location.X; var py = vertices[k - 1].Location.Y;
        var dx = ax - px; var dy = ay - py;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared <= 0 ? 0 : Math.Max(0, Math.Min(1, ((x - px) * dx + (y - py) * dy) / lengthSquared));
        var cx = px + t * dx; var cy = py + t * dy;
        best = Math.Min(best, Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)));
    }
    return best;
}

// Plan every line: rename, keep (outside the range or the prefix) or stop.
var rows = new List<(SampleLine line, double station, string oldName, string newName, string status)>();
var unconfirmed = new List<object>();
foreach (var line in lines)
{
    var station = line.Station;
    var inRange = station >= firstStation - 1e-6 && station <= lastStation + 1e-6;
    var prefixOk = onlyNamePrefix == "" || line.Name.StartsWith(onlyNamePrefix, StringComparison.OrdinalIgnoreCase);
    if (!inRange || !prefixOk)
    {
        rows.Add((line, station, line.Name, line.Name, inRange ? "kept: name prefix" : "kept: outside range"));
        continue;
    }

    // The station is confirmed by the line's actual vertices: the alignment
    // point at Station must lie on the sample line itself. Projecting the end
    // points back to the alignment can give a wrong station near curves.
    var vertices = line.Vertices.Cast<SampleLineVertex>().ToList();
    double x = 0, y = 0;
    var located = true;
    try { alignment.PointLocation(station, 0, ref x, ref y); }
    catch { located = false; }
    var distance = located && vertices.Count > 0 ? DistanceToLine(vertices, x, y) : double.PositiveInfinity;
    if (!located || distance > tolerance)
    {
        unconfirmed.Add(new { name = line.Name, handle = line.Handle.ToString(), station, vertexCount = vertices.Count,
            distanceFromLine = double.IsInfinity(distance) ? (double?)null : Math.Round(distance, 4) });
        continue;
    }
    if (station < 0 && usesKmParts)
        return new { success = false, error = "Negative stations cannot be written with {km} and {m}; use {station}.", name = line.Name, station };

    var newName = BuildName(station);
    if (!isName(newName))
        return new { success = false, error = "The name format gives an empty, untrimmed or too long name.", newName };
    rows.Add((line, station, line.Name, newName, newName == line.Name ? "unchanged" : "rename"));
}
if (unconfirmed.Count > 0)
    return new { success = false, error = "The station of some sample lines is not on the line's own vertices; nothing was renamed. Check these lines first.", unconfirmed };

// New names must be unique among themselves and against the kept names.
var collisions = rows.GroupBy(r => r.newName, StringComparer.OrdinalIgnoreCase)
    .Where(g => g.Count() > 1)
    .Select(g => new { name = g.Key, lines = g.Select(r => new { r.oldName, r.station, r.status }).ToList() })
    .ToList();
if (collisions.Count > 0)
    return new { success = false, error = "New names would collide; raise nameDecimals, narrow the range or the prefix.", collisions };

var toRename = rows.Where(r => r.status == "rename").ToList();
if (apply && toRename.Count > 0)
{
    // Two passes, so swapped or shifted names never meet a duplicate.
    // Any failure below throws, so nothing is kept.
    foreach (var row in toRename)
    {
        row.line.UpgradeOpen();
        row.line.Name = "TMP_RENAME_" + row.line.Handle.ToString();
    }
    foreach (var row in toRename)
        row.line.Name = row.newName;
    foreach (var row in toRename)
        if (row.line.Name != row.newName)
            throw new InvalidOperationException($"Sample line {row.line.Handle} reads back as '{row.line.Name}', expected '{row.newName}'.");
}

return new {
    success = true,
    applied = apply,
    alignment = new { name = alignment.Name, handle = alignment.Handle.ToString() },
    group = new { name = group.Name, handle = group.Handle.ToString(), sampleLineCount = lines.Count },
    nameFormat,
    renameCount = toRename.Count,
    unchangedCount = rows.Count(r => r.status == "unchanged"),
    keptCount = rows.Count(r => r.status.StartsWith("kept")),
    lines = rows.Select(r => new {
        handle = r.line.Handle.ToString(),
        station = Math.Round(r.station, 4),
        oldName = r.oldName,
        newName = r.newName,
        status = r.status == "rename" && apply ? "renamed" : r.status
    }).ToList()
};
```

## Usage Notes

- This is a write-capable template: confirm the full drawing identity first and run it with `civil3d_execute`. Run it first with `apply: false` (the default), which only lists the planned names and opens nothing for write; then with `apply: true`, and `saveDrawing: true` only for an approved save.
- The new name comes from each line's `Station`, which Civil 3D keeps current when a line is moved. Before renaming, the template checks that the alignment point at that station lies on the line's own vertices (within 1 mm); if any selected line fails this check, nothing is renamed and the lines are listed. It does not project the end points back to the alignment, which can jump to another station near tight curves.
- `nameFormat` placeholders: `{station}` is the station with `nameIntegerDigits` zero padding (default `SL_{station}` gives `SL_014.01`, the same names `create_sample_lines` makes); `{km}` and `{m}` split it into kilometres and metres, so `KSZ_{km}+{m}` gives `KSZ_0+014.01` and `KSZ_1+000.00` at station 999.999 with 2 decimals. Text outside the braces is kept, invariant culture.
- Only lines inside `rangeStart..rangeEnd` whose current name starts with `onlyNamePrefix` are renamed; for example `onlyNamePrefix: "SL_"` leaves hand-named lines such as `KSZ_A` alone. Kept lines are listed with their reason, and their names count for the collision check.
- Two lines at the same station (at the chosen decimals), or a new name equal to a kept name, stop the run before writing; raise `nameDecimals` or narrow the selection. Renaming goes through temporary names, so names can be swapped or shifted along the group in one run.
- Sample line labels show the new names. Section view names are separate text and are not changed by this recipe. Alignments with station equations are refused.
