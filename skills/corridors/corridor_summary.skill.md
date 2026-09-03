---
name: corridor_summary
category: corridors
description: Summarize corridors, baselines, regions, surfaces, targets, and rebuild state with bounded details
requires_write: false
parameters:
  - name: corridorName
    type: string
    required: false
    description: Optional exact corridor name filter
  - name: limit
    type: int
    required: false
    description: Maximum corridors to return (default 20)
  - name: baselineLimit
    type: int
    required: false
    description: Maximum baseline summaries per corridor (default 20)
---

## Code Template

```csharp
var corridorNameFilter = "";
var limit = 20;
var baselineLimit = 20;
if (limit < 1 || baselineLimit < 1) return new { error = "limits must be at least 1" };

var corridors = new List<object>();
var total = 0;

foreach (ObjectId corridorId in CivilDoc.CorridorCollection)
{
    var corridor = Transaction.GetObject(corridorId, OpenMode.ForRead) as Corridor;
    if (corridor == null) continue;
    if (!string.IsNullOrWhiteSpace(corridorNameFilter)
        && !corridor.Name.Equals(corridorNameFilter, StringComparison.OrdinalIgnoreCase))
        continue;

    total++;
    if (corridors.Count >= limit) continue;

    var baselines = new List<object>();
    var totalRegions = 0;

    foreach (Baseline baseline in corridor.Baselines)
    {
        totalRegions += baseline.BaselineRegions.Count;
        if (baselines.Count >= baselineLimit) continue;

        string alignmentName = null;
        string profileName = null;
        if (!baseline.AlignmentId.IsNull)
        {
            var alignment = Transaction.GetObject(baseline.AlignmentId, OpenMode.ForRead) as Alignment;
            alignmentName = alignment?.Name;
        }
        if (!baseline.ProfileId.IsNull)
        {
            var profile = Transaction.GetObject(baseline.ProfileId, OpenMode.ForRead) as Profile;
            profileName = profile?.Name;
        }

        double? startStation = null;
        double? endStation = null;
        foreach (BaselineRegion region in baseline.BaselineRegions)
        {
            if (!startStation.HasValue || region.StartStation < startStation.Value)
                startStation = region.StartStation;
            if (!endStation.HasValue || region.EndStation > endStation.Value)
                endStation = region.EndStation;
        }

        baselines.Add(new {
            name = baseline.Name,
            type = baseline.BaselineType.ToString(),
            alignmentName,
            profileName,
            startStation,
            endStation,
            regionCount = baseline.BaselineRegions.Count
        });
    }

    var surfaceNames = corridor.CorridorSurfaces.SurfaceNames()
        .Cast<string>()
        .Take(baselineLimit)
        .ToList();

    corridors.Add(new {
        name = corridor.Name,
        handle = corridor.Handle.ToString(),
        layer = corridor.Layer,
        style = corridor.StyleName,
        codeSetStyle = corridor.CodeSetStyleName,
        isOutOfDate = corridor.IsOutOfDate,
        isReference = corridor.IsReferenceObject,
        isReferenceStale = corridor.IsReferenceStale,
        baselineTotal = corridor.Baselines.Count,
        baselineReturned = baselines.Count,
        baselineTruncated = corridor.Baselines.Count > baselines.Count,
        totalRegions,
        targetGroupCount = corridor.GetTargets().Count,
        surfaceCount = corridor.CorridorSurfaces.Count,
        surfaceNamesReturned = surfaceNames.Count,
        surfaceNamesTruncated = corridor.CorridorSurfaces.Count > surfaceNames.Count,
        surfaceNames,
        baselines
    });
}

return new {
    total,
    returned = corridors.Count,
    truncated = total > corridors.Count,
    limit,
    baselineLimit,
    corridorNameFilter = string.IsNullOrWhiteSpace(corridorNameFilter) ? null : corridorNameFilter,
    corridors
};
```

## Usage Notes
- `isOutOfDate` is a warning that the corridor needs attention; this query does not rebuild it
- Region details and surface names are sampled to keep the response compact
- Target count is an inventory value, not proof that every target assignment is valid
