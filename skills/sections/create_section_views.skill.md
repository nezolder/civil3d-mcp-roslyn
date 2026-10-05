---
name: create_section_views
category: sections
description: Create bounded individual section views from an existing local sample-line group with explicit styles and measured grid placement
requires_write: true
aliases: ["keresztszelvény nézetek létrehozása meglévő mintavonalakból", "create individual cross section views from a sample-line group", "keresztszelvény rajzi nézetek elrendezése"]
workflow_tags: ["drawing_view"]
tested_civil_version: "2025"
validation_summary: "Offline + live: three individual views, complete saved-state readback with empty bands. Populated bands, reopen and print quality unverified."
parameters:
  - name: groupHandle
    type: string
    required: true
    description: Hex handle of the existing local sample-line group
  - name: startStation
    type: double
    required: true
    description: Inclusive first selected station
  - name: endStation
    type: double
    required: true
    description: Inclusive last selected station
  - name: viewNamePrefix
    type: string
    required: true
    description: Prefix for unique view names suffixed by sample-line handle
  - name: styleName
    type: string
    required: true
    description: Existing section-view style name
  - name: bandSetStyleName
    type: string
    required: true
    description: Existing section-view band-set style name
  - name: originX
    type: double
    required: true
    description: Left edge of the resulting grid in model coordinates
  - name: originY
    type: double
    required: true
    description: Top edge of the resulting grid in model coordinates
  - name: columns
    type: int
    required: false
    description: Grid column count from 1 to 10 (default 3)
  - name: gapX
    type: double
    required: false
    description: Nonnegative horizontal clearance in drawing units (default 10)
  - name: gapY
    type: double
    required: false
    description: Nonnegative vertical clearance in drawing units (default 10)
  - name: viewLimit
    type: int
    required: false
    description: Maximum selected views from 1 to 50 (default 25); overflow refuses the entire write
---

## Code Template

