---
name: plan_section_sheet_fit
category: sections
description: Read-only sheet fit plan for cross sections; for each template layout, the view width and elevation height that fit its viewport, and which sample lines fit
requires_write: false
aliases: ["keresztszelvény nézetek laphoz illesztése", "melyik lapra férnek el a keresztszelvények", "section view sheet fit for A4 or A3 layouts", "keresztszelvény nézetmagasság és szélesség a lapmérethez"]
workflow_tags: ["drawing_view", "source_state"]
tested_civil_version: "2025"
validation_summary: "Offline + live (Civil 3D 2025): six layouts of one view template; 16 layouts of a second with two cover sheets skipped, group offsets and a 12 mm reserve giving a height whose 15 views fit A3 1:100 sheets. Multi-viewport layouts, imperial drawings and plot output unverified."
parameters:
  - name: sampleLineGroupHandle
    type: string
    required: true
    description: Hex handle of the sample line group whose sample lines are planned
  - name: templatePath
    type: string
    required: true
    description: Drawing template (DWT/DWG) whose paper space layouts are the candidate sheets; it is read, never changed
  - name: layoutNames
    type: string
    required: false
    description: Candidate layout names separated by "|"; empty plans every paper space layout of the template (default "")
  - name: sectionViewStyleName
    type: string
    required: true
    description: Section view style to plan with; its vertical exaggeration turns sheet height into elevation height
  - name: bandSetStyleName
    type: string
    required: false
    description: Section view band set style whose band heights are taken off the sheet height; empty plans without bands (default "")
  - name: referenceSourceHandle
    type: string
    required: true
    description: Handle of the sampled section source whose lowest and highest points must fit, such as the existing ground surface
  - name: clearance
    type: double
    required: false
    description: Distance kept below the lowest point, for example for utilities projected later, 0 or more (default 2)
  - name: topClearance
    type: double
    required: false
    description: Distance kept above the highest point, 0 or more (default 0.5)
  - name: heightStep
    type: double
    required: false
    description: The planned elevation height is rounded down to a multiple of this, greater than 0 (default 0.5)
  - name: reservedHeightMm
    type: double
    required: false
    description: Paper height in millimetres kept free in the viewport besides the graph and bands, for what Civil 3D places above the graph (view title, annotation), 0 or more (default 0)
  - name: widthStep
    type: double
    required: false
    description: The planned left and right view widths are rounded up to a multiple of this, greater than 0 (default 1)
  - name: startStation
    type: double
    required: false
    description: First station of the sample lines to plan; NaN means no lower bound (default NaN)
  - name: endStation
    type: double
    required: false
    description: Last station of the sample lines to plan; NaN means no upper bound (default NaN)
---

## Code Template

