---
name: shift_section_view_elevations
category: sections
description: Move the elevation window of the section views in one section view group, by a fixed amount or to a clearance below a section's lowest point, keeping each view's height or setting a sheet height
requires_write: true
aliases: ["keresztszelvény nézetek alapszintjének eltolása", "shift section view elevation range keeping view height", "keresztszelvény magassági ablak lejjebb tolása", "section view datum below lowest ground", "keresztszelvény nézetmagasság laphoz igazítása", "fit section view height to sheet below lowest ground"]
workflow_tags: ["drawing_view"]
tested_civil_version: "2025"
validation_summary: "Offline: run-by-name binding and syntax check. Live shift, below-lowest mode and height preservation in Civil 3D 2025 are not yet recorded."
parameters:
  - name: sampleLineGroupHandle
    type: string
    required: true
    description: Hex handle of the sample line group that owns the section view group
  - name: sectionViewGroupName
    type: string
    required: false
    description: Name of the section view group; empty requires exactly one group (default "")
  - name: mode
    type: string
    required: false
    description: shift moves both limits by delta; belowLowest puts the lower limit clearance below the lowest point of the reference section (default "shift")
  - name: delta
    type: double
    required: false
    description: Shift mode amount added to both limits, negative moves the window down (default 0)
  - name: clearance
    type: double
    required: false
    description: belowLowest mode distance of the lower limit below the lowest point, 0 or more (default 0)
  - name: referenceSourceHandle
    type: string
    required: false
    description: belowLowest mode handle of the sampled section source whose lowest point is used, such as the existing ground surface (default "")
  - name: height
    type: double
    required: false
    description: belowLowest mode view height, for example the height that fits the chosen sheet; 0 keeps each view's own height (default 0)
  - name: startStation
    type: double
    required: false
    description: First station of the views to change; NaN means no lower bound (default NaN)
  - name: endStation
    type: double
    required: false
    description: Last station of the views to change; NaN means no upper bound (default NaN)
---

## Code Template

