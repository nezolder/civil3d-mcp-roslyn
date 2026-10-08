---
name: create_alignment_from_pis
category: alignments
description: Create a siteless alignment from ordered PI coordinates with an optional circular curve or clothoid-curve-clothoid group at each interior PI
requires_write: true
aliases: ["nyomvonal létrehozása töréspontokból ívsugárral és átmeneti ívvel", "create alignment from PI coordinates with radii and spirals", "tengely felvétele PI pontokból klotoiddal", "alignment by PIs tangent intersection layout"]
workflow_tags: ["modeling"]
tested_civil_version: "2025"
validation_summary: "Live 2025: five PIs with an arc, an asymmetric SCS and a corner at start station 1000; radii, spirals and station kept after a native PI grip edit, save and reopen. An oversized radius was rejected before writing."
parameters:
  - name: alignmentName
    type: string
    required: true
    description: New unique alignment name
  - name: alignmentStyleName
    type: string
    required: true
    description: Existing alignment style name
  - name: alignmentLabelSetName
    type: string
    required: true
    description: Existing alignment label-set style name
  - name: pis
    type: array
    required: true
    description: 2 to 200 ordered (x, y, radius, spiralIn, spiralOut) rows in drawing units; first and last row are the start and end point with zeros, an interior radius of 0 leaves an angle point without curve, and spiral lengths are both 0 (simple arc) or both greater than 0 (clothoid-arc-clothoid)
  - name: startStation
    type: double
    required: false
    description: Station of the start point (default 0)
  - name: layerName
    type: string
    required: false
    description: Existing layer for the alignment; empty uses the current layer (default "")
---

## Code Template

