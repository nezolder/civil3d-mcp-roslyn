---
name: surface_elevation
category: surfaces
description: Get the elevation of a surface at a specific X,Y coordinate
requires_write: false
aliases: ["terepmagasság lekérdezése XY koordinátán", "surface elevation at an XY point", "felületi magasság egy adott pontban"]
workflow_tags: ["geometry_query"]
tested_civil_version: "2025"
validation_summary: "Offline: 2025 API compilation. Recipe-specific live coverage is not recorded here; verify the required case before use."
parameters:
  - name: surfaceName
    type: string
    required: true
    description: Exact surface name
  - name: x
    type: double
    required: true
    description: X of the point in drawing coordinates
  - name: y
    type: double
    required: true
    description: Y of the point in drawing coordinates
---

## Code Template

```csharp
var surfaceName = "";
var x = double.NaN;
var y = double.NaN;

if (string.IsNullOrWhiteSpace(surfaceName)) return new { error = "surfaceName is required" };
if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
    return new { error = "x and y must be finite numbers" };

// Find the surface by name
Autodesk.Civil.DatabaseServices.Surface targetSurface = null;
foreach (ObjectId id in CivilDoc.GetSurfaceIds())
{
    var s = Transaction.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
    if (s != null && s.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase))
    {
        targetSurface = s;
        break;
    }
}

if (targetSurface == null)
    return new { error = "Surface not found" };

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
- For many points at once, or to compare two surfaces, use `compare_surface_elevations`
