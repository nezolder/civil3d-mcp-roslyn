---
name: list_surfaces
category: surfaces
description: List all surfaces in the active Civil 3D drawing with name, type, layer, and basic statistics
requires_write: false
parameters: []
---

## Code Template

```csharp
var surfaces = new List<object>();

foreach (ObjectId id in CivilDoc.GetSurfaceIds())
{
    var surface = Transaction.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
    if (surface == null) continue;

    var info = new Dictionary<string, object>
    {
        ["name"] = surface.Name,
        ["handle"] = surface.Handle.ToString(),
        ["layer"] = surface.Layer,
        ["style"] = surface.StyleName,
        ["type"] = surface is TinVolumeSurface ? "TINVolume" : surface is TinSurface ? "TIN" : surface is GridSurface ? "Grid" : "Other"
    };

    if (surface is TinSurface tin)
    {
        var props = tin.GetGeneralProperties();
        info["minElevation"] = props.MinimumElevation;
        info["maxElevation"] = props.MaximumElevation;
        info["numberOfPoints"] = props.NumberOfPoints;
    }

    surfaces.Add(info);
}

return new { count = surfaces.Count, surfaces };
```

## Usage Notes
- Returns all surfaces in the drawing (TIN, Grid, TINVolume)
- TIN surfaces include elevation statistics; Grid and TIN volume surfaces are listed without them
- Use the `name` field to reference surfaces in other operations
