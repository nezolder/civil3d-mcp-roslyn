import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerTools } from "./tools/register.js";
import { createLogger } from "./utils/logger.js";
import { SERVER_BUILD, serverVersionLabel } from "./buildInfo.js";

const log = createLogger("MCP");

const server = new McpServer({
  name: "civil3d-mcp",
  // The commit the server was built from follows the version, e.g. 1.0.0+abc1234.
  version: serverVersionLabel(),
});

async function main() {
  await registerTools(server);

  const transport = new StdioServerTransport();
  await server.connect(transport);
  log.info("Civil 3D MCP Server started", {
    commit: SERVER_BUILD.commit,
    builtAtUtc: SERVER_BUILD.builtAtUtc,
  });
}

main().catch((error) => {
  log.error("Error starting Civil 3D MCP Server", { error: String(error) });
  process.exit(1);
});
