# Published development status

Updated: 2026-11-25

This repository contains the accepted runtime, six focused engineering recipes with scoped evidence, and an explicitly experimental, offline-validated surface-profile recipe. Client data, live-run artifacts, proprietary assemblies and private development history are excluded.

## Current catalogue

The catalogue contains **50 skills** with **51 C# templates**; **49** of the skills run by name. These numbers, and the ones in the README, are compared with the `skills/` folder by `tests/skills/documented-counts.test.mjs`, so a change to the folder fails the test until the text follows. Counts inside the dated sections below describe the state at their date and are not updated.

## Save until clean, corridor read recipes, API notes (2026-11-25)

- **Save until clean:** Civil 3D can mark a drawing modified again right after the command that saved it, when it updates objects that depend on the written ones (seen in the live smoke runs, `W2b`). After a saved write the plugin now waits a second, reads `DBMOD` in a command of its own (the read command, no Undo step) and saves again while the drawing is marked, at most three times, so the receipt's `dbmodAfter` is the final state. Only the drawing that was written is saved again; the step stops when another drawing is active, a save fails or the step cannot start, and it never turns the finished write into an error. The receipt has a `saveSettle` block (`clean`, `checks`, `extraSaves`, `dbmodAfterFirstSave`, `stopped`), the journal line `extraSaves` and `settledClean`. Re-marking that comes much later (section views after about 30 seconds) is not covered.
- **Three read-only corridor recipes:** `assembly_inventory` (assemblies, subassemblies with class or Composer package, side, attachment and input parameters by key, and the corridor regions that use each assembly), `corridor_region_inventory` (each region's station range, assembly, frequency settings, built and added stations, targets, offset baselines; gaps and overlaps between neighbouring regions) and `corridor_applied_section` (the built section at one station: coded points, links with slope and length, shapes with area). None reads `AppliedSubassembly.Parameters`, which has crashed Civil 3D in field use where a parameter was overridden for a station range.
- **API notes:** a script that uses one of 12 noted members (for example `AppliedSubassembly.Parameters`, `SectionViewGroups.Add`, `GetBoundedVolumes`, `Corridor.Rebuild`, `Database.SaveAs`, opening or closing documents) gets a short note next to its result, `{"apiNotes": [...]}`, also on an error reply. The plugin finds them from what the compiler resolved, like the script guard, and refuses nothing. Each note says whether it was seen live here, reported from field work, or follows from the API. Recipes run by name get none. `npm run test:skills` checks that every noted member exists in the Civil 3D 2025 assemblies.
- The plugin lists `saveSettle` and `apiNotes` among its `features`.

Evidence boundaries: **proven offline:** the settle loop with stub drawings (re-marked once, clean at once, never clean, failing extra save, switched drawing, no save requested; native and modal backends), the notes with stub types (inherited members, properties, extension methods, cached scripts, look-alikes, failed compiles) and the reply shape in the server; the three recipes compile against the Civil 3D 2025 assemblies. **Not yet run in Civil 3D:** all of it.

## Code set labels on API section views (2026-11-25)

Live comparison on copies of a field project (`docs/validation/section-view-code-set-labels-2026-11-25.md`): section views created through the .NET API do not show the labels a corridor section's code set style draws (for example in the bottom bands), while views placed with the Civil 3D dialog do. Both create the same objects with the same readable properties; re-applying the style, `REGENALL` and a layout update do not add them, and the .NET API has no setting for it. The `civil` guide topic and the `create_section_view_group` notes now say so, and that `SectionViewGroups.Add` can leave out sample lines exactly at the start and end stations (recipe code not changed yet).

## Preview receipt after 3D polylines; dependency alerts (2026-11-25)

- **Receipt:** after a preview or a failed script, the plugin checks that nothing the script added is still in the drawing. It used to count every new handle whose id was not marked erased. In the field, a preview that created a layer and 196 3D polylines was reported with `leftAfterPreview: 196` although the layer and the polylines were gone, so the clean mark was not restored. An object now counts only when it can still be opened, is not erased, and its owner is still there and still lists it; an object that cannot be opened for another reason still counts. A nonzero count comes with `leftTypes`, the types of what was left.
- **Live smoke test:** case `W10` previews a new layer with three 3D polylines and expects `rolledBack: true`, the layer and the polylines gone and `DBMOD` 0.
- **Dependencies:** `npm audit` reported moderate alerts in `hono`, `@hono/node-server` and `ip-address` (through the MCP SDK; the server uses the stdio transport, not the HTTP parts) and in `glob`/`minimatch` (through `rimraf`, used only by `npm run clean`). The lock file now has the fixed versions and `rimraf` is 6.1.4; `npm audit` reports 0.

Evidence boundaries: **proven live** in Civil 3D 2025 (`docs/validation/preview-receipt-7d6b2d0-2026-11-25.md`): the live smoke test passed with `W10`, and a diagnostic showed that the old check counted the end marker (`AcDbSequenceEnd`) of each abandoned 3D polyline, whose id stays unmarked while its owner is erased. A failed script with 3D polylines (`leftBehind`) and other complex entities were not run live.

## Recipes from the street renovation findings (2026-11-24)

Four new recipes and one new option, for the steps that the field project had done with ad hoc scripts. All five were run live on copies of that project in Civil 3D 2025 and fixed on the way (report: `docs/validation/street-recipes-d6eb7e8-2026-11-24.md`).

- `data_shortcut_folder_audit` (read-only): duplicate keys, two keys on one object, keys whose source object has another name or is gone, missing sources; source drawings are read in side databases.
- `corridor_section_check` (read-only): built corridor sections at the corridor's or a sample line group's stations; repeated codes on one side, overlapping links, missing shape codes, short links. Codes and lengths are inputs.
- `add_sample_line_stations_to_corridor` (write): where each sample line crosses each main baseline, a station in every region that holds it. Offset baselines are only listed; the .NET API has no call for them.
- `create_breaklines_from_survey_strings` (write): surveyed points along the road joined into 3D polylines by code or layer, ties limited in length and sideways jump, optionally one breakline group on a TIN surface.
- `create_design_profile_from_pvis` has `curveMode: "k"` and `minCurveLength`: curves set by K keep their radius when a tangent moves. Its postcondition refuses a curve whose length does not follow from the K-value, in case Civil 3D reads K in another unit.

Evidence boundaries:

- **Proven live (copies of one real project):** the shortcut audit found two duplicate keys; the section check read 30 sample line stations snapped to corridor stations; two stations were added, built and read back; a K 150 curve got the length K × |Δg| × 100; 196 survey strings became one breakline operation on a TIN surface.
- **Not seen firing on real data:** the section check's finding kinds (that project had none). **Unverified:** offset baselines, the weeding-angle unit.
- **Found on the way:** a script using the short type name `Subassembly` fails (CS0118) once a corridor drawing is open; recipes write it in full, and the guide and an error hint say so. A preview that created 3D polylines got a wrong `leftAfterPreview` in its receipt (plugin; not fixed here).

## Field findings from a street renovation project (2026-11-24)

Twelve findings from live Civil 3D 2025 work in another project were sorted into guide text, a guard and error hints. No recipe changed.

- **Guide topic `civil`** (`civil3d_skills` `action: "guide"`, `topic: "civil"`): when Civil 3D raises the modified flag by itself, the section source list, data shortcuts (no synchronisation call in the .NET API, a file locked by another instance's refresh, stale and duplicate keys after a rename), long operations one per call, section view labels and projection, K-value vertical curves, corridor targets after a rebuild.
- **Guard:** `civil3d_query` refuses a script that calls `SampleLineGroup.GetSectionSources()` (`CIVIL3D.SANDBOX_VIOLATION`, nothing runs); `civil3d_execute` and `preview: true` still run it. The check reads what the compiler resolved and runs on every query, also for a script already cached from a write. A catalog test makes sure that no read-only recipe calls it.
- **Error hints**, added by the MCP server so they also reach older plugins: `CIVIL3D.COMMAND_CONTEXT_TIMEOUT` (Civil 3D busy; ask for Esc or a click, then send once more), `CIVIL3D.SAVE_FAILED` with `eFileAccessErr` (another instance holds the file; do not close the drawing or repeat the change), `CIVIL3D.TRANSACTION_FAILED` with `eNotOpenForWrite` (open the object for write).

Evidence boundaries:

- **Proven offline:** the guard with stub types (refused query, allowed write, cached script, method group, compile error first, look-alikes allowed); the hints in the Node error tests; the plugin compiles against the Civil 3D 2025 assemblies, and the member names quoted in the guide exist in them.
- **Reported from the field, not reproduced here:** every behaviour in the `civil` topic. The guide marks the unverified causes (stale reference, missing code set labels, freezes from several heavy operations in one call).
- **Not yet run in Civil 3D:** the guard inside the plugin.

## What a write changed, preview, and a token on the local connection (2026-11-24)

Three additions around `civil3d_execute`, taken from a comparison with other Civil 3D MCP projects. Live-tested in two rounds on Civil 3D 2025 on disposable drawing copies; the record is `docs/validation/change-list-preview-token-7bc79cd-2026-11-24.md`.

- **Change list in the receipt.** Every committed write now reports, next to `committed` and `saved`, which objects it added, modified and erased: counts by type and the first objects by handle, name and layer (`changes`; `changeItems` sets how many are named). The plugin finds added objects by the handles the drawing gave out during the script, modified and erased ones by the flags of the objects in the script's transaction and by the drawing's own notifications, and reads the drawing again after the commit, so the list does not depend on what the script says about itself. Symbol tables, dictionaries and layout blocks are counted apart. The journal keeps the three counts.
- **Preview.** `preview: true` runs the same call and keeps nothing: the transaction is abandoned, nothing is saved or backed up, and an `idempotencyKey` is not used up. The checks of the real call still run. The receipt lists what the script would change and says whether everything was taken back (`rolledBack`). The MCP server never sends a preview to a plugin that does not report the feature `preview`, because such a plugin would commit the write.
- **Session token.** Each plugin session makes up a random 256-bit token, writes it into its endpoint record in the user's profile and runs a request only when the request presents it; the MCP server reads the record and sends the token by itself. A request without it is refused with `CIVIL3D.UNAUTHORIZED` before the drawing is looked at. Health without the token gets a short reply without drawing data, and the status action shows such an instance as `authorized: false`. `CIVIL3D_MCP_REQUIRE_TOKEN=0` in Civil 3D's environment switches the requirement off for an older MCP server.

Evidence boundaries:

- **Proven live (Civil 3D 2025, `7bc79cd`, disposable copies, every write read back):** the list names the right objects for plain entities, layers and Civil 3D objects (alignment, profile from a surface, TIN surface), including what Civil 3D adds by itself (label groups, a hidden feature style) and, for a real write, what it updates at commit (display sets, the settings node, a dependent profile). A preview took back everything, also changes a script made beside its transaction through open-close transactions and `ObjectId.Open`, and a clean drawing stayed clean afterwards, so the real write could still ask for `requireClean` and `backup`. A drawing with unsaved changes kept its mark. A failed script reports `rolledBack: true` and leaves a clean drawing clean. The draw order table is counted under `containers`. The token: refused without it, with a wrong one and with the other instance's token; two instances each served their own drawing. 20000 lines added about 0.6 s to the call.
- **Fixed after the first live round (`b2e70dc`):** a preview had marked a clean drawing as modified; the draw order table had shown up as modified on most writes; a failed script's receipt had no `changes`.
- **Unverified in Civil 3D:** corridors and sample lines in the list; Undo after a preview; a failed commit; `CIVIL3D_MCP_REQUIRE_TOKEN=0` and `CIVIL3D_MCP_CHANGE_EVENTS=0`; how much of the overhead is the tracking itself.
- **Known limits by design:** a preview cannot show what Civil 3D updates only when a change is kept; side effects outside the drawing (files, system variables, queued commands) are not taken back; handle numbers used by a preview are not given back; the token does not separate programs that run as the same user.

## Measured heights and slopes of survey rows (2026-11-15)

The last part of the fourth review round: the measured cross slope of an existing road, as far as it is reading the drawing.

- **`survey_row_station_candidates`** has two new optional inputs. With `includeElevations` every listed point of a row carries its elevation, and the plan distance, height difference and slope in percent towards the next point across the road. The height is the geometry's Z (point, block insertion point, COGO elevation), or, with `elevationAttributeTag`, the number in that block attribute, for surveys drawn flat.
- Items on the same spot are one point: a height beats none and a height other than 0 beats 0, so a symbol drawn flat on top of a measured point does not hide the measured height. Each row and the whole result count known, missing, zero and conflicting heights of the candidate rows of the call, and the result warns when every height read from the geometry is 0, when only some are, when the named attribute holds no number and when one spot carries two different heights.
- The row search itself is unchanged and stays in one place; without the new inputs the result differs only by an empty `elevations` entry at the top and in each listed row.
- The recipe returns measured values only. Which points are pavement edges and which slope is acceptable is the caller's knowledge and is not part of this repository.

Evidence boundaries:

- **Proven offline:** the recipe compiles against the Civil 3D 2025 assemblies as a method and, bound by name with the new inputs, as a script; seven new host-free cases (heights written with a decimal point or comma, refused texts, the slope between neighbours including a missing height and two points on one spot, the attribute tag turning the elevations on, a refused tag, whose height counts on one spot, when two heights disagree) next to the 84 earlier ones, which pass unchanged.
- **Proven live (Civil 3D 2025, read-only, on copies of a real drawing and of its survey xref, 2026-11-15, two sessions, the second on the fixed recipe):**
  - The row search is unchanged: the same 24 candidates (the 14 hand-measured rows and 10 junction groups) with and without the new inputs, with heights from the geometry, from the attribute, and with the sources narrowed to the survey point block.
  - Heights from a block attribute inside an xref: 316 of the 317 row points have a height, none is 0; the one without is a label block. Six heights were read back independently from the xref and four neighbour pairs were recomputed by hand (distance, height difference, slope); all equal.
  - Heights from the geometry: all 317 row points have a height and 316 equal the attribute value within 1 mm. The first session found 41 heights of 0 because symbol blocks drawn flat sit on measured points and were read first, which gave slopes of thousands of percent in 13 rows; with the fix one 0 is left (the same label block, standing alone), the warning names it, and with the sources narrowed to the survey point block there is none. Three of the formerly hidden heights were read back from the xref next to the symbol's 0.
  - The top-level counts follow the station range: six ranges add up to the counts of the whole alignment.
  - Result size: with elevations the candidates list of the whole alignment is cut for size at 7 of 24 rows (12 without elevations) while `sampleLineStations` stays complete; six station ranges each came back whole.
  - DBMOD stayed 0.
- **Unverified:** the COGO elevation, a bare AutoCAD point next to a block with the attribute, a survey really drawn flat (the all-zero warning), and two different heights on one spot on a real drawing.
- **Known limits seen live:** a symbol or label standing alone at height 0 in a row still gives a false slope (warned); two points a few millimetres apart gave 133 % from an 11 mm height difference; a merged point keeps the id and label of the item read first.

## Pictures in a reply, and a PDF check of sheets (2026-11-14)

The first part of the fourth review round: an assistant can look at the drawing, and can check and plot sheets.

- **Pictures.** A script returns `ImageFile(path)` for a PNG or JPEG file it wrote. The plugin sends the file next to the JSON result and the MCP server passes it on as an MCP image block after the text; the JSON holds a numbered reference with type, byte count and pixel size. At most 4 pictures per result, 2 MB each, 4 MB together. `NewImagePath()` gives a file path in the plugin's image folder, which the plugin clears of files older than a day. The plugin reports the ability as the feature `images`.
- **`plot_view_image`.** Pictures the extents or a window of the model space or of a sheet through the plot engine and a raster device, so the picture shows plot styles, lineweights and paper space and does not depend on the screen.
- **`plot_layouts_to_pdf`.** Reads the page setup of each sheet, lists what would stop the plot (no device, a device or plot style table missing on the computer, a paper size the device does not offer) and, unless `plot` is false, plots every sheet to its own PDF file with its own page setup.
- **Recipe test.** Every template is now also parsed as a script, the form the plugin compiles; a statement that is valid only inside a method is caught offline.

Evidence boundaries:

- **Proven offline:** the serializer tests (PNG and JPEG recognised by content, pixel size of a PNG, each limit and its message), the plugin reply with pictures under both execution routes, the MCP server's image blocks after the text and the receipt, and rejection of malformed entries; both recipes compile against the Civil 3D 2025 assemblies as methods and, bound with sample values, as scripts.
- **Proven in two Civil 3D 2025 sessions on throwaway copies (2026-11-14, candidate builds):** every picture call returned a text block and an image block with type, bytes and pixel size, PNG and JPEG; a window of the model space showed exactly that area, as a wide or a tall picture after the window; a plot style table changed the picture; a sheet that was not current was pictured and the previous layout restored; unknown layout, unknown device and a partial window were refused with the lists; a missing file and a fifth picture failed with their messages, two pictures arrived as two blocks. The page setup check listed 16 sheets of a sheet template without a problem and left `DBMOD` at 0; four sheets were plotted to real PDF files in about 2.7 s each, two of them custom paper sizes; existing files were refused and `overwrite` replaced them.
- **Found live and fixed:** a page setup that stores its device with the full path was reported as not installed (now matched by file name; check and plot proven); a tall picture size won among equally large ones (the size now lies like the window, proven; for a sheet like its paper, proven as a code variant, the recipe text itself only compiled); `outputDirectory` was required also for the check alone; a JPEG reference had no pixel size.
- **Observed, not avoidable in the recipe:** a plot can mark the drawing as modified although no object is written. The graphics generation of a model space page moved `DBMOD` from 0 to 4 on read-only copies and to 5 on a writable one, also for a window with no Civil object in it; building the settings, reading the view, setting `BACKGROUNDPLOT` and the engine calls without graphics left it at 0. An extents picture of the model space also recomputed the drawing extents. A layout switch for the plot added 8 and 16. A sheet plotted while current stayed at 0. Which variable the plot touches is open. Both recipes report `dbmodBefore` and `dbmodAfter`.
- **Unverified:** a window on a sheet; a twisted model space view; a picture of a whole sheet with its paper edge (the layout plot area with fit-to-size came out small in a corner, so it is not offered); page setups with a missing device, paper size or plot style table (the messages exist, the cases were not produced); whether a given client shows the image blocks to its model.

## Queries without an Undo step, and a manual way out of a stuck operation (2026-11-14)

The rest of the third review round.

- **Undo.** A query (`civil3d_query`, the drawing identity) runs through its own internal command `C3DMCPREAD`, registered without an Undo marker. A write keeps `C3DMCPRUN` and stays one Undo step. A request runs only under the command it was queued for.
- **Manual release.** `C3DMCPRESET`, typed by the person at the computer, releases an operation that is stuck although Civil 3D is idle: a request that never started (nothing ran), or one whose script finished but whose command end the host never reported (`CIVIL3D.COMMAND_FAILED`, with the commit fact). It cannot interrupt a running script.
- **Start timeout.** `CIVIL3D.COMMAND_CONTEXT_TIMEOUT` says what is known about the cause: a prompt waiting for input, a command handed over but not begun, or no idle moment at all.

Evidence boundaries:

- **Proven offline:** the core tests for command registration and flags, read and write command separation, manual release in all three states and the three timeout messages; the shared dispatch contracts still pass under both routes on Windows CI.
- **Proven in a Civil 3D 2025 session on a throwaway drawing copy (2026-11-14, candidate build):** one `U` sent after a write followed by queries (three trivial ones and two recipes by name) removed that write; with two writes one `U` removed only the second; a query that changed an entity inside its own transaction left the entity unchanged, and the next `U` removed the write before it. `C3DMCPRESET` with nothing stuck ran and ended, and later calls worked. A request sent while `DELAY` was running ended after 15 s with `CIVIL3D.COMMAND_CONTEXT_TIMEOUT` and `not_started`, and the next request ran normally.
- **Probable:** on the previous build one `U` after a write and one query left the written line in place, which fits the query having left its own Undo step; that the `U` itself ran there was not observed directly.
- **Observed, not compared with the previous build:** a query that opened an object for write moved `DBMOD` from 0 to 1 although the change was rolled back. A plain query left `DBMOD` at 0.
- **Unverified:** a real stuck operation was not produced live, so `C3DMCPRESET` releasing one is proven only in the core tests; its command-line text was not captured live. The wording of the "no idle moment" message was changed after the live session (during a running command there is no idle moment either) and has only the offline test. The report that a freshly started Civil 3D needed one click before the first request was not reproduced in two fresh starts; no wake-up timer was added.

## Smaller replies and fewer rounds from the architecture review (2026-11-14)

The third round of the review is about what an assistant pays per call: the size of a reply, and the extra calls caused by a refused script, a missing name or a template sent back and forth.

- **Reply size.** A result is limited to 40000 characters. A longer one is shortened from the end of its largest lists (then long texts, then a table keyed by name) and every cut is listed under `truncated` with `reason: "size"`; before, 1000 items and 10000 nodes allowed replies of more than 100000 tokens, and a result over the node limit failed as a whole. A `double` is rounded to 10 significant digits with its whole part untouched. Enums return as their names.
- **Running by name.** 38 of the 39 recipes run by name (26 before). Twelve older recipes were given bindable parameters; `station_offset` and `create_polyline` became one template each. Plain lists (`double[]`, `string[]`, `int[]`, `long[]`, `bool[]`) can be bound besides rows. A missing required parameter is answered with a line that names every parameter with type and default.
- **Parameter card.** `civil3d_skills` `get` with `view: "card"` returns only the parameters of a recipe (type, default, row fields, one-line description), read from the template itself. Over the whole catalogue the cards are about 58000 characters, the texts without code 115000 and the full recipes 377000.
- **API lookup.** The exactly named type or member comes first instead of alphabetical order, and the pressure-pipe and data-shortcut assemblies are read when loaded.
- **Script guard.** The guard compares the types and members the compiler resolved with its list, instead of matching text patterns. It no longer refuses `process.StartStation`, `styleRegistry.Add` or a comment, and it does refuse `File.Delete` behind a `using` line, `new Process()`, `FileInfo.Delete`, `WebClient` and `Environment.FailFast`. It stays a guard rail, not a security boundary.

Evidence boundaries:

- **Proven offline:** the serializer, guard and lookup tests (rounding, name of an enum, each kind of cut and its report, 18 refused and 6 allowed script forms, ranking); all 40 templates and the twelve changed recipes bound with sample values compile against the Civil 3D 2025 assemblies; no shipped template is one the guard refuses.
- **Proven in a Civil 3D 2025 session on a throwaway drawing copy (2026-11-14, merged build):** rounding, enum names, the cut of a long list and of a 100000-character text with its report, the guard refusing `Process.Start` and `File.Delete` and allowing a look-alike, the lookup ranking and both added assemblies being read, the parameter card and the missing-parameter line; by name `surface_definition_summary`, `station_offset` in both directions, `surface_elevation`, `earthwork_report`, and the three rewritten write recipes (`create_polyline` in 2D and 3D, `create_cogo_points`, `create_tin_surface`), each written once and read back independently.
- **Unverified:** `corridor_summary`, `pipe_network_qc`, `list_profiles`, `section_inventory` and `surface_volume` ran but returned empty results, because the test drawing has no such objects.
- **Not changed:** `replace_alignment_with_fixed_primitives` still takes its table as adapted code. Making it bindable changes the input of a live-validated write recipe and belongs with a new live validation. The manual unlock command and the undo steps left by queries (G4, G5 of the review) followed separately; see the section above.

## The server hands over its own working order (2026-11-13)

Until now an assistant received only the three tool descriptions; the order of work around them (status first, identifying the target drawing, the safety inputs of a write, the receipt, what to do after an error) stood in the README and in separately installed skills. It is now written once, in [docs/USAGE.md](docs/USAGE.md), and the server delivers it:

- **One source.** `docs/USAGE.md`, in English, about the mechanism only. The build copies it next to the compiled server, so an installed build carries the text it was built with. The README, `docs/ENGINEERING_SKILLS.md` and four recipe notes refer to it instead of restating the older hand-made check and copy.
- **Always delivered.** The first part (under 2 KB) is the server's MCP `instructions`. Because not every client shows instructions, the descriptions of `civil3d_query` and `civil3d_execute` name the first step and point to the guide, and the `status` reply carries the same pointer.
- **On request.** `civil3d_skills` gains `action: "guide"` with an optional `topic` (`drawing`, `writing`, `errors`, `csharp`); each topic is under 6 KB. The three public tools remain; the format of the recipe files is unchanged.
- **Kept in step by tests.** Every error code named in the guide must exist in the code, every `civil3d_execute` input must be explained, the receipt, journal and status field names are compared with the source, and the sizes are limited. The text must be plain ASCII without a local path.

Evidence boundaries:

- **Proven offline:** over a real stdio connection the server sends the working order as its instructions and as the guide reply, and each topic is returned; the tool descriptions lead to the guide within their first 500 characters. The four compile-time API pitfalls in the guide were reproduced with the offline code-body check against the Civil 3D 2025 assemblies.
- **Unverified:** which MCP clients show server instructions to the model; an assistant that has only this server (no skill) carrying out a write in Civil 3D by following the guide. The API pitfalls listed as field reports have no reproduction in this repository.

## Operational safety from the architecture review (2026-11-11)

The second group of review findings concerns running the server day to day: which version runs, what a write did, and how several Civil 3D instances are told apart. The three public tools remain; `civil3d_execute` gains three optional inputs and `civil3d_skills` one action.

- **Choosing an instance without commands.** With several Civil 3D instances the host used to run an internal command in every one of them before each guarded call, also in instances another assistant was working in. The plugin now remembers its active drawing and repeats it in the health reply; the host matches `expectedDrawing` against those replies. The drawing guard in the plugin is unchanged and still decides.
- **Safety switches.** `requireClean` refuses a drawing with unsaved changes before the script runs; `backup` first copies the saved DWG to a time-stamped file (`backupDirectory` chooses the folder). An older plugin that would ignore them is detected and the call refused.
- **Receipt.** Every write that reached the drawing returns, next to its result, the drawing, `DBMOD` before and after, whether it was committed and saved, the file's size and time, and the backup path.
- **Journal.** The plugin appends one line per write request to a local monthly file: time, session, drawing, description, code hash, outcome. No code and no result. The `description` of a write is also shown on the Civil 3D command line.
- **Versions.** Server and plugin record the commit and time of their build. `civil3d_skills` `action: "status"` shows both, the running instances and their active drawings, and warns when the two sides come from different commits.
- **Installing.** `scripts/install.mjs` copies the built server to a folder of its own and the built plugin into the autoload bundle, with a backup of what it replaces. It does not change any client configuration. Since 2026-11-15 the installed server in `%LOCALAPPDATA%\Civil3dMcpRoslyn\server` is the documented standard place that clients start (README, Setup); the script prints the client settings for it and keeps only the newest 3 plugin backups it made.
- **Stale endpoint records.** A record whose process ID was reused by another program is now recognised by the program name; previously it was kept when the new owner was a system process.
- **Tests stay on their own.** Discovery also tries the default port 8080. With `CIVIL3D_MCP_DEFAULT_PORT_FALLBACK=0` it uses the endpoint records only; the routing tests set it, so they cannot reach a Civil 3D that runs on the same machine.
- **Documented counts.** The skill counts in the README and in this file are tied to the `skills/` folder by a test. The count in the discovery test is computed from the folder.

Evidence boundaries:

- **Proven offline:** plugin core tests for the remembered drawing (unknown, known, switched, none open, refreshed after an operation), `requireClean`, the backup (made before the script, never overwriting, refused when unsaved, unnamed or uncopyable), the receipt on success, failure and refusal, the journal content and its failure isolation, the command-line text, and the program-name check; Node tests for routing two instances without any identity command, the fallback for an older plugin, the feature check before a safety switch is sent, the receipt block, the status action and the install script. The plugin builds against the Civil 3D 2025 assemblies.
- **Proven live (2026-11-11, commit `3eeec27`, two Civil 3D 2025 instances on disposable copies):** the status action with matching builds; routing by `expectedDrawing` with no identity command in either instance; the refusal without a target; `requireClean` accepting a saved and refusing an unsaved drawing; `DBMOD` 0 after a host-managed save, so the switch works on consecutive saved writes; the backup of a drawing that Civil 3D holds open, in the default and in a given folder; the receipt on success, refusal and a failed script; the journal written by both instances; the description on the command line; the remembered drawing following a drawing switch within about half a second; both halves of the install script, including its refusal while Civil 3D runs; removal of a stale record whose process ID belongs to another program. Report: `docs/validation/operational-safety-3eeec27-2026-11-11.md`.
- **Observed, not explained:** one of the two freshly opened copies showed `DBMOD` 1 before any call. `requireClean` refuses such a drawing until it is saved.
- **Unverified in Civil 3D:** three or more instances; an older plugin next to a new one; a backup from a network drive or of a very large drawing; two instances writing the journal in the same moment.

## Reliability fixes from the architecture review (2026-11-10)

A read-only review of the whole server found several defects in timeouts, cancellation, discovery and result handling. This update corrects the first group. The three public tools and their existing inputs are unchanged; `timeoutSeconds` is a new optional input on the two code tools.

- **Script time limit.** The limit was fixed at 120 seconds on both sides. The Node host started its timer first and gave up first, so a slow call was reported as `outcome: "unknown"`; a script without loops could then still finish and commit with nobody listening. Now a call can set `timeoutSeconds` (1–1800), the host always waits longer than the plugin's limit, and a script that returns after its limit, or after the client is gone, is not committed. `CIVIL3D.TIMEOUT` is therefore a definite `reported_error`.
- **Client cancel.** A cancelled tool call (Stop) now closes the plugin connection, which stops the script at its next loop iteration and rolls it back. Previously the script ran on until the host timeout.
- **Stale endpoint records.** Records left by a crashed or killed Civil 3D were probed on every call. The host now skips records whose process is gone, and each new plugin session deletes them.
- **Skill search.** A query with one unknown word returned nothing. The closest skills are now returned and flagged `partial`.
- **Read-only drawings.** `saveDrawing` on a drawing opened read-only is refused before the script runs (`CIVIL3D.DRAWING_READ_ONLY`), instead of committing in memory and failing at the save.
- **NaN and Infinity.** One non-finite number failed the whole result without saying where. It now becomes `null`, with its path listed under `nonFinite`.
- **Ambiguous type names.** Bare `DBObject`, `Entity`, `ErrorStatus`, `Exception`, `Surface` and `Section` exist in two auto-imported namespaces and failed with CS0104. Each now has a default meaning; full names and the script's own aliases still win.
- **Tool annotations and dependencies.** The tools carry MCP read-only/destructive hints. `@modelcontextprotocol/sdk` is 1.32.2 and `zod` 4.6.7.

Evidence boundaries:

- **Proven offline:** Node tests for the reply wait, `timeoutSeconds` validation and forwarding, cancellation before and after sending, stale-record skipping with fallback, partial search and annotations. Host-independent .NET tests run the real Roslyn executor for the per-call limit, the discarded late result, the default aliases and their overrides, and non-finite serialization; plugin core tests cover the read-only refusal, limit validation and stale-record cleanup. The six aliased names were confirmed ambiguous against Civil 3D 2025 metadata, the plugin builds against those assemblies, and all 42 recipe code blocks compile both with and without the aliases.
- **Proven in a targeted Civil 3D 2025 check** ([report](docs/validation/reliability-fixes-9ac927e-2026-11-10.md)), one fresh instance on a disposable drawing under hu-HU: the default aliases, non-finite results, a 5-second and the default 120-second limit on looping scripts, a loop-free write discarded when it returned after its limit, a 130-second write under a 180-second limit, a client cancel that stopped a looping write without committing it, the read-only refusal before the script, a normal host-managed save, and removal of a stale endpoint record at plugin start.
- **Unverified:** a raised limit on a real long Civil operation, Stop pressed in a desktop client (the test sent the same MCP cancel from an SDK client), and stale-record handling while several Civil 3D instances run.

## Runtime and named-recipe updates (2026-10-05–06)

The accepted runtime now includes a bounded compiled-script cache keyed by exact source text, compilation/member/namespace diagnostics and runtime source-line reporting, cooperative loop cancellation, explicit rejection of asynchronous `await`, and broader bounded result serialization. Reviewed recipes can also run by name with validated parameters through the existing query/execute tools. The public surface remains exactly three tools; dynamic Roslyn execution, drawing guards, host-managed saving and the no-automatic-retry contract remain.

- **Proven in scoped offline and Civil 3D 2025 checks:** exact-code cache hits, compilation-error repeats followed by successful queries, and bounded eviction/collision tests. The [cache report](docs/validation/script-cache-2026-10-05.md) records the tested implementation and comparison baseline. General speedups and long-session memory behavior remain **Unverified**.
- **Proven in scoped checks:** a valid typed missing-member probe, namespace hints and runtime line reporting. The [initial diagnostics report](docs/validation/script-error-diagnostics-2026-10-05.md) and [retest](docs/validation/script-error-diagnostics-retest-2026-10-05.md) preserve the constructor limitation and residual unknown-name latency. A source-equivalent hint is not proof that every malformed expression can be corrected.
- **Proven in disposable live fixtures:** loop timeout/disconnect cancellation, write rollback and abandonment of a queued request. The [cancellation report](docs/validation/script-cancellation-2026-10-05.md) records the independent readbacks. Long individual Civil API calls are not interruptible, and these cases do not prove recovery of arbitrary native operations.
- **Proven in scoped live checks:** `await` is rejected with `MCP0001`, and subsequent ordinary and named queries succeed. See the [await-rejection report](docs/validation/await-rejection-91c55ad-2026-10-05.md).
- **Proven in scoped offline and live checks:** named execution of reviewed recipes, bound-parameter validation and the corrected Windows CRLF regression. The [named-recipe retest](docs/validation/run-by-name-9324388-2026-10-05.md) and [saved named-write/readback report](docs/validation/run-by-name-live-write-2026-10-05.md) establish one polyline-to-alignment write, save and independent reopen, plus specific populated reads. Broader production-model coverage and other named writes remain **Unverified**.
- **Proven in scoped checks at `b75e765`:** LINQ/lazy sequences, dates, tuples, script-defined return types and explicit collection-truncation metadata. The [serialization report](docs/validation/result-serialization-b75e765-2026-10-06.md) records 104 .NET cases and the targeted live query results. The live run did not repeat the offline runaway-lazy-loop cases; arbitrary custom getters and general performance remain **Unverified**.

The later accepted dependency-only update installs `proxy-addr 2.0.8`; the retained local receipt records a successful Node build, 101 Node tests, a fresh three-tool MCP smoke check and zero known npm advisories at that check. The Civil plugin source was unchanged by that dependency update. These are recorded results, not a fresh live certification of every capability. Existing desktop clients still need reconnect/restart after Node module or tool-description updates.

The older sections below retain their original dates and evidence scopes. The linked reports provide the newer runtime details; historical test totals must not be treated as the current suite size.

## Recipe discovery metadata (2026-10-05)

The 29 stable recipes now carry specific Hungarian/English task aliases, workflow tags, a tested Civil version and a scoped validation summary. List/search expose write scope and concise evidence; aliases are included in the full `get` response. Search normalizes accents and hyphens while preserving all-word matching, category filtering and pagination. See [the catalogue contract](docs/SKILL_CATALOG.md).

The extension is additive. Missing or invalid evidence remains unknown, and exact-key consumers must accept the new fields. Existing names, categories, descriptions, parameter definitions, write requirements and all C# recipe bodies are unchanged. The Node catalogue module changes; the Civil plugin, transport, save and retry contracts are unchanged. A newly started MCP server loads the new behavior; existing servers need a client reconnect/restart.

**Proven offline:** TypeScript typecheck/build, all 24 skill/API Node tests, metadata compilation of 32 Civil 3D 2025 templates, 83 host-free input/math cases and the three-tool/29-skill MCP catalogue check passed. Fixtures cover legacy defaults, quoted inline metadata, CRLF/Unicode, malformed fields and real Hungarian/English task queries. The control-character regression fails before the fix and passes after it. The catalogue checks do not connect to or modify a Civil drawing.

**Unverified:** automatic task selection quality, general latency/token savings and new live engineering cases. The recorded version includes offline API compilation and does not imply live certification. Existing recipe-specific evidence and remaining limitations are preserved.

## Boundaries and evidence

- Autodesk Civil 3D 2025 is the primary target; retained live checks used Civil 3D 2025 Hungary. Other versions are **Unverified**.
- Dynamic Roslyn/C# execution remains the core, with exactly `civil3d_query`, `civil3d_execute` and `civil3d_skills` as public MCP tools.
- **Proven** means supported by the stated code, offline test or targeted live evidence. **Probable** means a supported explanation that is not fully established. **Unverified** means a fresh targeted check is still needed.
- Offline tests do not establish live Civil behavior. The live results below concern specific disposable fixtures, not universal feature or road-standard compliance.

## Included capabilities

| Capability | Evidence and limits |
| --- | --- |
| Benchmark recorder and opt-in read-only runner | **Proven offline and for one live query scenario:** five cold and five warm first-pass successes. Other benchmark scenarios and total workflow/token savings remain **Unverified**. |
| Structured errors, bounded framing/results, serialized execution and drawing guards | **Proven offline**, with targeted normal live execution and saved-write checks. Uncertain outcomes are never retried automatically. |
| Private health, audit logging and session idempotency | **Proven offline**, with responsive live health during pending work. Logs omit code and drawing content. Idempotency is session-scoped, not durable exactly-once execution. |
| Filtered/paged skills and bounded API lookup | **Proven offline**; skill discovery was also checked through a fresh client. Lookup reads already-loaded allowlisted public metadata without running Civil code. |
| Multiple-instance routing | **Proven live:** two Civil sessions used distinct endpoints, ambiguous access failed closed, and selected requests reached the intended drawing fingerprints with `DBMOD=0`. |
| Bounded road-model inventories | **Proven offline and on an empty live fixture:** profiles, corridors, sample lines/sections, gravity pipe-network QC, TIN definition counts, selection and drawing identity. Populated cases are not all live-validated. |
| Road-design style readiness | **Proven live** for twelve baseline style groups in one Hungary template. Other templates remain **Unverified**. |
| Alignment from an identified polyline | **Proven live**, including saved-file reopen. The source is preserved; this narrow recipe adds no automatic curves or standards decisions. |
| Connected alignment geometry audit | **Proven offline and live** on straight and compound line/arc/clothoid geometry. Standards compliance is explicitly not evaluated. |
| Fixed-primitive alignment replacement | **Proven offline and in one isolated live case**, including save/reopen of an eight-primitive reverse-clothoid chain. The recipe requires an audited independent, siteless, zero-station centreline without station equations, superelevation, criteria/check state, profiles or corridor dependencies. |
| Six reusable engineering recipes | Explicit-PVI design profiles, individual section views, native material quantities, sampled TIN differences, data-reference state and corridor targets. **Proven offline**, with the scoped live evidence and remaining limits below. See the [recipe guide](docs/ENGINEERING_SKILLS.md). |
| Experimental surface profile and ordinary profile view | **Proven offline; live authoring Unverified.** Creates one full-length dynamic profile from an explicitly identified local alignment and TIN surface, plus one view using existing named styles. Rejects reference/volume sources, duplicate names and invalid inputs. Finite endpoint elevations do not prove that the surface has no interior gaps. |
| Post-commit saving | **Proven live** with `saveDrawing: true`, `DBMOD=0` and independent readback. Saving occurs after transaction/lock disposal; scripts must not call `Database.SaveAs` or queue `QSAVE`. |

At the 2026-10-02 publication the catalog contained 29 skills and 32 C# templates, behind the same three public tools; the current numbers are under [Current catalogue](#current-catalogue). The surface-profile recipe is experimental and has no live authoring evidence. Build instructions and test commands remain in [README.md](README.md) and `package.json`; Autodesk reference assemblies must be supplied locally and are not distributed.

**Proven offline for the previous accepted snapshot (2026-09-03):** TypeScript typecheck/build, plugin Release build, benchmark/error/discovery/routing tests, plugin core and serialization tests, transport tests, the three-tool MCP smoke check, all 24 template metadata compilations and 18 fixed-primitive input checks passed. NuGet vulnerability-feed retrieval produced an environment warning; these checks are not a dependency-security audit.

## Node search, output and configuration correction (2026-10-02)

The Node server now matches all normalized search words across skill and parameter metadata, returns compact success JSON, defaults to the plugin's IPv4 loopback listener and falls back safely for invalid command/save timeouts or log levels. Public tool descriptions clarify synchronous Civil scripts and the bounded return-value contract. The unused direct WebSocket dependency was removed, and six transitive packages were updated within the dependency lockfile.

The submitted patch was reviewed against the current public source. The review corrected inherited-property names being accepted as log levels, normalized-empty searches returning the whole catalog, and incomplete namespace/result guidance. Timeout, logging, parameter-search, empty-search and compact query/execute/save regressions are covered by focused tests.

**Proven offline on the publication files:** TypeScript build, all 88 Node tests (benchmark, errors, routing, transport, drawing guards and skill discovery), and the three-tool/29-skill MCP smoke check passed. A clean lockfile installation with lifecycle scripts disabled succeeded; npm's dependency audit reported zero known vulnerabilities at the time of the check. This audit is limited to the package advisory data.

**Unverified:** general runtime latency/token savings and fresh live Civil 3D behavior after this Node update. The integration tests use an isolated local TCP mock; they do not load or modify a Civil drawing. The Civil plugin and its C# execution/save/retry contracts are unchanged. Existing recipe evidence below retains its original scope.

## Reusable engineering recipes and publication checks (2026-10-02)

Six focused recipes use the existing skill catalog and dynamic C# execution path. The plugin runtime and Node implementation are unchanged. Profiles require explicit approved PVI values; section views require an existing sampled group and named styles. The four read recipes report existing native quantities, sampled elevations, reference state or target assignments without repairing the model.

**Proven offline on the publication files:** TypeScript build, all 15 skill/API-discovery tests, metadata compilation of all 32 C# templates, 18 fixed-primitive, 11 surface-profile and 54 engineering input/math cases, and the three-tool/29-skill MCP smoke check passed. These checks did not open or modify a Civil drawing.

**Proven in retained, scoped Civil 3D 2025 evidence (2026-10-01):** a three-PVI design profile with one symmetric parabola survived authoring/save/independent reopen. Three section views passed complete independent saved-state readback, empty-band checks, requested bounds and grid-overlap checks. A known +1 TIN comparison and an out-of-surface point exercised all-point statistics and coverage; an existing three-section native material list returned the independently expected fill total. All four targeted read recipes retained `DBMOD=0`. Reference reporting was checked on local objects, and the corridor audit on an empty corridor with one baseline.

**Unverified:** data-bearing DREF source states, populated corridor target warnings, broader profile combinations, populated section-view bands, section-view saved-file reopen/print quality, model freshness and road-standard compliance. Saved-state readback is not a section-view reopen. Post-creation native dirty flags were reconciled with a geometry-neutral host-managed save and independent `DBMOD=0` checks; successful creation was not repeated and the runtime save/retry contract did not change.

Detail caps do not establish complete coverage. Surface statistics include all supplied valid points; reference and target scans report incomplete coverage explicitly. Native material rows represent intervals ending at their stations, so selected rows do not prove an exactly clipped volume at arbitrary range boundaries. Inputs, limits and upstream idea/license attribution are recorded in [docs/ENGINEERING_SKILLS.md](docs/ENGINEERING_SKILLS.md).

## Experimental surface-profile recipe (2026-09-28)

The recipe and its tests are now published with their evidence limits. The plugin runtime and Node implementation are unchanged. Use the existing drawing guard, outer transaction and optional `saveDrawing` path; the recipe does not save or commit independently. A fresh targeted Civil 3D 2025 authoring/save/readback test is still required before claiming live validation.

**Proven offline on the publication files:** TypeScript build, all 14 skill/API-discovery tests, metadata compilation of all 25 C# templates, 18 fixed-primitive and 11 surface-profile input cases, and the three-tool/23-skill MCP smoke check passed. These checks did not open or modify a Civil drawing. Dependency vulnerability auditing was disabled for the metadata test restore; this is not a dependency-security audit.

## Offline C# code-body checks (2026-09-28)

The skill test runner now accepts `--code` followed by an absolute `.cs` file path. It uses the local Civil 3D metadata and standard context parameters to report compilation diagnostics without executing the body. Invalid arguments, missing input files and missing Autodesk references return exit code 2; compilation errors return 1. The no-argument catalog checks remain available.

**Proven offline on the publication files:** all 25 template compilations and 29 input cases still pass. Eight focused command-line checks passed: a valid Civil API body, an invalid API member, a throwing body that was not executed, relative paths, missing files, wrong extensions, unknown flags and missing reference assemblies. **Unverified:** successful metadata compilation does not prove live Civil behavior or runtime loading.

## Modal command completion

- **Selection compatibility follow-up:** the internal modal command also carries `UsePickSet | Redraw`, preserving implied/PickFirst selection when entering the command and reading it. It remains modal and does not suppress Undo markers; no `NoUndoMarker` flag was added.
- **Proven offline for the follow-up:** all 66 core/serialization cases pass, including 20 shared contracts exercised under both native and modal dispatch. The test assembly now compiles the production plugin entry and checks its command metadata. Native-awaitable diagnostics remain backend-specific. Test host doubles do not establish live selection, Undo, or latency behavior.
- **Proven in a targeted Civil 3D 2025 selection check:** two synthetic lines remained selected after an unrelated query, two consecutive selection queries, and a geometry-neutral execute followed by another selection query, with `DBMOD=0`. This used an isolated source-equivalent test module with distinct command names to coexist with the installed runtime; the module was removed afterwards and diagnostics stayed disabled. Broader Undo/Redo behavior and general small-query latency remain **Unverified**.

- **Proven in code and targeted Civil 3D 2025 checks:** the default backend runs the existing dynamic Roslyn body through a genuine modal command, avoiding the native `ExecuteInCommandContextAsync` completion path. An isolated comparison using identical drawing copies and the same rebuild/save operation reproduced the old completion hang; both the candidate and the final installed modal build returned, and subsequent queries also completed.
- The same drawing guard, transaction, post-commit save and serialization gate remain. The gate requires both body disposal and the matching command lifecycle event. Only an opaque one-use token enters the command input; abandoned tokens cannot run later requests. Started work is never released on timeout or automatically replayed.
- Private health reports `executionBackend`. `CIVIL3D_MCP_EXECUTION_BACKEND=native` is reserved for an explicitly selected fresh-session comparison; there is no automatic fallback. Detailed completion diagnostics are disabled by default and contain only bounded, fixed metadata when explicitly enabled.
- **Proven offline for this update:** the plugin build, plugin core/serialization and modal admission/lifecycle tests, TypeScript checks and the three-tool MCP smoke check passed. This is targeted execution-completion evidence, not proof of every possible long-running or asynchronous script scenario.
- Corridor dependency freshness is outside this execution-completion fix. This update does not infer an MCP defect from a corridor's stale flag, and does not change corridor or data-reference modeling behavior.

## Earlier command-context correction

- **Proven offline:** a 15-second atomic admission deadline rejects a callback that has not started, returning `CIVIL3D.COMMAND_CONTEXT_TIMEOUT`, `outcome: not_started` and `retryable: false`. A late abandoned callback does no drawing work. Once an operation starts, its gate is retained until native completion.
- **Proven local API inspection and deterministic reproduction:** a completion recheck closes a lost-notification window in the Civil 3D 2025 native awaitable. The completion race is a **Probable** contributor to earlier hangs, not a proven explanation of every case.
- **Proven offline:** private health reports only fixed stage names and elapsed times. Failure/cancellation, stale callbacks, subsequent requests and started-operation serialization are covered by regression tests.
- **Proven live on the source-equivalent development build:** with no drawing open, one request returned the non-retryable pre-start error after about 15 seconds and health returned to idle. After opening a disposable drawing, a new request succeeded in the same Civil process without restart. A separate backed-up disposable copy completed a geometry-neutral save, follow-up query and normal close/reopen with `DBMOD=0`.
- **Unverified:** elimination of every intermittent post-save or side-database hang, native cancellation, or recovery of arbitrary already-running code. Do not clear the gate or repeat an uncertain write as automatic recovery.

The publication build is checked offline; it is not a separate live installation. Public source is aligned with the accepted development implementation, and this source update does not alter the active Civil session.

## Not yet included

Automatic grade design, general corridor authoring, automatic standards compliance and later Civil version support are not claimed. The explicit-PVI recipe creates a profile from supplied values; it does not choose design criteria. The experimental surface-profile/profile-view recipe still has no live authoring validation.

Further development should address an observed work problem or explicitly approved validation. Do not introduce a general refactor or new public tool without a concrete demonstrated need.
