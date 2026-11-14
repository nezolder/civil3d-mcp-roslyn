---
name: plot_layouts_to_pdf
category: drawing
description: Checks the page setup of sheets (device, paper size, plot style table, scale) and plots each one to its own PDF file with exactly that setup, reporting the files written
requires_write: false
aliases: ["lapok PDF-be nyomtatása", "nyomtatási beállítások ellenőrzése", "plot layouts to PDF with their page setups", "check page setup device paper size and CTB", "PDF készítése a lapokból"]
workflow_tags: ["drawing_view", "quality_control"]
tested_civil_version: "2025"
validation_summary: "Offline: 2025 API compilation only. Plotting, the page setup checks against real devices and the PDF files are unverified in a live Civil 3D."
parameters:
  - name: outputDirectory
    type: string
    required: true
    description: Existing folder that receives the PDF files, one per sheet, named after the drawing and the layout
  - name: layoutNames
    type: array
    required: false
    description: Names of the layouts to handle, at most 30; empty means every paper space layout in tab order (default empty)
  - name: plot
    type: bool
    required: false
    description: false only checks and reports the page setups, writing no file (default true)
  - name: overwrite
    type: bool
    required: false
    description: true replaces PDF files that already exist; false refuses the whole call when one exists (default false)
  - name: deviceName
    type: string
    required: false
    description: Plot device to use instead of the one in each page setup; empty keeps the page setups' own device (default "")
  - name: plotStyleTable
    type: string
    required: false
    description: Plot style table (CTB or STB file name) to use instead of the one in each page setup; empty keeps the page setups' own (default "")
---

## Code Template

