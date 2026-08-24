---
name: list_cogo_points
category: points
description: List COGO points in the drawing with a configurable result limit
requires_write: false
parameters:
  - name: limit
    type: int
    required: false
    description: Max points to return (default 500)
---

## Code Template

```csharp
var points = new List<object>();
var limit = 500;
var count = 0;

foreach (ObjectId id in CivilDoc.CogoPoints)
{
    if (count >= limit) break;
    var pt = Transaction.GetObject(id, OpenMode.ForRead) as CogoPoint;
    if (pt == null) continue;

    points.Add(new {
        pointNumber = pt.PointNumber,
        easting = pt.Easting,
        northing = pt.Northing,
        elevation = pt.Elevation,
        rawDescription = pt.RawDescription,
        fullDescription = pt.FullDescription
    });
    count++;
}

return new { count = points.Count, total = CivilDoc.CogoPoints.Count, points };
```

## Usage Notes
- Replace the `limit` value when adapting the template
- Point-group filtering is not included in this template
