---
name: create_tin_surface
category: surfaces
description: Create a new TIN surface and optionally add 3D points to it
requires_write: true
aliases: ["TIN felület készítése megadott 3D pontokból", "create TIN surface from 3D points", "új terepfelület létrehozása koordinátákból"]
workflow_tags: ["modeling"]
tested_civil_version: "2025"
validation_summary: "Offline: 2025 API compilation. Recipe-specific live coverage is not recorded here; verify the required case before use."
parameters:
  - name: surfaceName
    type: string
    required: true
    description: Name for the new surface
  - name: points
    type: array
    required: false
    description: Up to 5000 (x, y, z) rows in drawing units to add as vertices (default none)
  - name: description
    type: string
    required: false
    description: Surface description; empty leaves it unset (default "")
---

## Code Template

```csharp
var surfaceName = "";
var points = new (double x, double y, double z)[] { };
var description = "";

if (string.IsNullOrWhiteSpace(surfaceName) || surfaceName != surfaceName.Trim() || surfaceName.Length > 255)
    return new { success = false, error = "surfaceName must be a name without leading or trailing spaces." };
if (points == null || points.Length > 5000)
    return new { success = false, error = "points must hold at most 5000 rows." };
for (var index = 0; index < points.Length; index++)
{
    var point = points[index];
    if (double.IsNaN(point.x) || double.IsInfinity(point.x)
        || double.IsNaN(point.y) || double.IsInfinity(point.y)
        || double.IsNaN(point.z) || double.IsInfinity(point.z))
        return new { success = false, error = $"points[{index}] must have finite x, y and z." };
}
foreach (ObjectId existingId in CivilDoc.GetSurfaceIds())
{
    var existing = Transaction.GetObject(existingId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
    if (existing != null && existing.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase))
        return new { success = false, error = "A surface with this name already exists." };
}

// Create the TIN surface
var surfaceId = TinSurface.Create(Database, surfaceName);
var surface = (TinSurface)Transaction.GetObject(surfaceId, OpenMode.ForWrite);
if (!string.IsNullOrWhiteSpace(description)) surface.Description = description;

if (points.Length > 0)
{
    var vertices = new Point3dCollection();
    foreach (var point in points) vertices.Add(new Point3d(point.x, point.y, point.z));
    surface.AddVertices(vertices);
}

return new {
    success = true,
    name = surface.Name,
    handle = surface.Handle.ToString(),
    style = surface.StyleName,
    pointCount = surface.GetGeneralProperties().NumberOfPoints
};
```

## Usage Notes
- Surface name must be unique in the drawing
- Points should be in the drawing's coordinate system
- Minimum 3 non-collinear points needed for a valid TIN surface
- The surface gets the drawing's default surface style; another style can be set afterwards via surface.StyleId
