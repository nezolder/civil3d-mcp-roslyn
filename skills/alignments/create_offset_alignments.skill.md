---
name: create_offset_alignments
category: alignments
description: Create one or more dynamic offset alignments at fixed horizontal offsets from an existing parent alignment
requires_write: true
aliases: ["eltolt nyomvonal létrehozása", "create offset alignments left and right of a centerline", "burkolatszél tengely eltolással", "párhuzamos nyomvonal adott távolságra"]
workflow_tags: ["modeling"]
tested_civil_version: "2025"
validation_summary: "Live 2025: -3.5/+3.5 offsets full length and over a station range followed a native parent PI edit within 1e-8 and survived save and reopen. Default Geometry lock moved a partial range with the geometry; the lockToStations option is not yet live-tested."
parameters:
  - name: parentAlignmentHandle
    type: string
    required: true
    description: Hex handle of the existing parent alignment
  - name: offsets
    type: array
    required: true
    description: 1 to 20 (name, offset) rows; offset in drawing units, negative to the left and positive to the right of the parent
  - name: alignmentStyleName
    type: string
    required: false
    description: Existing alignment style name; empty uses the parent's style (default "")
  - name: fullLength
    type: bool
    required: false
    description: true follows the whole parent; false uses startStation..endStation (default true)
  - name: startStation
    type: double
    required: false
    description: Parent station where the offsets start when fullLength is false (default 0)
  - name: endStation
    type: double
    required: false
    description: Parent station where the offsets end when fullLength is false (default 0)
  - name: lockToStations
    type: bool
    required: false
    description: false keeps Civil 3D's Geometry lock mode, where the start and end follow the parent geometry; true uses Station lock mode, which keeps them at the given parent stations after parent edits (default false)
---

## Code Template

