---
name: create_sample_lines
category: sections
description: Create perpendicular sample lines on an alignment at explicit stations, a regular interval and optionally geometry points, with set left and right widths, sampled sources and station-based names
requires_write: true
aliases: ["mintavonalak létrehozása szelvényenként", "create sample lines at stations with left and right swath widths", "mintavonal csoport keresztszelvényekhez", "sample lines from a station list or interval"]
workflow_tags: ["modeling", "drawing_view"]
tested_civil_version: "2025"
validation_summary: "Offline: run-by-name binding and syntax check. Live creation, vertex verification, sampled sources and names in Civil 3D 2025 are not yet recorded."
parameters:
  - name: alignmentHandle
    type: string
    required: true
    description: Hex handle of the alignment (without station equations)
  - name: groupName
    type: string
    required: true
    description: Sample line group name; an existing group of this name on the alignment is extended, otherwise a new one is created
  - name: leftWidth
    type: double
    required: true
    description: Width left of the alignment in drawing units, greater than 0 (sample line from offset -leftWidth)
  - name: rightWidth
    type: double
    required: true
    description: Width right of the alignment in drawing units, greater than 0 (sample line to offset +rightWidth)
  - name: stations
    type: array
    required: false
    description: Explicit (station, name) rows; an empty name uses the station-based name (default none)
  - name: interval
    type: double
    required: false
    description: Regular station interval; 0 adds none, otherwise stations at multiples of the interval plus the range ends (default 0)
  - name: rangeStart
    type: double
    required: false
    description: First station for interval and geometry points; NaN uses the alignment start (default NaN)
  - name: rangeEnd
    type: double
    required: false
    description: Last station for interval and geometry points; NaN uses the alignment end (default NaN)
  - name: includeGeometryPoints
    type: bool
    required: false
    description: Also add the alignment's geometry points (tangent, curve and spiral points) inside the range (default false)
  - name: sampledSources
    type: array
    required: false
    description: (handle, sectionStyleName) rows of section sources to sample, such as the existing ground surface; an empty style keeps the current one (default none)
  - name: namePrefix
    type: string
    required: false
    description: Prefix of generated names (default "SL_")
  - name: nameDecimals
    type: int
    required: false
    description: Station decimals in generated names, 0 to 4 (default 2)
  - name: nameIntegerDigits
    type: int
    required: false
    description: Minimum integer digits of the station in generated names, zero padded, 0 to 6 (default 3, so 14.01 becomes 014.01)
---

## Code Template

