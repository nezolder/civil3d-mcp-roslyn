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
| `civil3d_execute` | Execute C# code with **write** access; optional clean-drawing check and backup before, save after, and a receipt of what happened | ⚠️ Modifies drawing |
| `civil3d_query` | Run intended read-only C# without committing the host transaction | Trusted code only; not a side-effect sandbox |
| `civil3d_skills` | Browse/search/read code skill templates; `api_lookup` searches already-loaded public Civil 3D API metadata; `status` shows the server and plugin versions and the running instances | ✅ Metadata only |

The tools carry MCP annotations so a client can tell them apart: `civil3d_skills` is read-only, `civil3d_execute` is a non-idempotent write, and none reaches outside the local Civil 3D session. `civil3d_query` is deliberately not advertised as read-only: its transaction is rolled back, but it still runs arbitrary code.

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

`civil3d_skills` also supports `action: "api_lookup"` for a bounded, read-only search of public type and member names/signatures from already-loaded allowlisted Civil 3D host assemblies. It does not load assemblies, run C# code, or access the active drawing. Supply a query and optionally an assembly, namespace prefix, and result limit. `action: "status"` reports [versions and running instances](#versions-and-installing-a-build).

Skills are documented C# code templates in `skills/`:

The catalog contains **39 skills**. Six focused engineering recipes cover design profiles from explicit PVIs, individual section views, existing native material quantities, sampled TIN elevation comparison, data-reference state and corridor target audits. Use `civil3d_skills` to search/get a recipe, bind project-approved inputs and run it through the existing query/execute tools. Recipes marked `run_by_name` (26 of 39, including all six engineering recipes and the volume, alignment layout and cross-section recipes) can run directly with `skill` and `params` instead of `code`, so the template is not sent back and forth; see [running by name](docs/SKILL_CATALOG.md#run-a-recipe-by-name). For earthwork quantities, `create_tin_volume_surface` keeps a TIN volume surface between two surfaces (with cut/fill factors) in the drawing, and `bounded_volumes` reads its cut, fill and net volumes inside labelled closed polylines, for example one per structure. For horizontal layout, `create_alignment_from_pis` builds an alignment from PI coordinates with an arc or clothoid-arc-clothoid group at each PI, and `create_offset_alignments` adds dynamic offset alignments, such as edges of pavement, to an existing alignment. For cross sections, `create_sample_lines` places perpendicular sample lines at explicit stations, an interval or geometry points with checked vertices and station-based names, `create_section_view_group` creates one native section view group (draft grid or template sheet placement, band sources, view names), `shift_section_view_elevations` moves the views' elevation window, keeping each view's height or setting a sheet height below the lowest ground point, `plan_section_sheet_fit` reads the template layouts to show which sheet fits the views' width and elevation height, `rename_sample_lines_by_station` renames sample lines after their actual station with a configurable name format, previewing the names first, and `survey_row_station_candidates` reads hand-measured survey rows across the road (AutoCAD points, survey blocks, also inside xrefs, or COGO points) and lists one candidate station per row, also in dense junctions, with an optional filter on the surveyor's point codes and a review flag for doubtful rows, ready for `create_sample_lines`. See [the recipe guide](docs/ENGINEERING_SKILLS.md) for inputs, limits and upstream attribution.

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

### Return values

Return a value to get it back as JSON. Supported: primitives, strings, `Guid`, dates and times (ISO 8601 text), anonymous objects and classes or records declared in the script (public properties and fields, camelCase names), arrays, lists and any other sequence including LINQ queries without `.ToList()`, tuples (as arrays; element names do not exist at run time), `Dictionary<string, …>`, `ObjectId`, `Handle`, `Point2d/3d` and `Vector2d/3d`. Enums return as numbers. `DBObject` instances are refused.

A collection with more than 1000 items keeps its first 1000, and the reply is then wrapped as `{"result": …, "truncated": [{"path": "$.items", "returned": 1000, "total": 5234}]}`; `total` is `null` for a lazy sequence whose length is unknown. A lazy sequence runs the script's own code while the result is read, still inside the transaction and under the same loop checkpoints and timeout; an exception there is reported with its type and path. Results are also limited to depth 8 and 10000 nodes.

NaN and Infinity have no JSON form. Each one becomes `null`, and the reply is wrapped as `{"result": …, "nonFinite": {"count": 3, "paths": ["$.rows[4].elevation", …]}}` with the first 20 paths, so one undefined number no longer discards the whole result. `truncated` and `nonFinite` appear side by side when both apply; a result with neither keeps its plain shape.

All Civil 3D namespaces are auto-imported. A few type names exist in two of them and would otherwise be rejected as ambiguous (CS0104); they have a default meaning:

| Bare name | Means | Other type, by full name |
|-----------|-------|--------------------------|
| `DBObject`, `Entity` | `Autodesk.AutoCAD.DatabaseServices.*` | `Autodesk.Civil.DatabaseServices.*` |
| `ErrorStatus` | `Autodesk.AutoCAD.Runtime.ErrorStatus` | `Autodesk.Civil.ErrorStatus` |
| `Exception` | `System.Exception` | `Autodesk.AutoCAD.Runtime.Exception` |
| `Surface`, `Section` | `Autodesk.Civil.DatabaseServices.*` | `Autodesk.AutoCAD.DatabaseServices.*` |

A full name, the script's own `using X = …;` alias or a type the script declares still wins. Other duplicated names (for example `Table`) stay ambiguous and need the full name.

Committing the `civil3d_execute` transaction changes the open drawing but does not by itself write the DWG file to disk. First identify the active drawing with `civil3d_query`, then pass its full `Database.Filename` and `FingerprintGuid` as `expectedDrawing`; the plugin checks that identity immediately before Civil API access. Set `saveDrawing: true` when the completed change should also be saved. The plugin saves only after the script transaction and document lock are closed; scripts must not call `Database.SaveAs` or queue `QSAVE` themselves. The save request uses a separate 10-minute default timeout and is never retried automatically. A drawing that is open read-only is refused with `CIVIL3D.DRAWING_READ_ONLY` (`outcome: "not_started"`) before the script runs, instead of committing the change in memory and failing only at the save.

### Safety switches, receipt and journal for writes

`civil3d_execute` can do the checks around a write itself, so the assistant does not need separate calls for them:

| Input | Effect |
|-------|--------|
| `requireClean: true` | Runs only when the drawing has no unsaved changes (`DBMOD` is 0). Otherwise nothing runs: `CIVIL3D.DRAWING_NOT_CLEAN`, `outcome: "not_started"`. |
| `backup: true` | Copies the saved DWG file to a time-stamped copy before the script runs, for example `Plan.20261111-103000.dwg`. The copy is verified by size, and an existing file is never overwritten. If it cannot be made, nothing runs (`CIVIL3D.BACKUP_FAILED`); an unnamed drawing is refused with `CIVIL3D.SAVE_PATH_REQUIRED`. Because the copy shows the last saved state, `backup` also asks for a clean drawing unless `requireClean: false` is set explicitly. |
| `backupDirectory` | Absolute folder for that copy. Default: `CIVIL3D_MCP_BACKUP_DIR` of the Civil 3D process, else `%LOCALAPPDATA%\Civil3dMcpRoslyn\backups`. Copies are never deleted automatically. |
| `description` | One line about the change. It is shown on the Civil 3D command line as `[Civil 3D MCP] …` before the script runs, and kept in the journal. |

Every write that reached the drawing returns a **receipt** next to its result. The result stays alone in the first text block; the receipt is a second text block:

```json
{"receipt": {"drawing": {"databaseFilename": "C:\\Projects\\Plan.dwg", "fingerprintGuid": "{…}"},
  "dbmodBefore": 0, "dbmodAfter": 1, "committed": true, "saved": true,
  "fileLastWriteUtc": "2026-11-11T09:30:00.0000000+00:00", "fileBytes": 1843200,
  "backupPath": "C:\\Users\\me\\AppData\\Local\\Civil3dMcpRoslyn\\backups\\Plan.20261111-103000.dwg"}}
```

`committed` says whether the change is in the open drawing, `saved` whether this call wrote the file, and the file fields describe the file on disk after the call. A refused or failed write carries the same block (and `structuredContent.receipt`), so a failed save is visibly a committed, unsaved change. Queries have no receipt.

The plugin also appends one line per write request to a local **journal**, `%LOCALAPPDATA%\Civil3dMcpRoslyn\journal\<year>-<month>.jsonl` (override with `CIVIL3D_MCP_JOURNAL_DIR` in the Civil 3D process): time, Civil 3D session, drawing, description, SHA-256 and size of the code, success or error code, committed, saved, backup path, `DBMOD` before and after, duration and plugin commit. It never contains the code or the result. A journal that cannot be written does not change the outcome of the write.

A plugin from before these switches would ignore them silently. The server therefore asks the plugin for its feature list first and refuses with `CIVIL3D.PLUGIN_UPDATE_REQUIRED` (`not_started`) when `requireClean`, `backup` or `backupDirectory` cannot be honoured.

### Time limit and cancelling

A script may run for 120 seconds. `timeoutSeconds` (1–1800) on `civil3d_query` and `civil3d_execute` changes the limit for that one call; use it for a known long operation such as a large rebuild. At the limit the script is stopped at its next loop iteration and the transaction is rolled back. A script that reaches no loop cannot be stopped midway, but when it returns after the limit its result is discarded and nothing is committed either. In both cases the reply is `CIVIL3D.TIMEOUT` with `outcome: "reported_error"`: the drawing is unchanged, and the same write can be sent again with a larger `timeoutSeconds`.

The MCP server waits longer than the plugin's limit, so that this definite answer arrives instead of an uncertain one: at least 30 seconds beyond the script limit, and `CIVIL3D_COMMAND_TIMEOUT` (or `CIVIL3D_SAVE_TIMEOUT` when the drawing is saved) extended by whatever `timeoutSeconds` adds to the default 120 seconds. The MCP client's own tool-call timeout is separate and must also allow the time.

When the client cancels a tool call (for example the user presses Stop), the server closes its connection to the plugin. A request that has not started is dropped; a running script stops at its next loop iteration, or has its result discarded when it returns, and is rolled back. The call reports `CIVIL3D.CANCELLED`: `not_started` if it was never sent, otherwise `unknown`, because a write that had already reached commit stays committed. Check the drawing before repeating a write.

## Setup

### 1. Build MCP Server
```bash
npm install && npm run build
```

### 2. Build Plugin
```bash
# Copy DLLs from Civil 3D to C_References/ (see C_References/README.md)
dotnet build plugin/Civil3dMcpPlugin -c Release
```

### 3. Load in Civil 3D
```
NETLOAD → select Civil3dMcpPlugin.dll
C3DMCPSTATUS → verify running
```

For a permanent installation use the autoload bundle instead; see [Installing a build](#installing-a-build).

### 4. Configure AI
```json
{
  "mcpServers": {
    "civil3d": {
      "command": "node",
      "args": ["/path/to/civil3d-mcp/build/index.js"]
    }
  }
}
```

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

- **Server:** copies `build/`, `skills/` and only the packages needed at run time to `%LOCALAPPDATA%\Civil3dMcpRoslyn\server`, checks that the copy loads on its own, then swaps it in. The copy it replaces is kept as `server.previous`. Point the MCP client at `<that folder>\build\index.js`.
- **Plugin:** copies the Release build into `%APPDATA%\Autodesk\ApplicationPlugins\Civil3dMcpPlugin.bundle`, which every Civil 3D loads at start. It refuses while a running Civil 3D holds the installed DLL, first copies the installed bundle to `%LOCALAPPDATA%\Civil3dMcpRoslyn\backup\<date-time>\`, and verifies each copied file by hash. Civil 3D asks once at its next start whether to load the new, unsigned file.

Neither command changes an MCP client's configuration.

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `CIVIL3D_HOST` | `127.0.0.1` | Plugin host. The plugin listens on IPv4 loopback only. |
| `CIVIL3D_PORT` | unset | Optional fixed-port override. When omitted, local instance discovery is active and the plugin prefers port 8080. |
| `CIVIL3D_CONNECT_TIMEOUT` | `5000` | TCP connection timeout (ms) |
| `CIVIL3D_DISCOVERY_TIMEOUT` | `5000` | Timeout for private health and drawing-identity probes (ms) |
| `CIVIL3D_COMMAND_TIMEOUT` | `120000` | Base wait for one plugin reply (ms). For scripts the wait is never shorter than the script limit plus 30 s; see [time limit](#time-limit-and-cancelling). |
| `CIVIL3D_MCP_EXECUTION_BACKEND` | `modal` | Plugin process: `native` selects the legacy route for a controlled comparison in a fresh Civil session. |
| `CIVIL3D_SAVE_TIMEOUT` | `600000` | Base wait for execute requests with `saveDrawing: true` (ms); extended the same way by `timeoutSeconds`. |
| `CIVIL3D_MCP_BACKUP_DIR` | `%LOCALAPPDATA%\Civil3dMcpRoslyn\backups` | Plugin process: default folder for `backup: true` copies (absolute path). |
| `CIVIL3D_MCP_JOURNAL_DIR` | `%LOCALAPPDATA%\Civil3dMcpRoslyn\journal` | Plugin process: folder of the write journal (absolute path). |
| `CIVIL3D_MCP_COMMIT` | from git | Build time only: commit recorded by `npm run build` when git cannot be asked. |
| `LOG_LEVEL` | `info` | Log level |

Command/save timeouts must resolve to a positive integer of at most `2147483647` milliseconds; invalid or empty settings use the listed defaults. Log levels are `debug`, `info`, `warn` and `error`, ignoring case and surrounding whitespace; unrecognized values use `info`. Invalid timeout warnings omit the supplied value.

Civil requests run as correlated modal commands. The existing serialization gate stays held until the script has disposed its resources and that command ends; completing the script body alone does not release it. A request that has not started within 15 seconds is abandoned, and its late token cannot execute another request. The legacy backend is never selected automatically after a timeout. Every loop in a script checks for cancellation at the start of each iteration, so a runaway loop stops at the script time limit (120 seconds unless the call sets `timeoutSeconds`) and its transaction is rolled back. When the MCP server closes the connection, for example after a client cancel or its own command timeout, a request that has not started is dropped and a running script stops at its next loop iteration, also without committing. Long single Civil API calls cannot be interrupted, but a script that returns after its limit, or after the client is gone, is not committed. A script that uses `await` is rejected before compiling, because awaiting on the Civil 3D main thread can deadlock the application.

## Multiple Civil 3D instances

Each loaded plugin session publishes a small local endpoint record containing only an opaque instance ID, process ID, program name, port, and start time. It contains no drawing name or project data. The first Civil 3D instance prefers port 8080; another instance automatically uses a free localhost port.

A record normally disappears when its Civil 3D session ends. One left behind by a crash or a killed process is removed by the next plugin session that starts, and until then the MCP server skips records whose process no longer exists, so stale records do not slow down every call. Windows may reuse the process ID of a closed Civil 3D for another program; a record whose ID now belongs to a different program is removed as well. A session that the caller names with `instanceId` is always probed.

If exactly one instance is available, existing calls work as before. With multiple instances, `expectedDrawing` is used to find the matching active drawing before the requested C# reaches any plugin. An unguarded bootstrap query fails closed with `CIVIL3D.INSTANCE_SELECTION_REQUIRED` and reports the live candidates; retry it with the optional `instanceId` on the existing tool. The final plugin-side drawing guard still checks the path and fingerprint immediately before Civil API access. No fourth public MCP tool is added.

Choosing between instances runs no command in any of them. Each plugin remembers which drawing is active, refreshed on the main thread whenever Civil 3D is idle (at most four times a second) and after each MCP operation, and repeats it in its health reply (`activeDrawingKnown`, `activeDrawing`). The server matches `expectedDrawing` against those replies, so the other instances, for example ones another assistant is working in, are not interrupted. Only an instance that cannot say yet (an older plugin, or one that has not been idle since it started) is still asked with the internal identity command. If the remembered drawing is a moment out of date, the call is refused by the drawing guard as `not_started`; it never reaches the wrong drawing. `civil3d_skills` with `action: "status"` lists the instances and their drawings.

Leave `CIVIL3D_PORT` unset for automatic selection. Setting it deliberately pins the MCP server to that one port for compatibility or diagnostics.

## Benchmarking

The phase 2A host-independent recorder, the phase 2A.1 opt-in internal live trace contract, and the phase 2A.2 read-only live runner are documented in [`benchmark/README.md`](benchmark/README.md). None adds an MCP tool, queue, or retry; the 2A.2 runner can invoke only its fixed read-only query when explicitly started.

## Structured errors (phase 2B.1)

`civil3d_query` and `civil3d_execute` keep their existing text error content and `isError: true`, while also returning `structuredContent` with schema `civil3d-mcp-error/v1`. The stable error fields are `code`, `category`, `message`, `source`, `outcome`, and `retryable`. A command timeout or a connection loss after sending has `outcome: "unknown"` and `retryable: false`; the server never retries it automatically. The plugin's own script time limit (`CIVIL3D.TIMEOUT`) is different: it is raised before any commit, so its outcome is `reported_error` and the drawing is unchanged. Successful responses and the three-tool public surface are unchanged.

An operation whose Civil command has not started within 15 seconds returns `CIVIL3D.COMMAND_CONTEXT_TIMEOUT` with `outcome: "not_started"` and `retryable: false`. A subsequently arriving abandoned token performs no drawing work. This deadline limits admission, not execution: started work retains the serialized gate until the selected backend's host completion, and an uncertain write must still be reconciled rather than repeated. The targeted completion checks do not establish that every possible hang is eliminated.

### Compile a single C# code body offline

The skill test runner can check an individual UTF-8 `.cs` code body against locally supplied Civil 3D 2025 assemblies:

```powershell
dotnet run --project tests/skills/Civil3dMcp.SkillTests.csproj --configuration Release -- --code (Resolve-Path ./probe.cs).Path
```

The file contains a method body that uses the provided `Document`, `CivilDoc`, `Database`, `Transaction` and `Editor` parameters. This checks compilation diagnostics only; it does not execute the code or connect to Civil 3D. Exit codes are `0` for success, `1` for compilation errors, and `2` for invalid arguments, a missing input file or missing Autodesk references. Calling the runner without arguments still checks every skill template and its input tests. A successful compile is not live behavior evidence.

## Private TCP framing (phase 2C.1)

Each localhost TCP connection carries one UTF-8 JSON-RPC request and one response. Each JSON body is followed by LF and is limited to 8 MiB, measured as UTF-8 bytes without the LF. The Node client still accepts the previous plugin's unframed response when that complete JSON body is followed by an orderly connection close. Oversized requests are rejected before they are written; oversized or malformed responses and interrupted connections produce non-retryable structured transport errors. If execution completed but the plugin could not return an oversized result, the reported outcome is `unknown`.

## Operation audit logging and write idempotency (phase 2I.1 / 2I.2)

At the default `info` log level, each accepted `civil3d_query` and `civil3d_execute` operation emits one bounded stderr audit event. It contains a new opaque operation ID, tool name, SHA-256 and UTF-8 byte length of the C# source, success/error status, and elapsed milliseconds; errors add only stable code/category/source/outcome fields. The audit event never contains caller code, description, drawing identity, result, or error message. It exists only where the client keeps the server's stderr; the lasting record of writes is the plugin's [journal](#safety-switches-receipt-and-journal-for-writes), which does hold the drawing path and the description.

`civil3d_execute` also accepts an optional opaque `idempotencyKey` (1–128 ASCII letters, digits, `.`, `_`, `:`, `-`). In one plugin session it binds the key to the UTF-8 C# SHA-256, normalized `expectedDrawing` identity, and `saveDrawing` choice. A duplicate is rejected as in-progress, conflicting, or already committed; committed entries retain no result and callers must reconcile with a read-only query. A save failure occurs after the in-memory write commit, so its key is kept as completed to prevent an accidental duplicate modification. The session keeps at most 256 completed keys, evicting the oldest deterministically. This adds neither persistence nor automatic retry or exactly-once semantics.

## Security

The Roslyn sandbox blocks:
- Process execution (`Process.Start`)
- File deletion (`File.Delete`)
- Network requests (`HttpClient`, `Sockets`)
- Registry access
- Dynamic assembly loading

All Civil 3D API operations are allowed.

This regex sandbox is defense in depth, not a trust boundary. Both code tools receive mutable Civil 3D and AutoCAD API objects; `civil3d_query` skips the host's transaction commit but cannot guarantee that arbitrary dynamic C# is side-effect-free. Run only trusted, approval-gated code. Loopback TCP prevents remote network access but does not authenticate other local processes.

## License

  MIT
