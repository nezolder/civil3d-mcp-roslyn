import assert from "node:assert/strict";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { registerSkillsTool } from "../../build/tools/skillsTool.js";

test("engineering recipes are discoverable with complete arguments and correct write classification", async () => {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "engineering-discovery", version: "1" });
  registerSkillsTool(server);
  const client = new Client({ name: "engineering-discovery-client", version: "1" });
  const expected = {
    create_design_profile_from_pvis: [true, "profiles", ["alignmentHandle", "profileName", "profileStyleName", "profileLabelSetName", "pviData"]],
    create_section_views: [true, "sections", ["groupHandle", "startStation", "endStation", "viewNamePrefix", "styleName", "bandSetStyleName", "originX", "originY", "columns", "gapX", "gapY", "viewLimit"]],
    material_quantity_report: [false, "sections", ["groupHandle", "materialListGuid", "startStation", "endStation", "limit"]],
    compare_surface_elevations: [false, "surfaces", ["surfaceAHandle", "surfaceBHandle", "samplePoints", "tolerance", "detailLimit"]],
    corridor_target_audit: [false, "corridors", ["corridorHandle", "baselineNameFilter", "regionNameFilter", "targetLimit", "targetObjectLimit"]],
    data_reference_audit: [false, "references", ["categoryFilter", "onlyReferences", "limit", "scanLimit"]],
  };
  try {
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    const list = await client.callTool({ name: "civil3d_skills", arguments: { action: "list", limit: 50 } });
    assert.notEqual(list.isError, true);
    const inventory = JSON.parse(list.content[0].text);
    const entries = new Map(inventory.skills.map(s => [s.name, s]));
    assert.equal(entries.size, inventory.total);
    for (const [name, [writes, category, parameters]] of Object.entries(expected)) {
      assert.ok(entries.has(name), `missing recipe ${name}`);
      const result = await client.callTool({ name: "civil3d_skills", arguments: { action: "get", skillName: name } });
      assert.notEqual(result.isError, true);
      const recipe = JSON.parse(result.content[0].text);
      assert.equal(recipe.requires_write, writes);
      assert.equal(recipe.category, category);
      if (parameters) assert.deepEqual(recipe.parameters.map(p => p.name), parameters);
      assert.match(recipe.content, /```csharp/);
      assert.match(recipe.content, /Host access starts here/);
      if (writes) {
        assert.match(recipe.content, /saveDrawing: true/);
        assert.match(recipe.content, /FingerprintGuid/);
      }
      const search = await client.callTool({ name: "civil3d_skills", arguments: { action: "search", query: name, category, limit: 50 } });
      assert.notEqual(search.isError, true);
      assert.ok(JSON.parse(search.content[0].text).results.some(s => s.name === name));
    }
  } finally {
    await client.close();
    await server.close();
  }
});
