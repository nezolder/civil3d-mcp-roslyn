// The working order for assistants lives in docs/USAGE.md and is handed over by
// the server itself. These tests keep the hand-over working, the text short,
// free of anything that belongs to one office, and in step with the code.
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { registerTools } from "../../build/tools/register.js";
import {
  DEFAULT_SCRIPT_TIMEOUT_SECONDS,
  MAX_SCRIPT_TIMEOUT_SECONDS,
} from "../../build/tools/scriptTimeout.js";
import {
  GUIDE_TOPICS,
  MAX_CORE_BYTES,
  MAX_TOPIC_BYTES,
  loadUsageGuide,
  parseUsageGuide,
  usageInstructions,
} from "../../build/usageGuide.js";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const read = (...parts) => fs.readFileSync(path.join(root, ...parts), "utf8");
const guideSource = read("docs", "USAGE.md");

function sourceFiles(directory, extension) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    if (entry.isDirectory()) {
      return ["bin", "obj", "node_modules"].includes(entry.name)
        ? []
        : sourceFiles(path.join(directory, entry.name), extension);
    }
    return entry.name.endsWith(extension) ? [path.join(directory, entry.name)] : [];
  });
}

const serverSource = sourceFiles(path.join(root, "src"), ".ts")
  .map((file) => fs.readFileSync(file, "utf8"))
  .join("\n");
const pluginSource = sourceFiles(path.join(root, "plugin"), ".cs")
  .map((file) => fs.readFileSync(file, "utf8"))
  .join("\n");
const pluginFile = (name) => read("plugin", "Civil3dMcpPlugin", name);

async function withClient(run) {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer(
    { name: "usage-guide-test", version: "1.0.0" },
    { instructions: usageInstructions() }
  );
  await registerTools(server);
  const client = new Client({ name: "usage-guide-test-client", version: "1.0.0" });
  try {
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    return await run(client);
  } finally {
    await client.close();
    await server.close();
  }
}

const guideCall = (client, topic) =>
  client.callTool({
    name: "civil3d_skills",
    arguments: topic ? { action: "guide", topic } : { action: "guide" },
  });

test("the build carries the guide of the repository", () => {
  const built = fs.readFileSync(path.join(root, "build", "USAGE.md"), "utf8");
  assert.equal(built, guideSource, "build/USAGE.md is a copy of docs/USAGE.md");
  assert.deepEqual(loadUsageGuide(), parseUsageGuide(guideSource));
});

test("an assistant that has only the tool descriptions is led to the guide", async () => {
  await withClient(async (client) => {
    const { tools } = await client.listTools();
    assert.deepEqual(tools.map((tool) => tool.name).sort(), [
      "civil3d_execute",
      "civil3d_query",
      "civil3d_skills",
    ]);
    const description = (name) => tools.find((tool) => tool.name === name).description;

    for (const name of ["civil3d_query", "civil3d_execute"]) {
      // Near the start, where a client that shortens descriptions still shows it.
      const opening = description(name).slice(0, 500);
      assert.match(opening, /civil3d_skills with action 'status'/, `${name} names the first step`);
      assert.match(opening, /action 'guide'/, `${name} says where the working order is`);
    }
    assert.match(description("civil3d_execute").slice(0, 500), /topic 'writing'/);
    assert.match(description("civil3d_execute").slice(0, 500), /topic 'errors'/);
    assert.match(description("civil3d_skills"), /'guide' to read how to work with this server/);

    const skills = tools.find((tool) => tool.name === "civil3d_skills").inputSchema.properties;
    assert.ok(skills.action.enum.includes("guide"));
    assert.deepEqual(skills.topic.enum, [...GUIDE_TOPICS]);
  });
});

test("guide without a topic returns the working order, which is also the server instructions", async () => {
  await withClient(async (client) => {
    const result = await guideCall(client);
    assert.notEqual(result.isError, true);
    const text = result.content[0].text;
    assert.equal(text, loadUsageGuide().core);
    assert.equal(client.getInstructions(), text);

    // The order the assistant is expected to follow for a write.
    const steps = ['action: "status"', "expectedDrawing", "civil3d_query", "requireClean", "receipt", "outcome"];
    const positions = steps.map((step) => text.indexOf(step));
    assert.ok(positions.every((position) => position >= 0), `every step is named: ${positions}`);
    assert.deepEqual([...positions].sort((a, b) => a - b), positions, "in working order");
    for (const topic of GUIDE_TOPICS) {
      assert.match(text, new RegExp("`" + topic + "`"), `the working order names the topic ${topic}`);
    }
  });
});

