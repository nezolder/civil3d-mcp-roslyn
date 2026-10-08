import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { registerSkillsTool } from "../../build/tools/skillsTool.js";

async function withClient(run, options = {}) {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "discovery-metadata", version: "1" });
  registerSkillsTool(server, options);
  const client = new Client({ name: "discovery-metadata-test", version: "1" });
  try {
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    await run(async (arguments_) => {
      const result = await client.callTool({ name: "civil3d_skills", arguments: arguments_ });
      assert.notEqual(result.isError, true, result.content?.[0]?.text);
      return JSON.parse(result.content[0].text);
    });
  } finally {
    await client.close();
    await server.close();
  }
}

function fixture(name, discovery = "", newline = "\n") {
  return [
    "---", `name: ${name}`, "category: fixtures", "description: Legacy fixture",
    "requires_write: false", discovery, "parameters:", "  - name: limit",
    "    type: number", "    required: false", "    description: Detail cap", "---",
    "", "# Fixture", "", "body_only_marker is not search metadata", "",
  ].join("\n").replaceAll("\n", newline);
}

async function withFixtures(definitions, run) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "civil3d-discovery-test-"));
  try {
    for (const [name, text] of Object.entries(definitions)) {
      fs.writeFileSync(path.join(directory, `${name}.skill.md`), text, "utf8");
    }
    await withClient(run, { skillsDirectory: directory });
  } finally {
    // The target is the unique directory just created above, never a repo path.
    assert.ok(path.basename(directory).startsWith("civil3d-discovery-test-"));
    assert.equal(path.dirname(fs.realpathSync(directory)), fs.realpathSync(os.tmpdir()));
    fs.rmSync(directory, { recursive: true, force: true });
  }
}

test("legacy recipes keep parameters/body and unknown validation defaults", async () => {
  await withFixtures({ legacy: fixture("legacy") }, async (call) => {
    const recipe = await call({ action: "get", skillName: "legacy" });
    assert.deepEqual(recipe.aliases, []);
    assert.deepEqual(recipe.workflow_tags, []);
    assert.equal(recipe.tested_civil_version, null);
    assert.equal(recipe.validation_summary, null);
    assert.equal(recipe.requires_write, false);
    assert.deepEqual(recipe.parameters.map((p) => p.name), ["limit"]);
    assert.match(recipe.content, /body_only_marker/);
    const result = await call({ action: "search", query: "body_only_marker" });
    assert.equal(result.total, 0);
  });
});

test("inline discovery JSON supports quoted commas/colons, Unicode and CRLF", async () => {
  const fields = [
    'aliases: ["hossz-szelvény", "station, elevation", "requires_write: true", "hossz-szelvény"]',
    'workflow_tags: ["drawing_view"]',
    'tested_civil_version: "2025"',
    'validation_summary: "Offline only: live, save and reopen unverified."',
  ].join("\n");
  await withFixtures({ unicode: fixture("unicode", fields, "\r\n") }, async (call) => {
    const recipe = await call({ action: "get", skillName: "unicode" });
    assert.deepEqual(recipe.aliases, ["hossz-szelvény", "station, elevation", "requires_write: true"]);
    assert.equal(recipe.requires_write, false);
    assert.equal(recipe.tested_civil_version, "2025");
    assert.match(recipe.validation_summary, /live, save and reopen unverified/);
    assert.equal((await call({ action: "search", query: "HOSSZ SZELVENY" })).total, 1);
    assert.equal((await call({ action: "search", query: "drawing view" })).total, 1);
    assert.deepEqual(recipe.parameters.map((p) => p.name), ["limit"]);
  });
});

