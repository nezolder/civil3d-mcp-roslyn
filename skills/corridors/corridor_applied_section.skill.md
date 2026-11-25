---
name: corridor_applied_section
category: corridors
description: Read the built corridor section at one station of a baseline - region, assembly, and per applied subassembly its coded points, links with slope and length, and shapes with area, by offset and elevation
requires_write: false
aliases: ["korridor keresztszelvény pontjai linkjei egy szelvényben", "applied corridor section at a station points links shapes", "beépített szerelvény adott szelvényben", "corridor section geometry by code at station"]
workflow_tags: ["geometry_query", "source_state"]
tested_civil_version: "2025"
validation_summary: "Offline: compiles against the Civil 3D 2025 API. Not yet run in Civil 3D."
parameters:
  - name: corridorHandle
    type: string
    required: true
    description: Hex handle of one corridor
  - name: baselineName
    type: string
    required: false
    description: Exact baseline name; may be empty when the corridor has one baseline (default "")
  - name: station
    type: double
    required: true
    description: Station on the baseline's alignment, in drawing units
  - name: stationSnapTolerance
    type: double
    required: false
    description: The nearest built corridor station is read when it is this close to the given station, in drawing units (default 0.02)
  - name: codeFilter
    type: array
    required: false
    description: Point, link and shape codes to list; empty lists every item (default none)
  - name: includeUncoded
    type: bool
    required: false
    description: Also list points and links that carry no code (default false)
  - name: maxItems
    type: int
    required: false
    description: Maximum points plus links plus shapes listed, 1 to 2000 (default 400)
---

## Code Template

