---
name: create_tin_volume_surface
category: surfaces
description: Create a persistent TIN volume surface between an existing base and comparison surface with cut and fill factors
requires_write: true
aliases: ["térfogatfelület létrehozása két felület között", "create TIN volume surface between existing ground and design", "földmunka mennyiség térfogatfelülettel", "bevágás feltöltés térfogatfelület"]
workflow_tags: ["modeling", "quantity_read"]
tested_civil_version: "2025"
validation_summary: "Live 2025: created by name with factors 1.2/0.9 and saved; surface, sources, factors and volumes persisted after document reopen. After a source edit it reported out of date and returned new volumes after rebuild."
parameters:
  - name: baseSurfaceHandle
    type: string
    required: true
    description: Hex handle of the base surface (usually existing ground)
  - name: comparisonSurfaceHandle
    type: string
    required: true
    description: Hex handle of the comparison surface (usually design or corridor datum)
  - name: volumeSurfaceName
    type: string
    required: true
    description: New unique surface name
  - name: cutFactor
    type: double
    required: false
    description: Cut (swell) factor, finite and greater than 0, at most 10 (default 1.0)
  - name: fillFactor
    type: double
    required: false
    description: Fill (compaction) factor, finite and greater than 0, at most 10 (default 1.0)
  - name: styleName
    type: string
    required: false
    description: Existing surface style name; empty keeps the Civil 3D default (default "")
---

## Code Template

```csharp
var baseSurfaceHandle = "BASE_SURFACE_HANDLE";
var comparisonSurfaceHandle = "COMPARISON_SURFACE_HANDLE";
var volumeSurfaceName = "NEW_VOLUME_SURFACE_NAME";
var cutFactor = 1.0;
var fillFactor = 1.0;
var styleName = "";

var isHandle = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value)
    && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{1,16}$"));
if (!isHandle(baseSurfaceHandle) || !isHandle(comparisonSurfaceHandle)
    || baseSurfaceHandle.Equals(comparisonSurfaceHandle, StringComparison.OrdinalIgnoreCase))
{
    return new { success = false, error = "Supply two distinct hexadecimal surface handles." };
}

if (string.IsNullOrWhiteSpace(volumeSurfaceName)
    || volumeSurfaceName != volumeSurfaceName.Trim()
    || volumeSurfaceName.Length > 255
    || volumeSurfaceName.Any(char.IsControl))
{
    return new { success = false, error = "volumeSurfaceName must be a trimmed non-empty name of at most 255 characters without control characters." };
}

if (!double.IsFinite(cutFactor) || !double.IsFinite(fillFactor)
    || cutFactor <= 0 || fillFactor <= 0 || cutFactor > 10 || fillFactor > 10)
{
    return new { success = false, error = "cutFactor and fillFactor must be finite, greater than 0 and at most 10." };
}

foreach (ObjectId existingId in CivilDoc.GetSurfaceIds())
{
    var existing = Transaction.GetObject(existingId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
    if (existing != null && existing.Name.Equals(volumeSurfaceName, StringComparison.OrdinalIgnoreCase))
    {
        return new { success = false, error = "A surface with this name already exists.", volumeSurfaceName };
    }
}

Autodesk.Civil.DatabaseServices.Surface ResolveSurface(string handle)
{
    try
    {
        var id = Database.GetObjectId(false, new Handle(System.Convert.ToInt64(handle, 16)), 0);
        var surface = Transaction.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
        return surface == null || surface.IsErased || surface is TinVolumeSurface || surface is GridVolumeSurface ? null : surface;
    }
    catch
    {
        return null;
    }
}

var baseSurface = ResolveSurface(baseSurfaceHandle);
var comparisonSurface = ResolveSurface(comparisonSurfaceHandle);
if (baseSurface == null || comparisonSurface == null)
{
    return new {
        success = false,
        error = "Both handles must identify existing non-volume Civil 3D surfaces in this drawing.",
        baseFound = baseSurface != null,
        comparisonFound = comparisonSurface != null
    };
}

ObjectId styleId = ObjectId.Null;
if (styleName.Length > 0)
{
    try
    {
        styleId = CivilDoc.Styles.SurfaceStyles[styleName];
    }
    catch
    {
        styleId = ObjectId.Null;
    }
    if (styleId.IsNull)
    {
        return new { success = false, error = "The requested surface style does not exist.", styleName };
    }
}

var volumeSurfaceId = styleId.IsNull
    ? TinVolumeSurface.Create(volumeSurfaceName, baseSurface.ObjectId, comparisonSurface.ObjectId)
    : TinVolumeSurface.Create(volumeSurfaceName, baseSurface.ObjectId, comparisonSurface.ObjectId, styleId);
if (volumeSurfaceId.IsNull)
{
    throw new InvalidOperationException("TIN volume surface creation returned an invalid object ID.");
}

var volumeSurface = (TinVolumeSurface)Transaction.GetObject(volumeSurfaceId, OpenMode.ForWrite);
volumeSurface.CutFactor = cutFactor;
volumeSurface.FillFactor = fillFactor;

var volumes = volumeSurface.GetVolumeProperties();
return new {
    success = true,
    name = volumeSurface.Name,
    handle = volumeSurface.Handle.ToString(),
    style = volumeSurface.StyleName,
    baseSurface = new { name = baseSurface.Name, handle = baseSurface.Handle.ToString() },
    comparisonSurface = new { name = comparisonSurface.Name, handle = comparisonSurface.Handle.ToString() },
    cutFactor = volumeSurface.CutFactor,
    fillFactor = volumeSurface.FillFactor,
    unadjustedCut = volumes.UnadjustedCutVolume,
    unadjustedFill = volumes.UnadjustedFillVolume,
    unadjustedNet = volumes.UnadjustedNetVolume,
    adjustedCut = volumes.AdjustedCutVolume,
    adjustedFill = volumes.AdjustedFillVolume,
    adjustedNet = volumes.AdjustedNetVolume
};
```

## Usage Notes

- This is a write-capable template: confirm the full drawing identity first and run it with `civil3d_execute`; use `saveDrawing: true` only for an approved save.
- The volume surface stays in the drawing. It references its base and comparison surfaces, so after the road model or ground changes it can be rebuilt and read again with `bounded_volumes` or `surface_volume` instead of being recreated. Until it is rebuilt, `isOutOfDate` is true and the old volumes are returned; with automatic rebuild off, rebuild it in Civil 3D first.
- Base is subtracted from comparison: where the comparison surface is lower than the base, the volume is cut; where it is higher, fill. Choose the order deliberately (for example existing ground as base, corridor datum as comparison).
- Volumes are in drawing units cubed (m³ in a metric drawing). Adjusted values apply the cut and fill factors; unadjusted values do not.
- Source surfaces may be data-shortcut references; they are not modified.
