import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const transport = new StdioClientTransport({
  command: process.execPath,
  args: ["build/index.js"],
  cwd: process.cwd(),
  env: {
    ...process.env,
    LOG_LEVEL: "error",
  },
});

const client = new Client({
  name: "civil3d-mcp-smoke-test",
  version: "1.0.0",
});

try {
  await client.connect(transport);
  const { tools } = await client.listTools();
  const names = tools.map((tool) => tool.name).sort();
  const expected = ["civil3d_execute", "civil3d_query", "civil3d_skills"];

  if (JSON.stringify(names) !== JSON.stringify(expected)) {
    throw new Error(`Unexpected tool list: ${names.join(", ")}`);
  }

  const skillResult = await client.callTool({
    name: "civil3d_skills",
    arguments: { action: "list" },
  });
  const skillPayload = JSON.parse(skillResult.content[0].text);

  if (skillPayload.count < 1) {
    throw new Error("Civil 3D skill library is empty");
  }

  console.log(
    JSON.stringify({ ok: true, tools: names, skillCount: skillPayload.count })
  );
} finally {
  await client.close();
}
