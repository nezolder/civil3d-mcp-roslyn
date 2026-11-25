# Civil 3D MCP Server — Dynamic Roslyn Fork

An MCP server that enables AI assistants to **write and execute C# code** directly inside Autodesk Civil 3D. Instead of a large set of fixed tools, the AI generates task-specific code that runs with Civil 3D API access.

## Project Scope and Lineage

This fork keeps the dynamic Roslyn/C# execution model and a deliberately small public surface of three MCP tools. Its current compatibility baseline is Autodesk Civil 3D 2025, with local work focused on reliability, safety, measurable efficiency, and reusable Civil 3D skills. Other Civil 3D versions can be added later through separately verified compatibility work.

The project is derived from [barbosaihan/civil3d-mcp](https://github.com/barbosaihan/civil3d-mcp). [SantosSjba/mcp-to-c3d](https://github.com/SantosSjba/mcp-to-c3d) was evaluated for selected, test-backed ideas, while [Sacred-G/Civil3D-mcp](https://github.com/Sacred-G/Civil3D-mcp) was used only as an architectural reference. See [PROVENANCE.md](PROVENANCE.md) for the detailed attribution and licensing boundaries.

This independent project is not affiliated with or endorsed by Autodesk. Autodesk assemblies and other proprietary Civil 3D files are not included.

## Architecture

```
+-------------------------+--------+-------------------------+--------+-------------------------+
| AI Assistant            | <----> | MCP server              | <----> | Civil 3D plugin         |
| (Codex, clients)        | stdio  | 3 public MCP tools      | TCP    | Dynamic Roslyn engine   |
+-------------------------+--------+-------------------------+--------+-------------------------+
                                                |                                  |
                                                v                                  v
                                   +-------------------------+--------+-------------------------+
                                   | Skills Library          |--------| C# execution            |
                                   | (*.skill.md)            |--------| Civil 3D API            |
                                   +-------------------------+--------+-------------------------+
```

## Exactly 3 Public MCP Tools

| Tool | Purpose | Safety |
|------|---------|--------|
| `civil3d_execute` | Execute C# code with **write** access; optional preview that keeps nothing, clean-drawing check and backup before, save after, and a receipt of what happened, including the objects added, modified and erased | ⚠️ Modifies drawing |
| `civil3d_query` | Run intended read-only C# without committing the host transaction | Trusted code only; not a side-effect sandbox |
| `civil3d_skills` | Browse/search/read code skill templates; `api_lookup` searches already-loaded public Civil 3D API metadata; `status` shows the server and plugin versions and the running instances; `guide` returns the working order for assistants | ✅ Metadata only |

The tools carry MCP annotations so a client can tell them apart: `civil3d_skills` is read-only, `civil3d_execute` is a non-idempotent write, and none reaches outside the local Civil 3D session. `civil3d_query` is deliberately not advertised as read-only: its transaction is rolled back, but it still runs arbitrary code.

### Working order for assistants

How an assistant works with the server (status first, identifying the target drawing, sending a write with its safety inputs, reading the receipt, what to do after an error or a timeout) is written in one place: **[docs/USAGE.md](docs/USAGE.md)**. The other sections of this README describe what the server does; they do not restate that order.

The server hands the guide to the assistant itself, so nothing else has to be installed:

- its first part is sent as the MCP server `instructions` when a client connects;
- `civil3d_skills` with `action: "guide"` returns the same part, and with `topic` (`drawing`, `writing`, `errors`, `csharp`, `civil`) the details;
- the descriptions of `civil3d_query` and `civil3d_execute` name the first step and point to the guide, because not every client shows server instructions;
- the `status` reply ends with a `guide` line that says the same.

The guide covers only how this server works. Where backups are kept, how many attempts are allowed and how results are reported are the user's own rules and are not part of it.

### How It Works

1. **AI reads a skill** → Gets a documented C# code template
2. **AI adapts the code** → Fills in parameters, combines patterns
3. **AI sends code** → Via `civil3d_execute` or `civil3d_query`
4. **Roslyn compiles + runs** → Inside Civil 3D with full API access
5. **Results return as JSON** → Back to the AI

### Example Interaction

```
User: "What surfaces are in my drawing?"

AI: Uses civil3d_query with:
  var surfaces = new List<object>();
  foreach (ObjectId id in CivilDoc.GetSurfaceIds()) {
    var s = Transaction.GetObject(id, OpenMode.ForRead) as TinSurface;
    surfaces.Add(new { s.Name, s.Layer });
  }
  return surfaces;

Result: [{ "Name": "EG", "Layer": "C-TOPO-EG" }, ...]
```

## Skills Library

`civil3d_skills` also supports `action: "api_lookup"` for a bounded, read-only search of public type and member names/signatures from already-loaded allowlisted Civil 3D host assemblies. It does not load assemblies, run C# code, or access the active drawing. Supply a query and optionally an assembly, namespace prefix, and result limit. The best match comes first: the type named exactly by a query word, then a member named exactly so, then types that start with the word, before the remaining matches in alphabetical order. Besides the core assemblies the search reads the pressure-pipe and data-shortcut assemblies when Civil 3D has loaded them. `action: "status"` reports [versions and running instances](#versions-and-installing-a-build), and `action: "guide"` returns the [working order for assistants](#working-order-for-assistants).

Skills are documented C# code templates in `skills/`:

The catalog contains **47 skills**. Six focused engineering recipes cover design profiles from explicit PVIs, individual section views, existing native material quantities, sampled TIN elevation comparison, data-reference state and corridor target audits. Use `civil3d_skills` to search/get a recipe, bind project-approved inputs and run it through the existing query/execute tools. Recipes marked `run_by_name` (46 of 47, all except `replace_alignment_with_fixed_primitives`, whose input is a free-form table) can run directly with `skill` and `params` instead of `code`, so the template is not sent back and forth, and `get` with `view: "card"` returns just their parameters with type, default and row fields; see [running by name](docs/SKILL_CATALOG.md#run-a-recipe-by-name). For earthwork quantities, `create_tin_volume_surface` keeps a TIN volume surface between two surfaces (with cut/fill factors) in the drawing, and `bounded_volumes` reads its cut, fill and net volumes inside labelled closed polylines, for example one per structure. For horizontal layout, `create_alignment_from_pis` builds an alignment from PI coordinates with an arc or clothoid-arc-clothoid group at each PI, and `create_offset_alignments` adds dynamic offset alignments, such as edges of pavement, to an existing alignment. For cross sections, `create_sample_lines` places perpendicular sample lines at explicit stations, an interval or geometry points with checked vertices and station-based names, `create_section_view_group` creates one native section view group (draft grid or template sheet placement, band sources, view names), `shift_section_view_elevations` moves the views' elevation window, keeping each view's height or setting a sheet height below the lowest ground point, `plan_section_sheet_fit` reads the template layouts to show which sheet fits the views' width and elevation height, `rename_sample_lines_by_station` renames sample lines after their actual station with a configurable name format, previewing the names first, and `survey_row_station_candidates` reads hand-measured survey rows across the road (AutoCAD points, survey blocks, also inside xrefs, or COGO points) and lists one candidate station per row, also in dense junctions, with an optional filter on the surveyor's point codes and a review flag for doubtful rows, ready for `create_sample_lines`; on request it also returns the measured height of each point and the distance, height difference and slope between neighbouring points of a row, read from the geometry or from a block attribute of a survey drawn flat. For looking at a drawing, `plot_view_image` returns a picture of the model space or of a sheet as the plot engine draws it (extents or a window), and `plot_layouts_to_pdf` checks the page setup of sheets (device, paper size, plot style table, scale) and plots each one to its own PDF file. For checking a save, `saved_drawing_readback` reads the saved DWG file in a separate database without opening it and compares chosen objects and model space counts with the open drawing. For editing ground models, `delete_tin_surface_points` deletes chosen TIN surface points (by position, or inside a boundary with an optional elevation filter) as one surface definition operation, listing the matches first on request, and `create_breaklines_from_survey_strings` joins surveyed points that run along the road (by point code or layer, also from xrefs) into 3D polylines, leaving out ties that are too long or jump sideways, and can add them to a TIN surface as one separate breakline group. `create_design_profile_from_pvis` can set each vertical curve by a K-value instead of its length, so the radius stays when a tangent moves. For corridors, `add_sample_line_stations_to_corridor` adds the stations where sample lines cross each baseline as extra corridor stations, and `corridor_section_check` checks the built sections at those or the corridor's own stations for repeated codes on one side, overlapping links, missing shape codes and too short links. For data shortcuts, `data_shortcut_folder_audit` reads a project's `_Shortcuts` folder and reports duplicate keys, keys left behind after a rename and keys whose source object is missing or renamed. See [the recipe guide](docs/ENGINEERING_SKILLS.md) for inputs, limits and upstream attribution.

Search also recognizes specific Hungarian/English task aliases and workflow tags, with accent/hyphen normalization: for example `hossz-szelvény`, `keresztszelvény`, `cross section`, `közmű` and `mennyiség`. List/search show write scope, the tested Civil version and a short validation summary. Offline compilation, live execution, saved readback and reopen remain separate evidence; missing evidence stays unknown. Aliases are included in `get` rather than every page. See [the catalogue contract](docs/SKILL_CATALOG.md) for response fields, legacy defaults, compact discovery and metadata authoring.

Skill search matches every supplied word across names, categories, descriptions and parameter metadata, in any order. Underscores and whitespace are equivalent: `surface volume` and `volume surface` both find `surface_volume`. When no skill contains every word, the skills closest to the query are returned instead and the reply carries `partial: true` with `matchedWords` and `queryWords`, so one extra or unknown word does not hide the catalog; closeness is the total length of the matched words, and words shorter than three letters do not count toward it. A query with no normalized word, or with no matching word at all, returns no matches. Successful tool results use compact JSON with the same fields and values.

**Proven:** offline compilation for all six and scoped Civil 3D 2025 synthetic-fixture checks for profiles, section views, surface comparison and native quantities. Reference reporting was checked only on local/empty objects, and target auditing on an empty corridor. Populated DREF/target cases, populated section-view bands, section-view saved-file reopen/print quality and general model/standards correctness remain **Unverified**. The separate `create_surface_profile_view` recipe is proven offline; live authoring and saving remain **Unverified**. Full evidence boundaries are in the guide and [PROJECT_STATUS.md](PROJECT_STATUS.md).

```
skills/
├── surfaces/           # Surface operations
├── alignments/         # Alignment + station/offset
├── profiles/           # Profile inventory + controlled profile creation
├── corridors/          # Corridor summaries + target audit
├── sections/           # Inventory, controlled views + native material quantities
├── references/         # Bounded data-reference state audit
├── pipe_networks/       # Gravity pipe-network QC
├── points/             # COGO points
├── geometry/           # Lines, polylines, text
├── drawing/            # Drawing info
└── workflows/          # Complex multi-object operations
```

### Script Globals

Code executed via `civil3d_execute` or `civil3d_query` has access to:

| Global | Type | Description |
|--------|------|-------------|
| `Document` | `Document` | Active AutoCAD document |
| `CivilDoc` | `CivilDocument` | Active Civil 3D document |
| `Database` | `Database` | Document database |
| `Transaction` | `Transaction` | Active transaction |
| `Editor` | `Editor` | Document editor |
| `ImageFile(path)` | function | Marks a PNG or JPEG file as a picture to return; see [pictures](#pictures) |
| `NewImagePath(extension)` | function | A fresh file path in the plugin's image folder for a plot or export (`"png"` by default, also `"jpg"` and `"pdf"`) |

### Return values

Return a value to get it back as JSON. Supported: primitives, strings, `Guid`, dates and times (ISO 8601 text), anonymous objects and classes or records declared in the script (public properties and fields, camelCase names), arrays, lists and any other sequence including LINQ queries without `.ToList()`, tuples (as arrays; element names do not exist at run time), `Dictionary<string, …>`, `ObjectId`, `Handle`, `Point2d/3d` and `Vector2d/3d`. Enums return as their names (`"Tangent"`; combined flags as `"A, B"`). `DBObject` instances are refused.

Replies are kept small for the assistant that reads them. A `double` is rounded to 10 significant digits, and its whole part is never rounded: `650123.456789012` returns as `650123.4568`, `0.30000000000000004` as `0.3`. That is a tenth of a millimetre on a seven-digit coordinate and far finer than any angle or slope in a drawing; a script that needs every digit returns the number as text. `float` and `decimal` values are returned as they are.

A collection with more than 1000 items keeps its first 1000, and the reply is then wrapped as `{"result": …, "truncated": [{"path": "$.items", "returned": 1000, "total": 5234}]}`; `total` is `null` for a lazy sequence whose length is unknown. A lazy sequence runs the script's own code while the result is read, still inside the transaction and under the same loop checkpoints and timeout; an exception there is reported with its type and path.

The whole result is limited to 40000 characters of JSON, which is roughly 10000 to 16000 tokens. A longer result is shortened instead of refused: the largest list loses items from its end, then the longest text its tail, and only when neither is left a table keyed by name its last entries. Lists also stop taking items once about 10000 values were converted. Each such cut is listed under `truncated` with `"reason": "size"`, for example `{"path": "$.rows", "returned": 412, "total": 900, "reason": "size"}`, or with `returnedCharacters` and `totalCharacters` for a text. Ask for the rest with a narrower script (a name, a station range, `Skip` and `Take`). Depth stays limited to 8.

NaN and Infinity have no JSON form. Each one becomes `null`, and the reply is wrapped as `{"result": …, "nonFinite": {"count": 3, "paths": ["$.rows[4].elevation", …]}}` with the first 20 paths, so one undefined number no longer discards the whole result. `truncated` and `nonFinite` appear side by side when both apply; a result with neither keeps its plain shape.

### Pictures

A script can hand a picture to the assistant: it writes a PNG or JPEG file, for example by plotting to a raster device, and returns `ImageFile(path)` anywhere in its result. The plugin reads the file and sends it next to the JSON result; the MCP server passes it on as an image block after the text. In the JSON the picture is replaced by a reference in the order of the image blocks, `{"image": 1, "mimeType": "image/png", "bytes": 48213, "width": 1600, "height": 1280}`.

One result carries at most 4 pictures, 2 MB each and 4 MB together; more, a file of another type or a missing file fails the call with a message instead of dropping the picture silently. `NewImagePath()` gives a path in `%LOCALAPPDATA%\Civil3dMcpRoslyn\images`; the plugin removes files older than a day from that folder when it hands out a new path, because a script cannot delete files. The `plot_view_image` recipe uses both. Whether a client shows image blocks of a tool result to its model depends on the client. An older plugin does not know `ImageFile`, so such a script fails there at compilation; the plugin lists `images` among its `features` in the `status` action.

All Civil 3D namespaces are auto-imported. A few type names exist in two of them and would otherwise be rejected as ambiguous (CS0104); they have a default meaning:

| Bare name | Means | Other type, by full name |
|-----------|-------|--------------------------|
| `DBObject`, `Entity` | `Autodesk.AutoCAD.DatabaseServices.*` | `Autodesk.Civil.DatabaseServices.*` |
| `ErrorStatus` | `Autodesk.AutoCAD.Runtime.ErrorStatus` | `Autodesk.Civil.ErrorStatus` |
| `Exception` | `System.Exception` | `Autodesk.AutoCAD.Runtime.Exception` |
| `Surface`, `Section` | `Autodesk.Civil.DatabaseServices.*` | `Autodesk.AutoCAD.DatabaseServices.*` |

A full name, the script's own `using X = …;` alias or a type the script declares still wins. Other duplicated names (for example `Table`) stay ambiguous and need the full name.

Committing the `civil3d_execute` transaction changes the open drawing but does not by itself write the DWG file to disk. A write must name its target as `expectedDrawing` (full `Database.Filename` and `FingerprintGuid`); the plugin checks that identity immediately before Civil API access. `saveDrawing: true` also saves the completed change. How the target is identified and in which order a write is sent is in the [usage guide](docs/USAGE.md). The plugin saves only after the script transaction and document lock are closed; scripts must not call `Database.SaveAs` or queue `QSAVE` themselves. The save request uses a separate 10-minute default timeout and is never retried automatically. A drawing that is open read-only is refused with `CIVIL3D.DRAWING_READ_ONLY` (`outcome: "not_started"`) before the script runs, instead of committing the change in memory and failing only at the save.

### Safety switches, receipt and journal for writes

`civil3d_execute` can do the checks around a write itself, so the assistant does not need separate calls for them. This section says what each input does; when to use them, and what to do with the receipt and the journal after an error, is in the [usage guide](docs/USAGE.md) (topics `writing` and `errors`).

| Input | Effect |
|-------|--------|
| `requireClean: true` | Runs only when the drawing has no unsaved changes (`DBMOD` is 0). Otherwise nothing runs: `CIVIL3D.DRAWING_NOT_CLEAN`, `outcome: "not_started"`. |
| `backup: true` | Copies the saved DWG file to a time-stamped copy before the script runs, for example `Plan.20261111-103000.dwg`. The copy is verified by size, and an existing file is never overwritten. If it cannot be made, nothing runs (`CIVIL3D.BACKUP_FAILED`); an unnamed drawing is refused with `CIVIL3D.SAVE_PATH_REQUIRED`. Because the copy shows the last saved state, `backup` also asks for a clean drawing unless `requireClean: false` is set explicitly. |
| `backupDirectory` | Absolute folder for that copy. Default: `CIVIL3D_MCP_BACKUP_DIR` of the Civil 3D process, else `%LOCALAPPDATA%\Civil3dMcpRoslyn\backups`. Copies are never deleted automatically. |
| `description` | One line about the change. It is shown on the Civil 3D command line as `[Civil 3D MCP] …` before the script runs, and kept in the journal. |
| `preview: true` | Runs the script with write access and then abandons its transaction: nothing is committed, saved or backed up, and an `idempotencyKey` is not used up. The other inputs stay as the real call will have them, and their checks (target drawing, clean state, a file that can be saved) still run, so a preview also shows that the real call would be refused. The receipt lists what the script would change. Needs a plugin with the feature `preview`; the server never sends a preview to an older plugin, which would commit it. |
| `changeItems` | How many objects the receipt's change list names in each group, 0 to 100, default 10. With 0 only the counts by type remain. |

Every write that reached the drawing returns a **receipt** next to its result. The result stays alone in the first text block; the receipt is a second text block:

```json
{"receipt": {"drawing": {"databaseFilename": "C:\\Projects\\Plan.dwg", "fingerprintGuid": "{…}"},
  "dbmodBefore": 0, "dbmodAfter": 1, "preview": false, "committed": true, "saved": true,
  "fileLastWriteUtc": "2026-11-11T09:30:00.0000000+00:00", "fileBytes": 1843200,
  "backupPath": "C:\\Users\\me\\AppData\\Local\\Civil3dMcpRoslyn\\backups\\Plan.20261111-103000.dwg",
  "changes": {
    "added": {"count": 3, "byType": {"Polyline": 2, "Alignment": 1},
      "items": [{"type": "Alignment", "handle": "2F3A", "name": "Axis-1", "layer": "C-ROAD"}, …]},
    "modified": {"count": 1, "byType": {"Profile": 1}, "items": [{"type": "Profile", "handle": "1C02", "name": "FG"}]},
    "erased": {"count": 0},
    "containers": {"BlockTableRecord": 1, "DBDictionary": 2},
    "complete": true}}}
```

`committed` says whether the change is in the open drawing, `saved` whether this call wrote the file, and the file fields describe the file on disk after the call. A refused or failed write carries the same block (and `structuredContent.receipt`), so a failed save is visibly a committed, unsaved change. Queries have no receipt.

#### What a write changed

`changes` is the plugin's own account of what the write did to the drawing. It does not come from the script: a script that reports success and a script that reports nothing get the same list.

- **`added`** is every object that exists after the write and did not before. The plugin notes the drawing's next free handle before the script runs and looks at every handle given out since. This also finds objects Civil 3D creates by itself, for example the parts of a new surface.
- **`modified`** and **`erased`** come from two sources: the objects the script opened for write in its transaction whose modified or erased flag is set, and the drawing's own notifications, which arrive when an object is closed. For a committed write the second source also shows what Civil 3D updated by itself during the commit, such as dependent objects.
- Each group has `count`, `byType` (the .NET type name a script would use, most frequent first) and the first `items` with `type`, `handle`, `name` and `layer`; `more` counts the ones not listed.
- **`containers`** counts symbol tables, dictionaries, layout blocks and draw order lists apart. They change whenever something is put into them and would otherwise hide the objects the write was about.
- For a committed write the list is made after the commit, by reading the drawing again in a separate short transaction, so it shows what is in the drawing and not what the script intended.
- **`complete`** is false, with a `note`, when the list can miss something: a preview (see below), more than 2,000,000 new handles, more than 200,000 touched objects, or the notifications switched off with `CIVIL3D_MCP_CHANGE_EVENTS=0`.
- When the list cannot be made, `changes` is `{"available": false, "reason": "…"}`. The write itself is never failed or changed by the list.
- After a script or a commit that failed there is no list either, but the plugin checks that nothing the script added is left in the drawing: `{"available": false, "reason": "…", "rolledBack": true}`; with `rolledBack: false`, `leftBehind` counts what stayed and `leftTypes` gives its types.

The list tells which objects were touched, not whether their geometry is right; reading the result back with a query stays necessary.

#### Preview

With `preview: true` the receipt has `preview: true`, `committed: false`, `saved: false`, and `changes` describes what the script would add, modify and erase. After the transaction is abandoned the plugin checks that no added object is still in the drawing and no erased object is still erased, and reports it as `rolledBack`; with `rolledBack: false`, `leftAfterPreview` counts what stayed behind and `leftTypes` gives its types.

A preview has limits that follow from how it works:

- It shows what the script itself does. What Civil 3D would update when the change is kept (dependent objects, rebuilds on commit) is not in the list, so `complete` is always false.
- Handles of new objects in the script's result do not exist afterwards, and handle numbers are used up: the real call gives other handles.
- Only the drawing is taken back. A file the script wrote, a system variable it set or a command it queued stays.
- AutoCAD marks the drawing as modified (`DBMOD`) as soon as a script touches it, also when nothing is kept. When the drawing was clean before the preview and the check found nothing left behind, the plugin takes the mark off again, so the real call can still ask for `requireClean` or `backup`; `dbmodAfter` shows the outcome. The same is done after a script that failed before its commit. A drawing that already had unsaved changes, a preview with `rolledBack: false` and a failed commit keep the mark.
- It is one Undo step in Civil 3D, like a write, and runs under the same command.

The plugin also appends one line per write request to a local **journal**, `%LOCALAPPDATA%\Civil3dMcpRoslyn\journal\<year>-<month>.jsonl` (override with `CIVIL3D_MCP_JOURNAL_DIR` in the Civil 3D process): time, Civil 3D session, drawing, description, SHA-256 and size of the code, success or error code, preview, committed, saved, backup path, `DBMOD` before and after, the number of objects added, modified and erased, duration and plugin commit. It never contains the code, the result or an object name. A journal that cannot be written does not change the outcome of the write.

A plugin from before these switches would ignore them silently. The server therefore asks the plugin for its feature list first and refuses with `CIVIL3D.PLUGIN_UPDATE_REQUIRED` (`not_started`) when `requireClean`, `backup` or `backupDirectory` cannot be honoured.

### Time limit and cancelling

A script may run for 120 seconds. `timeoutSeconds` (1–1800) on `civil3d_query` and `civil3d_execute` changes the limit for that one call; use it for a known long operation such as a large rebuild. At the limit the script is stopped at its next loop iteration and the transaction is rolled back. A script that reaches no loop cannot be stopped midway, but when it returns after the limit its result is discarded and nothing is committed either. In both cases the reply is `CIVIL3D.TIMEOUT` with `outcome: "reported_error"`: the drawing is unchanged, and the same write can be sent again with a larger `timeoutSeconds`.

The MCP server waits longer than the plugin's limit, so that this definite answer arrives instead of an uncertain one: at least 30 seconds beyond the script limit, and `CIVIL3D_COMMAND_TIMEOUT` (or `CIVIL3D_SAVE_TIMEOUT` when the drawing is saved) extended by whatever `timeoutSeconds` adds to the default 120 seconds. The MCP client's own tool-call timeout is separate and must also allow the time.

When the client cancels a tool call (for example the user presses Stop), the server closes its connection to the plugin. A request that has not started is dropped; a running script stops at its next loop iteration, or has its result discarded when it returns, and is rolled back. The call reports `CIVIL3D.CANCELLED`: `not_started` if it was never sent, otherwise `unknown`, because a write that had already reached commit stays committed. What the assistant does after a time limit or an unknown outcome is in the [usage guide](docs/USAGE.md) (topic `errors`).

## Setup

### Where everything lives

On a Windows computer the MCP keeps everything in one standard folder per user, `%LOCALAPPDATA%\Civil3dMcpRoslyn` (`C:\Users\<user>\AppData\Local\Civil3dMcpRoslyn`). It needs no administrator rights, and every computer gets the same layout:

| Folder | Contents |
|--------|----------|
| `server\` | The installed server that MCP clients start: `server\build\index.js`. The copy it replaced is kept as `server.previous\`. |
| `backup\` | Plugin bundles replaced by `install:plugin`; the newest 3 are kept. |
| `backups\` | Drawing copies made by `backup: true` writes. |
| `journal\` | The [write journal](#safety-switches-receipt-and-journal-for-writes). |
| `images\` | Picture files of [`NewImagePath()`](#pictures). |
| `endpoints\` | Records of the running Civil 3D instances, each with its [session token](#security). |

The Civil 3D plugin goes where Autodesk loads add-ins from: `%APPDATA%\Autodesk\ApplicationPlugins\Civil3dMcpPlugin.bundle`. The source folder (the git clone) is needed only to build and can be anywhere; the standard place is `%USERPROFILE%\source\repos\civil3d-mcp-roslyn`, the default folder of Windows development tools, which OneDrive does not sync. MCP clients never start the server from the source folder.

### 1. Build

Needs Node.js 22 or later, the .NET 8 SDK and git.

```powershell
git clone https://github.com/nezolder/civil3d-mcp-roslyn.git "$env:USERPROFILE\source\repos\civil3d-mcp-roslyn"
cd "$env:USERPROFILE\source\repos\civil3d-mcp-roslyn"
npm ci
npm run build
# Copy the Civil 3D reference DLLs to C_References/ first (see C_References/README.md)
dotnet build plugin/Civil3dMcpPlugin -c Release
```

### 2. Install

```powershell
npm run install:server   # copies the server to %LOCALAPPDATA%\Civil3dMcpRoslyn\server
npm run install:plugin   # close every Civil 3D first
```

`install:server` ends by printing the settings for the clients below with the full path filled in. Civil 3D asks once at its next start whether to load the new, unsigned plugin. Details: [Installing a build](#installing-a-build).

### 3. Connect the AI clients

JSON settings do not expand `%LOCALAPPDATA%`, so they name the full path. Restart the client after a change.

Claude Desktop (`claude_desktop_config.json`) and other JSON clients:

```json
{
  "mcpServers": {
    "civil3d": {
      "command": "C:\\Program Files\\nodejs\\node.exe",
      "args": ["C:\\Users\\<user>\\AppData\\Local\\Civil3dMcpRoslyn\\server\\build\\index.js"]
    }
  }
}
```

Claude Code:

```powershell
claude mcp add civil3d --scope user -- node "$env:LOCALAPPDATA\Civil3dMcpRoslyn\server\build\index.js"
```

Codex (`~/.codex/config.toml`):

```toml
[mcp_servers.civil3d]
command = 'C:\Program Files\nodejs\node.exe'
args = ['C:\Users\<user>\AppData\Local\Civil3dMcpRoslyn\server\build\index.js']
```

### 4. Update

```powershell
git pull
npm ci                   # only when package-lock.json changed
npm run build
npm run install:server   # close the AI clients that use the MCP first
dotnet build plugin/Civil3dMcpPlugin -c Release
npm run install:plugin   # close every Civil 3D first
```

Then start the AI clients and Civil 3D again. The client settings stay as they are.

### Loading by hand during development

```
NETLOAD → select Civil3dMcpPlugin.dll
C3DMCPSTATUS → verify running
C3DMCPRESET  → release an operation that is stuck while Civil 3D is idle
```

To try a server build without installing it, a client can start `<source folder>\build\index.js` directly; switch it back to the installed copy afterwards.

Rules for development tests against a live Civil 3D are in [CONTRIBUTING.md](CONTRIBUTING.md).

## Versions and installing a build

Both halves say which source state they were built from. `npm run build` writes the commit and build time next to the compiled server, and the plugin build stamps them into the DLL. The server reports itself to MCP clients as `1.0.0+<short commit>`.

`civil3d_skills` with `action: "status"` returns the server's commit, build time and skills folder, and for every running Civil 3D instance its `instanceId`, process, port, plugin commit and build time, the active drawing, whether an operation is running, and `buildMatchesServer`. Its `warnings` list names an instance whose plugin was built from another commit, a plugin too old to report its build, and builds made with uncommitted changes. The status reads health replies only and runs no command in Civil 3D. A mismatch is also logged once per Civil 3D session.

A build made where git is unavailable reports `unknown`. Pass the commit in when needed: `CIVIL3D_MCP_COMMIT=<full hash>` for `npm run build`, `-p:Civil3dMcpCommit=<full hash>` for `dotnet build`.

### Installing a build

The development folder is a poor place to run from: switching a branch there changes the live skill catalogue at once, and `npm run build` empties `build/` while it compiles. Two commands copy a finished build to where it runs:

```bash
npm run install:server   # or: node scripts/install.mjs --server --target <folder>
npm run install:plugin   # or: node scripts/install.mjs --plugin
```

- **Server:** copies `build/`, `skills/` and only the packages needed at run time to `%LOCALAPPDATA%\Civil3dMcpRoslyn\server`, checks that the copy loads on its own, then swaps it in. The copy it replaces is kept as `server.previous`. Close the clients that use it first: a running server keeps its old code until it restarts, and if Windows still holds a file in the folder the command stops and leaves the installed copy as it was. At the end it prints the client settings that start `<that folder>\build\index.js`.
- **Plugin:** copies the Release build into `%APPDATA%\Autodesk\ApplicationPlugins\Civil3dMcpPlugin.bundle`, which every Civil 3D loads at start. It refuses while a running Civil 3D holds the installed DLL, first copies the installed bundle to `%LOCALAPPDATA%\Civil3dMcpRoslyn\backup\<date-time>\`, and verifies each copied file by hash. Afterwards it keeps the newest 3 of the backup folders it made and removes the older ones; `--keep-backups <n>` changes the number and `0` keeps all. Folders with other names are never touched. Civil 3D asks once at its next start whether to load the new, unsigned file.

Neither command changes an MCP client's configuration.

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `CIVIL3D_HOST` | `127.0.0.1` | Plugin host. The plugin listens on IPv4 loopback only. |
| `CIVIL3D_PORT` | unset | Optional fixed-port override. When omitted, local instance discovery is active and the plugin prefers port 8080. |
| `CIVIL3D_MCP_DEFAULT_PORT_FALLBACK` | `1` | With `0`, discovery uses the endpoint records only and never tries port 8080 on its own. The repository tests set it so that they cannot reach a Civil 3D running on the same machine. |
| `CIVIL3D_CONNECT_TIMEOUT` | `5000` | TCP connection timeout (ms) |
| `CIVIL3D_DISCOVERY_TIMEOUT` | `5000` | Timeout for private health and drawing-identity probes (ms) |
| `CIVIL3D_COMMAND_TIMEOUT` | `120000` | Base wait for one plugin reply (ms). For scripts the wait is never shorter than the script limit plus 30 s; see [time limit](#time-limit-and-cancelling). |
| `CIVIL3D_MCP_EXECUTION_BACKEND` | `modal` | Plugin process: `native` selects the legacy route for a controlled comparison in a fresh Civil session. |
| `CIVIL3D_SAVE_TIMEOUT` | `600000` | Base wait for execute requests with `saveDrawing: true` (ms); extended the same way by `timeoutSeconds`. |
| `CIVIL3D_MCP_BACKUP_DIR` | `%LOCALAPPDATA%\Civil3dMcpRoslyn\backups` | Plugin process: default folder for `backup: true` copies (absolute path). |
| `CIVIL3D_MCP_JOURNAL_DIR` | `%LOCALAPPDATA%\Civil3dMcpRoslyn\journal` | Plugin process: folder of the write journal (absolute path). |
| `CIVIL3D_MCP_IMAGE_DIR` | `%LOCALAPPDATA%\Civil3dMcpRoslyn\images` | Plugin process: folder for the files of `NewImagePath()` (absolute path). |
| `CIVIL3D_MCP_CHANGE_EVENTS` | `1` | Plugin process: with `0` the change list of a write does not listen to the drawing's notifications and uses the handles and the script's transaction only. A switch for the case that the notifications cause trouble; the list then says that it can be incomplete. |
| `CIVIL3D_MCP_REQUIRE_TOKEN` | `1` | Plugin process: with `0` the plugin also runs requests that present no [session token](#security), for an MCP server from before the token existed. |
| `CIVIL3D_MCP_TOKEN` | unset | MCP server: the session token to present when the port is fixed with `CIVIL3D_PORT` and no endpoint record can be read. Normally not needed. |
| `CIVIL3D_MCP_COMMIT` | from git | Build time only: commit recorded by `npm run build` when git cannot be asked. |
| `LOG_LEVEL` | `info` | Log level |

Command/save timeouts must resolve to a positive integer of at most `2147483647` milliseconds; invalid or empty settings use the listed defaults. Log levels are `debug`, `info`, `warn` and `error`, ignoring case and surrounding whitespace; unrecognized values use `info`. Invalid timeout warnings omit the supplied value.

Civil requests run as correlated modal commands. The existing serialization gate stays held until the script has disposed its resources and that command ends; completing the script body alone does not release it. A request that has not started within 15 seconds is abandoned, and its late token cannot execute another request. The legacy backend is never selected automatically after a timeout. Every loop in a script checks for cancellation at the start of each iteration, so a runaway loop stops at the script time limit (120 seconds unless the call sets `timeoutSeconds`) and its transaction is rolled back. When the MCP server closes the connection, for example after a client cancel or its own command timeout, a request that has not started is dropped and a running script stops at its next loop iteration, also without committing. Long single Civil API calls cannot be interrupted, but a script that returns after its limit, or after the client is gone, is not committed. A script that uses `await` is rejected before compiling, because awaiting on the Civil 3D main thread can deadlock the application.

## Multiple Civil 3D instances

Each loaded plugin session publishes a small local endpoint record containing only an opaque instance ID, process ID, program name, port, start time and the session's [token](#security). It contains no drawing name or project data. The first Civil 3D instance prefers port 8080; another instance automatically uses a free localhost port.

A record normally disappears when its Civil 3D session ends. One left behind by a crash or a killed process is removed by the next plugin session that starts, and until then the MCP server skips records whose process no longer exists, so stale records do not slow down every call. Windows may reuse the process ID of a closed Civil 3D for another program; a record whose ID now belongs to a different program is removed as well. A session that the caller names with `instanceId` is always probed.

If exactly one instance is available, existing calls work as before. With multiple instances, `expectedDrawing` is used to find the matching active drawing before the requested C# reaches any plugin. An unguarded bootstrap query fails closed with `CIVIL3D.INSTANCE_SELECTION_REQUIRED` and reports the live candidates; retry it with the optional `instanceId` on the existing tool. The final plugin-side drawing guard still checks the path and fingerprint immediately before Civil API access. No fourth public MCP tool is added.

Choosing between instances runs no command in any of them. Each plugin remembers which drawing is active, refreshed on the main thread whenever Civil 3D is idle (at most four times a second) and after each MCP operation, and repeats it in its health reply (`activeDrawingKnown`, `activeDrawing`). The server matches `expectedDrawing` against those replies, so the other instances, for example ones another assistant is working in, are not interrupted. Only an instance that cannot say yet (an older plugin, or one that has not been idle since it started) is still asked with the internal identity command. If the remembered drawing is a moment out of date, the call is refused by the drawing guard as `not_started`; it never reaches the wrong drawing. `civil3d_skills` with `action: "status"` lists the instances and their drawings.

Leave `CIVIL3D_PORT` unset for automatic selection. Setting it deliberately pins the MCP server to that one port for compatibility or diagnostics.

## Live smoke test

`npm run smoke:live -- --start-civil` starts Civil 3D on a new generated test drawing and runs a fixed list of read and write cases through this checkout's server, with a report at the end. Details and safety rules: [`docs/LIVE_SMOKE.md`](docs/LIVE_SMOKE.md).

## Benchmarking

The phase 2A host-independent recorder, the phase 2A.1 opt-in internal live trace contract, and the phase 2A.2 read-only live runner are documented in [`benchmark/README.md`](benchmark/README.md). None adds an MCP tool, queue, or retry; the 2A.2 runner can invoke only its fixed read-only query when explicitly started.

## Structured errors (phase 2B.1)

`civil3d_query` and `civil3d_execute` keep their existing text error content and `isError: true`, while also returning `structuredContent` with schema `civil3d-mcp-error/v1`. The stable error fields are `code`, `category`, `message`, `source`, `outcome`, and `retryable`. A command timeout or a connection loss after sending has `outcome: "unknown"` and `retryable: false`; the server never retries it automatically. The plugin's own script time limit (`CIVIL3D.TIMEOUT`) is different: it is raised before any commit, so its outcome is `reported_error` and the drawing is unchanged. Successful responses and the three-tool public surface are unchanged.

An operation whose Civil command has not started within 15 seconds returns `CIVIL3D.COMMAND_CONTEXT_TIMEOUT` with `outcome: "not_started"` and `retryable: false`. A subsequently arriving abandoned token performs no drawing work. This deadline limits admission, not execution: started work retains the serialized gate until the selected backend's host completion, and an uncertain write must still be reconciled rather than repeated. The targeted completion checks do not establish that every possible hang is eliminated.

The timeout message says what is known about the cause: a command or prompt was waiting for input, the command was handed over but did not begin, or Civil 3D did not become idle at all (a command is still running, or its window needs one click).

A query runs through its own internal command (`C3DMCPREAD`), registered without an Undo marker, so that queries do not pile up in the user's Undo list; a write runs through `C3DMCPRUN` and stays one Undo step. A query that opens an object for write can still mark the drawing as modified (`DBMOD`), although its change is rolled back; queries should only read.

If an operation stays marked as running although Civil 3D sits idle at its command line, the person at the computer can type `C3DMCPRESET` in Civil 3D. It releases a request that never started (nothing ran) or one whose script had finished but whose command end Civil 3D never reported (`CIVIL3D.COMMAND_FAILED`; `committed` in the receipt says whether the change went in). It cannot interrupt a running script, because no command starts while one runs.

### Compile a single C# code body offline

The skill test runner can check an individual UTF-8 `.cs` code body against locally supplied Civil 3D 2025 assemblies:

```powershell
dotnet run --project tests/skills/Civil3dMcp.SkillTests.csproj --configuration Release -- --code (Resolve-Path ./probe.cs).Path
```

The file contains a script body as it would be sent to `civil3d_query` or `civil3d_execute`. It is compiled the way the plugin compiles it: as a script with the same imports, the default names for `Surface`, `Section`, `Entity` and the others, the globals `Document`, `CivilDoc`, `Database`, `Transaction` and `Editor`, and the picture helpers; the body may start with its own `using` lines and declare classes or records. The plugin's `await` rule and script guard are applied too. This checks diagnostics only; it does not execute the code or connect to Civil 3D. Success is exit code `0` together with the line `Civil 3D single C# code-body compilation passed: <file>`; `1` means compile errors, an `await` or a guard refusal, and `2` invalid arguments, a missing input file or missing Autodesk references. Calling the runner without arguments checks every skill template and its input tests instead, not your file. A successful compile is not live behavior evidence.

## Private TCP framing (phase 2C.1)

Each localhost TCP connection carries one UTF-8 JSON-RPC request and one response. Each JSON body is followed by LF and is limited to 8 MiB, measured as UTF-8 bytes without the LF. The Node client still accepts the previous plugin's unframed response when that complete JSON body is followed by an orderly connection close. Oversized requests are rejected before they are written; oversized or malformed responses and interrupted connections produce non-retryable structured transport errors. If execution completed but the plugin could not return an oversized result, the reported outcome is `unknown`.

## Operation audit logging and write idempotency (phase 2I.1 / 2I.2)

At the default `info` log level, each accepted `civil3d_query` and `civil3d_execute` operation emits one bounded stderr audit event. It contains a new opaque operation ID, tool name, SHA-256 and UTF-8 byte length of the C# source, success/error status, and elapsed milliseconds; errors add only stable code/category/source/outcome fields. The audit event never contains caller code, description, drawing identity, result, or error message. It exists only where the client keeps the server's stderr; the lasting record of writes is the plugin's [journal](#safety-switches-receipt-and-journal-for-writes), which does hold the drawing path and the description.

`civil3d_execute` also accepts an optional opaque `idempotencyKey` (1–128 ASCII letters, digits, `.`, `_`, `:`, `-`). In one plugin session it binds the key to the UTF-8 C# SHA-256, normalized `expectedDrawing` identity, and `saveDrawing` choice. A duplicate is rejected as in-progress, conflicting, or already committed; committed entries retain no result and callers must reconcile with a read-only query. A save failure occurs after the in-memory write commit, so its key is kept as completed to prevent an accidental duplicate modification. The session keeps at most 256 completed keys, evicting the oldest deterministically. This adds neither persistence nor automatic retry or exactly-once semantics.

## Security

Before a script runs, the plugin refuses it (`CIVIL3D.SANDBOX_VIOLATION`) when it uses:
- Processes (`System.Diagnostics.Process`, `ProcessStartInfo`)
- Deleting files or folders (`File.Delete`, `Directory.Delete`, `FileInfo.Delete`, `DirectoryInfo.Delete`)
- Network requests (`System.Net.Http`, `System.Net.Sockets`, `WebClient`, `WebRequest`)
- The registry (`Microsoft.Win32.Registry`, `RegistryKey`)
- Loading assemblies (`Assembly.Load…`, `AssemblyLoadContext`, `AppDomain.CreateDomain`)
- Native calls (`DllImport`, `Marshal`, `NativeLibrary`)
- Ending the process (`Environment.Exit`, `Environment.FailFast`)

All Civil 3D API operations are allowed, with one exception in `civil3d_query` only: `SampleLineGroup.GetSectionSources()` needs the group opened for write and has closed Civil 3D 2025 in a read-only call, so a query that calls it is refused with the same code. The same script runs in `civil3d_execute`, where `preview: true` keeps nothing.

The check reads what the compiler resolved, not the script text. A comment, a string or a name that only looks similar (`process.StartStation`, `styleRegistry.Add`) is therefore not refused, and a short name behind a `using` line (`File.Delete`) is. The error names the type or member and its script line.

This guard is defense in depth, not a trust boundary: a script can still reach these operations indirectly, for example through reflection. Both code tools receive mutable Civil 3D and AutoCAD API objects; `civil3d_query` skips the host's transaction commit but cannot guarantee that arbitrary dynamic C# is side-effect-free. Run only trusted, approval-gated code.

### Who may send requests

The plugin listens on the IPv4 loopback address only, so it cannot be reached from the network. Loopback alone is open to every program and every user on the same computer, so each plugin session also makes up a random 256-bit **session token** when its listener starts and runs a request only when the request presents it. The token is written into the session's endpoint record under `%LOCALAPPDATA%\Civil3dMcpRoslyn\endpoints`, a folder in the user's own profile, where the MCP server reads it by itself; nothing has to be configured. A new token is made at every listener start and is never sent back over the connection.

- A request without the right token is refused with `CIVIL3D.UNAUTHORIZED` (`outcome: "not_started"`) before the drawing is looked at; it is not journaled. The health request alone is still answered, with a short reply that says a plugin is here, which session and which build, and nothing about the drawing or a running operation. `civil3d_skills` with `action: "status"` then lists that instance with `authorized: false`.
- What this protects against: other users of the same computer, and programs that can open a local connection but cannot read the user's profile folder. What it does not protect against: a program that runs as the same user can read the record, as it can read the drawings themselves.
- An MCP server from before the token cannot use a plugin that has it, and says so in the error. Update both sides together (`npm run install:server`, `npm run install:plugin`). For the time in between, `CIVIL3D_MCP_REQUIRE_TOKEN=0` in the environment of Civil 3D switches the requirement off. A new server works with an older plugin unchanged.

## License

  MIT
