---
name: create_section_view_group
category: sections
description: Create one native section view group for a sample line group, in draft grid or template-based production placement, with view style, band set, band data sources and station-based view names
requires_write: true
aliases: ["keresztszelvény nézetcsoport létrehozása mintavonalakból", "create native section view group with production sheet template", "keresztszelvények lapra rendezése sablonból", "section view group with band sources"]
workflow_tags: ["drawing_view"]
tested_civil_version: "2025"
validation_summary: "Offline + live (Civil 3D 2025, disposable copies): draft grid of 17 views; template placement of 15 views on 15 A3 1:100 sheets with group plot style, sheet height and sheetFit check, saved and read back; cover sheet and scale mismatch rejected. Plot output unverified."
parameters:
  - name: sampleLineGroupHandle
    type: string
    required: true
    description: Hex handle of the existing sample line group
  - name: insertionX
    type: double
    required: true
    description: Model-space X of the group insertion point
  - name: insertionY
    type: double
    required: true
    description: Model-space Y of the group insertion point
  - name: startStation
    type: double
    required: false
    description: First station; NaN uses the first sample line (default NaN)
  - name: endStation
    type: double
    required: false
    description: Last station; NaN uses the last sample line (default NaN)
  - name: leftWidth
    type: double
    required: false
    description: View width left of the alignment, greater than 0; NaN keeps Civil 3D's automatic offset range (default NaN)
  - name: rightWidth
    type: double
    required: false
    description: View width right of the alignment, greater than 0; NaN keeps the automatic range (default NaN)
  - name: elevationHeight
    type: double
    required: false
    description: User-specified view height in elevation units, greater than 0, for example the planned height that fits the sheet; NaN keeps Civil 3D's automatic elevation range (default NaN)
  - name: sectionViewStyleName
    type: string
    required: false
    description: Existing section view style; empty keeps the default (default "")
  - name: bandSetStyleName
    type: string
    required: false
    description: Existing section view band set style to import into every view; empty keeps the default (default "")
  - name: bandSources
    type: array
    required: false
    description: (bandIndex, section1SourceHandle, section2SourceHandle, showLabels) rows for bottom bands after the band set import; an empty handle leaves that source unchanged (default none)
  - name: templatePath
    type: string
    required: false
    description: Drawing template for production placement; empty uses draft placement in a model-space grid (default "")
  - name: layoutName
    type: string
    required: false
    description: Layout in the template used for production placement (default "")
  - name: groupPlotStyleName
    type: string
    required: false
    description: Existing group plot style applied to the new group; empty keeps the default (default "")
  - name: viewNamePrefix
    type: string
    required: false
    description: Rename views to prefix plus sample line station; empty keeps Civil 3D's names (default "")
  - name: nameDecimals
    type: int
    required: false
    description: Station decimals in view names, 0 to 4 (default 2)
  - name: nameIntegerDigits
    type: int
    required: false
    description: Minimum zero-padded integer digits of the station in view names, 0 to 6 (default 3)
---

## Code Template

