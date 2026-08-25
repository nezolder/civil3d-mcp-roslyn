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

  const executeTool = tools.find((tool) => tool.name === "civil3d_execute");
  const saveDrawingSchema = executeTool?.inputSchema?.properties?.saveDrawing;
  if (
    saveDrawingSchema?.type !== "boolean" ||
    executeTool.inputSchema.required?.includes("saveDrawing")
  ) {
    throw new Error("civil3d_execute.saveDrawing must be an optional boolean");
  }

  const firstPageResult = await client.callTool({
    name: "civil3d_skills",
    arguments: { action: "list", limit: 1 },
  });
  const firstPage = JSON.parse(firstPageResult.content[0].text);

  if (
    firstPage.total < 2 ||
    firstPage.returned !== 1 ||
    typeof firstPage.nextCursor !== "string"
  ) {
    throw new Error("Civil 3D skill first-page contract is invalid");
  }

  const secondPageResult = await client.callTool({
    name: "civil3d_skills",
    arguments: {
      action: "list",
      limit: 1,
      cursor: firstPage.nextCursor,
    },
  });
  const secondPage = JSON.parse(secondPageResult.content[0].text);
  if (
    secondPage.returned !== 1 ||
    secondPage.skills[0].name === firstPage.skills[0].name
  ) {
    throw new Error("Civil 3D skill pagination did not advance");
  }

  const searchResult = await client.callTool({
    name: "civil3d_skills",
    arguments: {
      action: "search",
      category: "surfaces",
      query: "elevation",
      limit: 1,
    },
  });
  const searchPage = JSON.parse(searchResult.content[0].text);
  if (searchPage.returned !== 1) {
    throw new Error("Civil 3D skill search returned no surface skill");
  }

  const skillName = searchPage.results[0].name;
  const getResult = await client.callTool({
    name: "civil3d_skills",
    arguments: { action: "get", skillName },
  });
  const skill = JSON.parse(getResult.content[0].text);
  if (skill.name !== skillName || typeof skill.content !== "string") {
    throw new Error("Civil 3D skill get returned an invalid skill");
  }

  console.log(
    JSON.stringify({
      ok: true,
      tools: names,
      saveDrawing: "optional-boolean",
      skillCount: firstPage.total,
      pagination: true,
      searchAndGet: skillName,
    })
  );
} finally {
  await client.close();
}
