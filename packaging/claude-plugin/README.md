# nezolder-civil3d-mcp (Claude plugin, connector only)

This plugin does one thing: it starts the Civil 3D Roslyn MCP server installed on this PC
([nezolder/civil3d-mcp-roslyn](https://github.com/nezolder/civil3d-mcp-roslyn)), so that Claude
gets its three tools: `civil3d_query`, `civil3d_execute` and `civil3d_skills`. It contains no
skills; the server hands over its own working order (`civil3d_skills`, `action: "guide"`).

The same plugin works on every PC. Install it from a plugin marketplace that lists it
(`/plugin install nezolder-civil3d-mcp@<marketplace>` in Claude Code, Customize > Plugins in the
Claude app), or upload the `.plugin` file of a release in the Claude app.

## Where it looks for the server

`.mcp.json` does not name a fixed path; it runs the launcher `start-civil3d-mcp.mjs`, which loads
the first of these that exists:

1. the `build\index.js` named in the environment variable `CIVIL3D_MCP_SERVER_ENTRY`, when set;
2. the standard place, `%LOCALAPPDATA%\Civil3dMcpRoslyn\server\build\index.js`, filled by
   `npm run install:server` in the source folder.

When neither exists, the launcher prints where it looked and exits with an error. Node.js is
started from `C:\Program Files\nodejs\node.exe`.

## Notes

- Civil 3D 2025 must have the MCP plugin loaded (`C3DMCPSTATUS`).
- Updating the server does not need a new plugin: `npm run build`, then `npm run install:server`
  with the AI clients closed, then restart the Claude app fully.
- Do not keep two copies of this plugin active (for example an uploaded one and one from a
  marketplace): each starts the server and the tools appear twice.
