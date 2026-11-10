import assert from "node:assert/strict";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { registerSkillsTool } from "../../build/tools/skillsTool.js";

async function search(query) {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "skills-search-test", version: "1.0.0" });
  registerSkillsTool(server);
  const client = new Client({ name: "skills-search-test-client", version: "1.0.0" });
  try {
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    const result = await client.callTool({
      name: "civil3d_skills",
      arguments: { action: "search", query, limit: 50 },
    });
    assert.notEqual(result.isError, true);
    return { text: result.content[0].text, body: JSON.parse(result.content[0].text) };
  } finally {
    await client.close();
    await server.close();
  }
}

test("multi-word search matches every word in any order and across underscores", async () => {
  const names = async (query) => (await search(query)).body.results.map((s) => s.name);

  assert.ok((await names("surface volume")).includes("surface_volume"));
  assert.ok((await names("volume surface")).includes("surface_volume"));
  assert.ok((await names("create profile")).includes("create_design_profile_from_pvis"));
  assert.ok((await names("  SECTION   views ")).includes("create_section_views"));
  assert.deepEqual(await names("zzzqqq xyzzyq"), []);
});

test("an unknown extra word returns the closest skills flagged as partial", async () => {
  const exact = (await search("térfogat")).body;
  assert.equal(Object.hasOwn(exact, "partial"), false);
  assert.ok(exact.results.some((skill) => skill.name === "surface_volume"));

  // No skill has all three words; before the fallback this returned nothing.
  const partial = (await search("felület térfogat számítás")).body;
  assert.equal(partial.partial, true);
  assert.equal(partial.queryWords, 3);
  assert.equal(partial.matchedWords, 2);
  assert.ok(partial.results.some((skill) => skill.name === "surface_volume"));
  assert.equal(partial.total, partial.results.length);

  // Short filler words must not outrank the real domain words.
  const filler = (await search("hossz-szelvény no_such_word")).body;
  assert.equal(filler.partial, true);
  assert.ok(filler.results.some((skill) => skill.name === "create_design_profile_from_pvis"));
  assert.deepEqual(
    filler.results.map((skill) => skill.name),
    (await search("hossz-szelvény")).body.results.map((skill) => skill.name)
  );
});

test("skill listings are returned as compact single-line JSON", async () => {
  const { text } = await search("surface");
  assert.equal(text.includes("\n"), false);
});

test("search can find a recipe through parameter metadata", async () => {
  const { body } = await search("samplePoints");
  assert.ok(body.results.some((skill) => skill.name === "compare_surface_elevations"));
});

test("search without a normalized word does not return the whole catalog", async () => {
  for (const query of ["   ", "___", " _ \t _ "]) {
    const { body } = await search(query);
    assert.equal(body.total, 0);
    assert.deepEqual(body.results, []);
    assert.equal(body.nextCursor, null);
  }
});
