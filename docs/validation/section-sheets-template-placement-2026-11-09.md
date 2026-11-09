# Cross-section sheets from a view template: live validation

Date: 2026-11-09 (Europe/Budapest). Tested recipes: `plan_section_sheet_fit`, `create_section_view_group` and `shift_section_view_elevations` on branch `claude/project-thread-jau0ig`, run by name from the working tree. The earlier report on these recipes left template placement, the group plot style and print fit unverified; this run covers the first two.

## Fixture and method

**Proven:** a separate Civil 3D 2025 Hungary process was started with the user's shortcut on a disposable copy of a test drawing (SHA-256 equal to the original at copy time; the original was not opened). The copy holds a data-referenced alignment of about 350 m and a data-referenced existing-ground TIN, a section view style with vertical exaggeration 1 and a band set of four 10 mm bottom bands. Fifteen sample lines (18 m left, 14 m right, every 25 m) were created with `create_sample_lines`.

The office view template was only read. It has 14 section sheet layouts (A4 portrait and landscape, A3 portrait and landscape, 630, 840 and 1050 x 297 landscape, each at 1:100 and 1:200) with one model viewport inset 10 mm on every side, and two cover sheet layouts without a model viewport.

Every write checked the full drawing identity and was saved with `saveDrawing: true`. The final state was read back independently in a new query: 1 group, 15 sheet frames, 15 views, `DBMOD=0` after a geometry-neutral save.

## Findings

- **Proven:** `plan_section_sheet_fit` read all 16 layouts and skipped both cover sheets. With the new group offset range (18 + 14 = 32 m) only sheets at least 32 m wide in model units qualified; A4 at 1:100 (19 and 27.7 m) was correctly rejected.
- **Proven:** template placement sizes the model-space sheet frames from the layout's paper size and the drawing's **annotation scale**, not from the viewport scale. With annotation scale 1:500, the A3 1:100 layout produced one 210 x 148.5 m frame holding all 15 views five abreast. After setting the annotation scale to 1:100 the frames were 42 x 29.7 m. `create_section_view_group` now refuses a layout whose viewport scale differs from the annotation scale (checked live with a 1:200 layout), and refuses a cover sheet (checked live).
- **Proven:** the views are distributed over the sheets once, from their size at creation. Created with the automatic elevation range, five views shared each sheet; raising their height to the sheet height with `shift_section_view_elevations` and then calling `SectionViewGroup.UpdateLayout()` left them overlapping on three sheets. Created with the new `elevationHeight` (user-specified range), each view got its own sheet, 15 sheets left to right by station.
- **Proven:** Civil 3D kept the graph top 12 mm (paper) below the viewport top and the left edge 7.5 mm inside it. With the plan height that ignored this (23.5 m) the lowest band labels left the viewport by about 9 mm at the bottom. With `reservedHeightMm: 12` the plan gave 22.5 m, and all 15 views and all 30 band label groups lay inside their sheet viewport (lowest band label 1.14 m above the frame, where the viewport starts at 1 m). The source of the 12 mm is **Unverified**; it is style dependent.
- **Proven:** the new `sheetFit` result of `create_section_view_group` reported `fitsAll: true` with one view per sheet for the 22.5 m run, and a 1 m bottom overflow for each of three views created at 23.5 m.
- **Proven:** `shift_section_view_elevations` in `belowLowest` mode with the same 22.5 m height then set each view's datum 2.5 m below its lowest ground point without changing view sizes or sheets; no view reported ground above its top.
- **Proven, side finding:** on this host, writing `GroupPlotStyle.SpaceRow` or `SpaceColumn` (0.02 and then 0.01) read back as 0. The final run therefore used a second, untouched group plot style with the same settings (by rows, at most 3 x 3, 10 mm spacing, 25 mm between pages). Spacing played no visible role with one view per sheet.

## Not covered

- Paper space layouts and plotted output: the recipes stop at model-space sheet frames. The view title, which this style places at the bottom left, is not part of the checked box. Print fit remains **Unverified**.
- Several views per sheet, the 1:200 and the 630–1050 mm sheets beyond planning, and reopening the saved copy in a new process.
