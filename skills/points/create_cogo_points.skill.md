---
name: create_cogo_points
category: points
description: Create COGO points from coordinates with optional descriptions
requires_write: true
aliases: ["COGO pontok létrehozása koordinátákból", "create COGO points from coordinates and descriptions", "mérési pontok felvétele koordinátákkal"]
workflow_tags: ["modeling"]
tested_civil_version: "2025"
validation_summary: "Offline: 2025 API compilation. Recipe-specific live coverage is not recorded here; verify the required case before use."
parameters:
  - name: points
    type: array
    required: true
    description: 1 to 1000 (easting, northing, elevation, description) rows in drawing units; an empty description leaves the raw description unset
---

## Code Template

```csharp
var points = new (double easting, double northing, double elevation, string description)[] { };

if (points == null || points.Length < 1 || points.Length > 1000)
    return new { success = false, error = "points must hold 1 to 1000 rows." };
for (var index = 0; index < points.Length; index++)
{
    var row = points[index];
    if (double.IsNaN(row.easting) || double.IsInfinity(row.easting)
        || double.IsNaN(row.northing) || double.IsInfinity(row.northing)
        || double.IsNaN(row.elevation) || double.IsInfinity(row.elevation))
        return new { success = false, error = $"points[{index}] must have finite easting, northing and elevation." };
    if (row.description != null && row.description.Length > 255)
        return new { success = false, error = $"points[{index}].description must be at most 255 characters." };
}

const int detailLimit = 200;
var created = new List<object>();
var createdCount = 0;
foreach (var row in points)
{
    var pointId = CivilDoc.CogoPoints.Add(new Point3d(row.easting, row.northing, row.elevation), false);
    var point = (CogoPoint)Transaction.GetObject(pointId, OpenMode.ForWrite);
    if (!string.IsNullOrWhiteSpace(row.description)) point.RawDescription = row.description;
    createdCount++;
    if (created.Count < detailLimit)
    {
        created.Add(new {
            pointNumber = point.PointNumber,
            handle = point.Handle.ToString(),
            easting = point.Easting,
            northing = point.Northing,
            elevation = point.Elevation,
            rawDescription = point.RawDescription
        });
    }
}

return new {
    success = true,
    createdCount,
    createdReturned = created.Count,
    createdTruncated = createdCount > created.Count,
    created
};
```

## Usage Notes
- Point numbers are assigned by Civil 3D; read them from the result
- RawDescription triggers description key matching
- Points are added to the default _All Points group automatically
- The result lists the first 200 created points; `createdCount` is the number actually created
