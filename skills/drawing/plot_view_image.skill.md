---
name: plot_view_image
category: drawing
description: Returns a picture of the model space or of a sheet as it would be plotted (extents or a window), so the assistant can look at the drawing
requires_write: false
aliases: ["kép a rajz egy részéről", "lap képe ellenőrzéshez", "picture of the model space or a layout", "plot a window to a PNG image", "nézetkép készítése"]
workflow_tags: ["drawing_view", "quality_control"]
tested_civil_version: "2025"
validation_summary: "Offline: 2025 API compilation only. The plot itself, the pixel sizes of the image device, the window in a twisted view and the switch to another layout are unverified in a live Civil 3D."
parameters:
  - name: layoutName
    type: string
    required: false
    description: Layout to picture, "Model" for the model space; empty means the layout that is current now (default "")
  - name: xmin
    type: double
    required: false
    description: Left edge of the window; world coordinates in the model space, paper units on a sheet. NaN on all four edges pictures the extents (default NaN)
  - name: ymin
    type: double
    required: false
    description: Bottom edge of the window (default NaN)
  - name: xmax
    type: double
    required: false
    description: Right edge of the window (default NaN)
  - name: ymax
    type: double
    required: false
    description: Top edge of the window (default NaN)
  - name: maxPixels
    type: int
    required: false
    description: Longer side of the picture in pixels, 320 to 4000; the largest size of the device that does not exceed it is used (default 1600)
  - name: deviceName
    type: string
    required: false
    description: Raster plot device that writes the picture (default "PublishToWeb PNG.pc3")
  - name: plotStyleTable
    type: string
    required: false
    description: Plot style table (CTB or STB file name) for the picture; empty keeps the one of the layout's page setup (default "")
---

## Code Template

