---
name: bounded_volumes
category: surfaces
description: Read cut, fill and net volumes of an existing TIN volume surface inside labelled closed polyline boundaries, such as one per structure
requires_write: false
aliases: ["határolt térfogat építményenként", "bounded volumes inside closed polylines on a volume surface", "földmunka mennyiség zárt határvonalon belül", "térfogat kiosztása építményekre"]
workflow_tags: ["quantity_read"]
tested_civil_version: "2025"
validation_summary: "Live 2025: raw cut/fill matched the native Bounded Volumes dashboard and whole-surface unadjusted totals; adjusted = raw x factor. Arc boundary converged with 64 chords. Stale flag and rebuild readback checked. Polyline3d boundaries untested."
parameters:
  - name: volumeSurfaceHandle
    type: string
    required: true
    description: Hex handle of an existing TIN volume surface
  - name: boundaries
    type: array
    required: true
    description: 1 to 200 distinct closed boundaries as {handle, label}; handle of a closed Polyline or Polyline3d, label a free text such as the structure name
  - name: arcSegments
    type: int
    required: false
    description: Chords used per arc segment of a Polyline boundary, 4 to 256 (default 64)
---

## Code Template

```csharp
var volumeSurfaceHandle = "VOLUME_SURFACE_HANDLE";
var boundaries = new (string handle, string label)[] { };
var arcSegments = 64;

var isHandle = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value)
    && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{1,16}$"));
if (!isHandle(volumeSurfaceHandle))
{
    return new { success = false, error = "volumeSurfaceHandle must be a hexadecimal handle." };
}
if (boundaries == null || boundaries.Length < 1 || boundaries.Length > 200
    || boundaries.Any(b => !isHandle(b.handle) || b.label == null || b.label.Length > 255)
    || boundaries.Select(b => b.handle.ToUpperInvariant()).Distinct().Count() != boundaries.Length)
{
    return new { success = false, error = "Supply 1..200 boundaries with distinct hexadecimal handles and labels of at most 255 characters." };
}
if (arcSegments < 4 || arcSegments > 256)
{
    return new { success = false, error = "arcSegments must be 4..256." };
}

ObjectId? ResolveId(string handle)
{
    try { return Database.GetObjectId(false, new Handle(System.Convert.ToInt64(handle, 16)), 0); }
    catch { return null; }
}

var volumeSurfaceId = ResolveId(volumeSurfaceHandle);
var volumeSurface = volumeSurfaceId.HasValue
    ? Transaction.GetObject(volumeSurfaceId.Value, OpenMode.ForRead) as TinVolumeSurface
    : null;
if (volumeSurface == null || volumeSurface.IsErased)
{
    return new { success = false, error = "volumeSurfaceHandle must identify an existing TIN volume surface.", volumeSurfaceHandle };
}

// Plan polygon of a closed boundary, closed by repeating the first point.
// Polyline arc segments are replaced by arcSegments chords.
(List<Point3d> points, string error) Polygon(ObjectId id)
{
    var entity = Transaction.GetObject(id, OpenMode.ForRead);
    var points = new List<Point3d>();
    if (entity is Polyline polyline)
    {
        if (!polyline.Closed) return (null, "Polyline is not closed.");
        for (var i = 0; i < polyline.NumberOfVertices; i++)
        {
            var vertex = polyline.GetPoint2dAt(i);
            points.Add(new Point3d(vertex.X, vertex.Y, 0));
            if (Math.Abs(polyline.GetBulgeAt(i)) > 1e-12)
            {
                for (var k = 1; k < arcSegments; k++)
                {
                    var p = polyline.GetPointAtParameter(i + (double)k / arcSegments);
                    points.Add(new Point3d(p.X, p.Y, 0));
                }
            }
        }
    }
    else if (entity is Polyline3d polyline3d)
    {
        if (!polyline3d.Closed) return (null, "Polyline3d is not closed.");
        foreach (ObjectId vertexId in polyline3d)
        {
            var vertex = (PolylineVertex3d)Transaction.GetObject(vertexId, OpenMode.ForRead);
            points.Add(new Point3d(vertex.Position.X, vertex.Position.Y, 0));
        }
    }
    else
    {
        return (null, $"Unsupported boundary type {entity.GetType().Name}; use a closed Polyline or Polyline3d.");
    }

    // Drop a repeated closing vertex, then require a real area.
    if (points.Count > 1 && points[0].DistanceTo(points[points.Count - 1]) < 1e-9) points.RemoveAt(points.Count - 1);
    if (points.Count < 3) return (null, "Boundary needs at least 3 distinct vertices.");
    points.Add(points[0]);
    return (points, null);
}

// Shoelace area relative to the first vertex, so large drawing coordinates
// do not cancel out.
double BoundaryArea(List<Point3d> closed)
{
    var origin = closed[0];
    var twice = 0.0;
    for (var i = 1; i < closed.Count - 2; i++)
    {
        twice += (closed[i].X - origin.X) * (closed[i + 1].Y - origin.Y)
            - (closed[i + 1].X - origin.X) * (closed[i].Y - origin.Y);
    }
    return Math.Abs(twice) / 2;
}

var cutFactor = volumeSurface.CutFactor;
var fillFactor = volumeSurface.FillFactor;

var rows = new List<object>();
double totalCut = 0, totalFill = 0;
var measured = 0;
foreach (var boundary in boundaries)
{
    var id = ResolveId(boundary.handle);
    if (!id.HasValue)
    {
        rows.Add(new { boundary.label, boundary.handle, error = "Handle does not resolve to an object in this drawing." });
        continue;
    }
    var (polygon, polygonError) = Polygon(id.Value);
    if (polygonError != null)
    {
        rows.Add(new { boundary.label, boundary.handle, error = polygonError });
        continue;
    }

    try
    {
        var collection = new Point3dCollection(polygon.ToArray());
        var info = volumeSurface.GetBoundedVolumes(collection);
        rows.Add(new {
            boundary.label,
            boundary.handle,
            boundaryArea = BoundaryArea(polygon),
            vertexCount = polygon.Count - 1,
            cut = info.Cut,
            fill = info.Fill,
            net = info.Net,
            adjustedCut = info.Cut * cutFactor,
            adjustedFill = info.Fill * fillFactor,
            adjustedNet = info.Fill * fillFactor - info.Cut * cutFactor
        });
        totalCut += info.Cut;
        totalFill += info.Fill;
        measured++;
    }
    catch (System.Exception ex)
    {
        rows.Add(new { boundary.label, boundary.handle, error = $"{ex.GetType().Name}: {ex.Message}" });
    }
}

return new {
    success = measured > 0,
    volumeSurface = new {
        name = volumeSurface.Name,
        handle = volumeSurface.Handle.ToString(),
        isOutOfDate = volumeSurface.IsOutOfDate,
        cutFactor,
        fillFactor
    },
    measured,
    failed = boundaries.Length - measured,
    totals = new {
        cut = totalCut,
        fill = totalFill,
        net = totalFill - totalCut,
        adjustedCut = totalCut * cutFactor,
        adjustedFill = totalFill * fillFactor,
        adjustedNet = totalFill * fillFactor - totalCut * cutFactor
    },
    boundaries = rows
};
```

