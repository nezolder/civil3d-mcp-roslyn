---
name: alignment_geometry_audit
category: alignments
description: Audit one existing alignment's horizontal geometry and configured design-criteria state without editing it
requires_write: false
parameters:
  - name: alignmentHandle
    type: string
    required: true
    description: Hex handle of an existing Alignment
  - name: limit
    type: number
    required: false
    description: Maximum top-level alignment entities to return (default 100; 1-200)
---

## Code Template

```csharp
var alignmentHandle = "ALIGNMENT_HANDLE";
var limit = 100;

if (string.IsNullOrWhiteSpace(alignmentHandle)
    || !System.Text.RegularExpressions.Regex.IsMatch(alignmentHandle, "^[0-9A-Fa-f]{1,16}$"))
{
    return new { success = false, error = "alignmentHandle must be a non-empty hexadecimal handle." };
}

if (limit < 1 || limit > 200)
{
    return new { success = false, error = "limit must be an integer between 1 and 200.", limit };
}

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
if (alignment == null || alignment.IsErased)
{
    return new { success = false, error = "alignmentHandle must identify an existing Alignment.", alignmentHandle };
}

var entities = new List<object>();
var warnings = new List<object>();
var lineCount = 0;
var arcCount = 0;
var spiralCount = 0;
var otherCount = 0;
var geometryItems = new List<object>();
var geometryTotal = 0;
var geometryLineCount = 0;
var geometryArcCount = 0;
var geometrySpiralCount = 0;
var geometryOtherCount = 0;
var allEntities = new List<AlignmentEntity>();
var parentEntityIndexes = new Dictionary<int, int>();
foreach (AlignmentEntity entity in alignment.Entities)
{
    parentEntityIndexes[entity.EntityId] = allEntities.Count;
    allEntities.Add(entity);
}
var geometryEntities = new List<AlignmentEntity>();
var geometryEntityIds = new HashSet<int>();
var geometryUsesConnectedOrder = true;
for (var chainIndex = 0; chainIndex < allEntities.Count; chainIndex++)
{
    try
    {
        var entity = alignment.Entities.GetEntityByOrder(chainIndex);
        if (!geometryEntityIds.Add(entity.EntityId))
        {
            geometryUsesConnectedOrder = false;
            break;
        }
        geometryEntities.Add(entity);
    }
    catch
    {
        geometryUsesConnectedOrder = false;
        break;
    }
}
var geometryConnectedEntityCount = geometryEntities.Count;
if (geometryEntities.Count != allEntities.Count)
{
    geometryUsesConnectedOrder = false;
    foreach (var entity in allEntities)
    {
        if (geometryEntityIds.Add(entity.EntityId)) geometryEntities.Add(entity);
    }
}
var index = 0;
foreach (AlignmentEntity entity in allEntities)
{
    var entityType = entity.EntityType.ToString();
    var isLine = entity is AlignmentLine;
    var isArc = entity is AlignmentArc;
    var isSpiral = entity is AlignmentSpiral;
    if (isLine) lineCount++;
    else if (isArc) arcCount++;
    else if (isSpiral) spiralCount++;
    else otherCount++;

    double? startStation = null;
    double? endStation = null;
    double? length = null;
    if (entity is AlignmentCurve geometryCurve)
    {
        startStation = geometryCurve.StartStation;
        endStation = geometryCurve.EndStation;
        length = geometryCurve.Length;
    }
    if (entities.Count < limit)
    {
        var item = new Dictionary<string, object>
        {
            ["index"] = index,
            ["entityType"] = entityType,
            ["entityId"] = entity.EntityId.ToString(),
            ["constraint"] = entity.Constraint1.ToString(),
            ["subentityCount"] = entity.SubEntityCount,
            ["startStation"] = startStation,
            ["endStation"] = endStation,
            ["length"] = length
        };

        if (entity is AlignmentCurve curve)
        {
            item["startXY"] = new { x = curve.StartPoint.X, y = curve.StartPoint.Y };
            item["endXY"] = new { x = curve.EndPoint.X, y = curve.EndPoint.Y };
            try
            {
                var entityHighestDesignSpeed = curve.HighestDesignSpeed;
                item["highestDesignSpeed"] = entityHighestDesignSpeed > 0.0
                    ? (double?)entityHighestDesignSpeed
                    : null;
            }
            catch { item["highestDesignSpeed"] = null; }
        }

        if (entity is AlignmentLine line)
        {
            item["direction"] = line.Direction;
        }
        else if (entity is AlignmentArc arc)
        {
            item["radius"] = arc.Radius;
            item["clockwise"] = arc.Clockwise;
            item["delta"] = arc.Delta;
            try
            {
                var minimumRadius = arc.MinimumRadius;
                item["minimumRadius"] = minimumRadius > 0.0 ? (double?)minimumRadius : null;
                item["meetsMinimumRadius"] = minimumRadius > 0.0
                    ? (bool?)(arc.Radius + 1e-9 >= minimumRadius)
                    : null;
            }
            catch
            {
                item["minimumRadius"] = null;
                item["meetsMinimumRadius"] = null;
            }
        }
        else if (entity is AlignmentSpiral spiral)
        {
            item["a"] = spiral.A;
            item["startRadius"] = Double.IsNaN(spiral.RadiusIn) || Double.IsInfinity(spiral.RadiusIn) ? null : (object)spiral.RadiusIn;
            item["endRadius"] = Double.IsNaN(spiral.RadiusOut) || Double.IsInfinity(spiral.RadiusOut) ? null : (object)spiral.RadiusOut;
            item["startIsTangent"] = Double.IsInfinity(spiral.RadiusIn);
            item["endIsTangent"] = Double.IsInfinity(spiral.RadiusOut);
            item["startRadiusUnknown"] = Double.IsNaN(spiral.RadiusIn);
            item["endRadiusUnknown"] = Double.IsNaN(spiral.RadiusOut);
            item["definition"] = spiral.SpiralDefinition.ToString();
            try
            {
                var minimumTransitionLength = spiral.MinimumTransitionLength;
                item["minimumTransitionLength"] = minimumTransitionLength > 0.0
                    ? (double?)minimumTransitionLength
                    : null;
                item["meetsMinimumTransitionLength"] = minimumTransitionLength > 0.0
                    ? (bool?)(spiral.Length + 1e-9 >= minimumTransitionLength)
                    : null;
            }
            catch
            {
                item["minimumTransitionLength"] = null;
                item["meetsMinimumTransitionLength"] = null;
            }
        }

        entities.Add(item);
    }
    index++;
}

double? previousPrimitiveEndStation = null;
for (var chainIndex = 0; chainIndex < geometryEntities.Count; chainIndex++)
{
    var entity = geometryEntities[chainIndex];
    var parentEntityIndex = parentEntityIndexes[entity.EntityId];

    for (var subentityIndex = 0; subentityIndex < entity.SubEntityCount; subentityIndex++)
    {
        var subentity = entity[subentityIndex];
        var isSubentityLine = subentity is AlignmentSubEntityLine;
        var isSubentityArc = subentity is AlignmentSubEntityArc;
        var isSubentitySpiral = subentity is AlignmentSubEntitySpiral;
        if (isSubentityLine) geometryLineCount++;
        else if (isSubentityArc) geometryArcCount++;
        else if (isSubentitySpiral) geometrySpiralCount++;
        else geometryOtherCount++;
        geometryTotal++;

        if (geometryUsesConnectedOrder && warnings.Count < 20)
        {
            if (subentity.Length <= 0.0)
            {
                warnings.Add(new { index = parentEntityIndex, parentEntityIndex, subentityIndex, code = "nonpositive_length", length = subentity.Length });
            }
            if (previousPrimitiveEndStation.HasValue)
            {
                var stationDifference = subentity.StartStation - previousPrimitiveEndStation.Value;
                if (Math.Abs(stationDifference) > 1e-6 && warnings.Count < 20)
                {
                    warnings.Add(new {
                        index = parentEntityIndex,
                        parentEntityIndex,
                        subentityIndex,
                        code = stationDifference > 0.0 ? "station_gap" : "station_overlap",
                        previousEndStation = previousPrimitiveEndStation.Value,
                        startStation = subentity.StartStation,
                        difference = stationDifference
                    });
                }
            }
            previousPrimitiveEndStation = subentity.EndStation;
        }

        if (geometryItems.Count >= limit) continue;

        var geometryItem = new Dictionary<string, object>
        {
            ["index"] = geometryTotal - 1,
            ["chainIndex"] = chainIndex,
            ["parentEntityIndex"] = parentEntityIndex,
            ["parentEntityId"] = entity.EntityId.ToString(),
            ["subentityIndex"] = subentityIndex,
            ["subentityType"] = subentity.SubEntityType.ToString(),
            ["startStation"] = subentity.StartStation,
            ["endStation"] = subentity.EndStation,
            ["length"] = subentity.Length,
            ["startXY"] = new { x = subentity.StartPoint.X, y = subentity.StartPoint.Y },
            ["endXY"] = new { x = subentity.EndPoint.X, y = subentity.EndPoint.Y }
        };

        if (subentity is AlignmentSubEntityLine subentityLine)
        {
            geometryItem["direction"] = subentityLine.Direction;
        }
        else if (subentity is AlignmentSubEntityArc subentityArc)
        {
            geometryItem["radius"] = subentityArc.Radius;
            geometryItem["clockwise"] = subentityArc.Clockwise;
            geometryItem["delta"] = subentityArc.Delta;
            geometryItem["startDirection"] = subentityArc.StartDirection;
            geometryItem["endDirection"] = subentityArc.EndDirection;
        }
        else if (subentity is AlignmentSubEntitySpiral subentitySpiral)
        {
            geometryItem["a"] = subentitySpiral.A;
            geometryItem["startRadius"] = Double.IsNaN(subentitySpiral.RadiusIn) || Double.IsInfinity(subentitySpiral.RadiusIn) ? null : (object)subentitySpiral.RadiusIn;
            geometryItem["endRadius"] = Double.IsNaN(subentitySpiral.RadiusOut) || Double.IsInfinity(subentitySpiral.RadiusOut) ? null : (object)subentitySpiral.RadiusOut;
            geometryItem["startIsTangent"] = Double.IsInfinity(subentitySpiral.RadiusIn);
            geometryItem["endIsTangent"] = Double.IsInfinity(subentitySpiral.RadiusOut);
            geometryItem["startRadiusUnknown"] = Double.IsNaN(subentitySpiral.RadiusIn);
            geometryItem["endRadiusUnknown"] = Double.IsNaN(subentitySpiral.RadiusOut);
            geometryItem["curveType"] = subentitySpiral.CurveType.ToString();
            geometryItem["definition"] = subentitySpiral.SpiralDefinition.ToString();
            geometryItem["direction"] = subentitySpiral.Direction.ToString();
            geometryItem["startDirection"] = subentitySpiral.StartDirection;
            geometryItem["endDirection"] = subentitySpiral.EndDirection;
        }

        geometryItems.Add(geometryItem);
    }
}

var designSpeeds = new List<object>();
try
{
    foreach (DesignSpeed designSpeed in alignment.DesignSpeeds)
    {
        if (designSpeeds.Count >= 50) break;
        designSpeeds.Add(new { station = designSpeed.Station, value = designSpeed.Value, comment = designSpeed.Comment });
    }
}
catch
{
    designSpeeds.Add(new { unavailable = true });
}

bool? useDesignSpeed = null;
bool? useDesignCriteriaFile = null;
bool? useDesignCheckSet = null;
string criteriaFileName = null;
string designCheckSetName = null;
try { useDesignSpeed = alignment.UseDesignSpeed; } catch { }
try { useDesignCriteriaFile = alignment.UseDesignCriteriaFile; } catch { }
try { useDesignCheckSet = alignment.UseDesignCheckSet; } catch { }
try { designCheckSetName = alignment.DesignCheckSetName; } catch { }
var isReference = alignment.IsReferenceObject;
bool? isReferenceStale = null;
if (isReference)
{
    try { isReferenceStale = alignment.IsReferenceStale; } catch { }
}

return new {
    success = true,
    alignment = new {
        name = alignment.Name,
        handle = alignment.Handle.ToString(),
        type = alignment.GetType().Name,
        length = alignment.Length,
        startStation = alignment.StartingStation,
        endStation = alignment.EndingStation,
        style = alignment.StyleName,
        layer = alignment.Layer,
        isSiteless = alignment.SiteId.IsNull,
        isReference,
        isReferenceStale
    },
    entities = new {
        total = alignment.Entities.Count,
        returned = entities.Count,
        truncated = alignment.Entities.Count > entities.Count,
        limit,
        counts = new { line = lineCount, arc = arcCount, spiral = spiralCount, other = otherCount },
        items = entities
    },
    geometry = new {
        total = geometryTotal,
        returned = geometryItems.Count,
        truncated = geometryTotal > geometryItems.Count,
        limit,
        ordering = new {
            connectedChainOrder = geometryUsesConnectedOrder,
            connectedEntityCount = geometryConnectedEntityCount,
            fallbackEntityCount = geometryEntities.Count - geometryConnectedEntityCount
        },
        counts = new { line = geometryLineCount, arc = geometryArcCount, spiral = geometrySpiralCount, other = geometryOtherCount },
        items = geometryItems
    },
    warnings,
    criteria = new {
        useDesignSpeed,
        designSpeeds,
        useDesignCriteriaFile,
        criteriaFileName,
        useDesignCheckSet,
        designCheckSetName
    },
    standardCompliance = "not_evaluated",
    unverified = new[] {
        "Road class, design-speed applicability, and local standard applicability are not decided by this audit.",
        "Superelevation is not evaluated by this audit."
    },
    dbmod = System.Convert.ToInt32(Application.GetSystemVariable("DBMOD"))
};
```