```csharp
var outputDirectory = "";
var layoutNames = new string[] { };
var plot = true;
var overwrite = false;
var deviceName = "";
var plotStyleTable = "";

const int MaxLayouts = 30;
var badText = new Func<string, bool>(value => value == null || value.Length > 255 || value.Any(char.IsControl));
if (plot && (string.IsNullOrWhiteSpace(outputDirectory) || outputDirectory.Length > 240 || outputDirectory.Any(char.IsControl)))
    return new { success = false, error = "outputDirectory is required." };
if (layoutNames == null || layoutNames.Length > MaxLayouts || layoutNames.Any(name => badText(name) || name.Trim().Length == 0)
    || layoutNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != layoutNames.Length)
    return new { success = false, error = "layoutNames must hold at most 30 different layout names." };
if (badText(deviceName) || badText(plotStyleTable))
    return new { success = false, error = "deviceName and plotStyleTable must be names or empty." };
if (plot && !System.IO.Directory.Exists(outputDirectory))
    return new { success = false, error = "The output folder does not exist.", outputDirectory };
if (plot && Autodesk.AutoCAD.PlottingServices.PlotFactory.ProcessPlotState
    != Autodesk.AutoCAD.PlottingServices.ProcessPlotState.NotPlotting)
    return new { success = false, error = "A plot is already running in Civil 3D. Try again when it has finished." };

var layoutDictionary = (DBDictionary)Transaction.GetObject(Database.LayoutDictionaryId, OpenMode.ForRead);
var allLayouts = new List<Layout>();
foreach (DBDictionaryEntry entry in layoutDictionary)
    allLayouts.Add((Layout)Transaction.GetObject(entry.Value, OpenMode.ForRead));
var chosen = new List<Layout>();
if (layoutNames.Length == 0)
{
    chosen = allLayouts.Where(candidate => !candidate.ModelType).OrderBy(candidate => candidate.TabOrder).ToList();
}
else
{
    var missing = layoutNames
        .Where(name => !allLayouts.Any(candidate => string.Equals(candidate.LayoutName, name, StringComparison.OrdinalIgnoreCase)))
        .ToList();
    if (missing.Count > 0)
        return new
        {
            success = false,
            error = "Some layouts were not found.",
            missing,
            layouts = allLayouts.OrderBy(candidate => candidate.TabOrder).Select(candidate => candidate.LayoutName).ToList(),
        };
    chosen = layoutNames
        .Select(name => allLayouts.First(candidate => string.Equals(candidate.LayoutName, name, StringComparison.OrdinalIgnoreCase)))
        .ToList();
}
if (chosen.Count == 0)
    return new { success = false, error = "The drawing has no paper space layout." };
if (chosen.Count > MaxLayouts)
    return new
    {
        success = false,
        error = "The drawing has more than 30 paper space layouts. Name at most 30 of them in layoutNames and call again for the rest.",
        layouts = chosen.Select(candidate => candidate.LayoutName).ToList(),
    };

var drawingName = System.IO.Path.GetFileNameWithoutExtension(Document.Name);
var invalidCharacters = System.IO.Path.GetInvalidFileNameChars();
string FileFor(Layout sheet)
{
    var name = new string((drawingName + "-" + sheet.LayoutName)
        .Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
    return System.IO.Path.Combine(outputDirectory, name + ".pdf");
}
if (plot)
{
    var files = chosen.Select(FileFor).ToList();
    if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
        return new { success = false, error = "Two layouts would get the same file name. Plot them in separate calls into separate folders.", files };
    var existing = files.Where(System.IO.File.Exists).ToList();
    if (existing.Count > 0 && !overwrite)
        return new
        {
            success = false,
            error = "Some PDF files already exist, so nothing was plotted. Choose another folder, or set overwrite to true.",
            existing,
        };
}

var validator = PlotSettingsValidator.Current;
var devices = validator.GetPlotDeviceList().Cast<string>().ToList();
var styleTables = validator.GetPlotStyleSheetList().Cast<string>().ToList();
var known = new Func<List<string>, string, bool>((list, name) =>
    list.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase)));
if (deviceName.Length > 0 && !known(devices, deviceName))
    return new { success = false, error = "The plot device was not found on this computer.", deviceName, devices };
if (plotStyleTable.Length > 0 && !known(styleTables, plotStyleTable))
    return new { success = false, error = "The plot style table was not found on this computer.", plotStyleTable, plotStyleTables = styleTables };

var layoutManager = LayoutManager.Current;
var currentLayout = layoutManager.CurrentLayout;
var backgroundPlot = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("BACKGROUNDPLOT");
var rows = new List<object>();
var plottedCount = 0;
var problemCount = 0;
try
{
    if (plot) Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("BACKGROUNDPLOT", (short)0);
    foreach (var sheet in chosen)
    {
        var problems = new List<string>();
        using var settings = new PlotSettings(sheet.ModelType);
        settings.CopyFrom(sheet);

        // The page setup as it is stored in the layout.
        var device = deviceName.Length > 0 ? deviceName : settings.PlotConfigurationName;
        var paper = settings.CanonicalMediaName;
        var styleTable = plotStyleTable.Length > 0 ? plotStyleTable : settings.CurrentStyleSheet;
        var paperLocal = "";

        if (string.IsNullOrWhiteSpace(device) || string.Equals(device, "None", StringComparison.OrdinalIgnoreCase))
            problems.Add("The page setup names no plot device.");
        else if (!known(devices, device))
            problems.Add("The plot device '" + device + "' is not installed on this computer.");
        else
        {
            try
            {
                // Setting the device together with the stored paper size fails when the device does not offer that size.
                validator.SetPlotConfigurationName(settings, device, paper);
                validator.RefreshLists(settings);
                paperLocal = validator.GetLocaleMediaName(settings, paper);
            }
            catch (System.Exception)
            {
                problems.Add("The device '" + device + "' does not offer the paper size '" + paper + "'.");
            }
        }
        if (!string.IsNullOrEmpty(styleTable) && !known(styleTables, styleTable))
            problems.Add("The plot style table '" + styleTable + "' was not found on this computer.");
        else if (plotStyleTable.Length > 0 && problems.Count == 0)
            validator.SetCurrentStyleSheet(settings, plotStyleTable);

        var file = plot ? FileFor(sheet) : null;
        var written = false;
        long bytes = 0;
        var isPdf = false;
        double seconds = 0;
        if (plot && problems.Count == 0)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // The plot engine draws the current layout.
                if (!string.Equals(layoutManager.CurrentLayout, sheet.LayoutName, StringComparison.OrdinalIgnoreCase))
                    layoutManager.CurrentLayout = sheet.LayoutName;
                var info = new Autodesk.AutoCAD.PlottingServices.PlotInfo { Layout = sheet.ObjectId, OverrideSettings = settings };
                new Autodesk.AutoCAD.PlottingServices.PlotInfoValidator
                {
                    MediaMatchingPolicy = Autodesk.AutoCAD.PlottingServices.MatchingPolicy.MatchEnabled,
                }.Validate(info);
                using (var engine = Autodesk.AutoCAD.PlottingServices.PlotFactory.CreatePublishEngine())
                {
                    engine.BeginPlot(null, null);
                    engine.BeginDocument(info, Document.Name, null, 1, true, file);
                    engine.BeginPage(new Autodesk.AutoCAD.PlottingServices.PlotPageInfo(), info, true, null);
                    engine.BeginGenerateGraphics(null);
                    engine.EndGenerateGraphics(null);
                    engine.EndPage(null);
                    engine.EndDocument(null);
                    engine.EndPlot(null);
                }
            }
            catch (System.Exception error)
            {
                problems.Add("The plot failed: " + error.Message);
            }
            seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);

            if (problems.Count == 0)
            {
                if (!System.IO.File.Exists(file))
                    problems.Add("The plot ended without writing the file.");
                else
                {
                    written = true;
                    using (var stream = System.IO.File.OpenRead(file))
                    {
                        bytes = stream.Length;
                        var head = new byte[5];
                        isPdf = stream.Read(head, 0, 5) == 5
                            && head[0] == (byte)'%' && head[1] == (byte)'P' && head[2] == (byte)'D' && head[3] == (byte)'F' && head[4] == (byte)'-';
                    }
                    if (!isPdf) problems.Add("The file written is not a PDF; the device '" + device + "' plots another format.");
                }
            }
        }
        if (written) plottedCount++;
        if (problems.Count > 0) problemCount++;

        var scale = settings.CustomPrintScale;
        rows.Add(new
        {
            layout = sheet.LayoutName,
            tab = sheet.TabOrder,
            device,
            paper,
            paperLocal,
            paperSize = new { width = settings.PlotPaperSize.X, height = settings.PlotPaperSize.Y, units = settings.PlotPaperUnits },
            rotation = settings.PlotRotation,
            area = settings.PlotType,
            centered = settings.PlotCentered,
            scale = new
            {
                fit = settings.UseStandardScale && settings.StdScaleType == StdScaleType.ScaleToFit,
                paperUnits = scale.Numerator,
                drawingUnits = scale.Denominator,
            },
            plotStyleTable = styleTable,
            plotWithStyles = settings.PlotPlotStyles,
            lineweights = settings.PrintLineweights,
            problems,
            file = written ? file : null,
            bytes = written ? (long?)bytes : null,
            isPdf = written ? (bool?)isPdf : null,
            seconds = written ? (double?)seconds : null,
        });
    }
}
finally
{
    Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("BACKGROUNDPLOT", backgroundPlot);
    if (!string.Equals(layoutManager.CurrentLayout, currentLayout, StringComparison.OrdinalIgnoreCase))
        layoutManager.CurrentLayout = currentLayout;
}

return new
{
    success = true,
    drawing = Document.Name,
    plotted = plot,
    outputDirectory = plot ? outputDirectory : null,
    layoutCount = rows.Count,
    plottedCount,
    problemCount,
    layouts = rows,
};
```

