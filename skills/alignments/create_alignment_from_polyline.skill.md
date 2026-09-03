---
name: create_alignment_from_polyline
category: alignments
description: Create a siteless alignment from one open non-degenerate 2D polyline while preserving its source
requires_write: true
parameters:
  - name: sourceHandle
    type: string
    required: true
    description: Hex handle of an existing open 2D model-space Polyline
  - name: alignmentName
    type: string
    required: true
    description: New unique alignment name
---

## Code Template

```csharp
var sourceHandle = "SOURCE_POLYLINE_HANDLE";
var alignmentName = "NEW_ALIGNMENT_NAME";

if (string.IsNullOrWhiteSpace(sourceHandle)
    || !System.Text.RegularExpressions.Regex.IsMatch(sourceHandle, "^[0-9A-Fa-f]{1,16}$"))
{
    return new { success = false, error = "sourceHandle must be a non-empty hexadecimal handle." };
}

if (string.IsNullOrWhiteSpace(alignmentName)
    || alignmentName != alignmentName.Trim()
    || alignmentName.Length > 255
    || alignmentName.Any(char.IsControl))
{
    return new { success = false, error = "alignmentName must be a trimmed non-empty name of at most 255 characters without control characters." };
}

foreach (ObjectId existingAlignmentId in CivilDoc.GetAlignmentIds())
{
    var existing = Transaction.GetObject(existingAlignmentId, OpenMode.ForRead) as Alignment;
    if (existing != null && existing.Name.Equals(alignmentName, StringComparison.OrdinalIgnoreCase))
    {
        return new { success = false, error = "An alignment with this name already exists.", alignmentName };
    }
}

ObjectId sourceId;
try
{
    var sourceHandleValue = new Handle(System.Convert.ToInt64(sourceHandle, 16));
    sourceId = Database.GetObjectId(false, sourceHandleValue, 0);
}
catch
{
    return new { success = false, error = "sourceHandle does not resolve to an object in this drawing.", sourceHandle };
}

var source = Transaction.GetObject(sourceId, OpenMode.ForRead) as Polyline;
if (source == null || source.IsErased)
{
    return new { success = false, error = "sourceHandle must identify an existing Autodesk.AutoCAD.DatabaseServices.Polyline.", sourceHandle };
}

var blockTable = (BlockTable)Transaction.GetObject(Database.BlockTableId, OpenMode.ForRead);
var modelSpaceId = blockTable[BlockTableRecord.ModelSpace];
if (source.OwnerId != modelSpaceId)
{
    return new { success = false, error = "sourceHandle must identify a Polyline directly in ModelSpace.", sourceHandle };
}

if (source.Closed || source.NumberOfVertices < 2 || source.Length <= 1e-9)
{
    return new { success = false, error = "source polyline must be open and non-degenerate.", sourceHandle };
}

ObjectId styleId;
ObjectId labelSetId;
try
{
    styleId = CivilDoc.Styles.AlignmentStyles["Tervező"];
    labelSetId = CivilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles["Út szelvény és geometriai pontok"];
}
catch
{
    return new { success = false, error = "Required alignment style or alignment label set is missing.", style = "Tervező", labelSet = "Út szelvény és geometriai pontok" };
}

if (styleId.IsNull || labelSetId.IsNull)
{
    return new { success = false, error = "Required alignment style or alignment label set is invalid.", style = "Tervező", labelSet = "Út szelvény és geometriai pontok" };
}

var options = new PolylineOptions
{
    PlineId = sourceId,
    EraseExistingEntities = false,
    AddCurvesBetweenTangents = false
};

var alignmentId = Alignment.Create(
    CivilDoc,
    options,
    alignmentName,
    ObjectId.Null,
    source.LayerId,
    styleId,
    labelSetId
);
if (alignmentId.IsNull)
{
    throw new InvalidOperationException("Alignment creation returned an invalid object ID.");
}

var alignment = Transaction.GetObject(alignmentId, OpenMode.ForRead) as Alignment;
if (alignment == null || alignment.IsErased)
{
    throw new InvalidOperationException("Alignment creation did not return a readable alignment.");
}

var sourceAfter = Transaction.GetObject(sourceId, OpenMode.ForRead, false) as Polyline;
var sourcePreserved = sourceAfter != null && !sourceAfter.IsErased;
if (!sourcePreserved)
{
    throw new InvalidOperationException("The source polyline was not preserved.");
}

return new {
    success = true,
    name = alignment.Name,
    handle = alignment.Handle.ToString(),
    style = alignment.StyleName,
    layer = alignment.Layer,
    length = alignment.Length,
    startStation = alignment.StartingStation,
    endStation = alignment.EndingStation,
    entityCount = alignment.Entities.Count,
    sourceHandle = source.Handle.ToString(),
    sourcePreserved
};
```

## Usage Notes

- This is a write-capable template; confirm the full drawing identity before execution and use `saveDrawing: true` only for an approved save.
- The source must be a directly owned ModelSpace `Polyline`, open, and longer than the tolerance above. It is not erased.
- The drawing must contain the `Tervező` alignment style and the `Út szelvény és geometriai pontok` alignment label set.
- This creates only the siteless alignment. It does not add spirals, superelevation, profiles, corridors, or sections.