```csharp
var corridorHandle = "CORRIDOR_HANDLE";
var baselineName = "";
var station = double.NaN;
var stationSnapTolerance = 0.02;
var codeFilter = new string[] { };
var includeUncoded = false;
var maxItems = 400;

codeFilter ??= new string[] { };
if (!double.IsFinite(station)) return new { success = false, error = "station must be a finite number." };
if (!double.IsFinite(stationSnapTolerance) || stationSnapTolerance < 0 || stationSnapTolerance > 10)
    return new { success = false, error = "stationSnapTolerance must be between 0 and 10." };
if (maxItems < 1 || maxItems > 2000) return new { success = false, error = "maxItems must be between 1 and 2000." };

Corridor corridor = null;
if (long.TryParse((corridorHandle ?? "").Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var handleValue)
    && handleValue > 0 && Database.TryGetObjectId(new Handle(handleValue), out var corridorId) && !corridorId.IsErased)
    corridor = Transaction.GetObject(corridorId, OpenMode.ForRead) as Corridor;
if (corridor == null) return new { success = false, error = "corridorHandle does not resolve to a corridor in this drawing." };

var baselineNames = new List<string>();
Baseline baseline = null;
foreach (Baseline candidate in corridor.Baselines)
{
    baselineNames.Add(candidate.Name);
    if (string.IsNullOrWhiteSpace(baselineName) || string.Equals(candidate.Name, baselineName.Trim(), StringComparison.OrdinalIgnoreCase))
        baseline ??= candidate;
}
if (string.IsNullOrWhiteSpace(baselineName) && baselineNames.Count > 1)
    return new { success = false, error = "The corridor has several baselines; give baselineName.", baselines = baselineNames };
if (baseline == null) return new { success = false, error = "No baseline with that name.", baselines = baselineNames };

// GetAppliedAssemblyAtStation needs an exact station of the built corridor.
var built = baseline.SortedStations();
if (built.Length == 0) return new { success = false, error = "The baseline has no built stations; rebuild the corridor first.", corridorOutOfDate = corridor.IsOutOfDate };
var nearest = built.OrderBy(s => Math.Abs(s - station)).First();
if (Math.Abs(nearest - station) > stationSnapTolerance)
{
    var index = Array.BinarySearch(built, station);
    if (index < 0) index = ~index;
    return new
    {
        success = false,
        error = "No built corridor station within stationSnapTolerance; the corridor has no section there.",
        stationBefore = index > 0 ? Math.Round(built[index - 1], 4) : (double?)null,
        stationAfter = index < built.Length ? Math.Round(built[index], 4) : (double?)null,
        hint = "Read one of these stations, or add the station to the region and rebuild."
    };
}

string regionName = null;
string regionAssembly = null;
foreach (BaselineRegion region in baseline.BaselineRegions)
{
    if (nearest < region.StartStation - 1e-6 || nearest > region.EndStation + 1e-6) continue;
    regionName = region.Name;
    try { regionAssembly = (Transaction.GetObject(region.AssemblyId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Assembly)?.Name; }
    catch (System.Exception) { regionAssembly = null; }
    break;
}

AppliedAssembly applied;
try { applied = baseline.GetAppliedAssemblyAtStation(nearest); }
catch (System.ArgumentException) { applied = null; }
if (applied == null) return new { success = false, error = "No assembly is applied at this station (Station not processed).", station = Math.Round(nearest, 4), region = regionName };

List<string> Codes(CorridorCodeCollection codes)
{
    var list = new List<string>();
    for (var i = 0; i < codes.Count; i++) list.Add(codes[i]);
    return list;
}
bool Wanted(List<string> codes)
{
    if (codes.Count == 0) return includeUncoded && codeFilter.Length == 0;
    return codeFilter.Length == 0 || codes.Any(c => codeFilter.Any(f => string.Equals(f, c, StringComparison.OrdinalIgnoreCase)));
}

var listed = 0;
var cut = false;
var codeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
void Count(List<string> codes) { foreach (var c in codes) codeCounts[c] = codeCounts.TryGetValue(c, out var n) ? n + 1 : 1; }
bool Room() { if (listed < maxItems) { listed++; return true; } cut = true; return false; }

var subassemblies = new List<object>();
foreach (AppliedSubassembly sub in applied.GetAppliedSubassemblies())
{
    string subName = null;
    try { subName = (Transaction.GetObject(sub.SubassemblyId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Subassembly)?.Name; }
    catch (System.Exception) { subName = null; }

    var points = new List<object>();
    foreach (CalculatedPoint point in sub.Points)
    {
        var codes = Codes(point.CorridorCodes);
        Count(codes);
        if (!Wanted(codes) || !Room()) continue;
        var soe = point.StationOffsetElevationToBaseline;
        points.Add(new { codes, offset = Math.Round(soe.Y, 4), elevation = Math.Round(soe.Z, 4), x = Math.Round(point.XYZ.X, 4), y = Math.Round(point.XYZ.Y, 4) });
    }

    var links = new List<object>();
    foreach (CalculatedLink link in sub.Links)
    {
        var codes = Codes(link.CorridorCodes);
        Count(codes);
        if (!Wanted(codes)) continue;
        var pts = new List<Point3d>();
        foreach (CalculatedPoint p in link.CalculatedPoints) pts.Add(p.StationOffsetElevationToBaseline);
        if (pts.Count < 2 || !Room()) continue;
        double length = 0;
        for (var i = 1; i < pts.Count; i++)
            length += Math.Sqrt(Math.Pow(pts[i].Y - pts[i - 1].Y, 2) + Math.Pow(pts[i].Z - pts[i - 1].Z, 2));
        // Slope from the end nearer the baseline to the end farther out: negative falls outward.
        var firstIsInner = Math.Abs(pts[0].Y) <= Math.Abs(pts[pts.Count - 1].Y);
        var inner = firstIsInner ? pts[0] : pts[pts.Count - 1];
        var outer = firstIsInner ? pts[pts.Count - 1] : pts[0];
        var run = Math.Abs(outer.Y - inner.Y);
        links.Add(new
        {
            codes,
            startOffset = Math.Round(pts[0].Y, 4),
            startElevation = Math.Round(pts[0].Z, 4),
            endOffset = Math.Round(pts[pts.Count - 1].Y, 4),
            endElevation = Math.Round(pts[pts.Count - 1].Z, 4),
            pointCount = pts.Count,
            length = Math.Round(length, 4),
            slopeOutwardPercent = run > 1e-9 ? Math.Round((outer.Z - inner.Z) / run * 100, 3) : (double?)null
        });
    }

    var shapes = new List<object>();
    foreach (CalculatedShape shape in sub.Shapes)
    {
        var codes = Codes(shape.CorridorCodes);
        Count(codes);
        if (!Wanted(codes) || !Room()) continue;
        shapes.Add(new { codes, area = Math.Round(shape.Area, 4) });
    }

    var origin = sub.OriginStationOffsetElevationToBaseline;
    subassemblies.Add(new
    {
        name = subName,
        handle = sub.SubassemblyId.IsNull ? null : sub.SubassemblyId.Handle.ToString(),
        originOffset = Math.Round(origin.Y, 4),
        originElevation = Math.Round(origin.Z, 4),
        points,
        links,
        shapes
    });
}

return new
{
    success = true,
    corridor = corridor.Name,
    corridorOutOfDate = corridor.IsOutOfDate,
    baseline = baseline.Name,
    requestedStation = station,
    station = Math.Round(nearest, 4),
    snapped = Math.Abs(nearest - station) > 1e-9,
    region = regionName,
    assembly = regionAssembly,
    adjustedElevation = Math.Round(applied.AdjustedElevation, 4),
    codeFilter,
    itemsListed = listed,
    itemLimitReached = cut,
    codesAtStation = codeCounts.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value),
    subassemblies
};
```

## Usage Notes

- Read-only: it reads the corridor as last built and changes nothing. With `corridorOutOfDate` true the section is the old build; ask the user to rebuild first.
- The section is read at a built corridor station. A station within `stationSnapTolerance` of one is read there (`snapped: true`); otherwise the reply names the built stations before and after. `add_sample_line_stations_to_corridor` adds a station to the regions.
- Offsets and elevations are relative to the baseline: negative offset is left. `x` and `y` of a point are drawing coordinates. A link's `slopeOutwardPercent` runs from its end nearer the baseline to the end farther out, so a negative value falls away from the baseline; a vertical link has none.
- `codesAtStation` counts every code at the station, also those left out by `codeFilter`; run once without a filter to learn the drawing's codes. Uncoded points and links are left out unless `includeUncoded` is true.
- The parameter values applied at the station (`AppliedSubassembly.Parameters`) are deliberately not read: reading them where a parameter is overridden for a station range has crashed Civil 3D in field use. The geometry here is what those values produced.
- `corridor_section_check` checks many stations for repeated codes, overlaps, missing shapes and short links; this recipe shows one station in full.
