# Civil 3D MCP Server — Dynamic Roslyn Fork

An MCP server that enables AI assistants to **write and execute C# code** directly inside Autodesk Civil 3D. Instead of a large set of fixed tools, the AI generates task-specific code that runs with Civil 3D API access.

## Project Scope and Lineage

This fork keeps the dynamic Roslyn/C# execution model and a deliberately small public surface of three MCP tools. Its current compatibility baseline is Autodesk Civil 3D 2025, with local work focused on reliability, safety, measurable efficiency, and reusable Civil 3D skills. Other Civil 3D versions can be added later through separately verified compatibility work.

The project is derived from [barbosaihan/civil3d-mcp](https://github.com/barbosaihan/civil3d-mcp). [SantosSjba/mcp-to-c3d](https://github.com/SantosSjba/mcp-to-c3d) was evaluated for selected, test-backed ideas, while [Sacred-G/Civil3D-mcp](https://github.com/Sacred-G/Civil3D-mcp) was used only as an architectural reference. See [PROVENANCE.md](PROVENANCE.md) for the detailed attribution and licensing boundaries.

This independent project is not affiliated with or endorsed by Autodesk. Autodesk assemblies and other proprietary Civil 3D files are not included.

## Architecture

```
┌─────────────────┐     stdio      ┌──────────────────┐     TCP/JSON-RPC    ┌──────────────────┐
│   AI Assistant   │ ◄────────────► │  MCP Server (TS) │ ◄──────────────────► │  Civil 3D Plugin │
│ (Codex, clients) │               │   3 meta-tools    │  discovered locally │  Roslyn Engine   │
└─────────────────┘               └──────────────────┘                      └──────────────────┘
                                         │                                         │
                                    Skills Library                           C# Code Execution
                                   (.skill.md files)                      (full Civil 3D API)
```

## 3 Meta-Tools

| Tool | Purpose | Safety |
|------|---------|--------|
| `civil3d_execute` | Execute C# code with **write** access; optional save after commit | ⚠️ Modifies drawing |
| `civil3d_query` | Run intended read-only C# without committing the host transaction | Trusted code only; not a side-effect sandbox |
| `civil3d_skills` | Browse/search/read code skill templates; `api_lookup` searches already-loaded public Civil 3D API metadata | ✅ Metadata only |

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

`civil3d_skills` also supports `action: "api_lookup"` for a bounded, read-only search of public type and member names/signatures from already-loaded allowlisted Civil 3D host assemblies. It does not load assemblies, run C# code, or access the active drawing. Supply a query and optionally an assembly, namespace prefix, and result limit.

Skills are documented C# code templates in `skills/`:

The current accepted catalog contains 22 skills. It includes bounded road-model inventories, template-style readiness, controlled alignment creation from an identified polyline, connected curve/spiral auditing, and a narrowly guarded fixed-primitive replacement recipe. Dynamic surface-profile/view authoring is still under development and is not included in this published catalog. See [PROJECT_STATUS.md](PROJECT_STATUS.md) for the tested scope and remaining limitations.

```
skills/
├── surfaces/           # Surface operations
├── alignments/         # Alignment + station/offset
├── profiles/           # Profile inventory + elevation lookup
├── corridors/          # Corridor and baseline summaries
├── sections/           # Sample-line and section inventory
├── pipe_networks/      # Gravity pipe-network QC
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

All Civil 3D namespaces are auto-imported.

Committing the `civil3d_execute` transaction changes the open drawing but does not by itself write the DWG file to disk. Set `saveDrawing: true` when the completed change should also be saved. The plugin saves only after the script transaction and document lock are closed; scripts must not call `Database.SaveAs` or queue `QSAVE` themselves. The save request uses a separate 10-minute default timeout and is never retried automatically.

## Setup

### 1. Build MCP Server
```bash
npm install && npm run build
```

### 2. Build Plugin
```bash
# Copy DLLs from Civil 3D to C_References/ (see C_References/README.md)
cd plugin/Civil3dMcpPlugin
dotnet build
```

### 3. Load in Civil 3D
```
NETLOAD → select Civil3dMcpPlugin.dll
C3DMCPSTATUS → verify running
```

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

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `CIVIL3D_HOST` | `localhost` | Plugin host |
| `CIVIL3D_PORT` | unset | Optional fixed-port override. When omitted, local instance discovery is active and the plugin prefers port 8080. |
| `CIVIL3D_CONNECT_TIMEOUT` | `5000` | TCP connection timeout (ms) |
| `CIVIL3D_DISCOVERY_TIMEOUT` | `5000` | Timeout for private health and drawing-identity probes (ms) |
| `CIVIL3D_COMMAND_TIMEOUT` | `120000` | Execution timeout (ms) |
| `CIVIL3D_MCP_EXECUTION_BACKEND` | `modal` | Plugin process: `native` selects the legacy route for a controlled comparison in a fresh Civil session. |
| `CIVIL3D_SAVE_TIMEOUT` | `600000` | Timeout for execute requests with `saveDrawing: true` (ms) |
| `LOG_LEVEL` | `info` | Log level |

Civil requests run as correlated modal commands. The existing serialization gate stays held until the script has disposed its resources and that command ends; completing the script body alone does not release it. A request that has not started within 15 seconds is abandoned, and its late token cannot execute another request. The legacy backend is never selected automatically after a timeout.

## Multiple Civil 3D instances

Each loaded plugin session publishes a small local endpoint record containing only an opaque instance ID, process ID, port, and start time. It contains no drawing name or project data. The first Civil 3D instance prefers port 8080; another instance automatically uses a free localhost port.

If exactly one instance is available, existing calls work as before. With multiple instances, `expectedDrawing` is used to find the matching active drawing before the requested C# reaches any plugin. An unguarded bootstrap query fails closed with `CIVIL3D.INSTANCE_SELECTION_REQUIRED` and reports the live candidates; retry it with the optional `instanceId` on the existing tool. The final plugin-side drawing guard still checks the path and fingerprint immediately before Civil API access. No fourth public MCP tool is added.

Leave `CIVIL3D_PORT` unset for automatic selection. Setting it deliberately pins the MCP server to that one port for compatibility or diagnostics.

## Benchmarking

The phase 2A host-independent recorder, the phase 2A.1 opt-in internal live trace contract, and the phase 2A.2 read-only live runner are documented in [`benchmark/README.md`](benchmark/README.md). None adds an MCP tool, queue, or retry; the 2A.2 runner can invoke only its fixed read-only query when explicitly started.

## Structured errors (phase 2B.1)

`civil3d_query` and `civil3d_execute` keep their existing text error content and `isError: true`, while also returning `structuredContent` with schema `civil3d-mcp-error/v1`. The stable error fields are `code`, `category`, `message`, `source`, `outcome`, and `retryable`. A command timeout or a connection loss after sending has `outcome: "unknown"` and `retryable: false`; the server never retries it automatically. Successful responses and the three-tool public surface are unchanged.

An operation whose Civil command has not started within 15 seconds returns `CIVIL3D.COMMAND_CONTEXT_TIMEOUT` with `outcome: "not_started"` and `retryable: false`. A subsequently arriving abandoned token performs no drawing work. This deadline limits admission, not execution: started work retains the serialized gate until the selected backend's host completion, and an uncertain write must still be reconciled rather than repeated. Private health exposes the execution backend, fixed stage names and elapsed times without drawing content. The targeted completion checks do not establish that every possible hang is eliminated.

## Private TCP framing (phase 2C.1)

Each localhost TCP connection carries one UTF-8 JSON-RPC request and one response. Each JSON body is followed by LF and is limited to 8 MiB, measured as UTF-8 bytes without the LF. The Node client still accepts the previous plugin's unframed response when that complete JSON body is followed by an orderly connection close. Oversized requests are rejected before they are written; oversized or malformed responses and interrupted connections produce non-retryable structured transport errors. If execution completed but the plugin could not return an oversized result, the reported outcome is `unknown`.

## Operation audit logging and write idempotency (phase 2I.1 / 2I.2)

At the default `info` log level, each accepted `civil3d_query` and `civil3d_execute` operation emits one bounded stderr audit event. It contains a new opaque operation ID, tool name, SHA-256 and UTF-8 byte length of the C# source, success/error status, and elapsed milliseconds; errors add only stable code/category/source/outcome fields. The audit event never contains caller code, description, drawing identity, result, or error message.

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