```csharp
var groupHandle = "SAMPLE_LINE_GROUP_HANDLE";
var startStation = double.NaN;
var endStation = double.NaN;
var viewNamePrefix = "VIEW_NAME_PREFIX";
var styleName = "SECTION_VIEW_STYLE_NAME";
var bandSetStyleName = "SECTION_VIEW_BAND_SET_STYLE_NAME";
var originX = 0.0;
var originY = 0.0;
var columns = 3;
var gapX = 10.0;
var gapY = 10.0;
var viewLimit = 25;
var tolerance = 1e-6;

var isHandle = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value)
    && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{1,16}$")
    && long.TryParse(value, System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0);
var isName = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 200 && !value.Any(char.IsControl));
if (!isHandle(groupHandle)) return new { success = false, error = "Configure a positive hexadecimal groupHandle." };
if (!Double.IsFinite(startStation) || !Double.IsFinite(endStation) || endStation < startStation)
    return new { success = false, error = "Configure a finite, ordered station range." };
if (!isName(viewNamePrefix) || viewNamePrefix == "VIEW_NAME_PREFIX"
    || !isName(styleName) || styleName == "SECTION_VIEW_STYLE_NAME"
    || !isName(bandSetStyleName) || bandSetStyleName == "SECTION_VIEW_BAND_SET_STYLE_NAME")
    return new { success = false, error = "Configure explicit view-prefix and existing style names." };
if (!Double.IsFinite(originX) || !Double.IsFinite(originY) || !Double.IsFinite(gapX) || !Double.IsFinite(gapY)
    || gapX < 0 || gapY < 0 || columns < 1 || columns > 10 || viewLimit < 1 || viewLimit > 50)
    return new { success = false, error = "Grid coordinates/gaps must be finite, gaps nonnegative, columns 1..10 and viewLimit 1..50." };

// Host access starts here.
ObjectId groupId;
try { groupId = Database.GetObjectId(false, new Handle(Convert.ToInt64(groupHandle, 16)), 0); }
catch { return new { success = false, error = "groupHandle does not resolve in this drawing." }; }
var group = Transaction.GetObject(groupId, OpenMode.ForRead) as SampleLineGroup;
if (group == null || group.IsErased || group.IsReferenceObject)
    return new { success = false, error = "An existing local SampleLineGroup is required." };
var alignment = Transaction.GetObject(group.ParentAlignmentId, OpenMode.ForRead) as Alignment;
if (alignment == null || alignment.IsReferenceObject || alignment.StationEquations.Count != 0
    || startStation < alignment.StartingStation || endStation > alignment.EndingStation)
    return new { success = false, error = "The local parent alignment must contain this range and have no station equations." };
var ids = group.GetSampleLineIds();
if (ids.Count > 5000) return new { success = false, error = "The group exceeds this recipe's 5000-sample preflight bound." };
var selected = new List<SampleLine>();
foreach (ObjectId id in ids)
{
    var line = Transaction.GetObject(id, OpenMode.ForRead) as SampleLine;
    if (line == null || line.IsErased) continue;
    if (line.Station < startStation || line.Station > endStation) continue;
    if (!Double.IsFinite(line.Station) || line.IsReferenceObject || line.GetSectionIds().Count == 0)
        return new { success = false, error = "Every selected sample line must be local and have existing section data." };
    if (line.GetSectionViewIds().Count != 0)
        return new { success = false, error = "A selected sample line already has section views; this recipe does not duplicate or modify them." };
    selected.Add(line);
    if (selected.Count > viewLimit)
        return new { success = false, error = "The selection exceeds viewLimit; narrow the station range before writing." };
}
selected = selected.OrderBy(line => line.Station).ThenBy(line => line.Handle.ToString(), StringComparer.Ordinal).ToList();
if (selected.Count == 0) return new { success = false, error = "No sampled local lines match the selected range." };
ObjectId styleId;
ObjectId bandSetId;
try
{
    styleId = CivilDoc.Styles.SectionViewStyles[styleName];
    bandSetId = CivilDoc.Styles.SectionViewBandSetStyles[bandSetStyleName];
}
catch { return new { success = false, error = "The named section-view or band-set style is unavailable." }; }
if (styleId.IsNull || bandSetId.IsNull)
    return new { success = false, error = "Both existing styles must resolve to valid IDs." };

var views = new List<(SectionView view, SampleLine line, Extents3d bounds)>();
foreach (var line in selected)
{
    var name = viewNamePrefix + "-" + line.Handle.ToString();
    var id = SectionView.Create(name, line.ObjectId, new Point3d(originX, originY, 0));
    var view = Transaction.GetObject(id, OpenMode.ForWrite) as SectionView;
    if (view == null) throw new InvalidOperationException("Section-view creation returned no readable view.");
    view.StyleId = styleId;
    view.Bands.ImportBandSetStyle(bandSetId);
    var bounds = view.GeometricExtents;
    var width = bounds.MaxPoint.X - bounds.MinPoint.X;
    var height = bounds.MaxPoint.Y - bounds.MinPoint.Y;
    if (!Double.IsFinite(width) || !Double.IsFinite(height) || width <= tolerance || height <= tolerance)
        throw new InvalidOperationException("A view has no usable styled extents; grid placement was refused before commit.");
    views.Add((view, line, bounds));
}
var rowCount = (views.Count + columns - 1) / columns;
var columnWidths = new double[columns];
var rowHeights = new double[rowCount];
for (var i = 0; i < views.Count; i++)
{
    columnWidths[i % columns] = Math.Max(columnWidths[i % columns], views[i].bounds.MaxPoint.X - views[i].bounds.MinPoint.X);
    rowHeights[i / columns] = Math.Max(rowHeights[i / columns], views[i].bounds.MaxPoint.Y - views[i].bounds.MinPoint.Y);
}
var created = new List<object>();
for (var i = 0; i < views.Count; i++)
{
    var item = views[i];
    var column = i % columns;
    var row = i / columns;
    var targetLeft = originX + columnWidths.Take(column).Sum() + column * gapX;
    var targetTop = originY - rowHeights.Take(row).Sum() - row * gapY;
    if (!Double.IsFinite(targetLeft) || !Double.IsFinite(targetTop))
        throw new InvalidOperationException("Grid placement overflowed; no partial grid may be committed.");
    item.view.Location = item.view.Location + new Vector3d(
        targetLeft - item.bounds.MinPoint.X, targetTop - item.bounds.MaxPoint.Y, 0);
    var actual = item.view.GeometricExtents;
    if (item.view.SampleLineId != item.line.ObjectId || item.view.StyleId != styleId
        || Math.Abs(actual.MinPoint.X - targetLeft) > tolerance || Math.Abs(actual.MaxPoint.Y - targetTop) > tolerance)
        throw new InvalidOperationException("A section view failed its parent/style/placement postconditions: "
            + "handle=" + item.view.Handle.ToString() + ", requestedLeft=" + targetLeft
            + ", actualLeft=" + actual.MinPoint.X + ", requestedTop=" + targetTop
            + ", actualTop=" + actual.MaxPoint.Y + ".");
    created.Add(new {
        handle = item.view.Handle.ToString(), name = item.view.Name,
        sampleLineHandle = item.line.Handle.ToString(), station = item.line.Station,
        style = item.view.StyleName, row, column,
        bounds = new { minX = actual.MinPoint.X, minY = actual.MinPoint.Y, maxX = actual.MaxPoint.X, maxY = actual.MaxPoint.Y }
    });
}
return new {
    success = true, groupHandle = group.Handle.ToString(), groupName = group.Name,
    selected = selected.Count, created = created.Count, columns, rowCount, gapX, gapY,
    requestedBandSet = bandSetStyleName, units = CivilDoc.Settings.DrawingSettings.UnitZoneSettings.DrawingUnits.ToString(),
    views = created
};
```

