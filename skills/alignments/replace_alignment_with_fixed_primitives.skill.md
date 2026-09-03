---
name: replace_alignment_with_fixed_primitives
category: alignments
description: Replace a pre-audited plain siteless alignment with a bounded, pre-planned chain of native fixed lines, arcs, and clothoids while preserving identity and design-speed records
requires_write: true
parameters:
  - name: alignmentHandle
    type: string
    required: true
    description: Hex handle of the audited Alignment to replace in place
  - name: expectedBaseline
    type: object
    required: true
    description: Fresh audit-derived identity, metadata, design-speed, and ordered primitive records
  - name: plannedPrimitives
    type: array
    required: true
    description: Finite ordered line, curve, and spiral records calculated and checked before authoring
  - name: tolerance
    type: number
    required: false
    description: Drawing-unit verification tolerance (default 0.001)
---

## Scope and authorization

Use this only for an approved **whole-alignment replacement** of one ordinary, siteless, independent centreline. It is not a route optimiser, a geometric solver, an offset/connected/railway-alignment editor, or a standards/superelevation tool.

The caller must first run a fresh `alignment_geometry_audit` and copy its complete connected `geometry.items` rows into the expected baseline below. Map audit `subentityType` values `Line`, `Arc`, and `Spiral` to `line`, `curve`, and `spiral`; `startXY`/`endXY` become points, a line's `direction` becomes `lineDirection`, and a spiral's `direction` becomes `spiralDirection`. For a tangent spiral, map `startRadius`/`endRadius` null plus the corresponding `startIsTangent`/`endIsTangent` flag to `radiusIn`/`radiusOut = double.PositiveInfinity`; an unknown radius is unsupported. Apply the same tangent mapping to planned spiral radii. Directions are Civil API radians (clockwise from north), not Cartesian headings; a Cartesian heading theta maps to `Math.PI / 2 - theta`. Coordinates and lengths use drawing units without coordinate-system conversion.

The plan must already be bounded and independently checked. This template deliberately limits the first reusable case to a starting station of `0`, no station equations, no superelevation, no design-criteria/check-set state, no profiles, and no corridor baseline using the alignment. The supplied audit assertions default to `false`; setting them true states independently verified prerequisites, not an automatic detector. The geometry audit alone does not prove absence of dependent alignments or superelevation. Do not infer those assertions from its empty warnings list.

Reading this skill grants no write permission. Before an approved execution, use the existing drawing guard with the full drawing filename and fingerprint, obtain the required unchanged timestamped backup through the established workflow, and use the approved `saveDrawing: true` path. Independently read the saved drawing back and require `DBMOD=0`. This template does not call `SaveAs`, `QSAVE`, or `WBLOCK`.

## Code Template