```csharp
var sampleLineGroupHandle = "SAMPLE_LINE_GROUP_HANDLE";
var insertionX = double.NaN;
var insertionY = double.NaN;
var startStation = double.NaN;
var endStation = double.NaN;
var leftWidth = double.NaN;
var rightWidth = double.NaN;
var elevationHeight = double.NaN;
var sectionViewStyleName = "";
var bandSetStyleName = "";
var bandSources = new (int bandIndex, string section1SourceHandle, string section2SourceHandle, bool showLabels)[] { };
var templatePath = "";
var layoutName = "";
var groupPlotStyleName = "";
var viewNamePrefix = "";
var nameDecimals = 2;
var nameIntegerDigits = 3;

var isHandle = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value)
    && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{1,16}$"));
var isOptionalName = new Func<string, bool>(value =>
    value != null && (value == "" || (value == value.Trim() && value.Length <= 255 && !value.Any(char.IsControl))));
if (!isHandle(sampleLineGroupHandle))
    return new { success = false, error = "sampleLineGroupHandle must be a hexadecimal handle." };
if (!double.IsFinite(insertionX) || !double.IsFinite(insertionY))
    return new { success = false, error = "insertionX and insertionY are required." };
if (double.IsInfinity(startStation) || double.IsInfinity(endStation))
    return new { success = false, error = "startStation and endStation must be finite or NaN." };
if (double.IsNaN(leftWidth) != double.IsNaN(rightWidth)
    || (!double.IsNaN(leftWidth) && (!double.IsFinite(leftWidth) || !double.IsFinite(rightWidth)
        || leftWidth <= 0 || rightWidth <= 0 || leftWidth > 1000 || rightWidth > 1000)))
    return new { success = false, error = "Give both leftWidth and rightWidth (greater than 0, at most 1000) or neither." };
if (!double.IsNaN(elevationHeight) && (!double.IsFinite(elevationHeight) || elevationHeight <= 0 || elevationHeight > 1000))
    return new { success = false, error = "elevationHeight must be NaN or greater than 0, at most 1000." };
if (!isOptionalName(sectionViewStyleName) || !isOptionalName(bandSetStyleName) || !isOptionalName(groupPlotStyleName)
    || !isOptionalName(layoutName) || templatePath == null || templatePath.Length > 1024 || templatePath.Any(char.IsControl))
    return new { success = false, error = "Style, layout and template names must be empty or trimmed names." };
if ((templatePath == "") != (layoutName == ""))
    return new { success = false, error = "Give templatePath and layoutName together for production placement, or neither for draft placement." };
if (viewNamePrefix == null || viewNamePrefix.Length > 100 || viewNamePrefix.Any(char.IsControl)
    || nameDecimals < 0 || nameDecimals > 4 || nameIntegerDigits < 0 || nameIntegerDigits > 6)
    return new { success = false, error = "viewNamePrefix must be at most 100 characters, nameDecimals 0..4 and nameIntegerDigits 0..6." };
if (bandSources == null || bandSources.Length > 20
    || bandSources.Any(b => b.bandIndex < 0 || b.bandIndex > 19
        || b.section1SourceHandle == null || b.section2SourceHandle == null
        || (b.section1SourceHandle != "" && !isHandle(b.section1SourceHandle))
        || (b.section2SourceHandle != "" && !isHandle(b.section2SourceHandle)))
    || bandSources.Select(b => b.bandIndex).Distinct().Count() != bandSources.Length)
    return new { success = false, error = "bandSources needs distinct band indexes 0..19 with empty or hexadecimal source handles." };

ObjectId? ResolveId(string handle)
{
    try { return Database.GetObjectId(false, new Handle(System.Convert.ToInt64(handle, 16)), 0); }
    catch { return null; }
}

var groupId = ResolveId(sampleLineGroupHandle);
var group = groupId.HasValue ? Transaction.GetObject(groupId.Value, OpenMode.ForRead) as SampleLineGroup : null;
if (group == null || group.IsErased || group.IsReferenceObject)
    return new { success = false, error = "sampleLineGroupHandle must identify an existing local sample line group." };

var lines = new List<SampleLine>();
foreach (ObjectId id in group.GetSampleLineIds())
    if (Transaction.GetObject(id, OpenMode.ForRead) is SampleLine line && !line.IsErased) lines.Add(line);
if (lines.Count == 0)
    return new { success = false, error = "The sample line group has no sample lines." };
var firstStation = double.IsNaN(startStation) ? lines.Min(l => l.Station) : startStation;
var lastStation = double.IsNaN(endStation) ? lines.Max(l => l.Station) : endStation;
var selected = lines.Where(l => l.Station >= firstStation - 1e-6 && l.Station <= lastStation + 1e-6)
    .OrderBy(l => l.Station).ToList();
if (lastStation < firstStation || selected.Count == 0 || selected.Count > 1000)
    return new { success = false, error = "The station range must select 1 to 1000 sample lines.", selected = selected.Count };

ObjectId LookupStyle(Func<ObjectId> lookup)
{
    try { return lookup(); }
    catch { return ObjectId.Null; }
}
var viewStyleId = sectionViewStyleName == "" ? ObjectId.Null : LookupStyle(() => CivilDoc.Styles.SectionViewStyles[sectionViewStyleName]);
var bandSetId = bandSetStyleName == "" ? ObjectId.Null : LookupStyle(() => CivilDoc.Styles.SectionViewBandSetStyles[bandSetStyleName]);
var plotStyleId = groupPlotStyleName == "" ? ObjectId.Null : LookupStyle(() => CivilDoc.Styles.GroupPlotStyles[groupPlotStyleName]);
if ((sectionViewStyleName != "" && viewStyleId.IsNull) || (bandSetStyleName != "" && bandSetId.IsNull)
    || (groupPlotStyleName != "" && plotStyleId.IsNull))
    return new { success = false, error = "A named section view, band set or group plot style does not exist.", sectionViewStyleName, bandSetStyleName, groupPlotStyleName };

// GetSectionSources needs the group open for write; on a read-open group
// Civil 3D 2025 ends with a fatal eNotOpenForWrite. Nothing is changed yet.
group.UpgradeOpen();
// Band sources must be sampled sources of this group.
var sampledSourceIds = new HashSet<ObjectId>();
foreach (SectionSource source in group.GetSectionSources())
    if (source.IsSampled) sampledSourceIds.Add(source.SourceId);
// Null means unchanged; an unknown or unsampled source is rejected.
(bool ok, ObjectId id) BandSource(string handle)
{
    if (handle == "") return (true, ObjectId.Null);
    var id = ResolveId(handle);
    return id.HasValue && sampledSourceIds.Contains(id.Value) ? (true, id.Value) : (false, ObjectId.Null);
}
var bandPlan = new List<(int bandIndex, ObjectId source1, ObjectId source2, bool showLabels)>();
foreach (var band in bandSources)
{
    var source1 = BandSource(band.section1SourceHandle);
    var source2 = BandSource(band.section2SourceHandle);
    if (!source1.ok || !source2.ok)
        return new { success = false, error = "Every band source handle must be a sampled section source of this sample line group.", band.bandIndex };
    bandPlan.Add((band.bandIndex, source1.id, source2.id, band.showLabels));
}

var placement = new SectionViewGroupCreationPlacementOptions();
(double widthMm, double heightMm, double customScale)? sheetViewport = null;
if (templatePath != "")
{
    string[] layouts;
    try { layouts = placement.GetAvailableLayoutNames(templatePath); }
    catch (System.Exception ex) { return new { success = false, error = "The template could not be read.", templatePath, detail = ex.Message }; }
    if (layouts == null || !layouts.Contains(layoutName))
        return new { success = false, error = "The layout does not exist in the template.", layoutName, available = layouts };
    // The views fill the layout's model viewport; a cover sheet without one cannot hold them.
    try
    {
        using (var side = new Database(false, true))
        {
            side.ReadDwgFile(templatePath, Autodesk.AutoCAD.DatabaseServices.FileOpenMode.OpenForReadAndAllShare, true, null);
            using (var tr = side.TransactionManager.StartTransaction())
            {
                var layoutDict = (DBDictionary)tr.GetObject(side.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layoutDict)
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (layout.ModelType || layout.LayoutName != layoutName) continue;
                    var viewports = new List<Viewport>();
                    foreach (ObjectId id in (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead))
                        if (tr.GetObject(id, OpenMode.ForRead) is Viewport viewport) viewports.Add(viewport);
                    var model = viewports.Skip(1).OrderByDescending(v => v.Width * v.Height).FirstOrDefault();
                    if (model != null && model.CustomScale > 0)
                        sheetViewport = (model.Width, model.Height, model.CustomScale);
                }
                tr.Commit();
            }
        }
    }
    catch (System.Exception ex) { return new { success = false, error = "The template could not be read.", templatePath, detail = ex.Message }; }
    if (sheetViewport == null)
        return new { success = false, error = "The layout has no scaled model viewport, for example a cover sheet; choose a section sheet layout.", layoutName };
    // Civil 3D sizes the model-space sheets from the paper size and the current annotation scale,
    // not from the viewport scale; a mismatch gives sheets that do not match the layout.
    var annotationScale = Database.Cannoscale;
    var annotationPaperPerUnit = annotationScale.DrawingUnits > 0 ? annotationScale.PaperUnits / annotationScale.DrawingUnits : double.NaN;
    if (!(Math.Abs(annotationPaperPerUnit - sheetViewport.Value.customScale) <= 1e-6 * sheetViewport.Value.customScale))
        return new { success = false, error = "The drawing's annotation scale differs from the layout's viewport scale; set the annotation scale to the layout scale first.",
            annotationScale = annotationScale.Name, layoutScale = "1:" + Math.Round(1000.0 / sheetViewport.Value.customScale, 3).ToString(System.Globalization.CultureInfo.InvariantCulture) };
    var sheetModelWidth = sheetViewport.Value.widthMm / sheetViewport.Value.customScale;
    if (!double.IsNaN(leftWidth) && leftWidth + rightWidth > sheetModelWidth + 1e-6)
        return new { success = false, error = "leftWidth plus rightWidth is wider than the layout's model viewport.", leftWidth, rightWidth, sheetModelWidth };
    placement.UseProductionPlacement(templatePath, layoutName);
}
else
{
    placement.UseDraftPlacement();
}

string FormatStation(double station)
{
    var text = Math.Abs(station).ToString("F" + nameDecimals, System.Globalization.CultureInfo.InvariantCulture);
    var dot = text.IndexOf('.');
    var integerPart = dot < 0 ? text : text.Substring(0, dot);
    text = integerPart.PadLeft(nameIntegerDigits, '0') + (dot < 0 ? "" : text.Substring(dot));
    return (station < 0 ? "-" : "") + text;
}
var plannedNames = new Dictionary<ObjectId, string>();
if (viewNamePrefix != "")
{
    var otherNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in lines)
        foreach (ObjectId viewId in line.GetSectionViewIds())
            if (Transaction.GetObject(viewId, OpenMode.ForRead) is SectionView view) otherNames.Add(view.Name);
    foreach (var line in selected) plannedNames[line.ObjectId] = viewNamePrefix + FormatStation(line.Station);
    var clashes = plannedNames.Values.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1 || otherNames.Contains(g.Key)).Select(g => g.Key).ToList();
    if (clashes.Count > 0)
        return new { success = false, error = "View names would collide; raise nameDecimals or change the prefix.", names = clashes };
}

// Model-space sheet frames that exist before; the new group's frames are the others.
var modelSpace = (BlockTableRecord)Transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(Database), OpenMode.ForRead);
var earlierSheets = new HashSet<ObjectId>();
foreach (ObjectId id in modelSpace)
    if (id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(Autodesk.Civil.DatabaseServices.Sheet)))) earlierSheets.Add(id);

// One native group: group-level editing and sheet placement need it.
// Any failure below throws, so nothing is kept.
SectionViewGroup viewGroup;
using (var range = new SectionViewGroupCreationRangeOptions(group.ObjectId))
{
    if (!double.IsNaN(leftWidth))
    {
        range.SetOffsetRange(-leftWidth, rightWidth);
        range.UseUserSpecifiedOffset = true;
    }
    // The sheet layout is made from the views' size at creation; a later height change does not re-lay it out.
    if (!double.IsNaN(elevationHeight))
    {
        range.UseUserSpecifiedElevation = true;
        range.Elevation = elevationHeight;
        range.ElevationRangeType = SectionViewElevationRangeType.LowestElevation;
    }
    viewGroup = group.SectionViewGroups.Add(new Point3d(insertionX, insertionY, 0), firstStation, lastStation, range, placement);
}
if (viewGroup == null || viewGroup.IsIndividual)
    throw new InvalidOperationException("Civil 3D did not create a native section view group.");
if (!plotStyleId.IsNull)
{
    viewGroup.PlotStyleId = plotStyleId;
    viewGroup.UpdateLayout();
}

var lineById = selected.ToDictionary(l => l.ObjectId);
var views = new List<(SectionView view, SampleLine line)>();
foreach (ObjectId viewId in viewGroup.GetSectionViewIds())
{
    var view = (SectionView)Transaction.GetObject(viewId, OpenMode.ForWrite);
    if (!lineById.TryGetValue(view.SampleLineId, out var line))
        throw new InvalidOperationException($"View '{view.Name}' belongs to a sample line outside the selected range.");
    views.Add((view, line));
}
if (views.Count != selected.Count || views.Select(v => v.line.ObjectId).Distinct().Count() != selected.Count)
    throw new InvalidOperationException($"Expected one view per selected sample line ({selected.Count}), got {views.Count}.");

foreach (var (view, line) in views)
{
    if (!viewStyleId.IsNull) view.StyleId = viewStyleId;
    if (!bandSetId.IsNull) view.Bands.ImportBandSetStyle(bandSetId);
    if (bandPlan.Count > 0)
    {
        var items = view.Bands.GetBottomBandItems();
        var itemList = items.Cast<SectionViewBandItem>().ToList();
        foreach (var band in bandPlan)
        {
            if (band.bandIndex >= itemList.Count)
                throw new InvalidOperationException($"View '{view.Name}' has {itemList.Count} bottom bands; band {band.bandIndex} does not exist.");
            var item = itemList[band.bandIndex];
            if (!band.source1.IsNull) item.Section1Id = line.GetSectionId(band.source1);
            if (!band.source2.IsNull) item.Section2Id = line.GetSectionId(band.source2);
            item.ShowLabels = band.showLabels;
        }
        view.Bands.SetBottomBandItems(items);
    }
}

// Rename through unique temporary names so swaps cannot collide.
if (plannedNames.Count > 0)
{
    foreach (var (view, line) in views) view.Name = "__tmp_" + view.Handle.ToString();
    foreach (var (view, line) in views) view.Name = plannedNames[line.ObjectId];
}

string SourceName(ObjectId sectionId) =>
    sectionId.IsNull ? null : (Transaction.GetObject(sectionId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Section)?.SourceName;
var ordered = views.OrderBy(v => v.line.Station).ToList();
var firstBands = new List<object>();
var bandIndex = 0;
foreach (SectionViewBandItem item in ordered[0].view.Bands.GetBottomBandItems())
{
    firstBands.Add(new {
        index = bandIndex++,
        type = item.BandType.ToString(),
        section1 = SourceName(item.Section1Id),
        section2 = SourceName(item.Section2Id),
        item.ShowLabels
    });
}
var steps = ordered.Zip(ordered.Skip(1), (a, b) => b.view.Location.X - a.view.Location.X).ToList();

// Sheet fit: each view's graph plus its bottom bands must lie inside the model viewport of a new sheet frame.
// The viewport is taken as centred on the frame, as in a template with equal margins.
object sheetFit = null;
if (sheetViewport != null)
{
    var frames = new List<Extents3d>();
    foreach (ObjectId id in modelSpace)
        if (!earlierSheets.Contains(id) && Transaction.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Sheet sheet && !sheet.IsErased)
            frames.Add(sheet.GeometricExtents);
    var bandsModel = 0.0;
    if (!bandSetId.IsNull)
    {
        var bandSet = (Autodesk.Civil.DatabaseServices.Styles.SectionViewBandSetStyle)Transaction.GetObject(bandSetId, OpenMode.ForRead);
        foreach (Autodesk.Civil.DatabaseServices.Styles.BandSetItem item in bandSet.GetBottomBandSetItems())
            bandsModel += (((Autodesk.Civil.DatabaseServices.Styles.BandStyle)Transaction.GetObject(item.BandStyleId, OpenMode.ForRead)).BandHeight + item.Gap) * 1000.0 / sheetViewport.Value.customScale;
    }
    var vpWidth = sheetViewport.Value.widthMm / sheetViewport.Value.customScale;
    var vpHeight = sheetViewport.Value.heightMm / sheetViewport.Value.customScale;
    var misses = new List<object>();
    var perFrame = new int[frames.Count];
    foreach (var (view, line) in ordered)
    {
        // The graph box from the view's own origin, offsets and elevations; extents are not current inside the transaction.
        var exaggeration = ((Autodesk.Civil.DatabaseServices.Styles.SectionViewStyle)Transaction.GetObject(view.StyleId, OpenMode.ForRead)).GraphStyle.VerticalExaggeration;
        var box = new Extents3d(
            new Point3d(view.Location.X + view.OffsetLeft, view.Location.Y, 0),
            new Point3d(view.Location.X + view.OffsetRight, view.Location.Y + (view.ElevationMax - view.ElevationMin) * exaggeration, 0));
        var bottom = box.MinPoint.Y - bandsModel;
        var index = frames.FindIndex(fr => box.MinPoint.X >= fr.MinPoint.X - 1e-6 && box.MaxPoint.X <= fr.MaxPoint.X + 1e-6
            && bottom >= fr.MinPoint.Y - 1e-6 && box.MaxPoint.Y <= fr.MaxPoint.Y + 1e-6);
        if (index < 0) { misses.Add(new { name = view.Name, reason = "not inside one sheet frame" }); continue; }
        perFrame[index]++;
        var f = frames[index];
        var marginX = (f.MaxPoint.X - f.MinPoint.X - vpWidth) / 2;
        var marginY = (f.MaxPoint.Y - f.MinPoint.Y - vpHeight) / 2;
        var overflow = new {
            left = Math.Max(0, f.MinPoint.X + marginX - box.MinPoint.X),
            right = Math.Max(0, box.MaxPoint.X - (f.MaxPoint.X - marginX)),
            bottom = Math.Max(0, f.MinPoint.Y + marginY - bottom),
            top = Math.Max(0, box.MaxPoint.Y - (f.MaxPoint.Y - marginY))
        };
        if (overflow.left > 1e-3 || overflow.right > 1e-3 || overflow.bottom > 1e-3 || overflow.top > 1e-3)
            misses.Add(new { name = view.Name, reason = "outside the sheet viewport", overflow });
    }
    sheetFit = new {
        sheetCount = frames.Count,
        viewsPerSheet = perFrame.Length == 0 ? null : new { min = perFrame.Min(), max = perFrame.Max() },
        bandsModelHeight = bandsModel,
        fitsAll = frames.Count > 0 && misses.Count == 0,
        notFittingCount = misses.Count,
        notFitting = misses.Take(10).ToList()
    };
}

return new {
    success = true,
    sampleLineGroup = new { name = group.Name, handle = group.Handle.ToString() },
    viewGroup = new {
        name = viewGroup.Name,
        isIndividual = viewGroup.IsIndividual,
        templatePath = viewGroup.TemplateFilePath,
        layoutName = viewGroup.LayoutName,
        viewCount = views.Count
    },
    sheetViewport = sheetViewport == null ? null : new {
        widthMm = sheetViewport.Value.widthMm,
        heightMm = sheetViewport.Value.heightMm,
        scale = "1:" + Math.Round(1000.0 / sheetViewport.Value.customScale, 3).ToString(System.Globalization.CultureInfo.InvariantCulture),
        modelWidth = sheetViewport.Value.widthMm / sheetViewport.Value.customScale,
        modelHeight = sheetViewport.Value.heightMm / sheetViewport.Value.customScale
    },
    sheetFit,
    stationRange = new { first = firstStation, last = lastStation },
    leftToRightByStation = steps.All(step => step > 0),
    views = ordered.Select(v => new {
        name = v.view.Name,
        handle = v.view.Handle.ToString(),
        sampleLine = v.line.Name,
        station = v.line.Station,
        x = v.view.Location.X,
        y = v.view.Location.Y,
        offsetLeft = v.view.OffsetLeft,
        offsetRight = v.view.OffsetRight,
        elevationMin = v.view.ElevationMin,
        elevationMax = v.view.ElevationMax,
        style = v.view.StyleName
    }).ToList(),
    firstViewBottomBands = firstBands
};
```

