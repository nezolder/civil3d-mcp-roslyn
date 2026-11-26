# nezolder-civil3d-mcp (Codex plugin, connector only)

The Codex variant of the [Claude connector plugin](https://github.com/nezolder/civil3d-mcp-roslyn/tree/main/packaging/claude-plugin): it starts the
Civil 3D Roslyn MCP server installed on this PC
([nezolder/civil3d-mcp-roslyn](https://github.com/nezolder/civil3d-mcp-roslyn)) as the MCP server
`civil3d_roslyn`, with the tools `civil3d_query`, `civil3d_execute` and `civil3d_skills`. It
contains no skills.

Install it from a plugin marketplace that lists it:
`codex plugin marketplace add <owner>/<repo>`, then `codex plugin add nezolder-civil3d-mcp@<marketplace>`;
check with `codex mcp list`.

## How it starts the server

`.mcp.json` runs the launcher `start-civil3d-mcp.mjs` (the same file as in the Claude plugin) with
`"cwd": "."`: Codex resolves that working folder to the installed plugin folder, so the relative
launcher path works on every PC. Codex has no `${CLAUDE_PLUGIN_ROOT}` for MCP servers. The launcher
loads `CIVIL3D_MCP_SERVER_ENTRY` when set, otherwise the standard
`%LOCALAPPDATA%\Civil3dMcpRoslyn\server\build\index.js` filled by `npm run install:server`. Node.js
is started from `C:\Program Files\nodejs\node.exe`.

## Notes

- A server of the same name in `~/.codex/config.toml` (`[mcp_servers.civil3d_roslyn]`) takes
  precedence over the plugin's, and one under another name runs next to it. Remove a hand-written
  entry for this server when the plugin is installed.
- Time limits: `tool_timeout_sec` 660 lets Codex wait for a write that saves the drawing (the
  server's save limit is 10 minutes, plus a margin); `startup_timeout_sec` 30 gives the server time
  to start. Without them Codex uses its own defaults (60 s for a tool call, 10 s for the start).
- Civil 3D 2025 must have the MCP plugin loaded (`C3DMCPSTATUS`).
