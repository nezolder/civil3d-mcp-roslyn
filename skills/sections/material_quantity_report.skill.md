---
name: material_quantity_report
category: sections
description: Read bounded station-based cut/fill/usable quantities from one explicitly selected existing Civil material list
requires_write: false
parameters:
  - name: groupHandle
    type: string
    required: true
    description: Hex handle of the sample-line group with existing computed quantities
  - name: materialListGuid
    type: string
    required: true
    description: Exact nonempty GUID selected with the included material-list inventory query
  - name: startStation
    type: double
    required: true
    description: Inclusive first station to report
  - name: endStation
    type: double
    required: true
    description: Inclusive last station to report
  - name: limit
    type: int
    required: false
    description: Maximum returned station rows from 1 to 1000 (default 200)
---

## Code Template

```csharp
var groupHandle = "SAMPLE_LINE_GROUP_HANDLE";
var materialListGuid = "MATERIAL_LIST_GUID";
var startStation = double.NaN;
var endStation = double.NaN;
var limit = 200;
var isHandle = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value)
    && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{1,16}$")
    && long.TryParse(value, System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0);
if (!isHandle(groupHandle) || !Guid.TryParse(materialListGuid, out var listGuid) || listGuid == Guid.Empty)
    return new { success = false, error = "Configure an existing groupHandle and nonempty materialListGuid." };
if (!Double.IsFinite(startStation) || !Double.IsFinite(endStation) || endStation < startStation || limit < 1 || limit > 1000)
    return new { success = false, error = "Station range must be finite/ordered and limit must be 1..1000." };

// Host access starts here.
ObjectId groupId;
try { groupId = Database.GetObjectId(false, new Handle(Convert.ToInt64(groupHandle, 16)), 0); }
catch { return new { success = false, error = "groupHandle does not resolve in this drawing." }; }
var group = Transaction.GetObject(groupId, OpenMode.ForRead) as SampleLineGroup;
if (group == null || group.IsErased)
    return new { success = false, error = "groupHandle must identify an existing SampleLineGroup." };
if (group.GetSampleLineIds().Count > 5000)
    return new { success = false, error = "The group exceeds this recipe's 5000-sample scan bound." };
QTOMaterialList selected = null;
foreach (QTOMaterialList list in group.MaterialLists)
    if (list.Guid == listGuid) { selected = list; break; }
if (selected == null) return new { success = false, error = "The selected material-list GUID is not in this group." };
var alignment = Transaction.GetObject(group.ParentAlignmentId, OpenMode.ForRead) as Alignment;
if (alignment == null || alignment.StationEquations.Count != 0)
    return new { success = false, error = "The parent alignment must exist and have no station equations in this first scope." };
var rows = new List<object>();
var total = 0;
var invalid = 0;
var sumCut = 0.0;
var sumFill = 0.0;
var sumUsable = 0.0;
using (var takeoff = group.GetTotalVolumeResultDataForMaterialList(listGuid))
{
    var sections = takeoff.GetResultsAlongSampleLines();
    if (sections.Length > 5000)
        return new { success = false, error = "The QTO result exceeds this recipe's 5000-record scan bound." };
    foreach (var section in sections.OrderBy(s => s.Station))
    {
        if (!Double.IsFinite(section.Station)) { invalid++; continue; }
        if (section.Station < startStation || section.Station > endStation) continue;
        total++;
        var volume = section.VolumeResult;
        var numbers = new[] {
            volume.CumulativeCutVolume, volume.CumulativeFillVolume, volume.CumulativeUsableVolume,
            volume.IncrementalCutVolume, volume.IncrementalFillVolume, volume.IncrementalUsableVolume
        };
        if (numbers.Any(v => !Double.IsFinite(v))) { invalid++; continue; }
        sumCut += volume.IncrementalCutVolume;
        sumFill += volume.IncrementalFillVolume;
        sumUsable += volume.IncrementalUsableVolume;
        if (rows.Count < limit)
            rows.Add(new {
                station = section.Station, sampleLineName = section.SampleLineName,
                sampleLineHandle = section.SampleLineId.IsNull ? null : section.SampleLineId.Handle.ToString(),
                cumulativeCut = volume.CumulativeCutVolume, cumulativeFill = volume.CumulativeFillVolume,
                cumulativeUsable = volume.CumulativeUsableVolume,
                incrementalCut = volume.IncrementalCutVolume, incrementalFill = volume.IncrementalFillVolume,
                incrementalUsable = volume.IncrementalUsableVolume
            });
    }
}
if (!Double.IsFinite(sumCut) || !Double.IsFinite(sumFill) || !Double.IsFinite(sumUsable))
    throw new InvalidOperationException("Quantity totals overflowed; no misleading result is returned.");
return new {
    success = true, groupHandle = group.Handle.ToString(), groupName = group.Name,
    alignmentName = alignment.Name, materialListName = selected.Name, materialListGuid = selected.Guid.ToString(),
    calculationMethod = group.MaterialLists.VolumeCalculationMethodType.ToString(),
    drawingLengthUnits = CivilDoc.Settings.DrawingSettings.UnitZoneSettings.DrawingUnits.ToString(),
    volumeUnits = "cubic drawing units", startStation, endStation,
    total, returned = rows.Count, truncated = total > rows.Count, limit, invalid,
    incrementalTotalsForSelectedEndingStations = new { cut = sumCut, fill = sumFill, usable = sumUsable },
    totalsComplete = invalid == 0, rows
};
```

