# Recipes from the street renovation findings: live validation

Date: 2026-11-24 (Europe/Budapest). Tested recipes, at the branch commits named per step (final `d6eb7e8`):
`data_shortcut_folder_audit`, `corridor_section_check`, `add_sample_line_stations_to_corridor`,
`create_breaklines_from_survey_strings` and the K-value option of `create_design_profile_from_pvis`.

## Fixture and method

**Proven:** a real road renovation project prepared by the user: a model drawing with 8 corridors
(one main street of about 430 m with 5 baselines and 63 regions on the centreline), 138 alignments,
221 profiles and 9 TIN surfaces; a cross section drawing that references the alignments, profiles,
corridors and the terrain surface by data shortcuts and holds 12 sample line groups; the terrain
drawing; a second source drawing with 12 corridors; the project's data shortcut folder (487 keys);
and the 2D survey drawing attached as an xref. Only these files were copied (494 files, 37.7 MB)
into a temporary folder with the same relative layout; every copy's SHA-256 matched its original.
The originals were never opened; a size, time and SHA-256 snapshot of the original folders was
compared after every save and never changed.

**Proven:** with the copies opened in one Civil 3D 2025 Hungary instance on the user's work PC,
every data reference and every resolved xref pointed to the copies (`GetReferenceInfo`, xref
status). Each data shortcut key also carries the absolute path of the original; the audit was run
with `useAbsolutePaths: false`, and all 487 keys resolved through their relative path.

The installed server and plugin (commit `23b0fe2`) do not contain these recipes, so each template
was sent as `code` with only its parameter lines changed. Reads went through `civil3d_query`;
writes through `civil3d_execute`, first with `preview: true`, then with `requireClean`, `backup`
and `saveDrawing`. Every call named `expectedDrawing`.

## Results

| Recipe | Result | Fixed during the run |
|---|---|---|
| `data_shortcut_folder_audit` | 487 keys read, all source drawings read in side databases (3 drawings, 3.2 s). 2 real problems: an alignment key and a profile key each present as two files that point to the same object (`DUPLICATE_KEY` + `SAME_OBJECT_HAS_SEVERAL_KEYS`). 8 keys renamed after publishing. No missing source, no handle mismatch. | The real file layout (`Shortcut@name`, `DwgRelPath@path`, `File@name`, `Object@name/type/handleLow/handleHigh`) differed from the assumed field names; relative path first, `useAbsolutePaths`; a rename (file name kept by Civil 3D) is `renamedFrom`, not a finding. |
| `corridor_section_check` | 30 sample line stations of a junction group checked on the main baseline in 0.5 s; no overlap of the top-surface links; `codesNotSeen` showed the stock `Top` code was absent. | CS0118 (see below); checks now work inside one subassembly (the project's assemblies are one Subassembly Composer subassembly with 8 to 42 links); `linkCodesSeen`/`codesNotSeen`; sample line stations snapped to the exact corridor station (they differed by 0.1 to 8 mm, and `GetAppliedAssemblyAtStation` answers "Station not processed" otherwise: 23 of 30 had been skipped); stations still without a section are listed as `notCorridorStations`. |
| `add_sample_line_stations_to_corridor` | Sample lines read from the saved cross section drawing in a side database. Main street: every crossing already present (earlier added by hand with rounded values). A side street: 2 stations planned and added, the corridor rebuilt in a separate call (157 ms), read back in `AdditionalStations` and `SortedStations`, sections available there, corridor not out of date; the same preview afterwards planned 0. | `sampleLineDrawingPath` (sample lines in the referencing drawing, stations in the source drawing); the postcondition reads `AdditionalStations`: `SortedStations` lists built stations and changes only at a rebuild (proven with a diagnostic preview: after `AddStation` 63 → 63 sorted, 13 → 14 additional; after `Rebuild` 64 sorted); default `stationTolerance` 0.02. |
| `create_design_profile_from_pvis`, `curveMode: "k"` | Three PVIs, K 150: Civil 3D made a symmetric parabola with K 150, radius 15000 and length 144.0274 m, equal to K × \|Δg\| × 100 (Δg = 0.960183 %); saved and read back. | A curve too long for its neighbours (K 1500) now says how long it would be and how much fits, instead of the tangent-gap message. |
| `create_breaklines_from_survey_strings` | 2,683 survey blocks read from the xref by block name, code attribute and insertion Z; 196 strings (1,142 vertices) drawn as 3D polylines on a new layer and added to a new TIN surface as one breakline operation with its description; the surface then held 1,142 points and 2,240 triangles. The three longest curb-top strings stay in a constant offset band; their largest sideways jumps are 0.66 to 0.71 m, at places that look like driveway curb drops (not verified). | `AddStandardBreaklines` refuses a mid-ordinate distance of 0; the four breakline settings are parameters with defaults that keep every vertex. |

Also found and fixed:

- **CS0118:** once a drawing with a corridor is open, the stock subassembly library is loaded and
  defines a root namespace `Subassembly`, so a script using the short type name no longer compiles.
  Recipes and live-smoke bodies use `Autodesk.Civil.DatabaseServices.Subassembly`; a test keeps them
  off the short name; the `csharp` guide topic says so and the server adds a hint to that error.

## Evidence boundaries

- **Proven live:** everything in the results table, on copies of one project in Civil 3D 2025.
- **Valid only for this project:** codes, link counts and the survey layout. The section check's
  findings were all zero here, so `REPEATED_CODE_ON_ONE_SIDE`, `OVERLAPPING_LINKS`,
  `MISSING_SHAPE_CODE` and `SHORT_LINK` have not been seen firing on real data.
- **Not verified:** the weeding-angle unit of `AddStandardBreaklines` (the default 0 does not weed);
  whether the curb-top jumps of 0.5 to 0.7 m are real curb breaks (needs a look at the survey);
  offset baselines (none in this project).
- **Seen, not fixed here (plugin):** the write receipt of a preview reported `rolledBack: false`
  with `leftAfterPreview` (and after a failed preview `leftBehind`) 196 although a separate read
  showed that the layer and all 196 polylines were gone. The script had created a layer and 3D
  polylines with vertices; previews that created a profile or a surface reported correctly.
  Because of it the clean mark was not restored after the preview (`DBMOD` 1).
- **Seen again:** Civil 3D marks the drawing modified some time after a rolled-back preview and
  after saved writes that touch profile views or corridors (`DBMOD` 1 or 13); the next
  `requireClean` call is refused until the drawing is saved once more.
