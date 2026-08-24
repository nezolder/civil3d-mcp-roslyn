---
name: drawing_info
category: drawing
description: Get comprehensive information about the active Civil 3D drawing
requires_write: false
parameters: []
---

## Code Template

```csharp
var settings = CivilDoc.Settings.DrawingSettings;

// Count objects
var surfaceCount = CivilDoc.GetSurfaceIds().Count;
var alignmentCount = CivilDoc.GetAlignmentIds().Count;
var corridorCount = CivilDoc.CorridorCollection.Count;
var siteCount = CivilDoc.GetSiteIds().Count;
var pipeNetworkCount = CivilDoc.GetPipeNetworkIds().Count;
var pointCount = CivilDoc.CogoPoints.Count;

return new {
    drawing = new {
        name = Document.Name,
        path = Database.Filename,
        coordinateSystem = settings.UnitZoneSettings.CoordinateSystemCode,
        units = settings.UnitZoneSettings.DrawingUnits.ToString(),
        angularUnits = settings.UnitZoneSettings.AngularUnits.ToString()
    },
    objectCounts = new {
        surfaces = surfaceCount,
        alignments = alignmentCount,
        corridors = corridorCount,
        sites = siteCount,
        pipeNetworks = pipeNetworkCount,
        cogoPoints = pointCount
    }
};
```

## Usage Notes
- This is a good first query to understand the drawing contents
- Coordinate system code follows EPSG or Autodesk format
- Units can be Meter, Foot, etc.
