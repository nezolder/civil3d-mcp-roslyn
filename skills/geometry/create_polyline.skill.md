---
name: create_polyline
category: geometry
description: Create a 2D or 3D polyline from a list of vertices
requires_write: true
parameters:
  - name: vertices
    type: array
    required: true
    description: Array of {x, y} or {x, y, z} coordinates
  - name: closed
    type: boolean
    required: false
    description: Whether to close the polyline
  - name: layer
    type: string
    required: false
    description: Layer name
---

## Code Template — 2D Polyline

```csharp
var polyline = new Polyline();
polyline.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
polyline.AddVertexAt(1, new Point2d(100, 0), 0, 0, 0);
polyline.AddVertexAt(2, new Point2d(100, 100), 0, 0, 0);
polyline.Closed = false;  // Set true to close
// polyline.Layer = "MyLayer";

var bt = (BlockTable)Transaction.GetObject(Database.BlockTableId, OpenMode.ForRead);
var btr = (BlockTableRecord)Transaction.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
btr.AppendEntity(polyline);
Transaction.AddNewlyCreatedDBObject(polyline, true);

return new { success = true, handle = polyline.Handle.ToString(), vertexCount = polyline.NumberOfVertices };
```

## Code Template — 3D Polyline

```csharp
var pts = new Point3dCollection();
pts.Add(new Point3d(0, 0, 10));
pts.Add(new Point3d(100, 0, 15));
pts.Add(new Point3d(100, 100, 20));

var polyline3d = new Polyline3d(Poly3dType.SimplePoly, pts, false);

var bt = (BlockTable)Transaction.GetObject(Database.BlockTableId, OpenMode.ForRead);
var btr = (BlockTableRecord)Transaction.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
btr.AppendEntity(polyline3d);
Transaction.AddNewlyCreatedDBObject(polyline3d, true);

return new { success = true, handle = polyline3d.Handle.ToString() };
```
