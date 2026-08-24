---
name: create_cogo_points
category: points
description: Create COGO points from coordinates with optional descriptions
requires_write: true
parameters:
  - name: points
    type: array
    required: true
    description: Array of {easting, northing, elevation, description}
---

## Code Template

```csharp
var created = new List<object>();

// Add points
var pt1 = CivilDoc.CogoPoints.Add(new Point3d(1000.0, 2000.0, 100.0), false);
var point1 = Transaction.GetObject(pt1, OpenMode.ForWrite) as CogoPoint;
point1.RawDescription = "TOPO";
created.Add(new { pointNumber = point1.PointNumber, easting = point1.Easting, northing = point1.Northing });

// Repeat for more points...

return new { success = true, created };
```

## Usage Notes
- Set second parameter of Add() to false to auto-assign point numbers
- RawDescription triggers description key matching
- Points are added to the default _All Points group automatically
