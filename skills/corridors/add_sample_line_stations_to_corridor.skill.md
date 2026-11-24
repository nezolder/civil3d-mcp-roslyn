---
name: add_sample_line_stations_to_corridor
category: corridors
description: Add the stations where the sample lines of one group cross each corridor baseline as extra corridor stations, in every baseline region they fall into, so that corridor sections are built exactly at the sample lines
requires_write: true
aliases: ["mintavonal-szelvények hozzáadása a korridor alapvonalaihoz", "add sample line stations to corridor baselines", "korridor kiegészítő szelvények a mintavonalak metszésénél", "corridor frequency stations at sample lines"]
workflow_tags: ["modeling"]
tested_civil_version: "2025"
validation_summary: "Live (Civil 3D 2025, copy of a real project): 2 stations added from a sample line group in another drawing, rebuilt, read back as built sections; a second run planned 0. Offset baselines untested. Report: docs/validation/street-recipes-d6eb7e8-2026-11-24.md."
parameters:
  - name: corridorHandle
    type: string
    required: true
    description: Hex handle of one corridor
  - name: sampleLineGroupHandle
    type: string
    required: true
    description: Hex handle of the sample line group whose lines give the stations
  - name: sampleLineDrawingPath
    type: string
    required: false
    description: Full path of the saved drawing that holds the sample line group, when it is not this drawing (for example the cross section drawing that references this corridor); read in a side database, read only (default "")
  - name: baselineNameFilter
    type: string
    required: false
    description: Exact baseline name; empty uses every baseline the sample lines cross (default "")
  - name: descriptionPrefix
    type: string
    required: false
    description: Text put before the sample line name in each added station's description (default "")
  - name: stationTolerance
    type: double
    required: false
    description: A station closer than this to an existing corridor station of the region is not added, in drawing units (default 0.02; sample line and corridor stations often differ by a fraction of a millimetre)
  - name: rebuild
    type: bool
    required: false
    description: Rebuild the corridor in the same call after adding; leave false for a large corridor and rebuild in a separate call (default false)
  - name: maxStations
    type: int
    required: false
    description: Refuse when more than this many stations would be added, 1 to 2000 (default 500)
---

## Code Template

