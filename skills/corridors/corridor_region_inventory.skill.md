---
name: corridor_region_inventory
category: corridors
description: List the regions of one or all corridors with station range, assembly, applied frequency settings, built and added stations, targets and offset baselines, and report gaps and overlaps between regions of a baseline
requires_write: false
aliases: ["korridor régiók szerelvény és gyakoriság listája", "corridor regions with assembly frequency and stations", "régiók közötti hézag és átfedés ellenőrzése", "corridor region gap and overlap check"]
workflow_tags: ["source_state", "audit"]
tested_civil_version: "2025"
validation_summary: "Offline: compiles against the Civil 3D 2025 API. Not yet run in Civil 3D."
parameters:
  - name: corridorHandle
    type: string
    required: false
    description: Hex handle of one corridor; empty lists every corridor (default "")
  - name: baselineNameFilter
    type: string
    required: false
    description: Exact baseline name; empty lists every baseline (default "")
  - name: gapTolerance
    type: double
    required: false
    description: A gap or overlap between neighbouring regions up to this length is ignored, in drawing units (default 0.001)
  - name: stationListLimit
    type: int
    required: false
    description: Maximum added stations listed per region, 0 to 200 (default 20)
  - name: limit
    type: int
    required: false
    description: Maximum regions listed over all corridors, 1 to 1000 (default 200)
---

## Code Template

