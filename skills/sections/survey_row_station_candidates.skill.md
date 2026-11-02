---
name: survey_row_station_candidates
category: sections
description: Read-only list of candidate stations along an alignment from rows of surveyed COGO points measured across the road, optionally filtered by point group and description, ready for create_sample_lines
requires_write: false
aliases: ["szelvényjelölt-lista geodéziai pontsorokból", "mintavonal szelvények a bemért keresztirányú pontsorokból", "candidate sample line stations from survey point rows", "survey cross section rows to stations"]
workflow_tags: ["source_state", "coordinate_conversion"]
tested_civil_version: "2025"
validation_summary: "Offline: 2025 API compilation and host-free row grouping, skew fit, filters and limits. Live run on a real survey, point group handling and DBMOD staying 0 unverified."
parameters:
  - name: alignmentHandle
    type: string
    required: true
    description: Hex handle of the alignment the points are projected to (without station equations)
  - name: pointGroupNames
    type: string
    required: false
    description: Point group names separated by "|"; only their points are used, empty uses every COGO point of the drawing (default "")
  - name: descriptionFilter
    type: string
    required: false
    description: Raw description patterns separated by "|", with * and ? wildcards, case-insensitive; empty keeps every description (default "")
  - name: excludeDescriptions
    type: string
    required: false
    description: Raw description patterns separated by "|" to leave out, such as trees or manholes measured between rows (default "")
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
    description: Most COGO points read, 1 to 1000000 (default 200000)
  - name: detailLimit
    type: int
    required: false
    description: Most candidate rows listed in detail, 1 to 1000; sampleLineStations always holds all (default 200)
---

## Code Template

```csharp
var alignmentHandle = "ALIGNMENT_HANDLE";
var pointGroupNames = "";
var descriptionFilter = "";
var excludeDescriptions = "";
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
if (pointGroupNames == null || descriptionFilter == null || excludeDescriptions == null
    || pointGroupNames.Length > 4000 || descriptionFilter.Length > 4000 || excludeDescriptions.Length > 4000
    || pointGroupNames.Any(char.IsControl) || descriptionFilter.Any(char.IsControl) || excludeDescriptions.Any(char.IsControl))
    return new { success = false, error = "pointGroupNames, descriptionFilter and excludeDescriptions must be texts of at most 4000 characters." };
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
var includePatterns = splitList(descriptionFilter).Select(toPattern).ToList();
var excludePatterns = splitList(excludeDescriptions).Select(toPattern).ToList();
if (groupNames.Count > 50 || includePatterns.Count > 100 || excludePatterns.Count > 100)
    return new { success = false, error = "At most 50 point groups and 100 include or exclude patterns." };
var descriptionMatches = new Func<string, bool>(description =>
{
    var text = (description ?? "").Trim();
    return (includePatterns.Count == 0 || includePatterns.Any(p => p.IsMatch(text)))
        && !excludePatterns.Any(p => p.IsMatch(text));
});

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
if (groupNames.Count > 0)
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

var projected = new List<(double station, double offset, int index)>();
var pointInfo = new List<(uint number, string description)>();
int scanned = 0, filteredOut = 0, outsideAlignment = 0, beyondMaxOffset = 0, projectionMismatch = 0;
var scanTruncated = false;
foreach (ObjectId id in CivilDoc.CogoPoints)
{
    if (scanned >= scanLimit) { scanTruncated = true; break; }
    scanned++;
    var point = Transaction.GetObject(id, OpenMode.ForRead) as CogoPoint;
    if (point == null || point.IsErased) continue;
    if ((allowedNumbers != null && !allowedNumbers.Contains(point.PointNumber)) || !descriptionMatches(point.RawDescription))
    {
        filteredOut++;
        continue;
    }
    double station = 0, offset = 0, backX = 0, backY = 0;
    try
    {
        alignment.StationOffset(point.Easting, point.Northing, ref station, ref offset);
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
    if (Math.Sqrt(Math.Pow(backX - point.Easting, 2) + Math.Pow(backY - point.Northing, 2)) > projectionTolerance)
    {
        projectionMismatch++;
        continue;
    }
    projected.Add((station, offset, pointInfo.Count));
    pointInfo.Add((point.PointNumber, (point.RawDescription ?? "").Trim()));
}
if (scanTruncated)
    warnings.Add($"Only the first {scanLimit} COGO points were read; raise scanLimit or narrow the point groups.");

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
        descriptions = candidate.row.GroupBy(p => pointInfo[p.index].description)
            .OrderByDescending(g => g.Count()).Take(10)
            .Select(g => new { description = g.Key, count = g.Count() }).ToList(),
        pointNumbers = candidate.row.Select(p => pointInfo[p.index].number).Take(30).ToList()
    });
}

return new {
    success = true,
    alignment = new { name = alignment.Name, handle = alignment.Handle.ToString(), start = alignment.StartingStation, end = alignment.EndingStation },
    range = new { start = firstStation, end = lastStation },
    pointGroups = groups,
    points = new {
        totalInDrawing = CivilDoc.CogoPoints.Count,
        scanned,
        scanTruncated,
        filteredOut,
        outsideAlignment,
        beyondMaxOffset,
        projectionMismatch,
        used = projected.Count
    },
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

- This is a read-only template: run it with `civil3d_query` after confirming the drawing identity. It only reads COGO points, point groups and the alignment; it never updates a point group, creates sample lines or changes the drawing.
- The input is the survey as COGO points, for example points imported from the surveyor's file or from a survey database. Each point is projected to the alignment. Points beyond `maxOffset`, outside the alignment's station range, or whose station and offset do not lead back to the point within 1 cm (an unreliable projection, for example far from a tight curve) are counted and left out.
- A surveyed row is a group of points measured across the road at about the same station: kerb, gutter, crown and so on. Points are sorted by station and a new row starts where the gap to the next point is more than `rowGap`. A row is a candidate when it has at least `minPoints` points, its stations differ by at most `maxRowSpread`, its outermost points are at least `minOffsetSpan` apart across the road and, with `requireCrossing`, it has points on both sides.
- Filters are optional: `pointGroupNames` limits the points to point groups (out-of-date groups are used as stored and reported), `descriptionFilter` keeps raw descriptions matching a pattern such as `SZ*|FO*|TE*`, and `excludeDescriptions` drops points measured between rows, such as trees or manholes, that would otherwise join two rows.
- Each candidate's `station` is where a straight line fitted through the row crosses the alignment, kept inside the row's own station range; with all points at one offset it is the median station. `skewDeg` is the row's angle from perpendicular (positive when the row runs to higher stations towards the right) and `straightnessRms` how far the points are from that straight line, both in station units. On curves station is measured along the alignment, so the angle is approximate far from it.
- `rejectedRows` lists up to 50 groups that are not rows and why. A long group rejected for its station spread usually means a dense survey or a line measured along the road (such as a kerb line); lower `rowGap`, use `excludeDescriptions` or narrow the filters.
- `sampleLineStations` is ready for the `stations` parameter of `create_sample_lines`, as `[station, ""]` rows so the sample lines get station-based names. `surveyExtent` is the largest surveyed distance left and right of the alignment, a starting point for that recipe's `leftWidth` and `rightWidth`. Check the list first: this recipe only proposes stations.
- `roundTo` rounds the stations, for example to 0.01 or 0.1; rows that round to the same station are merged and counted.
- Office conventions such as the surveyor's description codes and usual tolerances belong to the caller's own instructions; this recipe only takes them as parameters.
