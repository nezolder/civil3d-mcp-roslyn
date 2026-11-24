# Saved-file readback and TIN point delete: live validation

Date: 2026-11-24 (Europe/Budapest). Recipes: `saved_drawing_readback` and `delete_tin_surface_points`. The delete recipe was run as committed in `3e77ff1` (unchanged since, apart from its notes); the readback recipe as committed in `3f7b8e8` (the surface fields were added during this run, after its first pass at `3e77ff1`).

## Fixture and method

**Proven:** Civil 3D 2025 Hungary on the user's work PC, one new instance started for this test only, with plugin and server at `23b0fe2` (`buildMatchesServer: true`, session token in use). No other Civil 3D was running when it started. The drawing was a plain file copy of the user's sample-line test drawing in a temporary folder; source and copy had the same size and SHA-256 before the first write. The original was not opened. Its only surface is a data reference TIN surface (133,504 points, no definition operations); one alignment.

The installed catalogue does not contain the new recipes, so their templates were sent as `code` through `civil3d_query` / `civil3d_execute` with only the parameter lines bound. Where a row below says "early-return part", only the template up to the refusal was sent; the refusal lines are identical to the template.

A local test surface `MCP_TIN_TESZT` was made with the catalogue recipe `create_tin_surface`: a 5 x 5 grid at 10 m, all points at 100.0 except two spikes, 150.0 at (700020, 210020) and 50.0 at (700030, 210010). That write used `requireClean`, `backup` and `saveDrawing`.

## delete_tin_surface_points

| Case | Call | Result |
|---|---|---|
| Data reference surface | query, early-return part | Refused: "The surface is a data reference". |
| `listOnly`, boundary around the grid, `deleteBelowElevation` 80, `deleteAboveElevation` 120 | query, full template | 2 of 25 matched, exactly the two spikes; `DBMOD` stayed 0. |
| Same without `listOnly` | execute, `requireClean`, `saveDrawing` | Deleted 2, points 25 -> 23, new operation `SurfaceOperationDeleteMultipleTinVertices` at index 1 holding both locations; receipt: committed, saved, `dbmodAfter` 0, modified only the surface. |
| Independent readback | query | 23 vertices, all at 100.0; operations: add vertices, delete multiple vertices (enabled); `IsOutOfDate` false; the data reference surface unchanged. |
| One XY row, 4 mm and 3 mm off the corner point, `tolerance` 0.01 | execute, `requireClean`, no save | Deleted 1, points 23 -> 22. A single point also gives `SurfaceOperationDeleteMultipleTinVertices` (index 2). `dbmodAfter` 1 as expected. |
| One valid row plus one row with no point within 0.01 | query, points part | Refused: "points[1] matches 0 surface points"; nothing deleted. |
| Boundary without filters, `maxPoints` 3 | execute with `preview` | Refused: 22 match, more than 3; receipt `rolledBack`, `dbmodAfter` unchanged. |

## saved_drawing_readback

| Case | Result |
|---|---|
| After the saved surface creation, handles of both surfaces and one unknown handle | `found` for both, `found: false` for the unknown one; `handlesDiffering` empty, `sameModelSpaceCounts` true, `fingerprintMatches` true, `dbmodOpen` 0; Civil list: 2 surfaces, 1 alignment, 0 corridors. |
| After the unsaved corner delete (surface fields added) | `handlesDiffering` = the test surface: file 23 points / 2 operations, open drawing 22 / 3; `dbmodOpen` 1. |
| After saving that change | `handlesDiffering` empty; file and open drawing both 22 points / 3 operations; the 133,504-point data reference surface read from the side database as well. |
| `filePath` = the time-stamped backup made before the first write (early-return-free subset) | `isActiveFile` false; the test surface not found in the backup, so it is flagged. |

Every query left `DBMOD` at 0 on a clean drawing, and each save after a readback succeeded, so the side database did not keep the file locked.

## Not covered

Undo of a delete (one `U` in Civil 3D) was not tried; a large surface (deleting thousands of points), a surface whose points come from a point group or file added after the delete, and a readback of a drawing with xrefs or a long load time.
