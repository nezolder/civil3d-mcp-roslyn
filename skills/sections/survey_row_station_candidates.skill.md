---
name: survey_row_station_candidates
category: sections
description: Read-only list of candidate stations along an alignment from survey point rows measured across the road, read from AutoCAD points, survey blocks (also inside xrefs) or COGO points, ready for create_sample_lines
requires_write: false
aliases: ["szelvényjelölt-lista geodéziai pontsorokból", "mintavonal szelvények a bemért keresztirányú pontsorokból", "candidate sample line stations from survey point rows", "geodéziai felmérés pontjai és blokkjai keresztszelvényekhez"]
workflow_tags: ["source_state", "coordinate_conversion"]
tested_civil_version: "2025"
validation_summary: "Offline + live (Civil 3D 2025, disposable copy of a real project): 13,843 survey points and blocks read from an xref; rows about every 25 m found with minPoints 6 and requireCrossing; DBMOD stayed 0. Name filters, COGO input, nested or rotated xrefs unverified."
parameters:
  - name: alignmentHandle
    type: string
    required: true
    description: Hex handle of the alignment the points are projected to (without station equations)
  - name: includePoints
    type: bool
    required: false
    description: Read AutoCAD point objects (POINT) from model space (default true)
  - name: includeBlocks
    type: bool
    required: false
    description: Read block references from model space at their insertion points, as surveyors often place a block on each measured point (default true)
  - name: includeXrefs
    type: bool
    required: false
    description: Also read points and blocks inside attached, loaded xrefs, such as the surveyor's DWG attached to the design drawing (default true)
  - name: includeCogoPoints
    type: bool
    required: false
    description: Also read COGO points of this drawing (default true)
  - name: layerFilter
    type: string
    required: false
    description: Layer patterns separated by "|", with * and ? wildcards, case-insensitive; xref layers also match without their "xref|" prefix; empty keeps every layer (default "")
  - name: excludeLayers
    type: string
    required: false
    description: Layer patterns separated by "|" to leave out, for example layers of trees or utilities (default "")
  - name: blockNames
    type: string
    required: false
    description: Block name patterns separated by "|" (dynamic blocks by their own name); empty keeps every block (default "")
  - name: excludeBlockNames
    type: string
    required: false
    description: Block name patterns separated by "|" to leave out, such as tree, manhole or north arrow symbols (default "")
  - name: pointGroupNames
    type: string
    required: false
    description: COGO point groups separated by "|"; only their COGO points are used, empty uses every COGO point (default "")
  - name: maxOffset
    type: double
    required: false
    description: Points farther than this from the alignment are ignored, greater than 0 (default 50)
  - name: rangeStart
    type: double
    required: false
    description: First candidate station; NaN uses the alignment start (default NaN)
  - name: rangeEnd
    type: double
    required: false
    description: Last candidate station; NaN uses the alignment end (default NaN)
  - name: rowGap
    type: double
    required: false
    description: Largest station gap between neighbouring points of one row; a larger gap starts a new row, 0.05 to 100 (default 1)
  - name: maxRowSpread
    type: double
    required: false
    description: Largest station difference inside one row, which allows skewed rows; wider groups are rejected as not a row (default 5)
  - name: minPoints
    type: int
    required: false
    description: Fewest points a row needs, 1 to 1000 (default 3)
  - name: minOffsetSpan
    type: double
    required: false
    description: Smallest distance across the road between the outermost points of a row, 0 or more (default 2)
  - name: requireCrossing
    type: bool
    required: false
    description: Keep only rows with points on both sides of the alignment (default false)
  - name: roundTo
    type: double
    required: false
    description: Round candidate stations to a multiple of this, 0 keeps them unrounded, at most rowGap (default 0)
  - name: scanLimit
    type: int
    required: false
    description: Most objects read (model space and xref entities plus COGO points), 1 to 1000000 (default 200000)
  - name: detailLimit
    type: int
    required: false
    description: Most candidate rows listed in detail, 1 to 1000; sampleLineStations always holds all (default 200)
