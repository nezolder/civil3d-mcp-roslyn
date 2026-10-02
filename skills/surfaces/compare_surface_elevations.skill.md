---
name: compare_surface_elevations
category: surfaces
description: Compare two existing TIN surfaces at explicit bounded XY control points with coverage and signed elevation statistics
requires_write: false
parameters:
  - name: surfaceAHandle
    type: string
    required: true
    description: Hex handle of the reference TIN surface
  - name: surfaceBHandle
    type: string
    required: true
    description: Hex handle of the comparison TIN surface
  - name: samplePoints
    type: array
    required: true
    description: 1 to 2000 unique finite XY points in the verified drawing coordinate system
  - name: tolerance
    type: double
    required: true
    description: Nonnegative absolute elevation tolerance in drawing units
  - name: detailLimit
    type: int
    required: false
    description: Maximum point details to return from 1 to 200 (default 50)
---

## Code Template

```csharp
var surfaceAHandle = "REFERENCE_SURFACE_HANDLE";
var surfaceBHandle = "COMPARISON_SURFACE_HANDLE";
var samplePoints = new (double x, double y)[] { };
var tolerance = double.NaN;
var detailLimit = 50;
var isHandle = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value)
    && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{1,16}$")
    && long.TryParse(value, System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0);
if (!isHandle(surfaceAHandle) || !isHandle(surfaceBHandle)
    || surfaceAHandle.Equals(surfaceBHandle, StringComparison.OrdinalIgnoreCase))
    return new { success = false, error = "Configure two distinct positive hexadecimal TIN surface handles." };
if (samplePoints == null || samplePoints.Length < 1 || samplePoints.Length > 2000
    || samplePoints.Any(p => !Double.IsFinite(p.x) || !Double.IsFinite(p.y))
    || samplePoints.Distinct().Count() != samplePoints.Length)
    return new { success = false, error = "Supply 1..2000 distinct finite XY sample points." };
if (!Double.IsFinite(tolerance) || tolerance < 0 || detailLimit < 1 || detailLimit > 200)
    return new { success = false, error = "Tolerance must be finite/nonnegative and detailLimit must be 1..200." };

// Pure statistics also support host-free arithmetic verification.
var summarize = new Func<double[], object>(values => {
    if (values.Length == 0) return null;
    if (values.Any(v => !Double.IsFinite(v))) throw new InvalidOperationException("Nonfinite differences cannot enter statistics.");
    var absolute = values.Select(Math.Abs).OrderBy(v => v).ToArray();
    var signed = values.OrderBy(v => v).ToArray();
    var scale = absolute[absolute.Length - 1];
    var normalized = values.Select(v => scale == 0 ? 0 : v / scale).ToArray();
    var normalizedMean = normalized.Average();
    var mean = normalizedMean * scale;
    var rmse = Math.Sqrt(normalized.Average(v => v * v)) * scale;
    var standardDeviation = Math.Sqrt(normalized.Average(v => (v - normalizedMean) * (v - normalizedMean))) * scale;
    var mae = (scale == 0 ? 0 : absolute.Average(v => v / scale)) * scale;
    var percentile = new Func<double[], double, double>((sorted, fraction) => {
        var position = (sorted.Length - 1) * fraction;
        var lo = (int)Math.Floor(position);
        var hi = (int)Math.Ceiling(position);
        var f = position - lo;
        return sorted[lo] * (1 - f) + sorted[hi] * f;
    });
    if (!Double.IsFinite(mean) || !Double.IsFinite(rmse) || !Double.IsFinite(standardDeviation) || !Double.IsFinite(mae))
        throw new InvalidOperationException("Statistics overflowed; no misleading finite result is returned.");
    var within = absolute.Count(v => v <= tolerance);
    return new {
        count = values.Length, mean, rmse, mae, standardDeviation,
        min = signed[0], max = signed[signed.Length - 1], median = percentile(signed, 0.5),
        p95Absolute = percentile(absolute, 0.95), withinTolerance = within,
        outsideTolerance = values.Length - within, withinTolerancePercent = 100.0 * within / values.Length
    };
});

// Host access starts here.
ObjectId aId;
ObjectId bId;
try
{
    aId = Database.GetObjectId(false, new Handle(Convert.ToInt64(surfaceAHandle, 16)), 0);
    bId = Database.GetObjectId(false, new Handle(Convert.ToInt64(surfaceBHandle, 16)), 0);
}
catch { return new { success = false, error = "A requested surface handle does not resolve in this drawing." }; }
if (aId == bId) return new { success = false, error = "The two handles resolve to the same surface." };
var surfaceA = Transaction.GetObject(aId, OpenMode.ForRead) as TinSurface;
var surfaceB = Transaction.GetObject(bId, OpenMode.ForRead) as TinSurface;
if (surfaceA == null || surfaceB == null || surfaceA.IsErased || surfaceB.IsErased
    || surfaceA is TinVolumeSurface || surfaceB is TinVolumeSurface)
    return new { success = false, error = "Two existing non-volume TIN surfaces are required." };
foreach (var surface in new[] { surfaceA, surfaceB })
    if (surface.IsReferenceObject && (!surface.IsReferenceValid || surface.IsReferenceStale || !surface.IsReferencedSourceExisting))
        return new { success = false, error = "A reference surface is invalid, stale or missing its source." };

var differences = new List<double>();
var rows = new List<object>();
var missingA = 0;
var missingB = 0;
var lookupErrors = 0;
var nonfiniteDifferences = 0;
var lookup = new Func<TinSurface, double, double, (bool available, double z, string status)>((surface, x, y) => {
    try
    {
        var z = surface.FindElevationAtXY(x, y);
        return Double.IsFinite(z) ? (true, z, "available") : (false, 0, "nonfinite_elevation");
    }
    catch (Autodesk.Civil.PointNotOnEntityException) { return (false, 0, "outside_surface"); }
    catch { lookupErrors++; return (false, 0, "lookup_error"); }
});
foreach (var p in samplePoints)
{
    var a = lookup(surfaceA, p.x, p.y);
    var b = lookup(surfaceB, p.x, p.y);
    if (!a.available) missingA++;
    if (!b.available) missingB++;
    double? difference = null;
    if (a.available && b.available)
    {
        var delta = b.z - a.z;
        if (Double.IsFinite(delta)) { difference = delta; differences.Add(delta); }
        else nonfiniteDifferences++;
    }
    if (rows.Count < detailLimit)
        rows.Add(new {
            x = p.x, y = p.y,
            elevationA = a.available ? (double?)a.z : null,
            elevationB = b.available ? (double?)b.z : null,
            statusA = a.status, statusB = b.status, difference
        });
}
return new {
    success = true, surfaceA = new { handle = surfaceA.Handle.ToString(), name = surfaceA.Name },
    surfaceB = new { handle = surfaceB.Handle.ToString(), name = surfaceB.Name },
    differenceDefinition = "B minus A", units = CivilDoc.Settings.DrawingSettings.UnitZoneSettings.DrawingUnits.ToString(),
    tolerance, total = samplePoints.Length, valid = differences.Count,
    missingA, missingB, lookupErrors, nonfiniteDifferences,
    coveragePercent = 100.0 * differences.Count / samplePoints.Length,
    returned = rows.Count, truncated = samplePoints.Length > rows.Count, limit = detailLimit,
    statistics = summarize(differences.ToArray()), samples = rows
};
```

