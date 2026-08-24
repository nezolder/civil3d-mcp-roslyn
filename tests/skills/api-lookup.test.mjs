import assert from "node:assert/strict";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { Civil3dMcpError, civil3dErrorFromPlugin } from "../../build/errors/structuredError.js";
import { registerSkillsTool } from "../../build/tools/skillsTool.js";

async function withSkillsClient(apiLookup, run) {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "api-lookup-test", version: "1.0.0" });
  registerSkillsTool(server, { apiLookup });
  const client = new Client({ name: "api-lookup-test-client", version: "1.0.0" });

  try {
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    await run(client);
  } finally {
    await client.close();
    await server.close();
  }
}

test("api_lookup keeps the three-tool schema and forwards only bounded metadata parameters", async () => {
  const calls = [];
  const lookupResult = {
    total: 2,
    returned: 1,
    truncated: true,
    results: [{ assembly: "AeccDbMgd", type: "Autodesk.Civil.DatabaseServices.TinSurface", member: "AddVertex(Point3d)" }],
  };

  await withSkillsClient(async (parameters) => {
    calls.push(parameters);
    return lookupResult;
  }, async (client) => {
    const { tools } = await client.listTools();
    assert.equal(tools.length, 1);
    assert.equal(tools[0].name, "civil3d_skills");
    assert.deepEqual(tools[0].inputSchema.properties.action.enum, ["list", "search", "get", "api_lookup"]);
    assert.match(tools[0].description, /already-loaded Civil 3D host assemblies/);

    const result = await client.callTool({
      name: "civil3d_skills",
      arguments: {
        action: "api_lookup",
        query: "TinSurface AddVertex",
        assembly: "aeccdbmgd",
        namespace: "Autodesk.Civil",
        limit: 7,
      },
    });
    assert.notEqual(result.isError, true);
    assert.deepEqual(JSON.parse(result.content[0].text), lookupResult);
    assert.deepEqual(calls, [{
      query: "TinSurface AddVertex",
      assembly: "aeccdbmgd",
      namespace: "Autodesk.Civil",
      limit: 7,
    }]);
  });
});

test("api_lookup returns structured plugin errors and preserves list/search/get behavior", async () => {
  const classifiedUnavailable = civil3dErrorFromPlugin({
    code: "CIVIL3D.API_LOOKUP_UNAVAILABLE",
    message: "Civil 3D API metadata is unavailable because no allowlisted host assemblies are loaded.",
  });
  assert.equal(classifiedUnavailable.category, "execution");
  assert.equal(classifiedUnavailable.outcome, "reported_error");

  await withSkillsClient(async () => {
    throw new Civil3dMcpError(
      "CIVIL3D.API_LOOKUP_UNAVAILABLE",
      "Civil 3D API metadata is unavailable because no allowlisted host assemblies are loaded.",
      "execution",
      "plugin",
      "reported_error"
    );
  }, async (client) => {
    const missing = await client.callTool({
      name: "civil3d_skills",
      arguments: { action: "api_lookup" },
    });
    assert.equal(missing.isError, true);
    assert.match(missing.content[0].text, /^API lookup failed: Parameter 'query' is required/);
    assert.equal(missing.structuredContent.error.code, "CIVIL3D.INVALID_INPUT");

    const unavailable = await client.callTool({
      name: "civil3d_skills",
      arguments: { action: "api_lookup", query: "TinSurface" },
    });
    assert.equal(unavailable.isError, true);
    assert.match(unavailable.content[0].text, /^API lookup failed:/);
    assert.equal(unavailable.structuredContent.error.code, "CIVIL3D.API_LOOKUP_UNAVAILABLE");
    assert.equal(unavailable.structuredContent.error.source, "plugin");

    const list = await client.callTool({
      name: "civil3d_skills",
      arguments: { action: "list", limit: 1 },
    });
    const listPayload = JSON.parse(list.content[0].text);
    assert.equal(listPayload.returned, 1);
    assert.ok(Array.isArray(listPayload.skills));

    const search = await client.callTool({
      name: "civil3d_skills",
      arguments: { action: "search", query: "surface", limit: 1 },
    });
    const searchPayload = JSON.parse(search.content[0].text);
    assert.equal(searchPayload.returned, 1);
    assert.ok(Array.isArray(searchPayload.results));

    const get = await client.callTool({
      name: "civil3d_skills",
      arguments: { action: "get", skillName: "drawing_info" },
    });
    assert.equal(JSON.parse(get.content[0].text).name, "drawing_info");
  });
});
