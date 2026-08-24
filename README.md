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
│ (Claude, Cline)  │               │   3 meta-tools    │     port 8080       │  Roslyn Engine   │
└─────────────────┘               └──────────────────┘                      └──────────────────┘
                                         │                                         │
                                    Skills Library                           C# Code Execution
                                   (.skill.md files)                      (full Civil 3D API)
```

## 3 Meta-Tools

| Tool | Purpose | Safety |
|------|---------|--------|
| `civil3d_execute` | Execute C# code with **write** access (transaction committed) | ⚠️ Modifies drawing |
| `civil3d_query` | Execute C# code **read-only** (no commit) | ✅ No side effects |
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

```
skills/
├── surfaces/           # Surface operations
├── alignments/         # Alignment + station/offset
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
| `CIVIL3D_PORT` | `8080` | Plugin port |
| `CIVIL3D_COMMAND_TIMEOUT` | `120000` | Execution timeout (ms) |
| `LOG_LEVEL` | `info` | Log level |

## Benchmarking

The phase 2A host-independent recorder, the phase 2A.1 opt-in internal live trace contract, and the phase 2A.2 read-only live runner are documented in [`benchmark/README.md`](benchmark/README.md). None adds an MCP tool, queue, or retry; the 2A.2 runner can invoke only its fixed read-only query when explicitly started.

## Structured errors (phase 2B.1)

`civil3d_query` and `civil3d_execute` keep their existing text error content and `isError: true`, while also returning `structuredContent` with schema `civil3d-mcp-error/v1`. The stable error fields are `code`, `category`, `message`, `source`, `outcome`, and `retryable`. A command timeout or a connection loss after sending has `outcome: "unknown"` and `retryable: false`; the server never retries it automatically. Successful responses and the three-tool public surface are unchanged.

## Private TCP framing (phase 2C.1)

Each localhost TCP connection carries one UTF-8 JSON-RPC request and one response. Each JSON body is followed by LF and is limited to 8 MiB, measured as UTF-8 bytes without the LF. The Node client still accepts the previous plugin's unframed response when that complete JSON body is followed by an orderly connection close. Oversized requests are rejected before they are written; oversized or malformed responses and interrupted connections produce non-retryable structured transport errors. If execution completed but the plugin could not return an oversized result, the reported outcome is `unknown`.

## Operation audit logging and write idempotency (phase 2I.1 / 2I.2)

At the default `info` log level, each accepted `civil3d_query` and `civil3d_execute` operation emits one bounded stderr audit event. It contains a new opaque operation ID, tool name, SHA-256 and UTF-8 byte length of the C# source, success/error status, and elapsed milliseconds; errors add only stable code/category/source/outcome fields. The audit event never contains caller code, description, drawing identity, result, or error message.

`civil3d_execute` also accepts an optional opaque `idempotencyKey` (1–128 ASCII letters, digits, `.`, `_`, `:`, `-`). In one plugin session it binds the key to the UTF-8 C# SHA-256 and normalized `expectedDrawing` identity. A duplicate is rejected as in-progress, conflicting, or already committed; committed entries retain no result and callers must reconcile with a read-only query. The session keeps at most 256 completed keys, evicting the oldest deterministically. This adds neither persistence nor automatic retry or exactly-once semantics.

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
