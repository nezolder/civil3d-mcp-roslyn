---
name: delete_tin_surface_points
category: surfaces
description: Delete chosen TIN surface points in one definition operation, by XY position or inside a boundary with an optional elevation filter; can list the matches first
requires_write: true
aliases: ["TIN felület hibás pontjainak törlése", "delete TIN surface points or vertices", "terepfelület pontok törlése egy definíciós művelettel", "felületi pont eltávolítása kiugró magasság szerint"]
workflow_tags: ["modeling", "source_state"]
tested_civil_version: "2025"
validation_summary: "Offline: 2025 API compilation. Live (Civil 3D 2025, throwaway copy, 2026-11-24): listOnly, boundary with elevation filters (2 of 25), one XY point, refusals (data reference, no match, maxPoints); saved and read back. Undo not tested."
parameters:
  - name: surfaceName
    type: string
    required: true
    description: Exact name of the TIN surface to edit
  - name: points
    type: array
    required: false
    description: Up to 1000 (x, y) rows; each must match exactly one surface point within tolerance (default none)
  - name: tolerance
    type: double
    required: false
    description: Search half-width around each (x, y) row in drawing units, 0.0001 to 1 (default 0.01)
  - name: boundary
    type: array
    required: false
    description: Closed boundary as at least 3 (x, y) rows; every surface point inside it is a candidate (default none). Not combined with points
  - name: deleteBelowElevation
    type: double
    required: false
    description: With boundary, keep only candidates lower than this elevation; NaN means no lower filter (default NaN)
  - name: deleteAboveElevation
    type: double
    required: false
    description: With boundary, keep only candidates higher than this elevation; NaN means no upper filter. With both filters a point outside either limit is deleted (default NaN)
  - name: maxPoints
    type: int
    required: false
    description: Refuse when more points match, 1 to 10000 (default 200)
  - name: listOnly
    type: bool
    required: false
    description: true lists the matching points and changes nothing (default false)
---

## Code Template