```csharp
var alignmentName = "NEW_ALIGNMENT_NAME";
var alignmentStyleName = "ALIGNMENT_STYLE_NAME";
var alignmentLabelSetName = "ALIGNMENT_LABEL_SET_NAME";
var pis = new (double x, double y, double radius, double spiralIn, double spiralOut)[] { };
var startStation = 0.0;
var layerName = "";
var tolerance = 1e-6;

var isName = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 255
    && !value.Any(char.IsControl));
if (!isName(alignmentName) || alignmentName == "NEW_ALIGNMENT_NAME"
    || !isName(alignmentStyleName) || alignmentStyleName == "ALIGNMENT_STYLE_NAME"
    || !isName(alignmentLabelSetName) || alignmentLabelSetName == "ALIGNMENT_LABEL_SET_NAME")
{
    return new { success = false, error = "Configure the new alignment name and existing style and label-set names." };
}
if (layerName != "" && !isName(layerName))
{
    return new { success = false, error = "layerName must be empty or a trimmed layer name without control characters." };
}
if (!double.IsFinite(startStation) || Math.Abs(startStation) > 1e9)
{
    return new { success = false, error = "startStation must be finite." };
}
if (pis == null || pis.Length < 2 || pis.Length > 200)
{
    return new { success = false, error = "pis must contain 2 to 200 rows." };
}

// Per-PI checks: finite values, curve parameters only at interior PIs.
for (var i = 0; i < pis.Length; i++)
{
    var pi = pis[i];
    if (!double.IsFinite(pi.x) || !double.IsFinite(pi.y) || !double.IsFinite(pi.radius)
        || !double.IsFinite(pi.spiralIn) || !double.IsFinite(pi.spiralOut))
        return new { success = false, error = $"PI {i}: all values must be finite." };
    if (pi.radius < 0 || pi.radius > 1e6 || pi.spiralIn < 0 || pi.spiralOut < 0 || pi.spiralIn > 1e4 || pi.spiralOut > 1e4)
        return new { success = false, error = $"PI {i}: radius must be 0..1e6 and spiral lengths 0..1e4." };
    var interior = i > 0 && i < pis.Length - 1;
    if (!interior && (pi.radius != 0 || pi.spiralIn != 0 || pi.spiralOut != 0))
        return new { success = false, error = $"PI {i}: the start and end point take radius and spiral lengths of 0." };
    if (pi.radius == 0 && (pi.spiralIn != 0 || pi.spiralOut != 0))
        return new { success = false, error = $"PI {i}: spirals need a radius greater than 0." };
    if ((pi.spiralIn > 0) != (pi.spiralOut > 0))
        return new { success = false, error = $"PI {i}: give both spiral lengths (clothoid-arc-clothoid) or neither (simple arc)." };
    if (i > 0 && Math.Sqrt(Math.Pow(pi.x - pis[i - 1].x, 2) + Math.Pow(pi.y - pis[i - 1].y, 2)) <= tolerance)
        return new { success = false, error = $"PI {i} coincides with PI {i - 1}." };
}

// Tangent lengths each curve takes from its two neighbouring segments.
// Clothoid shift p and offset k use the usual series approximations.
double SpiralShift(double length, double radius) =>
    length * length / (24 * radius) - Math.Pow(length, 4) / (2688 * Math.Pow(radius, 3));
double SpiralOffset(double length, double radius) =>
    length / 2 - Math.Pow(length, 3) / (240 * radius * radius);

var segmentLengths = new double[pis.Length - 1];
for (var i = 0; i < segmentLengths.Length; i++)
    segmentLengths[i] = Math.Sqrt(Math.Pow(pis[i + 1].x - pis[i].x, 2) + Math.Pow(pis[i + 1].y - pis[i].y, 2));
var tangentIn = new double[pis.Length];
var tangentOut = new double[pis.Length];
var deflections = new double[pis.Length];
var turnsLeft = new bool[pis.Length];
for (var i = 1; i < pis.Length - 1; i++)
{
    double ax = pis[i].x - pis[i - 1].x, ay = pis[i].y - pis[i - 1].y;
    double bx = pis[i + 1].x - pis[i].x, by = pis[i + 1].y - pis[i].y;
    var cross = ax * by - ay * bx;
    var delta = Math.Atan2(Math.Abs(cross), ax * bx + ay * by);
    deflections[i] = delta;
    turnsLeft[i] = cross > 0;
    var pi = pis[i];
    if (pi.radius == 0) continue;
    if (delta < 1e-6 || delta > Math.PI - 1e-6)
        return new { success = false, error = $"PI {i}: the deflection angle must be between 0 and 180 degrees to hold a curve.", deflectionDeg = delta * 180 / Math.PI };
    if (pi.spiralIn == 0)
    {
        tangentIn[i] = tangentOut[i] = pi.radius * Math.Tan(delta / 2);
        continue;
    }
    var spiralAngle = (pi.spiralIn + pi.spiralOut) / (2 * pi.radius);
    if (spiralAngle >= delta)
        return new { success = false, error = $"PI {i}: the spirals turn more than the deflection angle; shorten them or increase the radius.", deflectionDeg = delta * 180 / Math.PI, spiralDeg = spiralAngle * 180 / Math.PI };
    double p1 = SpiralShift(pi.spiralIn, pi.radius), p2 = SpiralShift(pi.spiralOut, pi.radius);
    tangentIn[i] = (pi.radius + p2) / Math.Sin(delta) - (pi.radius + p1) / Math.Tan(delta) + SpiralOffset(pi.spiralIn, pi.radius);
    tangentOut[i] = (pi.radius + p1) / Math.Sin(delta) - (pi.radius + p2) / Math.Tan(delta) + SpiralOffset(pi.spiralOut, pi.radius);
}
for (var i = 0; i < segmentLengths.Length; i++)
{
    var needed = tangentOut[i] + tangentIn[i + 1];
    if (needed > segmentLengths[i] + 1e-3)
        return new {
            success = false,
            error = $"Segment PI {i} - PI {i + 1} is too short for its curves; reduce the radius or spiral lengths.",
            segmentLength = segmentLengths[i],
            neededApprox = needed
        };
}

foreach (ObjectId existingId in CivilDoc.GetAlignmentIds())
{
    var existing = Transaction.GetObject(existingId, OpenMode.ForRead) as Alignment;
    if (existing != null && existing.Name.Equals(alignmentName, StringComparison.OrdinalIgnoreCase))
        return new { success = false, error = "An alignment with this name already exists.", alignmentName };
}

ObjectId styleId;
ObjectId labelSetId;
try
{
    styleId = CivilDoc.Styles.AlignmentStyles[alignmentStyleName];
    labelSetId = CivilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles[alignmentLabelSetName];
}
catch
{
    return new { success = false, error = "The named alignment style or label set does not exist.", alignmentStyleName, alignmentLabelSetName };
}
if (styleId.IsNull || labelSetId.IsNull)
{
    return new { success = false, error = "The named alignment style or label set is invalid.", alignmentStyleName, alignmentLabelSetName };
}

var layerId = Database.Clayer;
if (layerName != "")
{
    var layers = (LayerTable)Transaction.GetObject(Database.LayerTableId, OpenMode.ForRead);
    if (!layers.Has(layerName))
        return new { success = false, error = "The named layer does not exist.", layerName };
    layerId = layers[layerName];
}

var alignmentId = Alignment.Create(CivilDoc, alignmentName, ObjectId.Null, layerId, styleId, labelSetId);
if (alignmentId.IsNull)
{
    throw new InvalidOperationException("Alignment creation returned an invalid object ID.");
}
var alignment = (Alignment)Transaction.GetObject(alignmentId, OpenMode.ForWrite);

// Fixed tangents PI to PI, then a free curve or SCS group between each pair
// of tangents. Any failure throws, so nothing is kept.
var lineIds = new List<int>();
for (var i = 0; i < pis.Length - 1; i++)
{
    var line = alignment.Entities.AddFixedLine(new Point3d(pis[i].x, pis[i].y, 0), new Point3d(pis[i + 1].x, pis[i + 1].y, 0));
    lineIds.Add(line.EntityId);
}
var piRows = new List<object>();
for (var i = 1; i < pis.Length - 1; i++)
{
    var pi = pis[i];
    var kind = pi.radius == 0 ? "angle point" : pi.spiralIn == 0 ? "arc" : "spiral-arc-spiral";
    try
    {
        if (pi.radius > 0 && pi.spiralIn == 0)
            alignment.Entities.AddFreeCurve(lineIds[i - 1], lineIds[i], pi.radius, CurveParamType.Radius, false, CurveType.Compound);
        else if (pi.radius > 0)
            alignment.Entities.AddFreeSCS(lineIds[i - 1], lineIds[i], pi.spiralIn, pi.spiralOut, SpiralParamType.Length, pi.radius, false, SpiralType.Clothoid);
    }
    catch (System.Exception ex)
    {
        throw new InvalidOperationException($"PI {i}: Civil 3D could not add the {kind}: {ex.Message}", ex);
    }
    piRows.Add(new {
        index = i,
        kind,
        deflectionDeg = deflections[i] * 180 / Math.PI,
        turn = turnsLeft[i] ? "left" : "right",
        pi.radius,
        pi.spiralIn,
        pi.spiralOut
    });
}

alignment.ReferencePointStation = startStation;
if (Math.Abs(alignment.StartingStation - startStation) > 1e-6)
{
    throw new InvalidOperationException("The alignment start station could not be set.");
}

// Created geometry in station order.
var geometry = new List<object>();
for (var order = 0; order < alignment.Entities.Count; order++)
{
    var entity = alignment.Entities.GetEntityByOrder(order);
    for (var child = 0; child < entity.SubEntityCount; child++)
    {
        var sub = entity[child];
        geometry.Add(new {
            type = sub.SubEntityType.ToString(),
            startStation = sub.StartStation,
            endStation = sub.EndStation,
            length = sub.Length,
            radius = sub is AlignmentSubEntityArc arc ? arc.Radius : (double?)null,
            spiralA = sub is AlignmentSubEntitySpiral spiral ? spiral.A : (double?)null
        });
    }
}

return new {
    success = true,
    name = alignment.Name,
    handle = alignment.Handle.ToString(),
    style = alignment.StyleName,
    layer = alignment.Layer,
    startStation = alignment.StartingStation,
    endStation = alignment.EndingStation,
    length = alignment.Length,
    entityCount = alignment.Entities.Count,
    pis = piRows,
    geometry
};
```

## Usage Notes

- This is a write-capable template: confirm the full drawing identity first and run it with `civil3d_execute`; use `saveDrawing: true` only for an approved save.
- Pass `pis` as rows `[x, y, radius, spiralIn, spiralOut]` or objects with those names, in drawing units. The first and last rows are the start and end point and use zeros. At an interior PI, radius 0 leaves an angle point, a radius with spiral lengths 0 adds a simple arc, and a radius with both spiral lengths adds a clothoid-arc-clothoid group (asymmetric lengths allowed).
- Tangents are fixed lines through the PIs; arcs and spiral groups are free entities between them, so moving a PI grip in Civil 3D keeps the curves attached with their radius and spiral lengths.
- Before writing, the template checks that each curve's tangent lengths fit on the neighbouring segments (spiral tangents use the usual series approximation) and that the spirals do not turn more than the deflection angle. If Civil 3D still refuses a curve, the whole write is rolled back and the error names the PI.
- The drawing must already contain the named alignment style and label set. The alignment is siteless; design speeds, criteria files, superelevation and profiles are outside scope.
- `geometry` lists the created lines, arcs and spirals in station order; `pis` lists each interior PI's deflection and turn direction.