## Usage Notes

- Select one exact group with `section_inventory`, then verify its parent alignment, sampled sources, intended station range, current section data and the two named styles. This creates only individual views; it does not create/resample lines, rebuild corridors, modify sources or create layouts/sheets.
- The entire selected set is preflighted before the first creation. Existing views on any selected line refuse the whole operation. Names include the unique sample-line handle. A native duplicate-name failure also aborts the host transaction.
- Grid positions use the actual styled `GeometricExtents` of the newly created views, plus explicit drawing-unit gaps. Movement uses the native `Graph.Location` setter: a Civil 3D 2025 live probe found stale extents immediately after `TransformBy`. There is no fixed cell size or assumed plot scale. Unavailable extents refuse the transaction; existing unrelated drawing content is not an obstacle map, so the caller must select a clear insertion area.
- Verify the full `Database.Filename` and `FingerprintGuid` using `civil3d_query`, require `DBMOD=0`, create an unchanged timestamped filesystem backup and execute once with matching `expectedDrawing` and `saveDrawing: true`. The host owns commit/rollback/save. On timeout reconcile state; do not retry a creation blindly.
- Independently reread each returned view handle, its parent, name, style, bounds and band contents; check no overlaps within the resulting grid, compare the complete selected set and confirm `DBMOD=0`. Visual review must confirm clipping, bands and legibility; bounds alone do not prove plotting quality.
- Independent implementation informed by the MIT [Mmpasta recipe](https://github.com/Mmpasta00/civil3d-mcp/blob/618fe787ca414f551e359eac02cc9fe6c6af61ad/skills/sections/create_section_views.skill.md) and [new-acad section workflow](https://github.com/Jjo37/new-acad/blob/2085394be8dc15a885d97bc8572efc5745441290/plugin/AcBridge-v24/src/SectionCommands.cs). No upstream code body or fixed-tool framework is copied.
- **Proven in a scoped Civil 3D 2025 live test (2026-10-01):** three existing sampled lines produced three styled views in two columns. The complete independent saved-state readback matched all handles, names, parents, styles, empty band contents and bounds; no views overlapped and `DBMOD` remained 0. Failed placement probes rolled back and were independently counted as zero views before correction. Populated bands, saved-file reopen and visual/print quality remain **Unverified**.
