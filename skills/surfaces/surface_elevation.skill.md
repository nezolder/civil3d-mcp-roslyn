---
name: surface_elevation
category: surfaces
description: Get the elevation of a surface at a specific X,Y coordinate
requires_write: false
parameters:
  - name: surfaceName
    type: string
    required: true
  - name: x
    type: double
    required: true
  - name: y
    type: double
    required: true
---

## Code Template

```csharp
// Find the surface by name
Autodesk.Civil.DatabaseServices.Surface targetSurface = null;
foreach (ObjectId id in CivilDoc.GetSurfaceIds())
{
    var s = Transaction.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
    if (s != null && s.Name.Equals("SURFACE_NAME", StringComparison.OrdinalIgnoreCase))
    {
        targetSurface = s;
        break;
    }
}

if (targetSurface == null)
    return new { error = "Surface not found" };

double x = 1000.0;  // Replace with actual X
double y = 2000.0;  // Replace with actual Y
double elevation = targetSurface.FindElevationAtXY(x, y);

return new {
    surfaceName = targetSurface.Name,
    x, y, elevation
};
```

## Usage Notes
- Throws exception if the point is outside the surface boundary
- Coordinates must be in the drawing's coordinate system
- Works with both TIN and Grid surfaces