```csharp
var alignmentHandle = "ALIGNMENT_HANDLE";
var groupName = "SAMPLE_LINE_GROUP_NAME";
var leftWidth = 0.0;
var rightWidth = 0.0;
var stations = new (double station, string name)[] { };
var interval = 0.0;
var rangeStart = double.NaN;
var rangeEnd = double.NaN;
var includeGeometryPoints = false;
var sampledSources = new (string handle, string sectionStyleName)[] { };
var namePrefix = "SL_";
var nameDecimals = 2;
var nameIntegerDigits = 3;
var tolerance = 1e-3;

var isHandle = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value)
    && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{1,16}$"));
var isName = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 255
    && !value.Any(char.IsControl));
if (!isHandle(alignmentHandle))
    return new { success = false, error = "alignmentHandle must be a hexadecimal handle." };
if (!isName(groupName) || groupName == "SAMPLE_LINE_GROUP_NAME")
    return new { success = false, error = "Configure a trimmed sample line group name." };
if (!double.IsFinite(leftWidth) || !double.IsFinite(rightWidth) || leftWidth <= 0 || rightWidth <= 0
    || leftWidth > 1000 || rightWidth > 1000)
    return new { success = false, error = "leftWidth and rightWidth must be greater than 0 and at most 1000." };
if (!double.IsFinite(interval) || interval < 0 || (interval > 0 && interval < 0.1))
    return new { success = false, error = "interval must be 0 or at least 0.1." };
if (double.IsInfinity(rangeStart) || double.IsInfinity(rangeEnd))
    return new { success = false, error = "rangeStart and rangeEnd must be finite or NaN." };
if (nameDecimals < 0 || nameDecimals > 4 || nameIntegerDigits < 0 || nameIntegerDigits > 6
    || namePrefix == null || namePrefix.Length > 100 || namePrefix.Any(char.IsControl))
    return new { success = false, error = "nameDecimals must be 0..4, nameIntegerDigits 0..6 and namePrefix at most 100 characters." };
if (stations == null || stations.Length > 1000
    || stations.Any(s => !double.IsFinite(s.station) || s.name == null || (s.name != "" && !isName(s.name))))
    return new { success = false, error = "stations must hold at most 1000 finite stations with empty or trimmed names." };
if (sampledSources == null || sampledSources.Length > 20
    || sampledSources.Any(s => !isHandle(s.handle) || s.sectionStyleName == null
        || (s.sectionStyleName != "" && !isName(s.sectionStyleName)))
    || sampledSources.Select(s => s.handle.ToUpperInvariant()).Distinct().Count() != sampledSources.Length)
    return new { success = false, error = "sampledSources must hold at most 20 distinct hexadecimal handles with empty or existing section style names." };

ObjectId? ResolveId(string handle)
{
    try { return Database.GetObjectId(false, new Handle(System.Convert.ToInt64(handle, 16)), 0); }
    catch { return null; }
}

var alignmentId = ResolveId(alignmentHandle);
var alignment = alignmentId.HasValue ? Transaction.GetObject(alignmentId.Value, OpenMode.ForRead) as Alignment : null;
if (alignment == null || alignment.IsErased)
    return new { success = false, error = "alignmentHandle must identify an existing alignment." };
if (alignment.StationEquations.Count != 0)
    return new { success = false, error = "Alignments with station equations are not supported; names and stations would be ambiguous." };

var firstStation = double.IsNaN(rangeStart) ? alignment.StartingStation : rangeStart;
var lastStation = double.IsNaN(rangeEnd) ? alignment.EndingStation : rangeEnd;
var inAlignment = new Func<double, bool>(s =>
    s >= alignment.StartingStation - 1e-6 && s <= alignment.EndingStation + 1e-6);
if (!inAlignment(firstStation) || !inAlignment(lastStation) || lastStation < firstStation)
    return new { success = false, error = "The station range must lie inside the alignment.", alignmentStart = alignment.StartingStation, alignmentEnd = alignment.EndingStation };

string FormatStation(double station)
{
    var text = Math.Abs(station).ToString("F" + nameDecimals, System.Globalization.CultureInfo.InvariantCulture);
    var dot = text.IndexOf('.');
    var integerPart = dot < 0 ? text : text.Substring(0, dot);
    text = integerPart.PadLeft(nameIntegerDigits, '0') + (dot < 0 ? "" : text.Substring(dot));
    return (station < 0 ? "-" : "") + text;
}

// Requested stations keyed to 1 mm; explicit rows win over generated ones.
var planned = new SortedDictionary<long, (double station, string name, string origin)>();
void Plan(double station, string name, string origin)
{
    var key = (long)Math.Round(station / tolerance);
    if (!planned.ContainsKey(key)) planned[key] = (station, name, origin);
}
foreach (var row in stations)
{
    if (!inAlignment(row.station))
        return new { success = false, error = $"Station {row.station} is outside the alignment." };
    Plan(row.station, row.name, "explicit");
}
if (interval > 0)
{
    Plan(firstStation, "", "range");
    for (var k = Math.Ceiling(firstStation / interval - 1e-9); k * interval <= lastStation + 1e-6; k++)
        Plan(Math.Min(k * interval, lastStation), "", "interval");
    Plan(lastStation, "", "range");
}
if (includeGeometryPoints)
{
    foreach (var point in alignment.GetStationSet(StationTypes.GeometryPoint))
        if (point.RawStation >= firstStation - 1e-6 && point.RawStation <= lastStation + 1e-6)
            Plan(point.RawStation, "", "geometry point");
}
if (planned.Count == 0)
    return new { success = false, error = "No stations: give stations, an interval or includeGeometryPoints." };
if (planned.Count > 1000)
    return new { success = false, error = "At most 1000 sample lines can be created in one run.", requested = planned.Count };

// Existing group of this name, its lines and names.
SampleLineGroup group = null;
foreach (ObjectId id in alignment.GetSampleLineGroupIds())
{
    var candidate = Transaction.GetObject(id, OpenMode.ForRead) as SampleLineGroup;
    if (candidate != null && !candidate.IsErased && candidate.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase))
        group = candidate;
}
var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var existingStations = new List<double>();
if (group != null)
{
    foreach (ObjectId id in group.GetSampleLineIds())
    {
        if (Transaction.GetObject(id, OpenMode.ForRead) is SampleLine existing && !existing.IsErased)
        {
            existingNames.Add(existing.Name);
            existingStations.Add(existing.Station);
        }
    }
}

var skippedExisting = new List<object>();
var toCreate = new List<(double station, string name, string origin)>();
foreach (var item in planned.Values)
{
    if (existingStations.Any(s => Math.Abs(s - item.station) <= tolerance))
    {
        skippedExisting.Add(new { item.station, reason = "A sample line already exists at this station in the group." });
        continue;
    }
    toCreate.Add((item.station, item.name != "" ? item.name : namePrefix + FormatStation(item.station), item.origin));
}
var collisions = toCreate.GroupBy(c => c.name, StringComparer.OrdinalIgnoreCase)
    .Where(g => g.Count() > 1 || existingNames.Contains(g.Key)).Select(g => g.Key).ToList();
if (collisions.Count > 0)
    return new { success = false, error = "Sample line names would collide; use more nameDecimals or explicit names.", names = collisions };

// Styles and sources are checked before anything is written.
var sourceStyles = new Dictionary<ObjectId, ObjectId>();
foreach (var source in sampledSources)
{
    var sourceId = ResolveId(source.handle);
    if (!sourceId.HasValue)
        return new { success = false, error = "A sampled source handle does not resolve.", source.handle };
    var styleId = ObjectId.Null;
    if (source.sectionStyleName != "")
    {
        try { styleId = CivilDoc.Styles.SectionStyles[source.sectionStyleName]; }
        catch { styleId = ObjectId.Null; }
        if (styleId.IsNull)
            return new { success = false, error = "The named section style does not exist.", source.sectionStyleName };
    }
    sourceStyles[sourceId.Value] = styleId;
}

var groupCreated = group == null;
if (groupCreated)
{
    var newGroupId = SampleLineGroup.Create(groupName, alignment.ObjectId);
    group = (SampleLineGroup)Transaction.GetObject(newGroupId, OpenMode.ForWrite);
}
else
{
    group.UpgradeOpen();
}

// Any failure below throws, so nothing is kept.
var matchedSources = new HashSet<ObjectId>();
foreach (SectionSource source in group.GetSectionSources())
{
    if (!sourceStyles.TryGetValue(source.SourceId, out var styleId)) continue;
    source.IsSampled = true;
    if (!styleId.IsNull) source.StyleId = styleId;
    matchedSources.Add(source.SourceId);
}
if (matchedSources.Count != sourceStyles.Count)
    throw new InvalidOperationException("Some sampledSources are not available as section sources of this sample line group.");

var created = new List<object>();
foreach (var item in toCreate)
{
    double leftX = 0, leftY = 0, centerX = 0, centerY = 0, rightX = 0, rightY = 0;
    try
    {
        alignment.PointLocation(item.station, -leftWidth, ref leftX, ref leftY);
        alignment.PointLocation(item.station, 0, ref centerX, ref centerY);
        alignment.PointLocation(item.station, rightWidth, ref rightX, ref rightY);
    }
    catch (System.Exception ex)
    {
        throw new InvalidOperationException($"Station {item.station}: the sample line end points could not be located: {ex.Message}", ex);
    }
    var lineId = SampleLine.Create(item.name, group.ObjectId,
        new Point2dCollection { new Point2d(leftX, leftY), new Point2d(rightX, rightY) });
    var line = (SampleLine)Transaction.GetObject(lineId, OpenMode.ForRead);

    // Verify the actual vertices against the requested points; projecting
    // the end points back to a station is not reliable near curves.
    var vertices = line.Vertices.Cast<SampleLineVertex>().ToList();
    var targets = new[] { (label: "left", x: leftX, y: leftY), (label: "center", x: centerX, y: centerY), (label: "right", x: rightX, y: rightY) };
    var sides = new Dictionary<string, string>();
    foreach (var target in targets)
    {
        var matches = vertices.Where(v => Math.Abs(v.Location.X - target.x) <= tolerance && Math.Abs(v.Location.Y - target.y) <= tolerance).ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException($"Sample line '{item.name}': the {target.label} point was found {matches.Count} times among its vertices.");
        sides[target.label] = matches[0].Side.ToString();
    }
    if (vertices.Count > 3 || Math.Abs(line.Station - item.station) > tolerance)
        throw new InvalidOperationException($"Sample line '{item.name}' has {vertices.Count} vertices at station {line.Station}, expected 3 at {item.station}.");

    created.Add(new {
        name = line.Name,
        handle = line.Handle.ToString(),
        station = line.Station,
        item.origin,
        vertexCount = vertices.Count,
        leftSide = sides["left"],
        rightSide = sides["right"]
    });
}

var sources = new List<object>();
foreach (SectionSource source in group.GetSectionSources())
{
    sources.Add(new {
        name = source.SourceName,
        type = source.SourceType.ToString(),
        handle = source.SourceId.IsNull ? null : source.SourceId.Handle.ToString(),
        sampled = source.IsSampled,
        style = source.StyleId.IsNull ? null : (Transaction.GetObject(source.StyleId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Styles.StyleBase)?.Name
    });
}

return new {
    success = true,
    alignment = new { name = alignment.Name, handle = alignment.Handle.ToString() },
    group = new { name = group.Name, handle = group.Handle.ToString(), created = groupCreated, sampleLineCount = group.GetSampleLineIds().Count },
    leftWidth,
    rightWidth,
    createdCount = created.Count,
    created,
    skippedExisting,
    sources
};
```