```csharp
var layoutName = "";
var xmin = double.NaN;
var ymin = double.NaN;
var xmax = double.NaN;
var ymax = double.NaN;
var maxPixels = 1600;
var deviceName = "PublishToWeb PNG.pc3";
var plotStyleTable = "";

if (layoutName == null || layoutName.Length > 255 || layoutName.Any(char.IsControl))
    return new { success = false, error = "layoutName must be a layout name, or empty for the current layout." };
if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Length > 255 || deviceName.Any(char.IsControl)
    || plotStyleTable == null || plotStyleTable.Length > 255 || plotStyleTable.Any(char.IsControl))
    return new { success = false, error = "deviceName is required; plotStyleTable is a plot style table name or empty." };
if (maxPixels < 320 || maxPixels > 4000)
    return new { success = false, error = "maxPixels must be 320..4000." };
var edges = new[] { xmin, ymin, xmax, ymax };
var hasWindow = edges.All(double.IsFinite);
if (!hasWindow && edges.Any(value => !double.IsNaN(value)))
    return new { success = false, error = "Give all four of xmin, ymin, xmax and ymax, or none of them." };
if (hasWindow && (xmax <= xmin || ymax <= ymin))
    return new { success = false, error = "The window needs xmax > xmin and ymax > ymin." };
if (Autodesk.AutoCAD.PlottingServices.PlotFactory.ProcessPlotState
    != Autodesk.AutoCAD.PlottingServices.ProcessPlotState.NotPlotting)
    return new { success = false, error = "A plot is already running in Civil 3D. Try again when it has finished." };

var layoutManager = LayoutManager.Current;
var currentLayout = layoutManager.CurrentLayout;
var targetLayout = layoutName.Length == 0 ? currentLayout : layoutName;
var layouts = (DBDictionary)Transaction.GetObject(Database.LayoutDictionaryId, OpenMode.ForRead);
if (!layouts.Contains(targetLayout))
{
    var known = new List<string>();
    foreach (DBDictionaryEntry entry in layouts) known.Add(entry.Key);
    return new { success = false, error = "The layout was not found.", layoutName = targetLayout, layouts = known };
}
var layout = (Layout)Transaction.GetObject(layouts.GetAt(targetLayout), OpenMode.ForRead);

var validator = PlotSettingsValidator.Current;
// The settings are a copy: the page setup stored in the layout is not changed.
using (var settings = new PlotSettings(layout.ModelType))
{
    settings.CopyFrom(layout);
    try
    {
        validator.SetPlotConfigurationName(settings, deviceName, null);
        validator.RefreshLists(settings);
    }
    catch (System.Exception)
    {
        return new
        {
            success = false,
            error = "The plot device was not found on this computer.",
            deviceName,
            devices = validator.GetPlotDeviceList().Cast<string>().ToList(),
        };
    }

    // The device names its paper sizes by their pixel size; take the largest one that is not above maxPixels.
    var pixelSize = new System.Text.RegularExpressions.Regex(
        @"(\d+(?:\.\d+)?)[_ ]x[_ ](\d+(?:\.\d+)?)[_ ]Pixels",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    var mediaNames = validator.GetCanonicalMediaNameList(settings).Cast<string>().ToList();
    string media = null;
    double mediaWidth = 0, mediaHeight = 0;
    foreach (var name in mediaNames)
    {
        var match = pixelSize.Match(name);
        if (!match.Success) continue;
        var width = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var height = double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (Math.Max(width, height) > maxPixels) continue;
        if (media == null || width * height > mediaWidth * mediaHeight)
        {
            media = name;
            mediaWidth = width;
            mediaHeight = height;
        }
    }
    if (media == null)
        return new
        {
            success = false,
            error = "The device offers no picture size in pixels at or below maxPixels. Use a raster device such as the PNG one, or raise maxPixels.",
            deviceName,
            maxPixels,
            sizes = mediaNames,
        };
    validator.SetPlotConfigurationName(settings, deviceName, media);

    if (plotStyleTable.Length > 0)
    {
        try { validator.SetCurrentStyleSheet(settings, plotStyleTable); }
        catch (System.Exception)
        {
            return new
            {
                success = false,
                error = "The plot style table was not found on this computer.",
                plotStyleTable,
                plotStyleTables = validator.GetPlotStyleSheetList().Cast<string>().ToList(),
            };
        }
    }

    var switched = !string.Equals(currentLayout, targetLayout, StringComparison.OrdinalIgnoreCase);
    var path = NewImagePath(deviceName.IndexOf("jpg", StringComparison.OrdinalIgnoreCase) >= 0 ? "jpg" : "png");
    var backgroundPlot = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("BACKGROUNDPLOT");
    object plottedWindow = null;
    try
    {
        // The plot engine draws the current layout, so another layout is made current for the plot and the old one restored.
        if (switched) layoutManager.CurrentLayout = targetLayout;

        if (hasWindow)
        {
            var low = new Point2d(xmin, ymin);
            var high = new Point2d(xmax, ymax);
            if (layout.ModelType)
            {
                // A plot window is given in the display coordinates of the view; turn the world window into them.
                using var view = Editor.GetCurrentView();
                if (view.ViewDirection.GetNormal().Z < 0.999999)
                    return new { success = false, error = "The model space view is not a plan view; a window can only be pictured from above." };
                var toDisplay = (Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)
                    * Matrix3d.Displacement(view.Target - Point3d.Origin)
                    * Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();
                var corners = new[]
                {
                    new Point3d(xmin, ymin, 0), new Point3d(xmax, ymin, 0),
                    new Point3d(xmax, ymax, 0), new Point3d(xmin, ymax, 0),
                }.Select(corner => corner.TransformBy(toDisplay)).ToList();
                low = new Point2d(corners.Min(corner => corner.X), corners.Min(corner => corner.Y));
                high = new Point2d(corners.Max(corner => corner.X), corners.Max(corner => corner.Y));
            }
            validator.SetPlotWindowArea(settings, new Extents2d(low, high));
            validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
            plottedWindow = new { xmin = low.X, ymin = low.Y, xmax = high.X, ymax = high.Y };
        }
        else
        {
            validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
        }
        validator.SetUseStandardScale(settings, true);
        validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
        validator.SetPlotCentered(settings, true);
        validator.SetPlotRotation(settings, PlotRotation.Degrees000);

        var info = new Autodesk.AutoCAD.PlottingServices.PlotInfo { Layout = layout.ObjectId, OverrideSettings = settings };
        new Autodesk.AutoCAD.PlottingServices.PlotInfoValidator
        {
            MediaMatchingPolicy = Autodesk.AutoCAD.PlottingServices.MatchingPolicy.MatchEnabled,
        }.Validate(info);

        // A background plot would return before the file exists.
        Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("BACKGROUNDPLOT", (short)0);
        using (var engine = Autodesk.AutoCAD.PlottingServices.PlotFactory.CreatePublishEngine())
        {
            engine.BeginPlot(null, null);
            engine.BeginDocument(info, Document.Name, null, 1, true, path);
            engine.BeginPage(new Autodesk.AutoCAD.PlottingServices.PlotPageInfo(), info, true, null);
            engine.BeginGenerateGraphics(null);
            engine.EndGenerateGraphics(null);
            engine.EndPage(null);
            engine.EndDocument(null);
            engine.EndPlot(null);
        }
    }
    finally
    {
        Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("BACKGROUNDPLOT", backgroundPlot);
        if (switched) layoutManager.CurrentLayout = currentLayout;
    }

    if (!System.IO.File.Exists(path))
        return new { success = false, error = "The plot ended without writing the picture file.", deviceName, file = path };

    return new
    {
        success = true,
        layout = targetLayout,
        plotted = hasWindow ? "window" : "extents",
        displayWindow = plottedWindow,
        device = deviceName,
        size = media,
        plotStyleTable = settings.CurrentStyleSheet,
        switchedLayout = switched,
        file = path,
        picture = ImageFile(path),
    };
}
```

## Usage Notes

- This is a read-only template: run it with `civil3d_query`. It changes nothing in the drawing; it plots to a picture file and returns that picture next to the JSON result, where `picture` holds its number, type and pixel size.
- The picture comes from the plot engine, not from the screen. It shows what a plot would show: plot style table, lineweights and paper space included, and it does not depend on the zoom or the size of the Civil 3D window.
- Without a window the extents of the layout are pictured. For a detail, give `xmin`, `ymin`, `xmax` and `ymax`: world coordinates in the model space, paper units on a sheet. The picture keeps the proportions of the device's size, so a long narrow window leaves empty margins; picture such an area in several windows.
- In the model space the lines keep their screen colours unless a plot style table is given. On a white picture, light colours are hard to see; a monochrome table (for example `monochrome.ctb`, when installed) gives black lines.
- `layoutName` other than the current layout makes that layout current for the plot and restores the previous one afterwards (`switchedLayout: true`). On a large drawing this regenerates the view twice.
- The picture files are kept in the plugin's image folder (`file` gives the path) and are removed by the plugin after a day.
- One result can carry at most 4 pictures of 2 MB each. A picture above that is refused with a message; lower `maxPixels` then.
- Limits:
  - A window in the model space needs a plan view (seen from above). A twisted plan view is supported by picturing the box around the turned window.
  - The pixel sizes are read from the names of the device's paper sizes ("... 1600.00 x 1280.00 Pixels"); a device that names them differently is refused with the list of its sizes.
  - Nothing here measures: read coordinates and sizes with a query, and use the picture to judge the look.