```csharp
var sampleLineGroupHandle = "SAMPLE_LINE_GROUP_HANDLE";
var templatePath = "";
var layoutNames = "";
var sectionViewStyleName = "";
var bandSetStyleName = "";
var referenceSourceHandle = "";
var clearance = 2.0;
var topClearance = 0.5;
var heightStep = 0.5;
var widthStep = 1.0;
var reservedHeightMm = 0.0;
var startStation = double.NaN;
var endStation = double.NaN;

var isHandle = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value)
    && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{1,16}$"));
if (!isHandle(sampleLineGroupHandle) || !isHandle(referenceSourceHandle))
    return new { success = false, error = "sampleLineGroupHandle and referenceSourceHandle must be hexadecimal handles." };
if (string.IsNullOrWhiteSpace(templatePath) || templatePath.Length > 1024 || templatePath.Any(char.IsControl))
    return new { success = false, error = "templatePath is required." };
if (layoutNames == null || layoutNames.Length > 4000 || layoutNames.Any(char.IsControl)
    || string.IsNullOrWhiteSpace(sectionViewStyleName) || bandSetStyleName == null)
    return new { success = false, error = "layoutNames must be \"|\"-separated names, and sectionViewStyleName is required." };
if (!double.IsFinite(clearance) || clearance < 0 || clearance > 1000 || !double.IsFinite(topClearance) || topClearance < 0 || topClearance > 1000
    || !double.IsFinite(heightStep) || heightStep <= 0 || heightStep > 100
    || !double.IsFinite(widthStep) || widthStep <= 0 || widthStep > 100
    || !double.IsFinite(reservedHeightMm) || reservedHeightMm < 0 || reservedHeightMm > 1000)
    return new { success = false, error = "clearance, topClearance and reservedHeightMm must be 0..1000, heightStep and widthStep greater than 0, at most 100." };
if (double.IsInfinity(startStation) || double.IsInfinity(endStation))
    return new { success = false, error = "startStation and endStation must be finite or NaN." };
if (!System.IO.File.Exists(templatePath))
    return new { success = false, error = "The template file does not exist.", templatePath };

ObjectId? ResolveId(string handle)
{
    try { return Database.GetObjectId(false, new Handle(System.Convert.ToInt64(handle, 16)), 0); }
    catch { return null; }
}

var groupId = ResolveId(sampleLineGroupHandle);
var group = groupId.HasValue ? Transaction.GetObject(groupId.Value, OpenMode.ForRead) as SampleLineGroup : null;
if (group == null || group.IsErased)
    return new { success = false, error = "sampleLineGroupHandle must identify an existing sample line group." };
var sourceId = ResolveId(referenceSourceHandle);
if (!sourceId.HasValue)
    return new { success = false, error = "referenceSourceHandle does not resolve." };

ObjectId LookupStyle(Func<ObjectId> lookup)
{
    try { return lookup(); }
    catch { return ObjectId.Null; }
}
var viewStyleId = LookupStyle(() => CivilDoc.Styles.SectionViewStyles[sectionViewStyleName]);
var bandSetId = bandSetStyleName == "" ? ObjectId.Null : LookupStyle(() => CivilDoc.Styles.SectionViewBandSetStyles[bandSetStyleName]);
if (viewStyleId.IsNull || (bandSetStyleName != "" && bandSetId.IsNull))
    return new { success = false, error = "The section view style or band set style does not exist.", sectionViewStyleName, bandSetStyleName };
var viewStyle = (Autodesk.Civil.DatabaseServices.Styles.SectionViewStyle)Transaction.GetObject(viewStyleId, OpenMode.ForRead);
var verticalExaggeration = viewStyle.GraphStyle.VerticalExaggeration;
if (!double.IsFinite(verticalExaggeration) || verticalExaggeration <= 0)
    return new { success = false, error = "The section view style has no usable vertical exaggeration." };

// Band heights are plotted sizes; with a metre drawing they read as paper metres.
var bandsPaperMm = 0.0;
var bands = new List<object>();
if (!bandSetId.IsNull)
{
    var bandSet = (Autodesk.Civil.DatabaseServices.Styles.SectionViewBandSetStyle)Transaction.GetObject(bandSetId, OpenMode.ForRead);
    foreach (var (location, items) in new[] { ("bottom", bandSet.GetBottomBandSetItems()), ("top", bandSet.GetTopBandSetItems()) })
        foreach (Autodesk.Civil.DatabaseServices.Styles.BandSetItem item in items)
        {
            var bandStyle = (Autodesk.Civil.DatabaseServices.Styles.BandStyle)Transaction.GetObject(item.BandStyleId, OpenMode.ForRead);
            bandsPaperMm += (bandStyle.BandHeight + item.Gap) * 1000.0;
            bands.Add(new { location, name = bandStyle.Name, heightMm = bandStyle.BandHeight * 1000.0, gapMm = item.Gap * 1000.0 });
        }
    bandsPaperMm = Math.Max(0, bandsPaperMm);
}

// Sample lines: total width along the line and the reference section's range.
var lines = new List<(string name, double station, double width, double low, double high, double left, double right)>();
var missingSections = new List<object>();
foreach (ObjectId id in group.GetSampleLineIds())
{
    if (!(Transaction.GetObject(id, OpenMode.ForRead) is SampleLine line) || line.IsErased) continue;
    if ((!double.IsNaN(startStation) && line.Station < startStation - 1e-6)
        || (!double.IsNaN(endStation) && line.Station > endStation + 1e-6)) continue;
    var width = 0.0;
    for (var k = 1; k < line.Vertices.Count; k++)
        width += line.Vertices[k - 1].Location.DistanceTo(line.Vertices[k].Location);
    var sectionId = line.GetSectionId(sourceId.Value);
    var section = sectionId.IsNull ? null : Transaction.GetObject(sectionId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Section;
    if (section == null || !double.IsFinite(section.MinmumElevation) || !double.IsFinite(section.MaximumElevation)
        || Math.Abs(section.MinmumElevation) > 1e6 || Math.Abs(section.MaximumElevation) > 1e6)
    {
        missingSections.Add(new { line.Name, line.Station });
        continue;
    }
    // Offsets of the sampled reference section: left is negative, right positive.
    var left = double.IsFinite(section.LeftOffset) ? Math.Max(0, -section.LeftOffset) : 0;
    var right = double.IsFinite(section.RightOffset) ? Math.Max(0, section.RightOffset) : 0;
    lines.Add((line.Name, line.Station, width, section.MinmumElevation, section.MaximumElevation, left, right));
}
if (lines.Count == 0)
    return new { success = false, error = "No sample line in the range has a section of the reference source.", missingSections };

// Template layouts: the first viewport of a layout is its paper space viewport; the largest other one shows the model.
var wanted = new HashSet<string>(layoutNames.Split('|').Select(n => n.Trim()).Where(n => n != ""), StringComparer.OrdinalIgnoreCase);
var sheets = new List<(string layout, double vpWidthMm, double vpHeightMm, double customScale)>();
var skipped = new List<object>();
var paperLayouts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
try
{
    using (var side = new Database(false, true))
    {
        side.ReadDwgFile(templatePath, Autodesk.AutoCAD.DatabaseServices.FileOpenMode.OpenForReadAndAllShare, true, null);
        using (var tr = side.TransactionManager.StartTransaction())
        {
            var layouts = (DBDictionary)tr.GetObject(side.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                if (layout.ModelType) continue;
                paperLayouts.Add(layout.LayoutName);
                if (wanted.Count > 0 && !wanted.Contains(layout.LayoutName)) continue;
                var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                var viewports = new List<Viewport>();
                foreach (ObjectId id in space)
                    if (tr.GetObject(id, OpenMode.ForRead) is Viewport viewport) viewports.Add(viewport);
                var modelViewport = viewports.Skip(1).OrderByDescending(v => v.Width * v.Height).FirstOrDefault();
                if (modelViewport == null || !(modelViewport.CustomScale > 0))
                {
                    skipped.Add(new { layout = layout.LayoutName, reason = "No scaled model viewport, for example a cover sheet." });
                    continue;
                }
                sheets.Add((layout.LayoutName, modelViewport.Width, modelViewport.Height, modelViewport.CustomScale));
            }
            tr.Commit();
        }
    }
}
catch (System.Exception ex)
{
    return new { success = false, error = "The template could not be read.", templatePath, detail = ex.Message };
}
var unknownLayouts = wanted.Where(n => !paperLayouts.Contains(n)).ToList();
if (sheets.Count == 0)
    return new { success = false, error = "No candidate layout with a scaled model viewport.", skipped, unknownLayouts, available = paperLayouts.ToList() };

var maxRelief = lines.Max(l => l.high - l.low);
var maxWidth = lines.Max(l => l.width);
// One offset range for the whole group: every view is as wide as the widest left plus the widest right side.
var leftWidth = Math.Ceiling(lines.Max(l => l.left) / widthStep - 1e-9) * widthStep;
var rightWidth = Math.Ceiling(lines.Max(l => l.right) / widthStep - 1e-9) * widthStep;
var groupWidth = leftWidth + rightWidth;
var plans = new List<(double customScale, double area, object plan, bool fitsAll)>();
foreach (var sheet in sheets)
{
    // CustomScale is paper millimetres per model unit (10 for 1:100 with metres).
    var modelWidth = sheet.vpWidthMm / sheet.customScale;
    var modelHeight = sheet.vpHeightMm / sheet.customScale;
    var bandsModel = bandsPaperMm / sheet.customScale;
    var graphHeight = modelHeight - bandsModel - reservedHeightMm / sheet.customScale;
    var height = graphHeight > 0 ? Math.Floor(graphHeight / verticalExaggeration / heightStep + 1e-9) * heightStep : 0;
    var checks = lines
        .Select(l => new { l.name, l.station, l.width, needed = (l.high - l.low) + clearance + topClearance })
        .Select(l => new { l.name, l.station, l.width, l.needed, wide = l.width > modelWidth + 1e-6, tall = l.needed > height + 1e-6 })
        .ToList();
    var notFitting = checks.Where(l => l.wide || l.tall).OrderBy(l => l.station)
        .Select(l => new { l.name, l.station, l.width, l.needed, reason = l.wide && l.tall ? "width and height" : l.wide ? "width" : "height" })
        .ToList();
    var fitsAll = height > 0 && notFitting.Count == 0;
    plans.Add((sheet.customScale, sheet.vpWidthMm * sheet.vpHeightMm, new {
        layout = sheet.layout,
        viewportMm = new { width = sheet.vpWidthMm, height = sheet.vpHeightMm },
        scale = "1:" + Math.Round(1000.0 / sheet.customScale, 3).ToString(System.Globalization.CultureInfo.InvariantCulture),
        modelViewport = new { width = modelWidth, height = modelHeight },
        bandsModelHeight = bandsModel,
        viewWidth = modelWidth,
        elevationHeight = height,
        fitsAll,
        fittingCount = lines.Count - notFitting.Count,
        notFittingCount = notFitting.Count,
        tooWideCount = checks.Count(l => l.wide),
        tooTallCount = checks.Count(l => l.tall),
        notFitting = notFitting.Take(10).ToList(),
        groupWidthFits = groupWidth > 0 && groupWidth <= modelWidth + 1e-6,
        widthParams = groupWidth > 0 && groupWidth <= modelWidth + 1e-6 ? new { leftWidth, rightWidth } : null,
        createParams = height > 0 ? new { layoutName = sheet.layout, elevationHeight = height } : null,
        shiftParams = height > 0 ? new { mode = "belowLowest", clearance, height, referenceSourceHandle } : null
    }, fitsAll && groupWidth <= modelWidth + 1e-6));
}
// The most detailed scale first, then the smallest sheet.
var ordered = plans.OrderByDescending(p => p.customScale).ThenBy(p => p.area).ToList();
var recommended = ordered.Where(p => p.fitsAll).Select(p => p.plan).FirstOrDefault();

return new {
    success = true,
    sampleLineGroup = new { name = group.Name, handle = group.Handle.ToString() },
    sectionViewStyle = new { name = viewStyle.Name, verticalExaggeration },
    bandSet = bandSetId.IsNull ? null : new { name = bandSetStyleName, totalHeightMm = bandsPaperMm, bands },
    clearance,
    topClearance,
    reservedHeightMm,
    sampleLines = new { count = lines.Count, maxWidth, maxRelief, maxNeeded = maxRelief + clearance + topClearance },
    groupOffsets = new { leftWidth, rightWidth, width = groupWidth, widthStep },
    missingSections,
    recommended,
    sheets = ordered.Select(p => p.plan).ToList(),
    skipped,
    unknownLayouts
};
```