```csharp
var parentAlignmentHandle = "PARENT_ALIGNMENT_HANDLE";
var offsets = new (string name, double offset)[] { };
var alignmentStyleName = "";
var fullLength = true;
var startStation = 0.0;
var endStation = 0.0;
var lockToStations = false;

var isName = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 255
    && !value.Any(char.IsControl));
if (string.IsNullOrWhiteSpace(parentAlignmentHandle)
    || !System.Text.RegularExpressions.Regex.IsMatch(parentAlignmentHandle, "^[0-9A-Fa-f]{1,16}$"))
{
    return new { success = false, error = "parentAlignmentHandle must be a hexadecimal handle." };
}
if (offsets == null || offsets.Length < 1 || offsets.Length > 20)
{
    return new { success = false, error = "offsets must contain 1 to 20 rows." };
}
if (offsets.Any(o => !isName(o.name) || !double.IsFinite(o.offset) || Math.Abs(o.offset) < 1e-6 || Math.Abs(o.offset) > 10000)
    || offsets.Select(o => o.name.ToUpperInvariant()).Distinct().Count() != offsets.Length)
{
    return new { success = false, error = "Each row needs a distinct trimmed name and a nonzero finite offset of at most 10000." };
}
if (alignmentStyleName != "" && !isName(alignmentStyleName))
{
    return new { success = false, error = "alignmentStyleName must be empty or a trimmed style name." };
}

Alignment parent = null;
try
{
    var parentId = Database.GetObjectId(false, new Handle(System.Convert.ToInt64(parentAlignmentHandle, 16)), 0);
    parent = Transaction.GetObject(parentId, OpenMode.ForRead) as Alignment;
}
catch
{
    parent = null;
}
if (parent == null || parent.IsErased)
{
    return new { success = false, error = "parentAlignmentHandle must identify an existing alignment.", parentAlignmentHandle };
}
if (!fullLength
    && (!double.IsFinite(startStation) || !double.IsFinite(endStation)
        || startStation < parent.StartingStation - 1e-6 || endStation > parent.EndingStation + 1e-6
        || endStation - startStation <= 1e-6))
{
    return new {
        success = false,
        error = "startStation and endStation must lie inside the parent's station range with startStation before endStation.",
        parentStartStation = parent.StartingStation,
        parentEndStation = parent.EndingStation
    };
}

var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (ObjectId existingId in CivilDoc.GetAlignmentIds())
{
    if (Transaction.GetObject(existingId, OpenMode.ForRead) is Alignment existing) existingNames.Add(existing.Name);
}
var taken = offsets.Where(o => existingNames.Contains(o.name)).Select(o => o.name).ToList();
if (taken.Count > 0)
{
    return new { success = false, error = "Alignments with these names already exist.", names = taken };
}

ObjectId styleId = parent.StyleId;
if (alignmentStyleName != "")
{
    try
    {
        styleId = CivilDoc.Styles.AlignmentStyles[alignmentStyleName];
    }
    catch
    {
        styleId = ObjectId.Null;
    }
    if (styleId.IsNull)
    {
        return new { success = false, error = "The named alignment style does not exist.", alignmentStyleName };
    }
}

// Any failure throws, so either every offset alignment is created or none.
var created = new List<object>();
foreach (var row in offsets)
{
    var id = fullLength
        ? Alignment.CreateOffsetAlignment(row.name, parent.ObjectId, row.offset, styleId)
        : Alignment.CreateOffsetAlignment(row.name, parent.ObjectId, row.offset, styleId, startStation, endStation);
    if (id.IsNull)
    {
        throw new InvalidOperationException($"Offset alignment '{row.name}' creation returned an invalid object ID.");
    }
    var alignment = (Alignment)Transaction.GetObject(id, OpenMode.ForWrite);
    if (alignment.AlignmentType.ToString() != "Offset")
    {
        throw new InvalidOperationException($"'{row.name}' was not created as an offset alignment.");
    }
    var info = alignment.OffsetAlignmentInfo;
    if (lockToStations)
    {
        info.LockMode = AlignmentLockModeType.Station;
        if (info.LockMode != AlignmentLockModeType.Station)
        {
            throw new InvalidOperationException($"'{row.name}' did not accept the Station lock mode.");
        }
    }
    created.Add(new {
        name = alignment.Name,
        handle = alignment.Handle.ToString(),
        row.offset,
        side = row.offset < 0 ? "left" : "right",
        style = alignment.StyleName,
        layer = alignment.Layer,
        updateMode = info.UpdateMode.ToString(),
        lockMode = info.LockMode.ToString(),
        lockToStartStation = info.LockToStartStation,
        lockToEndStation = info.LockToEndStation,
        startStation = alignment.StartingStation,
        endStation = alignment.EndingStation,
        length = alignment.Length
    });
}

return new {
    success = true,
    parent = new {
        name = parent.Name,
        handle = parent.Handle.ToString(),
        type = parent.AlignmentType.ToString(),
        isReference = parent.IsReferenceObject,
        startStation = parent.StartingStation,
        endStation = parent.EndingStation
    },
    created
};
```

## Usage Notes

- This is a write-capable template: confirm the full drawing identity first and run it with `civil3d_execute`; use `saveDrawing: true` only for an approved save.
- Pass `offsets` as rows `["Bal burkolatszél", -3.5]` or objects `{"name": ..., "offset": ...}`. Negative offsets are left of the parent in its stationing direction, positive offsets right.
- Offset alignments stay linked to the parent: when the parent's geometry changes, Civil 3D updates them, so they need not be recreated after a centerline edit.
- With `fullLength: false` the offsets cover only `startStation..endStation` of the parent. By default Civil 3D uses Geometry lock mode: the start and end stay attached to the nearest parent geometry points, so after a parent edit that changes its length their parent stations can shift (in the live test the end moved from 2600 to about 2636). Set `lockToStations: true` to use Station lock mode, which keeps them at the given parent stations. The result reports each alignment's `lockMode`.
- Widenings, transitions and curb returns are outside scope; add them in Civil 3D afterwards.
- Every row is created in one transaction; if one fails nothing is kept and the error names it.