test("invalid discovery fields cannot invent validation or drop a legacy recipe", async () => {
  const cases = [
    'aliases: {"wrong":"type"}\nworkflow_tags: [1]\ntested_civil_version: 2025\nvalidation_summary: true',
    'aliases: not-json\nworkflow_tags: null\ntested_civil_version: []\nvalidation_summary: {"status":"proven"}',
    `aliases: ${JSON.stringify(Array(9).fill("too_many"))}\nworkflow_tags: [""]\nvalidation_summary: ${JSON.stringify("a".repeat(281))}`,
    `aliases: ${JSON.stringify(["line\nbreak"])}\ntested_civil_version: ${JSON.stringify("2025\nlive")}\nvalidation_summary: null`,
    `aliases: ${JSON.stringify(["line\u0085break"])}\nworkflow_tags: ${JSON.stringify(["audit\u007f"])}\ntested_civil_version: ${JSON.stringify("2025\u007f")}\nvalidation_summary: ${JSON.stringify("offline\u0085only")}`,
  ];
  for (const fields of cases) {
    await withFixtures({ invalid: fixture("invalid", fields) }, async (call) => {
      const recipe = await call({ action: "get", skillName: "invalid" });
      assert.deepEqual(recipe.aliases, []);
      assert.deepEqual(recipe.workflow_tags, []);
      assert.equal(recipe.tested_civil_version, null);
      assert.equal(recipe.validation_summary, null);
      assert.equal(recipe.requires_write, false);
      assert.deepEqual(recipe.parameters.map((p) => p.name), ["limit"]);
    });
  }
});

test("real catalogue aliases cover engineering work words without claiming new operations", async () => {
  await withClient(async (call) => {
    const names = async (query, category) => (await call({ action: "search", query, category, limit: 50 })).results.map((p) => p.name);
    assert.ok((await names("hossz-szelvény")).includes("create_design_profile_from_pvis"));
    assert.deepEqual(await names("hossz-szelvény"), await names("HOSSZ SZELVENY"));
    assert.deepEqual(await names("hossz–szelvény"), await names("hossz-szelvény"));
    assert.ok((await names("keresztszelvény")).includes("create_section_views"));
    assert.ok((await names("cross section")).includes("section_inventory"));
    assert.ok((await names("közmű")).includes("pipe_network_qc"));
    assert.ok((await names("kozmu")).includes("pipe_network_qc"));
    assert.ok((await names("mennyiség")).includes("material_quantity_report"));
    assert.ok((await names("mennyiség")).includes("surface_volume"));
    assert.deepEqual(await names("cross section", "profiles"), []);
    assert.deepEqual(await names("hossz-szelvény no_such_word"), []);
    assert.deepEqual(await names("–– ___"), []);
  });
});

test("pages expose scoped evidence and write flags, with aliases only in get", async () => {
  await withClient(async (call) => {
    const inventory = await call({ action: "list", limit: 50 });
    assert.equal(inventory.total, 36);
    for (const item of inventory.skills) {
      assert.ok(item.workflow_tags.length > 0, item.name);
      assert.equal(item.tested_civil_version, "2025", item.name);
      assert.equal(typeof item.validation_summary, "string", item.name);
      assert.ok(item.validation_summary.length <= 280);
      assert.equal(Object.hasOwn(item, "content"), false);
      assert.equal(Object.hasOwn(item, "aliases"), false);
      const recipe = await call({ action: "get", skillName: item.name });
      assert.ok(recipe.aliases.length >= 2, item.name);
      assert.equal(recipe.requires_write, item.requires_write);
      assert.equal(recipe.validation_summary, item.validation_summary);
    }
    const profile = (await call({ action: "search", query: "TIN surface profile", limit: 50 })).results.find((p) => p.name === "create_surface_profile_view");
    assert.equal(profile.requires_write, true);
    assert.match(profile.validation_summary, /offline/i);
    assert.match(profile.validation_summary, /live.*unverified/i);
    const targets = (await call({ action: "get", skillName: "corridor_target_audit" }));
    assert.match(targets.validation_summary, /empty corridor/i);
    assert.match(targets.validation_summary, /populated.*unverified/i);
    const sections = (await call({ action: "get", skillName: "create_section_views" }));
    assert.match(sections.validation_summary, /reopen.*unverified/i);
  });
});