## Usage Notes

- Supply explicit control points, or generate a finite grid outside Civil for a specifically agreed area. Verify drawing identity, XY coordinate system, drawing units and a common vertical datum first. This recipe does not guess boundaries, datum shifts or acceptable tolerances.
- All requested unique points contribute to coverage/statistics, even when the detail list is truncated. Points missing either surface are excluded from difference statistics and counted explicitly; missing Z never becomes zero. A zero-valid-point comparison has `statistics: null`, not a zero-error result.
- Differences are signed `B - A`; percentiles use linear interpolation at `(n-1)*p`; standard deviation uses the population denominator. Finite normalization avoids squaring overflow. Lookup exceptions other than out-of-surface are separately counted as errors and must be investigated before accepting the comparison.
- A sampled comparison does not prove complete mesh/geometry equivalence or survey accuracy. Statistics do not authorize adopting either surface's elevations. This query never changes a TIN, reference or boundary and never writes a CSV/file inside Civil.
- Independent implementation informed by the MIT [surface-research and coverage approach](https://github.com/xuantinhnbs-rgb/civil3d-mcp/blob/ca2f6a336e80e2745a217c77eed8f768378e751b/civil3d_mcp/research.py). The existing .NET/Roslyn path remains in use; no COM server or upstream code body is copied.
- **Proven in a scoped Civil 3D 2025 live test (2026-10-01):** two synthetic TINs differing by +1 at four valid points returned the expected signed statistics. One point outside both surfaces was counted separately, a two-row detail cap preserved all-point statistics, and `DBMOD` stayed 0. Full-mesh equivalence, project tolerances and survey accuracy remain **Unverified**.