```csharp
var alignmentHandle = "ALIGNMENT_HANDLE";
var tolerance = 0.001;
var directionTolerance = 0.000001;

// Copy these scalar and primitive values from a fresh bounded alignment_geometry_audit.
// This first recipe supports only the live-proven zero-start-station case.
var expectedName = "EXISTING_ALIGNMENT_NAME";
var expectedLayer = "EXISTING_LAYER_NAME";
var expectedStyle = "EXISTING_STYLE_NAME";
var expectedStartingStation = 0.0;
var expectedUseDesignSpeed = true;
var expectedStart = new Point2d(0.0, 0.0);
var expectedEnd = new Point2d(1.0, 0.0);
var auditVerifiedPlainIndependentCentreline = false;
var auditVerifiedNoStationEquations = false;
var auditVerifiedNoSuperelevation = false;
var preserveEndpoints = true;

var expectedSpeedRecords = new[]
{
    new { station = 0.0, value = 0.0, comment = "AUDITED_SPEED_COMMENT" }
};

// Every prior primitive is required, in the audit's connected order. Unused fields
// are still supplied so this remains one strongly typed, easy-to-review table.
var expectedExistingPrimitives = new[]
{
    new {
        kind = "line",
        start = new Point2d(0.0, 0.0), end = new Point2d(1.0, 0.0),
        length = 1.0, radius = 0.0, clockwise = false,
        lineDirection = Math.PI / 2, startDirection = 0.0, endDirection = 0.0,
        spiralDirection = "", curveType = "",
        radiusIn = 0.0, radiusOut = 0.0
    }
};

// This is an already-calculated replacement, not solver input. Keep it connected:
// each item's start equals the preceding item's expected end within tolerance.
// A spiral's pi is its independently calculated spiral PI/control point.
var plannedPrimitives = new[]
{
    new {
        kind = "line",
        start = new Point2d(0.0, 0.0), middle = new Point2d(0.0, 0.0),
        end = new Point2d(1.0, 0.0), pi = new Point2d(0.0, 0.0),
        length = 1.0, radius = 0.0, transitionLength = 0.0,
        radiusIn = 0.0, radiusOut = 0.0,
        startDirection = Math.PI / 2, endDirection = Math.PI / 2,
        curveType = SpiralCurveType.InCurve, clockwise = false
    }
};

bool IsFinite(double value)
{
    return !double.IsNaN(value) && !double.IsInfinity(value);
}

bool IsFinitePoint(Point2d point)
{
    return IsFinite(point.X) && IsFinite(point.Y);
}

bool SamePoint(Point2d left, Point2d right)
{
    return left.GetDistanceTo(right) <= tolerance;
}

bool SameAlignmentPoint(Point3d left, Point2d right)
{
    return SamePoint(new Point2d(left.X, left.Y), right);
}

bool SameRadius(double left, double right)
{
    return (left == double.PositiveInfinity && right == double.PositiveInfinity)
        || (IsFinite(left) && IsFinite(right) && Math.Abs(left - right) <= tolerance);
}

bool IsSpiralRadius(double radius)
{
    return radius == double.PositiveInfinity || (IsFinite(radius) && radius > tolerance);
}

bool SameDirection(double left, double right)
{
    return Math.Abs(Math.Atan2(Math.Sin(left - right), Math.Cos(left - right))) <= directionTolerance;
}

if (string.IsNullOrWhiteSpace(alignmentHandle)
    || !System.Text.RegularExpressions.Regex.IsMatch(alignmentHandle, "^[0-9A-Fa-f]{1,16}$"))
{
    return new { success = false, error = "alignmentHandle must be a non-empty hexadecimal handle." };
}

if (!IsFinite(tolerance) || tolerance < 0.000001 || tolerance > 1.0)
{
    return new { success = false, error = "tolerance must be finite and between 0.000001 and 1.0 drawing units." };
}

if (!IsFinite(directionTolerance) || directionTolerance < 0.000000001 || directionTolerance > 0.01)
{
    return new { success = false, error = "directionTolerance must be finite and between 1e-9 and 0.01 radians." };
}

if (!auditVerifiedPlainIndependentCentreline
    || !auditVerifiedNoStationEquations
    || !auditVerifiedNoSuperelevation)
{
    return new {
        success = false,
        error = "This first recipe requires a fresh audit explicitly confirming a plain independent centreline with no station equations or superelevation."
    };
}

if (expectedExistingPrimitives.Length < 1 || expectedExistingPrimitives.Length > 64
    || plannedPrimitives.Length < 1 || plannedPrimitives.Length > 64
    || expectedSpeedRecords.Length > 32)
{
    return new { success = false, error = "The audited baseline, replacement plan, or design-speed records exceed this recipe's bounded limits." };
}

if (!IsFinite(expectedStartingStation) || Math.Abs(expectedStartingStation) > 1e-9
    || !IsFinitePoint(expectedStart) || !IsFinitePoint(expectedEnd))
{
    return new { success = false, error = "This recipe supports only a finite zero-start-station audit baseline." };
}

for (var index = 0; index < expectedSpeedRecords.Length; index++)
{
    var speed = expectedSpeedRecords[index];
    if (!IsFinite(speed.station) || !IsFinite(speed.value) || speed.comment == null)
        return new { success = false, error = "Each expected design-speed record needs finite station/value and a non-null comment.", index };
}

for (var index = 0; index < expectedExistingPrimitives.Length; index++)
{
    var primitive = expectedExistingPrimitives[index];
    if (!IsFinitePoint(primitive.start) || !IsFinitePoint(primitive.end)
        || !IsFinite(primitive.length) || primitive.length <= tolerance)
        return new { success = false, error = "Each expected baseline primitive must have finite non-zero geometry.", index };
    if (primitive.kind == "line" && !IsFinite(primitive.lineDirection))
        return new { success = false, error = "Each expected baseline line needs a finite direction.", index };
    if (primitive.kind == "curve" && (!IsFinite(primitive.radius) || primitive.radius <= tolerance))
        return new { success = false, error = "Each expected baseline curve needs a finite positive radius.", index };
    if (primitive.kind == "spiral" && (!IsFinite(primitive.startDirection) || !IsFinite(primitive.endDirection)
        || string.IsNullOrWhiteSpace(primitive.spiralDirection) || string.IsNullOrWhiteSpace(primitive.curveType)
        || !IsSpiralRadius(primitive.radiusIn) || !IsSpiralRadius(primitive.radiusOut)))
        return new { success = false, error = "Each expected baseline spiral needs audited radii, directions, and type fields.", index };
    if (primitive.kind != "line" && primitive.kind != "curve" && primitive.kind != "spiral")
        return new { success = false, error = "Expected baseline primitive kind must be line, curve, or spiral.", index };
    if (index > 0 && !SamePoint(expectedExistingPrimitives[index - 1].end, primitive.start))
        return new { success = false, error = "Expected baseline primitives are not connected in audit order.", index };
}

for (var index = 0; index < plannedPrimitives.Length; index++)
{
    var primitive = plannedPrimitives[index];
    if ((primitive.kind != "line" && primitive.kind != "curve" && primitive.kind != "spiral")
        || !IsFinitePoint(primitive.start) || !IsFinitePoint(primitive.end)
        || !IsFinitePoint(primitive.middle) || !IsFinitePoint(primitive.pi)
        || !IsFinite(primitive.length) || primitive.length <= tolerance
        || !IsFinite(primitive.radius) || !IsFinite(primitive.transitionLength)
        || !IsFinite(primitive.startDirection) || !IsFinite(primitive.endDirection)
        || primitive.start.GetDistanceTo(primitive.end) <= tolerance)
        return new { success = false, error = "Each planned primitive must be a finite line, curve, or spiral with distinct start/end points.", index };
    if (index > 0 && !SamePoint(plannedPrimitives[index - 1].end, primitive.start))
        return new { success = false, error = "Planned primitives are not connected.", index };
    if (index > 0 && !SameDirection(plannedPrimitives[index - 1].endDirection, primitive.startDirection))
        return new { success = false, error = "Planned primitives are not tangent-continuous.", index };
    if (primitive.kind == "curve" && (!IsFinite(primitive.radius) || primitive.radius <= tolerance
        || primitive.start.GetDistanceTo(primitive.middle) <= tolerance
        || primitive.middle.GetDistanceTo(primitive.end) <= tolerance))
        return new { success = false, error = "A fixed curve needs distinct start, middle, and end points.", index };
    if (primitive.kind == "spiral" && (primitive.radius <= tolerance || primitive.transitionLength <= tolerance
        || Math.Abs(primitive.length - primitive.transitionLength) > tolerance
        || !IsSpiralRadius(primitive.radiusIn) || !IsSpiralRadius(primitive.radiusOut)))
        return new { success = false, error = "A fixed spiral needs audited radii, directions, and matching positive length/transitionLength.", index };
    if (primitive.kind == "spiral" && !(
        (primitive.curveType == SpiralCurveType.InCurve && primitive.radiusIn == double.PositiveInfinity && SameRadius(primitive.radiusOut, primitive.radius))
        || (primitive.curveType == SpiralCurveType.OutCurve && SameRadius(primitive.radiusIn, primitive.radius) && primitive.radiusOut == double.PositiveInfinity)))
        return new { success = false, error = "Only a consistent tangent-to-circle or circle-to-tangent clothoid is supported.", index };
    if (index == 0 && primitive.kind != "line")
        return new { success = false, error = "The fixed-primitive replacement must begin with a line; fixed curves and spirals need a preceding entity.", index };
}

var plannedLength = plannedPrimitives.Sum(primitive => primitive.length);
if (!IsFinite(plannedLength) || expectedSpeedRecords.Any(speed => speed.station < 0 || speed.station > plannedLength))
    return new { success = false, error = "The planned length must be finite and retain the domain of every design-speed station." };

if (preserveEndpoints && (!SamePoint(plannedPrimitives[0].start, expectedStart)
    || !SamePoint(plannedPrimitives[plannedPrimitives.Length - 1].end, expectedEnd)))
{
    return new { success = false, error = "The replacement plan does not preserve the audited endpoints." };
}

// Host access starts here.
ObjectId alignmentId;
try
{
    alignmentId = Database.GetObjectId(false, new Handle(System.Convert.ToInt64(alignmentHandle, 16)), 0);
}
catch
{
    return new { success = false, error = "alignmentHandle does not resolve to an object in this drawing.", alignmentHandle };
}

var alignment = Transaction.GetObject(alignmentId, OpenMode.ForRead) as Alignment;
if (alignment == null || alignment.IsErased || !alignment.Handle.ToString().Equals(alignmentHandle, StringComparison.OrdinalIgnoreCase))
    return new { success = false, error = "alignmentHandle must identify the audited existing Alignment.", alignmentHandle };
if (alignment.IsReferenceObject || !alignment.SiteId.IsNull || alignment.GetProfileIds().Count != 0
    || alignment.AlignmentType.ToString() != "Centerline")
    return new { success = false, error = "Only a non-reference siteless plain centreline alignment without profiles is supported." };
if (Math.Abs(alignment.StartingStation) > 1e-9 || alignment.StationEquations.Count != 0)
    return new { success = false, error = "This first recipe supports only actual zero starting station with no station equations." };
if (alignment.UseDesignCriteriaFile || alignment.UseDesignCheckSet)
    return new { success = false, error = "Design-criteria-file and design-check-set state are outside this recipe." };
if (System.Convert.ToInt32(Application.GetSystemVariable("DBMOD")) != 0)
    return new { success = false, error = "Drawing must be clean at the approved backup/guard baseline before mutation." };
if (alignment.Entities.Count < 1 || alignment.Entities.Count > 64)
    return new { success = false, error = "The existing alignment exceeds this recipe's bounded entity traversal limit." };

foreach (ObjectId corridorId in CivilDoc.CorridorCollection)
{
    var corridor = Transaction.GetObject(corridorId, OpenMode.ForRead) as Corridor;
    if (corridor != null && corridor.Baselines.Cast<Baseline>().Any(baseline => baseline.AlignmentId == alignmentId))
        return new { success = false, error = "A corridor baseline uses this alignment; whole-alignment replacement is unsupported.", corridor = corridor.Name };
}

if (alignment.Name != expectedName || alignment.Layer != expectedLayer
    || alignment.StyleName != expectedStyle
    || Math.Abs(alignment.StartingStation - expectedStartingStation) > tolerance
    || !SameAlignmentPoint(alignment.StartPoint, expectedStart) || !SameAlignmentPoint(alignment.EndPoint, expectedEnd)
    || alignment.UseDesignSpeed != expectedUseDesignSpeed)
{
    return new { success = false, error = "Alignment metadata, endpoint, station, or design-speed baseline no longer matches the fresh audit." };
}

var existingPrimitives = new List<AlignmentSubEntity>();
for (var order = 0; order < alignment.Entities.Count; order++)
{
    var entity = alignment.Entities.GetEntityByOrder(order);
    if (entity == null) return new { success = false, error = "Audit baseline has disconnected alignment entities." };
    if (entity.SubEntityCount < 1 || existingPrimitives.Count + entity.SubEntityCount > 64)
        return new { success = false, error = "The existing alignment exceeds this recipe's bounded primitive traversal limit." };
    for (var child = 0; child < entity.SubEntityCount; child++) existingPrimitives.Add(entity[child]);
}
if (existingPrimitives.Count != expectedExistingPrimitives.Length)
    return new { success = false, error = "Primitive count no longer matches the fresh audit." };

for (var index = 0; index < existingPrimitives.Count; index++)
{
    var actual = existingPrimitives[index];
    var expected = expectedExistingPrimitives[index];
    if (!SamePoint(actual.StartPoint, expected.start) || !SamePoint(actual.EndPoint, expected.end)
        || Math.Abs(actual.Length - expected.length) > tolerance)
        return new { success = false, error = "A baseline primitive no longer matches the fresh audit.", index };
    if (expected.kind == "line")
    {
        var line = actual as AlignmentSubEntityLine;
        if (line == null || !SameDirection(line.Direction, expected.lineDirection))
            return new { success = false, error = "Baseline line differs from the fresh audit.", index };
    }
    else if (expected.kind == "curve")
    {
        var arc = actual as AlignmentSubEntityArc;
        if (arc == null || Math.Abs(arc.Radius - expected.radius) > tolerance || arc.Clockwise != expected.clockwise)
            return new { success = false, error = "Baseline curve differs from the fresh audit.", index };
    }
    else if (expected.kind == "spiral")
    {
        var spiral = actual as AlignmentSubEntitySpiral;
        if (spiral == null || spiral.SpiralDefinition != SpiralType.Clothoid
            || spiral.CurveType.ToString() != expected.curveType
            || spiral.Direction.ToString() != expected.spiralDirection
            || !SameRadius(spiral.RadiusIn, expected.radiusIn)
            || !SameRadius(spiral.RadiusOut, expected.radiusOut)
            || !SameDirection(spiral.StartDirection, expected.startDirection)
            || !SameDirection(spiral.EndDirection, expected.endDirection))
            return new { success = false, error = "Baseline spiral differs from the fresh audit.", index };
    }
    else
    {
        return new { success = false, error = "Expected baseline primitive kind must be line, curve, or spiral.", index };
    }
}

var savedSpeeds = alignment.DesignSpeeds.Cast<DesignSpeed>()
    .Select(speed => new { station = speed.Station, value = speed.Value, comment = speed.Comment })
    .OrderBy(speed => speed.station)
    .ThenBy(speed => speed.value)
    .ThenBy(speed => speed.comment, StringComparer.Ordinal)
    .ToArray();
if (savedSpeeds.Length != expectedSpeedRecords.Length)
    return new { success = false, error = "Design-speed count no longer matches the fresh audit." };
for (var index = 0; index < savedSpeeds.Length; index++)
{
    var actual = savedSpeeds[index];
    var expected = expectedSpeedRecords.OrderBy(speed => speed.station).ThenBy(speed => speed.value).ThenBy(speed => speed.comment, StringComparer.Ordinal).ElementAt(index);
    if (Math.Abs(actual.station - expected.station) > tolerance
        || Math.Abs(actual.value - expected.value) > tolerance
        || actual.comment != expected.comment)
        return new { success = false, error = "Design-speed record no longer matches the fresh audit.", index };
}

// No return after this point: any mismatch must throw so the host transaction aborts.
var originalHandle = alignment.Handle.ToString();
var originalName = alignment.Name;
var originalLayer = alignment.Layer;
var originalStyleId = alignment.StyleId;
var originalDescription = alignment.Description;
var originalStartingStation = alignment.StartingStation;

double StartDirectionOf(AlignmentSubEntity primitive)
{
    if (primitive is AlignmentSubEntityLine line) return line.Direction;
    if (primitive is AlignmentSubEntityArc arc) return arc.StartDirection;
    if (primitive is AlignmentSubEntitySpiral spiral) return spiral.StartDirection;
    throw new InvalidOperationException("Unsupported constructed primitive type.");
}

double EndDirectionOf(AlignmentSubEntity primitive)
{
    if (primitive is AlignmentSubEntityLine line) return line.Direction;
    if (primitive is AlignmentSubEntityArc arc) return arc.EndDirection;
    if (primitive is AlignmentSubEntitySpiral spiral) return spiral.EndDirection;
    throw new InvalidOperationException("Unsupported constructed primitive type.");
}

int VerifyReplacementGeometry()
{
    if (alignment.Entities.Count != plannedPrimitives.Length)
        throw new InvalidOperationException("Top-level entity count differs from the bounded replacement plan.");

    AlignmentSubEntity previous = null;
    var verifiedCount = 0;
    var directPairs = 0;
    for (var order = 0; order < alignment.Entities.Count; order++)
    {
        var entity = alignment.Entities.GetEntityByOrder(order);
        if (entity == null || entity.SubEntityCount != 1)
            throw new InvalidOperationException("Replacement is not a one-to-one connected primitive chain at index " + order + ".");

        var actual = entity[0];
        var planned = plannedPrimitives[verifiedCount];
        if (!SamePoint(actual.StartPoint, planned.start) || !SamePoint(actual.EndPoint, planned.end)
            || Math.Abs(actual.Length - planned.length) > tolerance
            || !SameDirection(StartDirectionOf(actual), planned.startDirection)
            || !SameDirection(EndDirectionOf(actual), planned.endDirection))
            throw new InvalidOperationException("Constructed primitive geometry or direction mismatch at index " + verifiedCount + ".");

        if (planned.kind == "line")
        {
            if (!(actual is AlignmentSubEntityLine))
                throw new InvalidOperationException("Constructed primitive is not the planned line at index " + verifiedCount + ".");
        }
        else if (planned.kind == "curve")
        {
            var arc = actual as AlignmentSubEntityArc;
            if (arc == null || Math.Abs(arc.Radius - planned.radius) > tolerance || arc.Clockwise != planned.clockwise)
                throw new InvalidOperationException("Constructed primitive is not the planned curve at index " + verifiedCount + ".");
        }
        else
        {
            var spiral = actual as AlignmentSubEntitySpiral;
            if (spiral == null || spiral.SpiralDefinition != SpiralType.Clothoid
                || spiral.CurveType != planned.curveType
                || !SameRadius(spiral.RadiusIn, planned.radiusIn)
                || !SameRadius(spiral.RadiusOut, planned.radiusOut))
                throw new InvalidOperationException("Constructed primitive is not the planned clothoid at index " + verifiedCount + ".");
            if (previous is AlignmentSubEntitySpiral) directPairs++;
        }

        if (previous != null && (!SamePoint(previous.EndPoint, actual.StartPoint)
            || !SameDirection(EndDirectionOf(previous), StartDirectionOf(actual))))
            throw new InvalidOperationException("Constructed chain is not position- and tangent-continuous at index " + verifiedCount + ".");
        previous = actual;
        verifiedCount++;
    }
    if (verifiedCount != plannedPrimitives.Length)
        throw new InvalidOperationException("Replacement primitive enumeration is incomplete.");
    return directPairs;
}

void VerifyRetainedState()
{
    if (alignment.Handle.ToString() != originalHandle || alignment.Name != originalName
        || alignment.Layer != originalLayer || alignment.StyleId != originalStyleId
        || alignment.Description != originalDescription
        || Math.Abs(alignment.StartingStation - originalStartingStation) > tolerance
        || Math.Abs(alignment.StartingStation) > 1e-9
        || alignment.StationEquations.Count != 0
        || alignment.AlignmentType.ToString() != "Centerline"
        || alignment.UseDesignSpeed != expectedUseDesignSpeed)
        throw new InvalidOperationException("Alignment identity, metadata, station state, type, or design-speed mode changed during replacement.");
    if (preserveEndpoints && (!SameAlignmentPoint(alignment.StartPoint, expectedStart)
        || !SameAlignmentPoint(alignment.EndPoint, expectedEnd)))
        throw new InvalidOperationException("Endpoint-preservation check failed after replacement.");
}

alignment.UpgradeOpen();
var entities = alignment.Entities;
entities.Clear();
var hasPreviousEntity = false;
var previousEntityId = 0;

for (var index = 0; index < plannedPrimitives.Length; index++)
{
    var planned = plannedPrimitives[index];
    AlignmentEntity created;
    if (planned.kind == "line")
    {
        created = hasPreviousEntity
            ? entities.AddFixedLine(previousEntityId, new Point3d(planned.start.X, planned.start.Y, 0.0), new Point3d(planned.end.X, planned.end.Y, 0.0))
            : entities.AddFixedLine(new Point3d(planned.start.X, planned.start.Y, 0.0), new Point3d(planned.end.X, planned.end.Y, 0.0));
    }
    else if (planned.kind == "curve")
    {
        created = entities.AddFixedCurve(previousEntityId, new Point3d(planned.start.X, planned.start.Y, 0.0), new Point3d(planned.middle.X, planned.middle.Y, 0.0), new Point3d(planned.end.X, planned.end.Y, 0.0));
    }
    else
    {
        created = entities.AddFixedSpiral(previousEntityId, new Point3d(planned.start.X, planned.start.Y, 0.0), new Point3d(planned.pi.X, planned.pi.Y, 0.0), planned.radius, planned.transitionLength, planned.curveType, planned.clockwise, SpiralType.Clothoid);
    }
    if (created == null)
        throw new InvalidOperationException("Fixed primitive construction returned null at index " + index + ".");
    previousEntityId = created.EntityId;
    hasPreviousEntity = true;
}

var directSpiralPairCount = VerifyReplacementGeometry();
VerifyRetainedState();

for (var index = alignment.DesignSpeeds.Count - 1; index >= 0; index--)
    alignment.DesignSpeeds.Remove(index);
if (alignment.DesignSpeeds.Count != 0)
    throw new InvalidOperationException("Could not clear design-speed records for scalar restoration.");
foreach (var speed in savedSpeeds)
{
    var restored = alignment.DesignSpeeds.Add(speed.station, speed.value);
    restored.Comment = speed.comment;
}
var restoredSpeeds = alignment.DesignSpeeds.Cast<DesignSpeed>()
    .Select(speed => new { station = speed.Station, value = speed.Value, comment = speed.Comment })
    .OrderBy(speed => speed.station)
    .ThenBy(speed => speed.value)
    .ThenBy(speed => speed.comment, StringComparer.Ordinal)
    .ToArray();
if (restoredSpeeds.Length != savedSpeeds.Length)
    throw new InvalidOperationException("Design-speed count changed during restoration.");
for (var index = 0; index < restoredSpeeds.Length; index++)
{
    if (Math.Abs(restoredSpeeds[index].station - savedSpeeds[index].station) > tolerance
        || Math.Abs(restoredSpeeds[index].value - savedSpeeds[index].value) > tolerance
        || restoredSpeeds[index].comment != savedSpeeds[index].comment)
        throw new InvalidOperationException("Design-speed scalar restoration mismatch at index " + index + ".");
}
directSpiralPairCount = VerifyReplacementGeometry();
VerifyRetainedState();

return new {
    success = true,
    alignment = new {
        handle = alignment.Handle.ToString(), name = alignment.Name, layer = alignment.Layer,
        style = alignment.StyleName,
        startStation = alignment.StartingStation, endStation = alignment.EndingStation
    },
    replacement = new { primitiveCount = plannedPrimitives.Length, directSpiralPairCount, endpointsPreserved = preserveEndpoints },
    designSpeeds = new { restored = true, count = restoredSpeeds.Length },
    standardCompliance = "not_evaluated",
    liveVerification = "unverified_pending_guarded_save_and_independent_reopen"
};
```

