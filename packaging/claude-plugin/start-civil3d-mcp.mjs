// Starts the Civil 3D MCP server (nezolder/civil3d-mcp-roslyn) installed on this PC.
// The standard place is %LOCALAPPDATA%\Civil3dMcpRoslyn\server, filled by
// `npm run install:server` in the source folder. The plugin is the same on every PC.
import { existsSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

const localAppData = process.env.LOCALAPPDATA || join(homedir(), "AppData", "Local");
const standard = join(localAppData, "Civil3dMcpRoslyn", "server", "build", "index.js");

const candidates = [
  // Optional override: full path of the server's build\index.js.
  process.env.CIVIL3D_MCP_SERVER_ENTRY,
  standard,
].filter((candidate) => typeof candidate === "string" && candidate.trim() !== "");

const entry = candidates.find((candidate) => existsSync(candidate));

if (!entry) {
  console.error(
    "[nezolder-civil3d-mcp] The Civil 3D MCP server was not found on this PC. Looked for:\n" +
      candidates.map((candidate) => "  " + candidate).join("\n") +
      "\nInstall it with `npm run install:server` in the source folder, " +
      "or set CIVIL3D_MCP_SERVER_ENTRY to the full path of the server's build\\index.js."
  );
  process.exit(1);
}

// The server runs in this process, so its standard input and output stay the MCP channel.
await import(pathToFileURL(entry).href);