---

## Code Template

```csharp
var alignmentHandle = "ALIGNMENT_HANDLE";
var includePoints = true;
var includeBlocks = true;
var includeXrefs = true;
var includeCogoPoints = true;
var layerFilter = "";
var excludeLayers = "";
var blockNames = "";
var excludeBlockNames = "";
var pointGroupNames = "";
var maxOffset = 50.0;
var rangeStart = double.NaN;
var rangeEnd = double.NaN;
var rowGap = 1.0;
var maxRowSpread = 5.0;
var minPoints = 3;
var minOffsetSpan = 2.0;
var requireCrossing = false;
var roundTo = 0.0;
var scanLimit = 200000;
var detailLimit = 200;
var projectionTolerance = 0.01;

if (string.IsNullOrWhiteSpace(alignmentHandle) || alignmentHandle == "ALIGNMENT_HANDLE"
    || !System.Text.RegularExpressions.Regex.IsMatch(alignmentHandle, "^[0-9A-Fa-f]{1,16}$"))
    return new { success = false, error = "alignmentHandle must be a hexadecimal handle." };
var filterTexts = new[] { layerFilter, excludeLayers, blockNames, excludeBlockNames, pointGroupNames };
if (filterTexts.Any(text => text == null || text.Length > 4000 || text.Any(char.IsControl)))
    return new { success = false, error = "layerFilter, excludeLayers, blockNames, excludeBlockNames and pointGroupNames must be texts of at most 4000 characters." };
if (!includePoints && !includeBlocks && !includeCogoPoints)
    return new { success = false, error = "Choose at least one source: includePoints, includeBlocks or includeCogoPoints." };
if (!double.IsFinite(maxOffset) || maxOffset <= 0 || maxOffset > 1000)
    return new { success = false, error = "maxOffset must be greater than 0 and at most 1000." };
if (double.IsInfinity(rangeStart) || double.IsInfinity(rangeEnd)
    || (!double.IsNaN(rangeStart) && !double.IsNaN(rangeEnd) && rangeEnd < rangeStart))
    return new { success = false, error = "rangeStart and rangeEnd must be finite or NaN, and rangeEnd not below rangeStart." };
if (!double.IsFinite(rowGap) || rowGap < 0.05 || rowGap > 100)
    return new { success = false, error = "rowGap must be between 0.05 and 100." };
if (!double.IsFinite(maxRowSpread) || maxRowSpread < 0 || maxRowSpread > 1000)
    return new { success = false, error = "maxRowSpread must be between 0 and 1000." };
if (minPoints < 1 || minPoints > 1000)
    return new { success = false, error = "minPoints must be between 1 and 1000." };
if (!double.IsFinite(minOffsetSpan) || minOffsetSpan < 0 || minOffsetSpan > 2000)
    return new { success = false, error = "minOffsetSpan must be between 0 and 2000." };
if (!double.IsFinite(roundTo) || roundTo < 0 || roundTo > rowGap)
    return new { success = false, error = "roundTo must be 0 or a positive value of at most rowGap." };
if (scanLimit < 1 || scanLimit > 1000000 || detailLimit < 1 || detailLimit > 1000)
    return new { success = false, error = "scanLimit must be 1..1000000 and detailLimit 1..1000." };

var splitList = new Func<string, List<string>>(text => text.Split('|')
    .Select(part => part.Trim()).Where(part => part != "")
    .Distinct(StringComparer.OrdinalIgnoreCase).ToList());
var toPattern = new Func<string, System.Text.RegularExpressions.Regex>(pattern =>
    new System.Text.RegularExpressions.Regex(
        "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant));
var groupNames = splitList(pointGroupNames);
var layerIncludes = splitList(layerFilter).Select(toPattern).ToList();
var layerExcludes = splitList(excludeLayers).Select(toPattern).ToList();
var blockIncludes = splitList(blockNames).Select(toPattern).ToList();
var blockExcludes = splitList(excludeBlockNames).Select(toPattern).ToList();
if (groupNames.Count > 50 || new[] { layerIncludes, layerExcludes, blockIncludes, blockExcludes }.Any(list => list.Count > 100))
    return new { success = false, error = "At most 50 point groups and 100 patterns per filter." };
// Names from an xref read "xref|name"; a pattern matches the full name or the part after the last "|".
var matchesAny = new Func<List<System.Text.RegularExpressions.Regex>, string, bool>((patterns, name) =>
{
    var text = (name ?? "").Trim();
    var shortName = text.Contains('|') ? text.Substring(text.LastIndexOf('|') + 1) : text;
    return patterns.Any(p => p.IsMatch(text) || p.IsMatch(shortName));
});
var layerMatches = new Func<string, bool>(layer =>
    (layerIncludes.Count == 0 || matchesAny(layerIncludes, layer)) && !matchesAny(layerExcludes, layer));
var blockMatches = new Func<string, bool>(block =>
    (blockIncludes.Count == 0 || matchesAny(blockIncludes, block)) && !matchesAny(blockExcludes, block));

// Points sorted by station; a row ends where the gap to the next point exceeds rowGap.
List<List<(double station, double offset, int index)>> GroupRows(IEnumerable<(double station, double offset, int index)> points)
{
    var rows = new List<List<(double station, double offset, int index)>>();
    List<(double station, double offset, int index)> current = null;
    foreach (var point in points.OrderBy(p => p.station).ThenBy(p => p.offset))
    {
        if (current == null || point.station - current[current.Count - 1].station > rowGap)
        {
            current = new List<(double station, double offset, int index)>();
            rows.Add(current);
        }
        current.Add(point);
    }
    return rows;
}

// A straight line station = crossing + slope * offset is fitted through the row,
// so a skewed row gets the station where it crosses the alignment.
(double station, double? crossing, double median, double minStation, double maxStation, double minOffset, double maxOffset,
    double skewDeg, double rms, int left, int right, string rejection) EvaluateRow(List<(double station, double offset, int index)> row)
{
    var sorted = row.Select(p => p.station).OrderBy(s => s).ToList();
    var n = sorted.Count;
    var median = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    var rowMinOffset = row.Min(p => p.offset);
    var rowMaxOffset = row.Max(p => p.offset);
    double? crossing = null;
    var slope = 0.0;
    var rms = 0.0;
    if (n >= 2 && rowMaxOffset - rowMinOffset > 1e-3)
    {
        var meanOffset = row.Average(p => p.offset);
        var meanStation = row.Average(p => p.station);
        slope = row.Sum(p => (p.offset - meanOffset) * (p.station - meanStation))
            / row.Sum(p => (p.offset - meanOffset) * (p.offset - meanOffset));
        var intercept = meanStation - slope * meanOffset;
        crossing = intercept;
        rms = Math.Sqrt(row.Sum(p => Math.Pow(p.station - (intercept + slope * p.offset), 2)) / n);
    }
    var station = crossing.HasValue ? Math.Min(sorted[n - 1], Math.Max(sorted[0], crossing.Value)) : median;
    string rejection = null;
    if (n < minPoints) rejection = "too few points";
    else if (sorted[n - 1] - sorted[0] > maxRowSpread) rejection = "station spread above maxRowSpread";
    else if (rowMaxOffset - rowMinOffset < minOffsetSpan) rejection = "offset span below minOffsetSpan";
    else if (requireCrossing && !(rowMinOffset < 0 && rowMaxOffset > 0)) rejection = "points on one side only";
    return (station, crossing, median, sorted[0], sorted[n - 1], rowMinOffset, rowMaxOffset,
        Math.Atan(slope) * 180 / Math.PI, rms, row.Count(p => p.offset < 0), row.Count(p => p.offset > 0), rejection);
}

double RoundStation(double station) => roundTo > 0 ? Math.Round(station / roundTo) * roundTo : station;

// Host access starts here.
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
    return new { success = false, error = "Alignments with station equations are not supported; stations would be ambiguous." };

var firstStation = double.IsNaN(rangeStart) ? alignment.StartingStation : rangeStart;
var lastStation = double.IsNaN(rangeEnd) ? alignment.EndingStation : rangeEnd;
if (firstStation < alignment.StartingStation - 1e-6 || lastStation > alignment.EndingStation + 1e-6 || lastStation < firstStation)
    return new { success = false, error = "The station range must lie inside the alignment.", alignmentStart = alignment.StartingStation, alignmentEnd = alignment.EndingStation };

var warnings = new List<string>();
HashSet<uint> allowedNumbers = null;
var groups = new List<object>();
if (includeCogoPoints && groupNames.Count > 0)
{
    allowedNumbers = new HashSet<uint>();
    var available = new List<string>();
    var byName = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
    foreach (ObjectId id in CivilDoc.PointGroups)
    {
        if (Transaction.GetObject(id, OpenMode.ForRead) is PointGroup candidate && !candidate.IsErased)
        {
            available.Add(candidate.Name);
            byName[candidate.Name] = id;
        }
    }
    var unknown = groupNames.Where(name => !byName.ContainsKey(name)).ToList();
    if (unknown.Count > 0)
        return new { success = false, error = "Unknown point group names.", unknown, available };
    foreach (var name in groupNames)
    {
        var group = (PointGroup)Transaction.GetObject(byName[name], OpenMode.ForRead);
        var numbers = group.GetPointNumbers();
        foreach (var number in numbers) allowedNumbers.Add(number);
        groups.Add(new { name = group.Name, pointCount = numbers.Length, outOfDate = group.IsOutOfDate });
        if (group.IsOutOfDate)
            warnings.Add($"Point group '{group.Name}' is out of date; its last stored point list was used. Update it in Civil 3D to include recent changes.");
    }
}

// Survey points: AutoCAD points, block insertion points (also inside xrefs) and COGO points.
var collected = new List<(double x, double y, string kind, string name, string layer, string id, bool fromXref)>();
var seenXy = new HashSet<(long, long)>();
var blockCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var xrefsRead = new List<string>();
var unresolvedXrefs = new List<string>();
int scanned = 0, filteredOut = 0, duplicates = 0;
var scanTruncated = false;

void Collect(double x, double y, string kind, string name, string layer, string id, bool fromXref)
{
    // A block and a point on the same spot are one measured point.
    if (!seenXy.Add(((long)Math.Round(x / 1e-3), (long)Math.Round(y / 1e-3)))) { duplicates++; return; }
    collected.Add((x, y, kind, name, layer, id, fromXref));
}

void Tally(Dictionary<string, int> counts, string key) => counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;

void Walk(ObjectId ownerId, Matrix3d transform, int depth, string xrefName)
{
    var owner = (BlockTableRecord)Transaction.GetObject(ownerId, OpenMode.ForRead);
    foreach (ObjectId id in owner)
    {
        if (scanTruncated) return;
        if (scanned >= scanLimit) { scanTruncated = true; return; }
        scanned++;
        var entity = Transaction.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
        if (entity == null || entity.IsErased) continue;
        var reference = xrefName == null ? entity.Handle.ToString() : xrefName + ":" + entity.Handle.ToString();
        if (entity is DBPoint point)
        {
            if (!includePoints) continue;
            Tally(layerCounts, entity.Layer);
            if (!layerMatches(entity.Layer)) { filteredOut++; continue; }
            var position = point.Position.TransformBy(transform);
            Collect(position.X, position.Y, "point", entity.Layer, entity.Layer, reference, xrefName != null);
        }
        else if (entity is BlockReference block)
        {
            var record = (BlockTableRecord)Transaction.GetObject(block.BlockTableRecord, OpenMode.ForRead);
            if (record.IsFromExternalReference || record.IsFromOverlayReference)
            {
                if (!includeXrefs) continue;
                if (!record.IsResolved) { if (!unresolvedXrefs.Contains(record.Name)) unresolvedXrefs.Add(record.Name); continue; }
                if (depth >= 4) continue;
                if (!xrefsRead.Contains(record.Name)) xrefsRead.Add(record.Name);
                Walk(record.ObjectId, transform * block.BlockTransform, depth + 1, record.Name);
                continue;
            }
            if (!includeBlocks || record.IsLayout) continue;
            var blockName = record.Name;
            if (block.IsDynamicBlock && !block.DynamicBlockTableRecord.IsNull)
                blockName = ((BlockTableRecord)Transaction.GetObject(block.DynamicBlockTableRecord, OpenMode.ForRead)).Name;
            Tally(blockCounts, blockName);
            Tally(layerCounts, entity.Layer);
            if (!blockMatches(blockName) || !layerMatches(entity.Layer)) { filteredOut++; continue; }
            var position = block.Position.TransformBy(transform);
            Collect(position.X, position.Y, "block", blockName, entity.Layer, reference, xrefName != null);
        }
    }
}

if (includePoints || includeBlocks)
    Walk(SymbolUtilityServices.GetBlockModelSpaceId(Database), Matrix3d.Identity, 0, null);
if (includeCogoPoints && !scanTruncated)
{
    foreach (ObjectId id in CivilDoc.CogoPoints)
    {
        if (scanned >= scanLimit) { scanTruncated = true; break; }
        scanned++;
        var cogo = Transaction.GetObject(id, OpenMode.ForRead) as CogoPoint;
        if (cogo == null || cogo.IsErased) continue;
        if ((allowedNumbers != null && !allowedNumbers.Contains(cogo.PointNumber)) || !layerMatches(cogo.Layer))
        {
            filteredOut++;
            continue;
        }
        Collect(cogo.Easting, cogo.Northing, "cogo", (cogo.RawDescription ?? "").Trim(), cogo.Layer, "#" + cogo.PointNumber, false);
    }
}
if (scanTruncated)
    warnings.Add($"Only the first {scanLimit} objects were read; raise scanLimit or narrow the sources.");
if (unresolvedXrefs.Count > 0)
    warnings.Add("Some xrefs are not loaded and were skipped: " + string.Join(", ", unresolvedXrefs.Take(10)));

var projected = new List<(double station, double offset, int index)>();
int outsideAlignment = 0, beyondMaxOffset = 0, projectionMismatch = 0;
for (var i = 0; i < collected.Count; i++)
{
    var item = collected[i];
    double station = 0, offset = 0, backX = 0, backY = 0;
    try
    {
        alignment.StationOffset(item.x, item.y, ref station, ref offset);
        alignment.PointLocation(station, offset, ref backX, ref backY);
    }
    catch
    {
        outsideAlignment++;
        continue;
    }
    if (!double.IsFinite(station) || !double.IsFinite(offset)) { outsideAlignment++; continue; }
    if (Math.Abs(offset) > maxOffset) { beyondMaxOffset++; continue; }
    // The station and offset must lead back to the point; otherwise the projection is unreliable.
    if (Math.Sqrt(Math.Pow(backX - item.x, 2) + Math.Pow(backY - item.y, 2)) > projectionTolerance)
    {
        projectionMismatch++;
        continue;
    }
    projected.Add((station, offset, i));
}

var rows = GroupRows(projected);
var candidates = new List<(double station, List<(double station, double offset, int index)> row,
    (double station, double? crossing, double median, double minStation, double maxStation, double minOffset, double maxOffset,
        double skewDeg, double rms, int left, int right, string rejection) info)>();
var rejectedRows = new List<object>();
int tooFewRows = 0, tooFewPoints = 0, rejectedCount = 0, outsideRange = 0, merged = 0;
var usedKeys = new HashSet<long>();
foreach (var row in rows)
{
    var info = EvaluateRow(row);
    if (info.rejection == "too few points")
    {
        tooFewRows++;
        tooFewPoints += row.Count;
        continue;
    }
    if (info.rejection != null)
    {
        rejectedCount++;
        if (rejectedRows.Count < 50)
            rejectedRows.Add(new { reason = info.rejection, pointCount = row.Count, info.minStation, info.maxStation, info.minOffset, info.maxOffset });
        continue;
    }
    var station = Math.Min(alignment.EndingStation, Math.Max(alignment.StartingStation, RoundStation(info.station)));
    if (station < firstStation - 1e-6 || station > lastStation + 1e-6) { outsideRange++; continue; }
    if (!usedKeys.Add((long)Math.Round(station / 1e-3))) { merged++; continue; }
    candidates.Add((station, row, info));
}
if (merged > 0)
    warnings.Add($"{merged} rows rounded to the station of an earlier row and were left out; use a smaller roundTo.");
if (candidates.Count > 1000)
    warnings.Add("More than 1000 candidates; create_sample_lines takes at most 1000 stations per run.");
var skewed = candidates.Count(c => Math.Abs(c.info.skewDeg) > 10);
if (skewed > 0)
    warnings.Add($"{skewed} rows are skewed more than 10 degrees; a perpendicular sample line at the crossing station leaves their outer points off the line.");

var details = new List<object>();
double? previous = null;
foreach (var candidate in candidates)
{
    var spacing = previous.HasValue ? candidate.station - previous.Value : (double?)null;
    previous = candidate.station;
    if (details.Count >= detailLimit) continue;
    var info = candidate.info;
    details.Add(new {
        station = candidate.station,
        crossingStation = info.crossing,
        medianStation = info.median,
        info.minStation,
        info.maxStation,
        stationSpread = info.maxStation - info.minStation,
        spacingFromPrevious = spacing,
        pointCount = candidate.row.Count,
        leftCount = info.left,
        rightCount = info.right,
        info.minOffset,
        info.maxOffset,
        skewDeg = info.skewDeg,
        straightnessRms = info.rms,
        names = candidate.row.GroupBy(p => collected[p.index].kind + ": " + collected[p.index].name)
            .OrderByDescending(g => g.Count()).Take(10)
            .Select(g => new { name = g.Key, count = g.Count() }).ToList(),
        points = candidate.row.OrderBy(p => p.offset)
            .Select(p => new { id = collected[p.index].id, offset = p.offset, station = p.station }).Take(30).ToList()
    });
}

return new {
    success = true,
    alignment = new { name = alignment.Name, handle = alignment.Handle.ToString(), start = alignment.StartingStation, end = alignment.EndingStation },
    range = new { start = firstStation, end = lastStation },
    sources = new {
        scanned,
        scanTruncated,
        filteredOut,
        duplicates,
        autocadPoints = collected.Count(c => c.kind == "point"),
        blocks = collected.Count(c => c.kind == "block"),
        cogoPoints = collected.Count(c => c.kind == "cogo"),
        fromXrefs = collected.Count(c => c.fromXref),
        xrefsRead,
        unresolvedXrefs,
        pointGroups = groups,
        blockNamesSeen = blockCounts.OrderByDescending(p => p.Value).Take(30).Select(p => new { name = p.Key, count = p.Value }).ToList(),
        layersSeen = layerCounts.OrderByDescending(p => p.Value).Take(30).Select(p => new { name = p.Key, count = p.Value }).ToList()
    },
    projection = new { outsideAlignment, beyondMaxOffset, projectionMismatch, used = projected.Count },
    rows = new {
        found = rows.Count,
        candidates = candidates.Count,
        tooFewPointRows = tooFewRows,
        tooFewPointPoints = tooFewPoints,
        rejected = rejectedCount,
        outsideRange,
        merged
    },
    surveyExtent = candidates.Count == 0 ? null : new {
        left = Math.Max(0, -candidates.Min(c => c.info.minOffset)),
        right = Math.Max(0, candidates.Max(c => c.info.maxOffset))
    },
    candidateCount = candidates.Count,
    candidates = details,
    candidatesTruncated = candidates.Count > details.Count,
    rejectedRows,
    sampleLineStations = candidates.Select(c => new object[] { c.station, "" }).ToList(),
    warnings
};
```