## Usage Notes

- This is read-only: it resolves exactly one existing `Alignment` by hexadecimal handle and does not run design checks.
- Returned top-level entity records are limited; their totals and type counts still cover the complete top-level collection enumeration. The separate `geometry` section flattens primitive line, arc, and spiral subentities in Civil's connected-chain order; it uses the same limit across all primitive children, not once per parent entity. Mechanical warnings examine the complete primitive chain, independently of the output limit; warning `index` remains the parent entity index.
- `geometry.ordering.connectedChainOrder` is false when Civil cannot enumerate every entity through `GetEntityByOrder` (for example, disconnected geometry); those remaining entities are appended only so the flattened totals remain complete. Station-continuity warnings are emitted only when this connected-chain order is complete, and identify both parent and child indexes. Direction values are raw Civil alignment azimuths (clockwise from north), not mathematical `atan2(dy, dx)` headings.
- Civil uses an infinite spiral radius at a tangent end. The audit returns that radius as null with `startIsTangent` or `endIsTangent` true; a NaN radius is null with the corresponding `RadiusUnknown` flag true and is not treated as tangent.
- Design-criteria fields are configuration only. No road-standard compliance, design-speed suitability, or superelevation decision is made.
- The Civil 3D 2025 metadata does not expose a safe criteria-file name member here, so `criteriaFileName` remains null; no local path is returned.
