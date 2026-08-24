---
name: surface_volume
category: surfaces
description: Read cut/fill volumes from an existing TIN volume surface
requires_write: false
parameters:
  - name: surfaceName
    type: string
    required: true
    description: Name of an existing TIN volume surface
---

## Code Template

```csharp
TinVolumeSurface volumeSurface = null;

foreach (ObjectId id in CivilDoc.GetSurfaceIds())
{
    var candidate = Transaction.GetObject(id, OpenMode.ForRead) as TinVolumeSurface;
    if (candidate != null && candidate.Name.Equals("VOLUME_SURFACE_NAME", StringComparison.OrdinalIgnoreCase))
    {
        volumeSurface = candidate;
        break;
    }
}

if (volumeSurface == null) return new { error = "TIN volume surface not found" };

var volumeProps = volumeSurface.GetVolumeProperties();

return new {
    surfaceName = volumeSurface.Name,
    cutVolume = volumeProps.UnadjustedCutVolume,
    fillVolume = volumeProps.UnadjustedFillVolume,
    netVolume = volumeProps.UnadjustedNetVolume
};
```

## Usage Notes
- Run this template through `civil3d_query`; it only reads an existing `TinVolumeSurface`
- Create the volume surface beforehand in Civil 3D or in a separately approved write operation
- Do not create a temporary `TinVolumeSurface` inside `civil3d_query`: a live Civil 3D 2025 test rolled the object back but still changed `DBMOD` from 0 to 1
- A live Civil 3D 2025 read test kept both the surface count and `DBMOD` unchanged
- Net volume positive = more cut than fill
- Units depend on drawing settings (typically m³ or ft³)
