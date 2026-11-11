import assert from "node:assert/strict";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { SERVER_BUILD, SERVER_VERSION, buildsDiffer, serverVersionLabel } from "../../build/buildInfo.js";
import { SKILLS_DIR, registerSkillsTool } from "../../build/tools/skillsTool.js";

const OTHER_COMMIT = "0123456789abcdef0123456789abcdef01234567";
const FIRST_INSTANCE = "11111111111141118111111111111111";
const SECOND_INSTANCE = "22222222222242228222222222222222";

function instance(overrides) {
  return {
    instanceId: FIRST_INSTANCE,
    processId: 41001,
    port: 8080,
    startedAtUtc: "2026-11-11T08:00:00Z",
    pluginBuild: { ...SERVER_BUILD },
    buildMatchesServer: SERVER_BUILD.commit === "unknown" ? null : true,
    activeDrawingKnown: true,
    activeDrawing: {
      databaseFilename: "C:\\Projects\\First.dwg",
      fingerprintGuid: "{AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE}",
      observedAtUtc: "2026-11-11T09:00:00Z",
    },
    operationInProgress: false,
    queueDepth: 0,
    executionBackend: "modal",
    features: ["activeDrawing", "writeSafety"],
    ...overrides,
  };
}

async function readStatus(listInstances) {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "status-test", version: "1.0.0" });
  registerSkillsTool(server, { listInstances });
  const client = new Client({ name: "status-test-client", version: "1.0.0" });
  try {
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    return await client.callTool({ name: "civil3d_skills", arguments: { action: "status" } });
  } finally {
    await client.close();
    await server.close();
  }
}

test("status names the server build, its skills folder and every running instance", async () => {
  const instances = [
    // Clean, so that only the commit comparison decides what is called out.
    instance({ pluginBuild: { ...SERVER_BUILD, dirty: false } }),
    instance({
      instanceId: SECOND_INSTANCE,
      processId: 41002,
      port: 49152,
      pluginBuild: { commit: OTHER_COMMIT, dirty: false, builtAtUtc: "2026-11-01T08:00:00Z" },
      buildMatchesServer: SERVER_BUILD.commit === "unknown" ? null : false,
    }),
  ];
  const result = await readStatus(async () => instances);
  assert.notEqual(result.isError, true);
  const status = JSON.parse(result.content[0].text);

  assert.equal(status.server.name, "civil3d-mcp");
  assert.equal(status.server.version, SERVER_VERSION);
  assert.equal(status.server.commit, SERVER_BUILD.commit);
  assert.equal(status.server.builtAtUtc, SERVER_BUILD.builtAtUtc);
  assert.equal(status.server.skillsDirectory, SKILLS_DIR);
  assert.ok(status.server.skillCount > 0);
  assert.ok(status.server.runByNameCount > 0 && status.server.runByNameCount <= status.server.skillCount);
  assert.deepEqual(status.instances, instances);

  if (SERVER_BUILD.commit !== "unknown") {
    const mismatch = status.warnings.filter((warning) => warning.includes(SECOND_INSTANCE));
    assert.equal(mismatch.length, 1, "a plugin from another commit is called out once");
    assert.match(mismatch[0], /0123456/);
    assert.match(mismatch[0], new RegExp(SERVER_BUILD.commit.slice(0, 7)));
    assert.equal(status.warnings.some((warning) => warning.includes(FIRST_INSTANCE)), false,
      "a matching plugin is not mentioned");
  }
});

test("status says so when nothing runs or a plugin is too old to report its build", async () => {
  const none = JSON.parse((await readStatus(async () => [])).content[0].text);
  assert.deepEqual(none.instances, []);
  assert.ok(none.warnings.some((warning) => /No running Civil 3D/.test(warning)));

  const older = JSON.parse(
    (
      await readStatus(async () => [
        instance({
          pluginBuild: null,
          buildMatchesServer: null,
          activeDrawingKnown: false,
          activeDrawing: null,
          features: [],
        }),
      ])
    ).content[0].text
  );
  assert.ok(older.warnings.some((warning) => /older plugin/.test(warning) && warning.includes(FIRST_INSTANCE)));

  const failed = await readStatus(async () => {
    throw new Error("discovery failed");
  });
  assert.equal(failed.isError, true);
  assert.match(failed.content[0].text, /discovery failed/);
});

test("the version label carries the commit only when it is known", () => {
  const known = { commit: OTHER_COMMIT, dirty: false, builtAtUtc: null };
  const unknown = { commit: "unknown", dirty: null, builtAtUtc: null };
  assert.equal(serverVersionLabel(known), `${SERVER_VERSION}+0123456`);
  assert.equal(serverVersionLabel(unknown), SERVER_VERSION);
  assert.equal(buildsDiffer(known, { ...known }), false);
  assert.equal(buildsDiffer(known, { ...known, commit: OTHER_COMMIT.toUpperCase() }), false);
  assert.equal(buildsDiffer(known, { ...known, commit: "f".repeat(40) }), true);
  assert.equal(buildsDiffer(known, unknown), null, "an unknown side makes no claim either way");
});
