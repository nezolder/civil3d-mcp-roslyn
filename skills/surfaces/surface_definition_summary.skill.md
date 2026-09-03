---
name: surface_definition_summary
category: surfaces
description: Summarize TIN surface source-definition counts and rebuild state without returning source file paths
requires_write: false
parameters:
  - name: surfaceName
    type: string
    required: false
    description: Optional exact surface name filter
  - name: limit
    type: int
    required: false
    description: Maximum surfaces to return (default 50)
---

## Code Template

```csharp
var surfaceNameFilter = "";
var limit = 50;
if (limit < 1) return new { error = "limit must be at least 1" };

var surfaces = new List<object>();
var total = 0;

foreach (ObjectId surfaceId in CivilDoc.GetSurfaceIds())
{
    var surface = Transaction.GetObject(surfaceId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
    if (surface == null) continue;
    if (!string.IsNullOrWhiteSpace(surfaceNameFilter)
        && !surface.Name.Equals(surfaceNameFilter, StringComparison.OrdinalIgnoreCase))
        continue;

    total++;
    if (surfaces.Count >= limit) continue;

    var info = new Dictionary<string, object>
    {
        ["name"] = surface.Name,
        ["handle"] = surface.Handle.ToString(),
        ["type"] = surface is TinVolumeSurface ? "TINVolume" : surface is TinSurface ? "TIN" : surface is GridSurface ? "Grid" : "Other",
        ["isOutOfDate"] = surface.IsOutOfDate,
        ["autoRebuild"] = surface.AutoRebuild,
        ["hasSnapshot"] = surface.HasSnapshot,
        ["isSnapshotOutOfDate"] = surface.IsSnapshotOutOfDate,
        ["isReference"] = surface.IsReferenceObject,
        ["isReferenceStale"] = surface.IsReferenceStale
    };

    if (surface is TinSurface tin)
    {
        var properties = tin.GetGeneralProperties();
        info["numberOfPoints"] = properties.NumberOfPoints;
        info["definition"] = new {
            pointFiles = tin.PointFilesDefinition.Count,
            pointGroups = tin.PointGroupsDefinition.Count,
            drawingObjects = tin.DrawingObjectsDefinition.Count,
            demFiles = tin.DEMFilesDefinition.Count,
            contours = tin.ContoursDefinition.Count,
            breaklines = tin.BreaklinesDefinition.Count,
            boundaries = tin.BoundariesDefinition.Count
        };
    }
    else
    {
        info["definition"] = new { available = false, reason = "Only TIN source definitions are included" };
    }

    surfaces.Add(info);
}

return new {
    total,
    returned = surfaces.Count,
    truncated = total > surfaces.Count,
    limit,
    surfaceNameFilter = string.IsNullOrWhiteSpace(surfaceNameFilter) ? null : surfaceNameFilter,
    surfaces
};
```

## Usage Notes
- Definition counts help identify how a TIN surface was assembled without exposing local source paths
- Counts do not prove that breaklines, boundaries, or source data are geometrically correct
- Use a separate, targeted QA workflow before proposing surface edits