```csharp
var surfaceName = "";
var points = new (double x, double y)[] { };
var tolerance = 0.01;
var boundary = new (double x, double y)[] { };
var deleteBelowElevation = double.NaN;
var deleteAboveElevation = double.NaN;
var maxPoints = 200;
var listOnly = false;

bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

if (string.IsNullOrWhiteSpace(surfaceName))
    return new { success = false, error = "surfaceName is required." };
points ??= new (double x, double y)[] { };
boundary ??= new (double x, double y)[] { };
if ((points.Length > 0) == (boundary.Length > 0))
    return new { success = false, error = "Give either points or boundary, not both and not neither." };
if (points.Length > 1000)
    return new { success = false, error = "points must hold at most 1000 rows." };
if (!Finite(tolerance) || tolerance < 0.0001 || tolerance > 1.0)
    return new { success = false, error = "tolerance must be between 0.0001 and 1 drawing unit." };
if (maxPoints < 1 || maxPoints > 10000)
    return new { success = false, error = "maxPoints must be between 1 and 10000." };
if (boundary.Length > 0 && boundary.Length < 3)
    return new { success = false, error = "boundary needs at least 3 rows." };
if (boundary.Length == 0 && (!double.IsNaN(deleteBelowElevation) || !double.IsNaN(deleteAboveElevation)))
    return new { success = false, error = "The elevation filters work with boundary only." };
if (double.IsInfinity(deleteBelowElevation) || double.IsInfinity(deleteAboveElevation))
    return new { success = false, error = "Elevation filters must be finite numbers or NaN." };
for (var i = 0; i < points.Length; i++)
    if (!Finite(points[i].x) || !Finite(points[i].y))
        return new { success = false, error = $"points[{i}] must have finite x and y." };
for (var i = 0; i < boundary.Length; i++)
    if (!Finite(boundary[i].x) || !Finite(boundary[i].y))
        return new { success = false, error = $"boundary[{i}] must have finite x and y." };

TinSurface surface = null;
foreach (ObjectId id in CivilDoc.GetSurfaceIds())
{
    var candidate = Transaction.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
    if (candidate != null && candidate.Name.Equals(surfaceName, StringComparison.OrdinalIgnoreCase))
    {
        surface = candidate as TinSurface;
        if (surface == null)
            return new { success = false, error = $"'{candidate.Name}' is not a TIN surface ({candidate.GetType().Name})." };
        break;
    }
}
if (surface == null)
    return new { success = false, error = $"No surface named '{surfaceName}'." };
if (surface.IsReferenceObject)
    return new { success = false, error = "The surface is a data reference; edit it in its source drawing." };

Point3dCollection Square(double x, double y, double half)
{
    var square = new Point3dCollection();
    square.Add(new Point3d(x - half, y - half, 0));
    square.Add(new Point3d(x + half, y - half, 0));
    square.Add(new Point3d(x + half, y + half, 0));
    square.Add(new Point3d(x - half, y + half, 0));
    square.Add(new Point3d(x - half, y - half, 0));
    return square;
}

// Collect every vertex first, from one surface state, then delete them in one call.
var selected = new List<TinSurfaceVertex>();
var seen = new HashSet<string>();
string Key(Point3d p) => p.X.ToString("R") + "|" + p.Y.ToString("R");

if (points.Length > 0)
{
    for (var i = 0; i < points.Length; i++)
    {
        var found = surface.GetVerticesInsideBorder(Square(points[i].x, points[i].y, tolerance))
            .Where(v => Math.Abs(v.Location.X - points[i].x) <= tolerance
                     && Math.Abs(v.Location.Y - points[i].y) <= tolerance)
            .ToList();
        if (found.Count != 1)
            return new { success = false, error = $"points[{i}] matches {found.Count} surface points within {tolerance}; it must match exactly one. Nothing was changed.", row = i, matches = found.Select(v => new { x = v.Location.X, y = v.Location.Y, z = v.Location.Z }).Take(5).ToList() };
        if (seen.Add(Key(found[0].Location))) selected.Add(found[0]);
    }
}
else
{
    var border = new Point3dCollection();
    foreach (var row in boundary) border.Add(new Point3d(row.x, row.y, 0));
    if (boundary[0].x != boundary[boundary.Length - 1].x || boundary[0].y != boundary[boundary.Length - 1].y)
        border.Add(new Point3d(boundary[0].x, boundary[0].y, 0));
    var useBelow = !double.IsNaN(deleteBelowElevation);
    var useAbove = !double.IsNaN(deleteAboveElevation);
    foreach (var vertex in surface.GetVerticesInsideBorder(border))
    {
        var z = vertex.Location.Z;
        var keep = (!useBelow && !useAbove) || (useBelow && z < deleteBelowElevation) || (useAbove && z > deleteAboveElevation);
        if (keep && seen.Add(Key(vertex.Location))) selected.Add(vertex);
    }
}

var listed = selected.Select(v => new { x = v.Location.X, y = v.Location.Y, z = v.Location.Z }).Take(200).ToList();
if (selected.Count > maxPoints)
    return new { success = false, error = $"{selected.Count} points match, more than maxPoints ({maxPoints}). Nothing was changed.", matched = selected.Count, points = listed };
if (selected.Count == 0)
    return new { success = true, deleted = 0, matched = 0, note = "No surface point matched. Nothing was changed." };

var pointsBefore = surface.GetGeneralProperties().NumberOfPoints;
if (listOnly)
    return new { success = true, listOnly = true, surface = surface.Name, matched = selected.Count, pointsBefore, points = listed };

var operationsBefore = surface.Operations.Count;
surface.UpgradeOpen();
surface.DeleteVertices(selected);

// Postcondition: one new definition operation, and fewer points. Throwing rolls everything back.
var operationsAfter = surface.Operations.Count;
var lastOperation = operationsAfter > 0 ? surface.Operations[operationsAfter - 1] : null;
if (operationsAfter != operationsBefore + 1)
    throw new InvalidOperationException($"Expected one new surface operation, found {operationsAfter - operationsBefore}. Rolled back.");
var pointsAfter = surface.GetGeneralProperties().NumberOfPoints;
if (pointsAfter >= pointsBefore)
    throw new InvalidOperationException($"The point count did not drop ({pointsBefore} -> {pointsAfter}). Rolled back.");

return new {
    success = true,
    surface = surface.Name,
    handle = surface.Handle.ToString(),
    deleted = selected.Count,
    pointsBefore,
    pointsAfter,
    operation = lastOperation?.GetType().Name,
    operationIndex = operationsAfter - 1,
    points = listed
};
```

## Usage Notes
- Run with `listOnly: true` first and check the listed points; then run again without it. The write is one Undo step and one entry in the surface definition (`SurfaceOperationDeleteMultipleTinVertices`, also for a single point), so it can also be removed or switched off later in the definition.
- `points` mode refuses the whole call when any row matches no point or more than one point within `tolerance`; nothing is deleted then.
- `boundary` mode without an elevation filter deletes every point inside the boundary; `maxPoints` caps it.
- A deleted point comes back if the surface is rebuilt with the operation disabled or removed. Points that come from a later definition item (a point group or a file added after this operation) are not affected.
- Vertex objects read before this call are stale afterwards; look them up again.
- A data reference surface is refused: edit the source drawing.