## Usage Notes

- This is a write-capable template: confirm the full drawing identity first and run it with `civil3d_execute`; use `saveDrawing: true` only for an approved save.
- It creates one native section view group, never individual views. Group-level property and band editing in Civil 3D expects such a group; batch-editing individually created views has crashed Civil 3D 2025 in practice.
- With `templatePath` and `layoutName` the views use production placement from that template layout (for example an A4 landscape sheet layout); without them they go into a draft grid. The layout must exist in the template and have a scaled model viewport; a layout without one, such as a cover sheet, is rejected before anything is created, and so are `leftWidth` plus `rightWidth` wider than that viewport in model units. `sheetViewport` reports the viewport used. No paper space layouts are created. Sheet fit depends on the layout's viewport, scale, view style and bands together, so check the result visually or in a plot preview.
- Template placement draws one model-space sheet frame per sheet. Civil 3D sizes these frames from the layout's paper size and the drawing's current annotation scale, not from the viewport scale: with a 1:500 annotation scale an A3 1:100 layout gave 210 x 148.5 m frames. The recipe therefore refuses a layout whose viewport scale differs from the annotation scale; set the annotation scale first.
- Give `elevationHeight` (the planned height from `plan_section_sheet_fit`) for sheets. Civil 3D distributes the views over the sheets once, from their size at creation; changing the height afterwards (with `shift_section_view_elevations` or `UpdateLayout`) does not move views to other sheets, so tall views end up overlapping. After creation, `shift_section_view_elevations` in `belowLowest` mode with the same height sets each view's datum without changing its size.
- `sheetFit` checks each view's graph and its bottom bands against the model viewport inside the new sheet frames, taking the viewport as centred on the frame (equal margins). It reports the sheet count, views per sheet and up to 10 views that leave the viewport with their overflow per side. The view title and annotation outside the graph are not in this box; in a live A3 1:100 test Civil 3D kept the graph top 12 mm below the viewport top, so plan with `reservedHeightMm` and still check a plot preview.
- `groupPlotStyleName` applies an existing group plot style and re-lays out the new group, for example one with one view per sheet and plotting by columns so stations run left to right. `leftToRightByStation` reports whether view X positions increase with station.
- `leftWidth`/`rightWidth` set the view offset range (left becomes offset `-leftWidth`); this is the view range only, not the sample line width.
- `bandSources` rewires bottom bands after the band set import: give each band's section sources by section source handle (for example the existing ground surface) and whether its labels show. Hide design bands with `showLabels: false` until a design section exists, then point them to the design source and show them. `firstViewBottomBands` shows the result for the first view.
- With `viewNamePrefix` views are renamed to the prefix plus their sample line's actual station, for example `KSZ_GK_014.01`, after checking that no name collides.
- To replace an older group, check the new one first, then remove the old group as a whole with `SectionViewGroups.Remove`; erasing its views one by one has crashed Civil 3D. Shift view elevations with `shift_section_view_elevations`.