## Native editing behavior and limits

- The replacement clears and rebuilds the target's complete horizontal entity collection in the same alignment object. Its handle, name, style, layer, description, and starting station are checked after construction, but top-level entity identities and original constraint relationships are not preserved.
- The template uses native `AddFixedLine`, `AddFixedCurve`, and `AddFixedSpiral` calls. It verifies every constructed primitive's type, endpoint, length, radius/direction where relevant, and tangent continuity using a separate angular tolerance. For a reverse S-C-S-S-C-S group, place the out-spiral and in-spiral next to each other in the plan with the same joined endpoint; do not insert an artificial tangent. The template never invents unplanned geometry.
- A successful live case also used `AddFreeSCS` for an independently planned initial SCS between two fixed tangents. That is a separate, constraint-driven prefix operation: a whole-chain fixed replacement does not preserve that free-SCS constraint. Do not substitute either failed free-SCSSCS solver variant as this recipe's default.
- Design-speed wrappers showed station drift when retained across geometry replacement in the exercised case. The internal cause is **Unverified**. This template therefore captures only station/value/comment scalars, removes records by reverse index after all geometry is built, adds them again, restores comments, and verifies the result.
- **Proven in one scoped live case (2026-09-03):** the unchanged template body, bound to a fresh audited independent synthetic centreline, built an eight-primitive `L-S-C-S-S-C-S-L` chain on its first execution in Civil 3D 2025 Hungary. Geometry, endpoints, source, alignment identity/metadata and two scalar speed records survived a guarded save and independent close/reopen; the reopened compact readback kept `DBMOD=0`. This is not proof for every valid input or a time/token-saving benchmark. Non-zero or equation stations, superelevation, design criteria, dependent objects, live rollback rejection paths and standards compliance remain unsupported or **Unverified** as scoped above.
- **Dirty-state caution:** the fuller baseline audit and first post-write inspection observed a dirty drawing flag in that test. A guarded no-geometry save restored clean state before replacement and before reopen; the exact source of the flag remains **Unverified**. Recheck `DBMOD` and use the authorized save/reconciliation workflow rather than ignoring a dirty result, repeating geometry writes, or assuming all read-only getters are side-effect-free.