```csharp
var corridorHandle = "CORRIDOR_HANDLE";
var sampleLineGroupHandle = "SAMPLE_LINE_GROUP_HANDLE";
var sampleLineDrawingPath = "";
var baselineNameFilter = "";
var descriptionPrefix = "";
var stationTolerance = 0.02;
var rebuild = false;
var maxStations = 500;

if (!double.IsFinite(stationTolerance) || stationTolerance <= 0 || stationTolerance > 1)
    return new { success = false, error = "stationTolerance must be greater than 0 and at most 1." };
if (maxStations < 1 || maxStations > 2000) return new { success = false, error = "maxStations must be between 1 and 2000." };
descriptionPrefix ??= "";

ObjectId Resolve(string text)
{
    if (string.IsNullOrWhiteSpace(text)) return ObjectId.Null;
    if (!long.TryParse(text.Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
        return ObjectId.Null;
    return Database.TryGetObjectId(new Handle(value), out var id) && !id.IsErased ? id : ObjectId.Null;
}

var corridorId = Resolve(corridorHandle);
var corridor = corridorId.IsNull ? null : Transaction.GetObject(corridorId, OpenMode.ForRead) as Corridor;
if (corridor == null) return new { success = false, error = "corridorHandle does not resolve to a corridor in this drawing." };
if (corridor.IsReferenceObject) return new { success = false, error = "The corridor is a data reference; change it in its source drawing." };
// The plan polyline of every sample line, from this drawing or from a saved drawing read in a side database.
var lines = new List<(string name, List<Point2d> points)>();
string groupName = null;
string ReadGroup(Database db, Transaction tr)
{
    if (!long.TryParse((sampleLineGroupHandle ?? "").Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0
        || !db.TryGetObjectId(new Handle(value), out var groupId) || groupId.IsErased)
        return "sampleLineGroupHandle does not resolve in the drawing that holds the sample lines.";
    var group = tr.GetObject(groupId, OpenMode.ForRead) as SampleLineGroup;
    if (group == null) return "sampleLineGroupHandle does not resolve to a sample line group.";
    groupName = group.Name;
    foreach (ObjectId id in group.GetSampleLineIds())
    {
        var line = tr.GetObject(id, OpenMode.ForRead) as SampleLine;
        if (line == null) continue;
        var pts = new List<Point2d>();
        for (var i = 0; i < line.Vertices.Count; i++) pts.Add(new Point2d(line.Vertices[i].Location.X, line.Vertices[i].Location.Y));
        if (pts.Count >= 2) lines.Add((line.Name, pts));
    }
    return null;
}
string groupError;
var sideDrawing = string.IsNullOrWhiteSpace(sampleLineDrawingPath) ? null : sampleLineDrawingPath.Trim();
if (sideDrawing == null) groupError = ReadGroup(Database, Transaction);
else
{
    if (!System.IO.Path.IsPathRooted(sideDrawing) || !System.IO.File.Exists(sideDrawing))
        return new { success = false, error = "sampleLineDrawingPath must be the full path of an existing drawing." };
    if (string.Equals(System.IO.Path.GetFullPath(sideDrawing), System.IO.Path.GetFullPath(Database.Filename), StringComparison.OrdinalIgnoreCase))
        return new { success = false, error = "sampleLineDrawingPath is this drawing; leave it empty." };
    using (var side = new Database(false, true))
    {
        side.ReadDwgFile(sideDrawing, FileOpenMode.OpenForReadAndAllShare, true, null);
        side.CloseInput(true);
        using (var tr = side.TransactionManager.StartOpenCloseTransaction())
        {
            groupError = ReadGroup(side, tr);
            tr.Commit();
        }
    }
}
if (groupError != null) return new { success = false, error = groupError };
if (lines.Count == 0) return new { success = false, error = "The sample line group has no sample lines." };

// Where a sample line crosses an alignment: the offset changes sign along a segment; refine by halving.
(double station, double offset)? StationOffsetAt(Alignment alignment, Point2d p)
{
    try
    {
        double station = 0, offset = 0;
        alignment.StationOffset(p.X, p.Y, ref station, ref offset);
        return (station, offset);
    }
    catch (System.Exception) { return null; }
}
List<double> Crossings(Alignment alignment, List<Point2d> pts)
{
    var result = new List<double>();
    for (var i = 1; i < pts.Count; i++)
    {
        var a = pts[i - 1];
        var b = pts[i];
        var sa = StationOffsetAt(alignment, a);
        var sb = StationOffsetAt(alignment, b);
        if (sa == null || sb == null) continue;
        if (Math.Abs(sa.Value.offset) < 1e-9) { result.Add(sa.Value.station); continue; }
        if (Math.Sign(sa.Value.offset) == Math.Sign(sb.Value.offset)) continue;
        var low = a; var high = b; var lowOffset = sa.Value.offset;
        (double station, double offset)? mid = null;
        for (var k = 0; k < 60; k++)
        {
            var m = new Point2d((low.X + high.X) / 2, (low.Y + high.Y) / 2);
            mid = StationOffsetAt(alignment, m);
            if (mid == null) break;
            if (Math.Abs(mid.Value.offset) < 1e-7 || low.GetDistanceTo(high) < 1e-7) break;
            if (Math.Sign(mid.Value.offset) == Math.Sign(lowOffset)) { low = m; lowOffset = mid.Value.offset; } else high = m;
        }
        if (mid != null && Math.Abs(mid.Value.offset) < 1e-4) result.Add(mid.Value.station);
    }
    var last = StationOffsetAt(alignment, pts[pts.Count - 1]);
    if (last != null && Math.Abs(last.Value.offset) < 1e-9) result.Add(last.Value.station);
    return result.Distinct().ToList();
}

// Plan every addition first; nothing is written before the whole plan holds.
var plan = new List<(string baseline, int regionIndex, string region, double station, string description, string line)>();
var report = new List<object>();
var offsetBaselines = new List<string>();
foreach (Baseline baseline in corridor.Baselines)
{
    if (!string.IsNullOrWhiteSpace(baselineNameFilter) && !string.Equals(baseline.Name, baselineNameFilter.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
    if (baseline.IsFeatureLineBased()) { report.Add(new { baseline = baseline.Name, skipped = "feature line based" }); continue; }
    var alignment = Transaction.GetObject(baseline.AlignmentId, OpenMode.ForRead) as Alignment;
    if (alignment == null) { report.Add(new { baseline = baseline.Name, skipped = "no alignment" }); continue; }
    var added = 0; var existing = 0; var outside = 0;
    var regions = baseline.BaselineRegions;
    foreach (var line in lines)
    {
        foreach (var station in Crossings(alignment, line.points))
        {
            var placed = false;
            for (var r = 0; r < regions.Count; r++)
            {
                var region = regions[r];
                if (station < region.StartStation - 1e-9 || station > region.EndStation + 1e-9) continue;
                placed = true;
                for (var o = 0; o < region.OffsetBaselines.Count; o++)
                {
                    var name = region.OffsetBaselines[o].Name;
                    if (!offsetBaselines.Contains(baseline.Name + " / " + name)) offsetBaselines.Add(baseline.Name + " / " + name);
                }
                if (region.SortedStations().Concat(region.AdditionalStations()).Any(s => Math.Abs(s - station) < stationTolerance)
                    || plan.Any(p => p.baseline == baseline.Name && p.regionIndex == r && Math.Abs(p.station - station) < stationTolerance))
                { existing++; continue; }
                plan.Add((baseline.Name, r, region.Name, station, (descriptionPrefix + line.name).Trim(), line.name));
                added++;
            }
            if (!placed) outside++;
        }
    }
    report.Add(new { baseline = baseline.Name, alignment = alignment.Name, toAdd = added, alreadyPresent = existing, crossingsOutsideRegions = outside });
}
if (plan.Count > maxStations)
    return new { success = false, error = $"{plan.Count} stations would be added, more than maxStations {maxStations}.", baselines = report };

var missing = new List<(string baseline, int regionIndex, string region, double station, string description, string line)>();
if (plan.Count > 0)
{
    // Open for write only now, so a call that adds nothing leaves the corridor untouched.
    var writable = (Corridor)Transaction.GetObject(corridorId, OpenMode.ForWrite);
    BaselineRegion RegionOf(string baselineName, int index)
    {
        foreach (Baseline b in writable.Baselines)
            if (b.Name == baselineName) return b.BaselineRegions[index];
        throw new InvalidOperationException($"Baseline {baselineName} is no longer in the corridor.");
    }
    foreach (var item in plan) RegionOf(item.baseline, item.regionIndex).AddStation(item.station, item.description);
    // Postcondition: every planned station is now an additional station of its region. SortedStations lists
    // the built stations and changes only when the corridor is rebuilt; AdditionalStations changes at once.
    foreach (var item in plan)
        if (!RegionOf(item.baseline, item.regionIndex).AdditionalStations().Any(s => Math.Abs(s - item.station) < 1e-6)) missing.Add(item);
    if (missing.Count > 0)
        throw new InvalidOperationException($"{missing.Count} added stations are not among the additional stations of their region afterwards (first: {missing[0].baseline} at {missing[0].station:0.###}).");
    if (rebuild) writable.Rebuild();
}

return new
{
    success = true,
    corridor = corridor.Name,
    sampleLineGroup = groupName,
    sampleLineDrawing = sideDrawing ?? "this drawing",
    sampleLines = lines.Count,
    stationsAdded = plan.Count,
    rebuilt = rebuild && plan.Count > 0,
    baselines = report,
    planned = plan.Take(200).Select(p => new { baseline = p.baseline, region = p.region, station = Math.Round(p.station, 4), sampleLine = p.line }).ToList(),
    offsetBaselinesNotChanged = offsetBaselines
};
```