## Usage Notes

- This is a write-capable template: confirm the full drawing identity first and run it with `civil3d_execute`; use `saveDrawing: true` only for an approved save.
- Stations come from three places, merged and deduplicated to 1 mm: explicit `stations` rows such as `[[14.01, ""], [41.44, "KSZ_A"]]`, multiples of `interval` within `rangeStart..rangeEnd` plus both range ends, and with `includeGeometryPoints` the alignment's geometry points in that range. Stations already present in an existing group are skipped and listed.
- Each sample line is straight and perpendicular, from offset `-leftWidth` (left of the alignment in its stationing direction) to `+rightWidth`. The template checks the created vertices against the computed left, centre and right points and the reported station; it does not rely on projecting end points back to the alignment, which can jump to another station near tight curves.
- Generated names are `namePrefix` plus the actual station, invariant culture and zero padded, for example `SL_014.01`. Collisions, including with names already in the group, stop the run before writing; raise `nameDecimals` or give explicit names. Names are labels only: after moving a sample line, its `Station` is the truth, not its name.
- `sampledSources` lists the surfaces (or other section sources) to sample, each with an optional section style chosen by role, for example the existing-ground style for the existing ground surface. Pick sources by handle; several surfaces of the same type are not guessed.
- A sample line group can then get a native section view group with `create_section_view_group`.