test("guide returns each topic as plain text and refuses an unknown one", async () => {
  await withClient(async (client) => {
    const guide = loadUsageGuide();
    for (const topic of GUIDE_TOPICS) {
      const result = await guideCall(client, topic);
      assert.notEqual(result.isError, true, topic);
      assert.equal(result.content[0].text, guide.topics[topic]);
      assert.match(result.content[0].text, /^## /, `${topic} starts with its heading`);
    }
    const unknown = await guideCall(client, "office");
    assert.equal(unknown.isError, true);
  });
});

test("the status reply points to the guide", async () => {
  await withClient(async (client) => {
    const result = await client.callTool({ name: "civil3d_skills", arguments: { action: "status" } });
    assert.notEqual(result.isError, true);
    const status = JSON.parse(result.content[0].text);
    assert.match(status.guide, /action "guide"/);
    for (const topic of GUIDE_TOPICS) assert.ok(status.guide.includes(topic));
  });
});

test("the always-delivered part stays short and the topics bounded", () => {
  const guide = loadUsageGuide();
  const bytes = (text) => Buffer.byteLength(text, "utf8");
  assert.ok(bytes(guide.core) <= MAX_CORE_BYTES, `working order is ${bytes(guide.core)} bytes`);
  for (const topic of GUIDE_TOPICS) {
    const size = bytes(guide.topics[topic]);
    assert.ok(size <= MAX_TOPIC_BYTES, `${topic} is ${size} bytes`);
  }
});

test("a guide file with a missing or repeated part is rejected", () => {
  assert.throws(() => parseUsageGuide("<!-- topic: core -->\n## Working order\n"), /no 'drawing' part/);
  const repeated = guideSource + "\n<!-- topic: errors -->\n## Again\n";
  assert.throws(() => parseUsageGuide(repeated), /'errors' twice/);
});

test("the guide holds no office content, local path or non-English text", () => {
  // Plain ASCII keeps out accented words and typographic characters alike.
  const nonAscii = [...guideSource].filter((character) => character.codePointAt(0) > 127);
  assert.deepEqual(nonAscii, []);
  assert.doesNotMatch(guideSource, /_avult/i, "no office archive folder");
  assert.doesNotMatch(guideSource, /\b[A-Za-z]:[\\/]/, "no drive-letter path");
  assert.doesNotMatch(guideSource, /\\\\[A-Za-z0-9]/, "no network share path");
  assert.doesNotMatch(guideSource, /\\Users\\/i, "no user folder");
});

test("every error code in the guide exists in the server or the plugin", () => {
  const codes = new Set(guideSource.match(/CIVIL3D\.[A-Z0-9_]+/g));
  assert.ok(codes.size >= 15, "the guide names the common error codes");
  for (const code of codes) {
    assert.ok(
      serverSource.includes(`"${code}"`) || pluginSource.includes(`"${code}"`),
      `${code} is raised or classified somewhere in the code`
    );
  }
});

test("the inputs, receipt, journal and status fields in the guide are the real ones", async () => {
  const backticked = new Set([...guideSource.matchAll(/`([A-Za-z]+)(?::[^`]*)?`/g)].map((match) => match[1]));

  await withClient(async (client) => {
    const { tools } = await client.listTools();
    const execute = Object.keys(tools.find((tool) => tool.name === "civil3d_execute").inputSchema.properties);
    for (const input of execute) {
      assert.ok(backticked.has(input), `the guide explains the civil3d_execute input ${input}`);
    }
  });

  const receipt = pluginFile("ExecutionRecord.cs");
  for (const field of ["dbmodBefore", "dbmodAfter", "committed", "saved", "fileLastWriteUtc", "fileBytes", "backupPath", "databaseFilename", "fingerprintGuid"]) {
    assert.ok(backticked.has(field) && receipt.includes(`["${field}"]`), `receipt field ${field}`);
  }
  const journal = pluginFile("WriteJournal.cs");
  for (const field of ["timeUtc", "description", "ok", "errorCode", "committed", "saved", "backupPath"]) {
    assert.ok(backticked.has(field) && journal.includes(`["${field}"]`), `journal field ${field}`);
  }
  const status = read("src", "utils", "ConnectionManager.ts");
  for (const field of ["instanceId", "activeDrawingKnown", "activeDrawing", "operationInProgress", "queueDepth"]) {
    assert.ok(backticked.has(field) && new RegExp(`\\b${field}\\b`).test(status), `status field ${field}`);
  }

  assert.match(guideSource, new RegExp(`default ${DEFAULT_SCRIPT_TIMEOUT_SECONDS}\\b`));
  assert.match(guideSource, new RegExp(`1 to ${MAX_SCRIPT_TIMEOUT_SECONDS} seconds`));
});

test("the README and the recipe guide refer to the working order instead of restating it", () => {
  const readme = read("README.md");
  assert.match(readme, /\]\(docs\/USAGE\.md\)/);
  assert.doesNotMatch(readme, /First identify the active drawing with `civil3d_query`/);

  const recipes = read("docs", "ENGINEERING_SKILLS.md");
  assert.match(recipes, /\]\(USAGE\.md\)/);
  // The hand-made check and copy from before the safety inputs existed.
  for (const text of [readme, recipes]) {
    assert.doesNotMatch(text, /timestamped filesystem backup/);
  }
  for (const file of sourceFiles(path.join(root, "skills"), ".skill.md")) {
    assert.doesNotMatch(
      fs.readFileSync(file, "utf8"),
      /timestamped (?:unchanged )?(?:filesystem )?backup/,
      `${path.basename(file)} leaves the backup to the guide`
    );
  }
});
