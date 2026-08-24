import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { registerExecuteTool } from "./executeTool.js";
import { registerQueryTool } from "./queryTool.js";
import { registerSkillsTool } from "./skillsTool.js";

/**
 * Register the 3 meta-tools for code execution architecture.
 *
 * - civil3d_execute: Write operations (committed transaction)
 * - civil3d_query: Read-only operations (no commit)
 * - civil3d_skills: Browse/search/read code skill templates
 */
export async function registerTools(server: McpServer) {
  registerExecuteTool(server);
  registerQueryTool(server);
  registerSkillsTool(server);
}