## Material-list selection query

Run this small read-only query first to select the exact list GUID. It never chooses the first list implicitly.

```csharp
var groupHandle = "SAMPLE_LINE_GROUP_HANDLE";
if (!long.TryParse(groupHandle, System.Globalization.NumberStyles.HexNumber,
    System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
    return new { success = false, error = "Configure a positive hexadecimal groupHandle." };
ObjectId id;
try { id = Database.GetObjectId(false, new Handle(parsed), 0); }
catch { return new { success = false, error = "groupHandle does not resolve in this drawing." }; }
var group = Transaction.GetObject(id, OpenMode.ForRead) as SampleLineGroup;
if (group == null || group.IsErased) return new { success = false, error = "An existing SampleLineGroup is required." };
var lists = new List<object>();
foreach (QTOMaterialList list in group.MaterialLists)
{
    if (lists.Count >= 50) break;
    lists.Add(new { name = list.Name, guid = list.Guid.ToString(), materialCount = list.Count });
}
return new { success = true, groupHandle = group.Handle.ToString(), groupName = group.Name,
    total = group.MaterialLists.Count, returned = lists.Count,
    truncated = group.MaterialLists.Count > lists.Count, limit = 50, materialLists = lists };
```

## Usage Notes

- This reads existing native QTO results. It does not calculate quantities, change sampling, synchronize references, delete material lists, set cut/fill factors or create tables. Check that the selected sources and material mapping are current before accepting the values.
- Choose the group and material-list GUID explicitly. Results are in cubic drawing units; confirm the drawing's unit settings rather than relabeling feet as metres. A station equation is refused so station ordering is unambiguous.
- The entire bounded result set contributes to counters and selected incremental totals; `limit` only restricts returned rows. `invalid` makes incomplete totals visible. For an empty range, `total=0` is distinct from a proved zero-volume engineering result.
- Cumulative values remain Civil's cumulative results for the original material list. An incremental row is the interval ending at that station: the first selected row can include volume before `startStation`. The returned sum is explicitly a sum for selected ending stations, not an exact clipped range volume. It makes no interpolated boundary claim.
- Use narrower station windows if `truncated=true` and full export is needed, reconcile all rows without omission/duplication, and write XLSX/CSV outside Civil. Check drawing identity and `DBMOD` before/after the query; native getter behavior must be observed rather than assumed.
- Independently written from the 2025 public API, informed by the Apache-2.0 [Civil3DFactory QTO workflow](https://github.com/7pka111223-jpg/C3D/blob/6b821fe627728c45547f37fc9eb2503c6b5bb255/engine/Ops.Chain.cs#L886). No upstream implementation, file writer or framework is copied.
- Native cumulative and incremental usable-volume fields are returned unchanged. Do not assume they have interchangeable meanings or reconstruct one from cut/fill without validating the selected material configuration.
- **Proven in a scoped Civil 3D 2025 live test (2026-10-01):** an existing synthetic three-section AverageEndArea list returned the independently expected fill total of 2000 cubic drawing units. A two-row detail cap retained the total over all three selected ending stations; the uncapped read returned all rows and `DBMOD` stayed 0. Source freshness, exact clipping and other material configurations remain **Unverified**.