```csharp
var corridorHandle = "";
var baselineNameFilter = "";
var gapTolerance = 0.001;
var stationListLimit = 20;
var limit = 200;

if (!double.IsFinite(gapTolerance) || gapTolerance < 0) return new { success = false, error = "gapTolerance must be finite and not negative." };
if (stationListLimit < 0 || stationListLimit > 200) return new { success = false, error = "stationListLimit must be between 0 and 200." };
if (limit < 1 || limit > 1000) return new { success = false, error = "limit must be between 1 and 1000." };

var corridorIds = new List<ObjectId>();
if (!string.IsNullOrWhiteSpace(corridorHandle))
{
    if (!long.TryParse(corridorHandle.Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0
        || !Database.TryGetObjectId(new Handle(value), out var id) || id.IsErased
        || !(Transaction.GetObject(id, OpenMode.ForRead) is Corridor))
        return new { success = false, error = "corridorHandle does not resolve to a corridor in this drawing." };
    corridorIds.Add(id);
}
else
{
    foreach (ObjectId id in CivilDoc.CorridorCollection) corridorIds.Add(id);
}

var names = new Dictionary<ObjectId, string>();
string NameOf(ObjectId id)
{
    if (id.IsNull) return null;
    if (names.TryGetValue(id, out var known)) return known;
    string name = null;
    try
    {
        var obj = Transaction.GetObject(id, OpenMode.ForRead);
        name = obj switch
        {
            Autodesk.Civil.DatabaseServices.Assembly a => a.Name,
            Alignment a => a.Name,
            Profile p => p.Name,
            Autodesk.Civil.DatabaseServices.Surface s => s.Name,
            FeatureLine f => f.Name,
            _ => obj.GetType().Name
        };
    }
    catch (System.Exception) { name = null; }
    names[id] = name;
    return name;
}

var rows = new List<object>();
var regionTotal = 0;
var findings = new List<object>();
var corridorsOut = new List<object>();
foreach (var corridorId in corridorIds)
{
    var corridor = Transaction.GetObject(corridorId, OpenMode.ForRead) as Corridor;
    if (corridor == null) continue;
    var baselinesOut = new List<object>();
    foreach (Baseline baseline in corridor.Baselines)
    {
        if (!string.IsNullOrWhiteSpace(baselineNameFilter)
            && !string.Equals(baseline.Name, baselineNameFilter.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

        var ranges = new List<(string name, double start, double end)>();
        foreach (BaselineRegion region in baseline.BaselineRegions)
        {
            regionTotal++;
            ranges.Add((region.Name, region.StartStation, region.EndStation));
            if (rows.Count >= limit) continue;

            var setting = region.AppliedAssemblySetting;
            var stations = region.SortedStations();
            double maxSpacing = 0;
            for (var i = 1; i < stations.Length; i++) maxSpacing = Math.Max(maxSpacing, stations[i] - stations[i - 1]);
            var added = setting.AdditionalAppliedAssemblies ?? new List<AdditionalAppliedAssemblyInfo>();

            var targets = new List<object>();
            var targetsWithoutObject = 0;
            var targetInfos = region.GetTargets();
            foreach (SubassemblyTargetInfo target in targetInfos)
            {
                var targetNames = new List<string>();
                foreach (ObjectId t in target.TargetIds) targetNames.Add(NameOf(t));
                if (targetNames.Count == 0) targetsWithoutObject++;
                if (targets.Count < 30)
                    targets.Add(new
                    {
                        subassembly = target.SubassemblyName,
                        target = target.DisplayName,
                        type = target.TargetType.ToString(),
                        objects = targetNames
                    });
            }

            var offsetBaselines = new List<string>();
            foreach (OffsetBaseline offset in region.OffsetBaselines) offsetBaselines.Add(offset.Name);

            rows.Add(new
            {
                corridor = corridor.Name,
                corridorHandle = corridor.Handle.ToString(),
                baseline = baseline.Name,
                region = region.Name,
                startStation = Math.Round(region.StartStation, 4),
                endStation = Math.Round(region.EndStation, 4),
                length = Math.Round(region.EndStation - region.StartStation, 4),
                assembly = NameOf(region.AssemblyId),
                isProcessed = region.IsProcessed,
                needsProcessing = region.NeedsProcessing,
                frequency = new
                {
                    tangents = setting.FrequencyAlongTangents,
                    curves = setting.FrequencyAlongCurves,
                    spirals = setting.FrequencyAlongSpirals,
                    profileCurves = setting.FrequencyAlongProfileCurves,
                    targetCurves = setting.FrequencyAlongTargetCurves,
                    curveOption = setting.CorridorAlongCurvesOption.ToString(),
                    midOrdinateAlongCurves = setting.MODAlongCurves,
                    atHorizontalGeometryPoints = setting.AppliedAtHorizontalGeometryPoints,
                    atSuperelevationCriticalPoints = setting.AppliedAtSuperelevationCriticalPoints,
                    atProfileGeometryPoints = setting.AppliedAtProfileGeometryPoints,
                    atProfileHighLowPoints = setting.AppliedAtProfileHighLowPoints,
                    atOffsetTargetGeometryPoints = setting.AppliedAtOffsetTargetGeometryPoints,
                    adjacentToOffsetTargetStartEnd = setting.AppliedAdjacentToOffsetTargetStartEnd
                },
                builtStationCount = stations.Length,
                largestStationSpacing = Math.Round(maxSpacing, 4),
                addedStationCount = added.Count,
                addedStations = added.Take(stationListLimit)
                    .Select(a => new { station = Math.Round(a.Station, 4), description = a.Description }).ToList(),
                targetCount = targetInfos.Count,
                targetsWithoutObject,
                targets,
                offsetBaselines
            });
        }

        // Neighbouring regions should meet: report a gap or an overlap between them.
        var ordered = ranges.OrderBy(r => r.start).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var difference = ordered[i].start - ordered[i - 1].end;
            if (Math.Abs(difference) <= gapTolerance) continue;
            if (findings.Count < 200)
                findings.Add(new
                {
                    finding = difference > 0 ? "GAP_BETWEEN_REGIONS" : "OVERLAPPING_REGIONS",
                    corridor = corridor.Name,
                    baseline = baseline.Name,
                    from = ordered[i - 1].name,
                    to = ordered[i].name,
                    fromEnd = Math.Round(ordered[i - 1].end, 4),
                    toStart = Math.Round(ordered[i].start, 4),
                    length = Math.Round(Math.Abs(difference), 4)
                });
        }

        baselinesOut.Add(new
        {
            corridor = corridor.Name,
            baseline = baseline.Name,
            alignment = NameOf(baseline.AlignmentId),
            profile = NameOf(baseline.ProfileId),
            startStation = ordered.Count > 0 ? Math.Round(ordered[0].start, 4) : (double?)null,
            endStation = ordered.Count > 0 ? Math.Round(ordered.Max(r => r.end), 4) : (double?)null,
            regionCount = ordered.Count
        });
    }
    corridorsOut.Add(new
    {
        name = corridor.Name,
        handle = corridor.Handle.ToString(),
        isOutOfDate = corridor.IsOutOfDate,
        baselines = baselinesOut
    });
}

return new
{
    success = true,
    corridors = corridorsOut,
    regionTotal,
    regionsListed = rows.Count,
    truncated = regionTotal > rows.Count,
    findingCount = findings.Count,
    findings,
    regions = rows
};
```

## Usage Notes

- Read-only. It reads the corridor as last built; with `isOutOfDate` or `needsProcessing` true the stations describe the old build. It does not rebuild.
- `frequency` holds the region's Frequency to Apply Assemblies settings: spacing along tangents, curves, spirals and profile curves (drawing units), the curve option and mid-ordinate distance, and whether stations are added at geometry, superelevation and profile points. `builtStationCount` and `largestStationSpacing` come from the stations the region was built at; a spacing far above the tangent frequency points to a region that was not rebuilt or a setting that differs from what was intended.
- `addedStations` are the stations added by hand or by a recipe such as `add_sample_line_stations_to_corridor`, with their descriptions.
- `targets` lists each target of the region with the objects it points to (first 30); `targetsWithoutObject` counts targets with no object set, which is often intended (an optional target) and is not reported as an error. `corridor_target_audit` goes deeper.
- `findings` reports neighbouring regions of a baseline that do not meet within `gapTolerance`: `GAP_BETWEEN_REGIONS` (no assembly applied between them) or `OVERLAPPING_REGIONS`. Whether a gap is intended, for example at a junction, is the designer's decision.
- Station overrides (`GetOverriddenStations`) and the parameter values applied at a station are not read: reading overridden applied parameters has crashed Civil 3D in field use.