## Usage Notes

- Run this template through `civil3d_query`; it only reads the volume surface and the boundaries.
- Draw one closed polyline per structure or area, then pass each handle with a label, for example `[{"handle": "2F3", "label": "1. híd"}]`. A failing boundary is reported in its own row and does not stop the others.
- Results come from Civil 3D's bounded-volume calculation (`GetBoundedVolumes`) in drawing units cubed. `cut`, `fill` and `net` are raw quantities: in Civil 3D 2025 they matched the surface's unadjusted volumes and the native Bounded Volumes rows with factors 1.0. `adjustedCut`, `adjustedFill` and `adjustedNet` apply the volume surface's cut and fill factors. `net` is signed, fill minus cut.
- Native Bounded Volumes rows in the Volumes Dashboard have their own factors (1.0 by default) and do not inherit the volume surface's factors; set them to match before comparing adjusted values.
- Totals simply add the measured rows. Overlapping boundaries are counted twice; parts of a boundary outside the volume surface contribute nothing.
- `boundaryArea` is the plan area of the boundary polygon. Where the boundary extends beyond the volume surface it is larger than the area actually measured.
- If `isOutOfDate` is true the volume surface has not been rebuilt since its source surfaces changed. Rebuild it in Civil 3D, then read again.
- Arc segments of a lightweight `Polyline` are approximated with `arcSegments` chords; `Polyline3d` vertices are used as they are, in plan only. The native dashboard uses a mid-ordinate distance instead, so curved boundaries agree only approximately: with 64 chords the cut differed by less than 0.001% from the native result at mid-ordinate 0.001. Raise `arcSegments` for long, large-radius arcs.
