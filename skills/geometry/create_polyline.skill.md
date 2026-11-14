---
name: create_polyline
category: geometry
description: Create a 2D or 3D polyline from a list of vertices
requires_write: true
aliases: ["2D vagy 3D polivonal készítése csúcspontokból", "create polyline from vertices with optional layer", "polivonal létrehozás megadott koordinátákkal"]
workflow_tags: ["modeling"]
tested_civil_version: "2025"
validation_summary: "Offline: 2025 API compilation. Recipe-specific live coverage is not recorded here; verify the required case before use."
parameters:
  - name: vertices
    type: array
    required: true
    description: 2 to 1000 ordered (x, y, z) rows in drawing units; z is the elevation, 0 for a flat polyline at zero
  - name: polyline3d
    type: bool
    required: false
    description: false creates a flat polyline, whose vertices must share one z; true creates a 3D polyline with its own z at every vertex (default false)
  - name: closed
    type: bool
    required: false
    description: Whether to close the polyline; needs at least 3 vertices (default false)
  - name: layer
    type: string
    required: false
    description: Existing layer name; empty keeps the current layer (default "")
---

## Code Template

```csharp
var vertices = new (double x, double y, double z)[] { };
var polyline3d = false;
var closed = false;
var layer = "";

if (vertices == null || vertices.Length < 2 || vertices.Length > 1000)
    return new { success = false, error = "vertices must hold 2 to 1000 rows." };
if (closed && vertices.Length < 3)
    return new { success = false, error = "A closed polyline needs at least 3 vertices." };
for (var index = 0; index < vertices.Length; index++)
{
    var vertex = vertices[index];
    if (double.IsNaN(vertex.x) || double.IsInfinity(vertex.x)
        || double.IsNaN(vertex.y) || double.IsInfinity(vertex.y)
        || double.IsNaN(vertex.z) || double.IsInfinity(vertex.z))
        return new { success = false, error = $"vertices[{index}] must have finite x, y and z." };
    if (!polyline3d && Math.Abs(vertex.z - vertices[0].z) > 1e-9)
        return new { success = false, error = "A flat polyline needs the same z at every vertex; set polyline3d to true for varying elevations." };
}
if (layer == null || layer != layer.Trim() || layer.Length > 255)
    return new { success = false, error = "layer must be a layer name without leading or trailing spaces, or empty." };
if (layer.Length > 0)
{
    var layerTable = (LayerTable)Transaction.GetObject(Database.LayerTableId, OpenMode.ForRead);
    if (!layerTable.Has(layer))
        return new { success = false, error = "Layer not found; this recipe does not create layers." };
}

var blockTable = (BlockTable)Transaction.GetObject(Database.BlockTableId, OpenMode.ForRead);
var modelSpace = (BlockTableRecord)Transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

Autodesk.AutoCAD.DatabaseServices.Entity created;
if (polyline3d)
{
    var points = new Point3dCollection();
    foreach (var vertex in vertices) points.Add(new Point3d(vertex.x, vertex.y, vertex.z));
    created = new Polyline3d(Poly3dType.SimplePoly, points, closed);
}
else
{
    var polyline = new Polyline();
    for (var index = 0; index < vertices.Length; index++)
        polyline.AddVertexAt(index, new Point2d(vertices[index].x, vertices[index].y), 0, 0, 0);
    polyline.Elevation = vertices[0].z;
    polyline.Closed = closed;
    created = polyline;
}

modelSpace.AppendEntity(created);
Transaction.AddNewlyCreatedDBObject(created, true);
if (layer.Length > 0) created.Layer = layer;

return new {
    success = true,
    handle = created.Handle.ToString(),
    type = polyline3d ? "Polyline3d" : "Polyline",
    vertexCount = vertices.Length,
    closed,
    layer = created.Layer
};
```

## Usage Notes
- The polyline is added to model space; coordinates are in the drawing's coordinate system
- A flat polyline lies at the z of its vertices; give 0 for a polyline at elevation zero
- The layer must exist; the recipe does not create layers
- Segments are straight; arcs (bulges) are not part of this recipe