## Usage Notes

- This is a read-only template: run it with `civil3d_query` after confirming the drawing identity. The template file is opened as a separate read-only database and is never changed.
- For each candidate layout it reads the viewport that shows the model (the largest one apart from the layout's own paper space viewport) and its scale, then works out the view width and the elevation height that fit: the viewport height in model units minus the band set's heights and `reservedHeightMm`, divided by the view style's vertical exaggeration, rounded down to `heightStep`.
- `reservedHeightMm` is paper height that Civil 3D keeps besides the graph and bands. In a live A3 1:100 test with a bottom-titled view style it placed the graph top 12 mm below the viewport top; without that reserve the lowest band left the viewport by 10 mm. The amount depends on the view style, so confirm it with the `sheetFit` result of `create_section_view_group`.
- `recommended` is the plan at the most detailed scale on which every line and the group width fit, and of those the smallest sheet; `createParams` give its `layoutName` and `elevationHeight` for `create_section_view_group`.
- A sample line fits a sheet when its width (along its vertices) fits the view width and its reference section's relief plus `clearance` below and `topClearance` above fits the elevation height. `tooWideCount` and `tooTallCount` count the misses, and `notFitting` lists the first 10 with the reason.
- Choosing between, say, A4 portrait, A4 landscape and A3 landscape is still an engineering decision: a wider sheet allows wider sample lines, a taller one more relief or utilities.
- `groupOffsets` is one offset range for the whole group: the widest left and the widest right side of the reference sections, each rounded up to `widthStep`. With these widths every view is equally wide, so a sheet takes the group only when `groupWidthFits`; then `widthParams` are the `leftWidth`/`rightWidth` for `create_section_view_group`. A sheet can fit every line by its own width and still miss `groupWidthFits` when the widest sides belong to different lines.
- Layouts without a scaled model viewport, such as cover sheets, are listed in `skipped` and never planned.
- `shiftParams` are the values for `shift_section_view_elevations` (`mode: "belowLowest"` with `clearance` and `height`) after the views exist. Create the views first with `create_section_view_group` using `createParams` and `widthParams`: the sheet layout is made from the views' size at creation, so the height must be set there and only the datum moved afterwards.
- The drawing's annotation scale must equal the layout's scale before the views are created; Civil 3D sizes the sheet frames from it.
- Sheets come from the template's viewport geometry and scale, not from layout or file names, so a renamed template or added sheet sizes need no change here; only `templatePath` and `layoutNames` follow them.
- Limits:
  - Each layout is planned with one model viewport, its largest; layouts that show several sections in several viewports are not planned separately.
  - The drawing is assumed to be in metres with millimetre paper: `CustomScale` is read as paper millimetres per metre and band heights as paper metres. Imperial drawings are untested.
  - The view title, axis labels and grid annotation outside the graph are not counted, so keep some `topClearance` and check the result in a plot preview.
- Office conventions such as the usual template, clearance and preferred styles belong to the caller's own instructions; this recipe only takes them as parameters.