```csharp
var sampleLineGroupHandle = "SAMPLE_LINE_GROUP_HANDLE";
var sectionViewGroupName = "";
var mode = "shift";
var delta = 0.0;
var clearance = 0.0;
var referenceSourceHandle = "";
var height = 0.0;
var startStation = double.NaN;
var endStation = double.NaN;

var isHandle = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value)
    && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{1,16}$"));
if (!isHandle(sampleLineGroupHandle))
    return new { success = false, error = "sampleLineGroupHandle must be a hexadecimal handle." };
if (mode != "shift" && mode != "belowLowest")
    return new { success = false, error = "mode must be \"shift\" or \"belowLowest\"." };
if (mode == "shift" && (!double.IsFinite(delta) || delta == 0 || Math.Abs(delta) > 1000))
    return new { success = false, error = "Shift mode needs a nonzero delta of at most 1000." };
if (mode == "belowLowest" && (!double.IsFinite(clearance) || clearance < 0 || clearance > 1000 || !isHandle(referenceSourceHandle)))
    return new { success = false, error = "belowLowest mode needs a clearance of 0..1000 and a referenceSourceHandle." };
if (!double.IsFinite(height) || height < 0 || height > 1000 || (height > 0 && mode != "belowLowest"))
    return new { success = false, error = "height must be 0..1000 and is only used in belowLowest mode." };
if (double.IsInfinity(startStation) || double.IsInfinity(endStation) || sectionViewGroupName == null)
    return new { success = false, error = "startStation and endStation must be finite or NaN." };

ObjectId? ResolveId(string handle)
{
    try { return Database.GetObjectId(false, new Handle(System.Convert.ToInt64(handle, 16)), 0); }
    catch { return null; }
}

var groupId = ResolveId(sampleLineGroupHandle);
var group = groupId.HasValue ? Transaction.GetObject(groupId.Value, OpenMode.ForRead) as SampleLineGroup : null;
if (group == null || group.IsErased)
    return new { success = false, error = "sampleLineGroupHandle must identify an existing sample line group." };

var viewGroups = group.SectionViewGroups.Cast<SectionViewGroup>().ToList();
var matching = sectionViewGroupName == ""
    ? viewGroups
    : viewGroups.Where(g => g.Name.Equals(sectionViewGroupName, StringComparison.OrdinalIgnoreCase)).ToList();
if (matching.Count != 1)
    return new {
        success = false,
        error = sectionViewGroupName == "" ? "The sample line group has several section view groups; name one." : "No single section view group has this name.",
        available = viewGroups.Select(g => new { g.Name, g.IsIndividual, viewCount = g.GetSectionViewIds().Count }).ToList()
    };
var viewGroup = matching[0];

ObjectId referenceSourceId = ObjectId.Null;
if (mode == "belowLowest")
{
    var id = ResolveId(referenceSourceHandle);
    // GetSectionSources needs the group open for write; on a read-open group
    // Civil 3D 2025 ends with a fatal eNotOpenForWrite. Nothing is changed.
    group.UpgradeOpen();
    foreach (SectionSource source in group.GetSectionSources())
        if (id.HasValue && source.SourceId == id.Value && source.IsSampled) referenceSourceId = id.Value;
    if (referenceSourceId.IsNull)
        return new { success = false, error = "referenceSourceHandle must be a sampled section source of this sample line group." };
}

// Plan every view first; nothing is written unless all can be changed.
var plan = new List<(ObjectId viewId, string name, double station, double oldMin, double oldMax, double newMin, double newMax, double referenceTop, bool wasAutomatic)>();
foreach (ObjectId viewId in viewGroup.GetSectionViewIds())
{
    var view = (SectionView)Transaction.GetObject(viewId, OpenMode.ForRead);
    var line = (SampleLine)Transaction.GetObject(view.SampleLineId, OpenMode.ForRead);
    if ((!double.IsNaN(startStation) && line.Station < startStation - 1e-6)
        || (!double.IsNaN(endStation) && line.Station > endStation + 1e-6)) continue;
    var oldHeight = view.ElevationMax - view.ElevationMin;
    if (!double.IsFinite(oldHeight) || oldHeight <= 0)
        return new { success = false, error = $"View '{view.Name}' has no valid elevation range." };
    double newMin;
    var referenceTop = double.NaN;
    if (mode == "shift")
    {
        newMin = view.ElevationMin + delta;
    }
    else
    {
        var sectionId = line.GetSectionId(referenceSourceId);
        var section = sectionId.IsNull ? null : Transaction.GetObject(sectionId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Section;
        if (section == null || !double.IsFinite(section.MinmumElevation) || Math.Abs(section.MinmumElevation) > 1e6
            || !double.IsFinite(section.MaximumElevation) || Math.Abs(section.MaximumElevation) > 1e6)
            return new { success = false, error = $"View '{view.Name}': the reference section has no usable elevation range.", station = line.Station };
        newMin = section.MinmumElevation - clearance;
        referenceTop = section.MaximumElevation;
    }
    // A sheet height replaces the view's own height; otherwise the height is kept.
    var newMax = newMin + (height > 0 ? height : oldHeight);
    plan.Add((viewId, view.Name, line.Station, view.ElevationMin, view.ElevationMax, newMin, newMax, referenceTop, view.IsElevationRangeAutomatic));
}
if (plan.Count == 0)
    return new { success = false, error = "No section views in the given station range." };

var changed = new List<object>();
foreach (var item in plan.OrderBy(p => p.station))
{
    var view = (SectionView)Transaction.GetObject(item.viewId, OpenMode.ForWrite);
    view.IsElevationRangeAutomatic = false;
    // Move the limit in the direction of travel first so min stays below max.
    if (item.newMin > item.oldMin)
    {
        view.ElevationMax = item.newMax;
        view.ElevationMin = item.newMin;
    }
    else
    {
        view.ElevationMin = item.newMin;
        view.ElevationMax = item.newMax;
    }
    if (Math.Abs(view.ElevationMin - item.newMin) > 1e-6 || Math.Abs(view.ElevationMax - item.newMax) > 1e-6)
        throw new InvalidOperationException($"View '{item.name}' did not accept the new elevation range.");
    changed.Add(new {
        item.name,
        handle = view.Handle.ToString(),
        item.station,
        oldMin = item.oldMin,
        oldMax = item.oldMax,
        newMin = view.ElevationMin,
        newMax = view.ElevationMax,
        height = view.ElevationMax - view.ElevationMin,
        referenceTop = double.IsNaN(item.referenceTop) ? (double?)null : item.referenceTop,
        referenceFits = double.IsNaN(item.referenceTop) ? (bool?)null : item.referenceTop <= view.ElevationMax + 1e-6,
        item.wasAutomatic
    });
}
// Views whose reference section rises above the new top; written anyway, reported for review.
var notFitting = plan.Where(p => !double.IsNaN(p.referenceTop) && p.referenceTop > p.newMax + 1e-6)
    .OrderBy(p => p.station)
    .Select(p => new { p.name, p.station, referenceTop = p.referenceTop, top = p.newMax, missing = p.referenceTop - p.newMax })
    .ToList();

return new {
    success = true,
    viewGroup = new { name = viewGroup.Name, viewCount = viewGroup.GetSectionViewIds().Count },
    mode,
    delta = mode == "shift" ? delta : (double?)null,
    clearance = mode == "belowLowest" ? clearance : (double?)null,
    height = height > 0 ? height : (double?)null,
    changedCount = changed.Count,
    notFittingCount = notFitting.Count,
    notFitting,
    views = changed
};
```

## Usage Notes

- This is a write-capable template: confirm the full drawing identity first and run it with `civil3d_execute`; use `saveDrawing: true` only for an approved save.
- Two different requests, chosen with `mode`:
  - `"shift"`: "move the current datum 2 m down" is `delta: -2`. Both limits move, so a 105–120 m view becomes 103–118 m and keeps its 15 m height. Lowering only the minimum would make the view taller and can push it off its sheet.
  - `"belowLowest"`: "datum 2 m below the lowest ground point" is `clearance: 2` with the existing ground surface as `referenceSourceHandle`. Each view gets its own lower limit and keeps its own height, unless `height` is given.
  - `"belowLowest"` with `height`: fit the views to a sheet. "Datum 2 m below the lowest ground point, 15 m tall views for the A4 sheet" is `clearance: 2, height: 15`. Every view gets its own lower limit and the same height. Derive the height from the sheet viewport, scale, vertical exaggeration and bands; the recipe does not read it from the template.
- Without `height`, each view keeps its previous height (its own `ElevationMax - ElevationMin`, not a fixed value). Keeping a small automatic height can leave the ground above the window, so for sheets give `height`. Views with an automatic elevation range are switched to a user range; `wasAutomatic` reports which.
- All views are planned before any change; if one cannot be changed, nothing is written. Limit the change with `startStation`/`endStation`.
- In belowLowest mode `notFitting` lists views whose reference section rises above the new top, with the missing height; they are still changed, so review them and choose a taller sheet height or another sheet.
- The window move does not move the views. If the taller content no longer fits the sheet, check the view style and group plot style.
