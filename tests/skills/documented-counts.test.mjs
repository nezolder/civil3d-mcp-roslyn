// The numbers in the documentation are written by hand. This test ties them to
// the skills folder, so that they cannot drift apart unnoticed again.
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { SKILLS_DIR, registerSkillsTool } from "../../build/tools/skillsTool.js";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");

function skillFiles(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) =>
    entry.isDirectory()
      ? skillFiles(path.join(directory, entry.name))
      : entry.name.endsWith(".skill.md") ? [path.join(directory, entry.name)] : []
  );
}

async function catalogueCounts() {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "documented-counts", version: "1.0.0" });
  registerSkillsTool(server, { listInstances: async () => [] });
  const client = new Client({ name: "documented-counts-client", version: "1.0.0" });
  try {
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    const result = await client.callTool({ name: "civil3d_skills", arguments: { action: "status" } });
    return JSON.parse(result.content[0].text).server;
  } finally {
    await client.close();
    await server.close();
  }
}

test("the documented skill counts equal what the skills folder holds", async () => {
  const files = skillFiles(SKILLS_DIR);
  const templates = files.reduce(
    (count, file) => count + (fs.readFileSync(file, "utf8").match(/^```csharp\s*$/gm) ?? []).length,
    0
  );
  const { skillCount, runByNameCount } = await catalogueCounts();
  assert.equal(skillCount, files.length, "every skill file must parse into the catalogue");

  const hint = (document) =>
    `${document} states a count that differs from the skills folder ` +
    `(${skillCount} skills, ${runByNameCount} run by name, ${templates} C# templates); update the text`;

  const readme = fs.readFileSync(path.join(root, "README.md"), "utf8");
  const readmeTotal = readme.match(/The catalog contains \*\*(\d+) skills\*\*/);
  const readmeByName = readme.match(/`run_by_name` \((\d+) of (\d+),/);
  assert.ok(readmeTotal && readmeByName, "README must state the catalogue size in its fixed wording");
  assert.deepEqual(
    [Number(readmeTotal[1]), Number(readmeByName[1]), Number(readmeByName[2])],
    [skillCount, runByNameCount, skillCount],
    hint("README.md")
  );

  const status = fs.readFileSync(path.join(root, "PROJECT_STATUS.md"), "utf8");
  const statusCounts = status.match(
    /\*\*(\d+) skills\*\* with \*\*(\d+) C# templates\*\*; \*\*(\d+)\*\* of the skills run by name/
  );
  assert.ok(statusCounts, "PROJECT_STATUS.md must state the current catalogue in its fixed wording");
  assert.deepEqual(
    statusCounts.slice(1).map(Number),
    [skillCount, templates, runByNameCount],
    hint("PROJECT_STATUS.md")
  );
});