## Usage Notes

- Write recipe: identify the drawing, then run it once with `preview: true` to see `stationsAdded` and the per-baseline counts, then for real with `requireClean`, `backup` and `saveDrawing` as the usage guide's `writing` topic says.
- The sample lines often live in the cross section drawing, which only references the corridor; the stations must be added in the corridor's source drawing. Run the recipe there with `sampleLineDrawingPath` set to the cross section drawing and `sampleLineGroupHandle` to the group's handle in that drawing. The saved file is read, so save the cross section drawing first when its sample lines changed.
- For every main baseline the station is where the sample line's plan polyline crosses the baseline's alignment, so side streets and other baselines get their own station, not the sample line's station number. A crossing is added to every region of that baseline that contains it, unless the region already has a station within `stationTolerance`.
- Offset baselines are listed in `offsetBaselinesNotChanged`: the .NET API has no call that adds a station to an offset baseline. Their sections follow the main baseline's stations in their region; check them in Civil 3D.
- The corridor is not rebuilt unless `rebuild` is true. For a large corridor rebuild in a separate call (or from the user interface), as the usage guide's topic `civil` advises for long operations. After rebuilding, `corridor_section_check` with the same `sampleLineGroupHandle` checks the sections at these stations.
- A crossing within `stationTolerance` of a built station (`SortedStations`) or of an additional station of the region counts as already present. Added stations appear in the region's `AdditionalStations` at once, but in `SortedStations` (and as sections) only after the corridor is rebuilt; the recipe checks the former. Seen live: sample line stations 0.1 to 15 mm from corridor stations that had been added earlier with rounded values.
- Feature line based baselines and data reference corridors are skipped or refused. The recipe changes nothing when every crossing is already present.
- In field use (Civil 3D 2025) sections between corridor stations did not match the sample lines until these stations were added to all affected baselines by hand or by ad hoc scripts.