## Usage Notes

- This is a read-only template for the drawing: run it with `civil3d_query`. It changes nothing in the drawing, but with `plot: true` it writes PDF files into `outputDirectory`. A script cannot delete files, so wrong output has to be removed by a person.
- Run it first with `plot: false`. That only reads each sheet's page setup and lists what would stop or spoil the plot under `problems`: no device in the page setup, a device that is not installed on this computer, a paper size the device does not offer (custom paper sizes live in the device's PMP file, so a missing or different PMP shows up here), or a plot style table that is not found.
- With `plot: true` every sheet without a problem is plotted with its own page setup to `<drawing>-<layout>.pdf`. The report gives the file, its size, whether it starts as a PDF file, and the seconds the plot took. A sheet with a problem is skipped and the others are still plotted.
- `deviceName` and `plotStyleTable` replace the page setups' own device and plot style table for this call only; the layouts keep their stored page setup. Leave them empty to prove that the sheets plot correctly as they are.
- `paperSize` is the stored paper size in the units given next to it; `scale` gives paper units per drawing units (1 to 1 for a sheet plotted at full size) or `fit: true`.
- Plotting takes a few seconds per sheet and each sheet is made current for its plot; the layout that was current before is restored at the end. For many sheets raise `timeoutSeconds`, or plot them in several calls with `layoutNames`. If the call times out, the files plotted so far stay on disk.
- To look at a sheet instead of only checking its setup, use `plot_view_image` with the layout's name.
- Limits:
  - One PDF file per sheet; a single multi-page PDF is not made here.
  - The content of the PDF is not read back: line colours, lineweights and missing references have to be judged from a picture or by opening the file.
  - Which device, paper sizes and plot style table are right for a project is the caller's knowledge; this recipe only reports what the page setups hold and whether this computer can plot them.