## Usage Notes

- This is a read-only template: run it with `civil3d_query` after confirming the drawing identity. It only reads entities, xrefs, COGO points, point groups and the alignment; it never changes the drawing, an xref or a point group.
- Purpose: for pavement rehabilitation designs (strengthening, wearing course replacement) surveyed by hand, the surveyor measures rows of points across the road, usually at the centreline, lane lines, pavement edges, kerb tops and shoulder edges. Cross sections taken exactly at these rows follow measured heights; between rows the surface is interpolated. This recipe finds those rows and proposes their stations.
- Sources: surveys usually arrive as plain AutoCAD DWGs. By default the recipe reads AutoCAD points (POINT) and block insertion points in model space, the same inside attached and loaded xrefs (nested xrefs up to 4 levels, with their insertion, rotation and scale), and COGO points. A block and a point on the same spot (within 1 mm) count once. Ordinary blocks are not opened: a block counts as one point at its insertion point, so the surveyor's point block must be inserted at the measured point.
- Filters: `layerFilter`/`excludeLayers` and `blockNames`/`excludeBlockNames` take `*`/`?` patterns separated by `|`; an xref's `xref|name` layers and blocks also match the pattern without the prefix. `sources.blockNamesSeen` and `sources.layersSeen` list what was found before filtering, so a first unfiltered run shows which names to keep or exclude (for example tree, manhole or text-only symbols that sit between rows). `pointGroupNames` limits COGO points only.
- Each point is projected to the alignment. Points beyond `maxOffset`, outside the alignment's station range, or whose station and offset do not lead back to the point within 1 cm (an unreliable projection, for example far from a tight curve) are counted and left out. Points of side streets close to the alignment can form a row at a junction; check those candidates.
- A row is a group of points at about the same station. Points are sorted by station and a new row starts where the gap to the next point is more than `rowGap`. A row is a candidate when it has at least `minPoints` points, its stations differ by at most `maxRowSpread`, its outermost points are at least `minOffsetSpan` apart across the road and, with `requireCrossing`, it has points on both sides. A dense or scanned survey forms one long group that is rejected for its station spread; this recipe is meant for hand-measured rows.
- Each candidate's `station` is where a straight line fitted through the row crosses the alignment, kept inside the row's own station range; with all points at one offset it is the median station. `skewDeg` is the row's angle from perpendicular (positive when the row runs to higher stations towards the right) and `straightnessRms` how far the points are from that straight line, both in station units. On curves station is measured along the alignment, so the angle is approximate far from it. `points` lists the row's points (entity handle, `xref:handle` or `#number` for COGO points) by offset.
- `rejectedRows` lists up to 50 groups that are not rows and why. A long group rejected for its station spread usually means a line measured along the road (such as a kerb line) or symbols between rows; lower `rowGap`, exclude those layers or blocks, or narrow the filters.
- `sampleLineStations` is ready for the `stations` parameter of `create_sample_lines`, as `[station, ""]` rows so the sample lines get station-based names. `surveyExtent` is the largest surveyed distance left and right of the alignment, a starting point for that recipe's `leftWidth` and `rightWidth`. Check the list first: this recipe only proposes stations.
- `roundTo` rounds the stations, for example to 0.01 or 0.1; rows that round to the same station are merged and counted.
- Office conventions such as the surveyor's layers, block names and usual tolerances belong to the caller's own instructions; this recipe only takes them as parameters.
