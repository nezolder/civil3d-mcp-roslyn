import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import * as net from "node:net";
import * as os from "node:os";
import * as path from "node:path";
import test from "node:test";

import { Civil3dMcpError } from "../../build/errors/structuredError.js";
import { SERVER_BUILD } from "../../build/buildInfo.js";
import { listInstances, withApplicationConnection } from "../../build/utils/ConnectionManager.js";

const FIRST_INSTANCE = "11111111111141118111111111111111";
const SECOND_INSTANCE = "22222222222242228222222222222222";
const FIRST_DRAWING = {
  databaseFilename: String.raw`C:\Projects\First.dwg`,
  fingerprintGuid: "{AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE}",
};
const SECOND_DRAWING = {
  databaseFilename: String.raw`C:\Projects\Second.dwg`,
  fingerprintGuid: "{BBBBBBBB-CCCC-4DDD-8EEE-FFFFFFFFFFFF}",
};
const OTHER_COMMIT = "0123456789abcdef0123456789abcdef01234567";
const ENVIRONMENT_NAMES = [
  "CIVIL3D_HOST",
  "CIVIL3D_PORT",
  "CIVIL3D_CONNECT_TIMEOUT",
  "CIVIL3D_DISCOVERY_TIMEOUT",
  "CIVIL3D_MCP_ENDPOINT_DIR",
];

const identityCommands = (plugin) =>
  plugin.methods.filter((method) => method === "getActiveDrawingIdentity").length;
const scriptCalls = (plugin) => plugin.methods.filter((method) => method === "executeCode").length;
const query = (client) => client.sendCommand("executeCode", { code: "return 1;", readOnly: true });

test("instances that report their active drawing are chosen without running a command in any of them", async () => {
  await withInstances(
    [
      { instanceId: FIRST_INSTANCE, processId: 41001, drawing: FIRST_DRAWING, reportsDrawing: true },
      { instanceId: SECOND_INSTANCE, processId: 41002, drawing: SECOND_DRAWING, reportsDrawing: true },
    ],
    async ([first, second]) => {
      const routed = await withApplicationConnection(query, {
        expectedDrawing: {
          databaseFilename: String.raw`c:\projects\archive\..\SECOND.dwg`,
          fingerprintGuid: "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff",
        },
      });
      assert.deepEqual(routed, { servedBy: SECOND_INSTANCE });
      assert.equal(scriptCalls(first), 0, "the other instance must not receive the script");

      const ambiguous = await captureError(withApplicationConnection(query));
      assert.equal(ambiguous.code, "CIVIL3D.INSTANCE_SELECTION_REQUIRED");
      assert.match(ambiguous.message, /First\.dwg/);
      assert.match(ambiguous.message, /Second\.dwg/);

      const mismatch = await captureError(
        withApplicationConnection(query, {
          expectedDrawing: { ...FIRST_DRAWING, databaseFilename: String.raw`C:\Projects\Missing.dwg` },
        })
      );
      assert.equal(mismatch.code, "CIVIL3D.DRAWING_MISMATCH");
      assert.equal(mismatch.outcome, "not_started");

      assert.equal(identityCommands(first) + identityCommands(second), 0,
        "choosing between instances must not enter a command in any instance");
      assert.equal(scriptCalls(first) + scriptCalls(second), 1);
    }
  );
});

test("only an instance that cannot say which drawing is active is still asked with a command", async () => {
  await withInstances(
    [
      { instanceId: FIRST_INSTANCE, processId: 41001, drawing: FIRST_DRAWING, reportsDrawing: true },
      // An older plugin: its health reply has no drawing fields at all.
      { instanceId: SECOND_INSTANCE, processId: 41002, drawing: SECOND_DRAWING, reportsDrawing: false },
    ],
    async ([current, older]) => {
      const routed = await withApplicationConnection(query, { expectedDrawing: SECOND_DRAWING });
      assert.deepEqual(routed, { servedBy: SECOND_INSTANCE });
      assert.equal(identityCommands(current), 0);
      assert.equal(identityCommands(older), 1);
    }
  );

  await withInstances(
    [
      { instanceId: FIRST_INSTANCE, processId: 41001, drawing: FIRST_DRAWING, reportsDrawing: true },
      // A current plugin that has not been idle since it started.
      { instanceId: SECOND_INSTANCE, processId: 41002, drawing: SECOND_DRAWING, reportsDrawing: true, drawingKnown: false },
    ],
    async ([known, notYetKnown]) => {
      const routed = await withApplicationConnection(query, { expectedDrawing: SECOND_DRAWING });
      assert.deepEqual(routed, { servedBy: SECOND_INSTANCE });
      assert.equal(identityCommands(known), 0);
      assert.equal(identityCommands(notYetKnown), 1);
    }
  );
});

test("an instance without an open drawing is listed as such and never matches", async () => {
  await withInstances(
    [
      { instanceId: FIRST_INSTANCE, processId: 41001, drawing: FIRST_DRAWING, reportsDrawing: true },
      { instanceId: SECOND_INSTANCE, processId: 41002, drawing: null, reportsDrawing: true },
    ],
    async ([withDrawing, empty]) => {
      const ambiguous = await captureError(withApplicationConnection(query));
      assert.equal(ambiguous.code, "CIVIL3D.INSTANCE_SELECTION_REQUIRED");
      assert.match(ambiguous.message, /no drawing open/);
      assert.match(ambiguous.message, /First\.dwg/);

      const routed = await withApplicationConnection(query, { expectedDrawing: FIRST_DRAWING });
      assert.deepEqual(routed, { servedBy: FIRST_INSTANCE });
      assert.equal(identityCommands(withDrawing) + identityCommands(empty), 0);
    }
  );
});

test("a call that needs a newer plugin is refused before anything is sent", async () => {
  await withInstances(
    [{ instanceId: FIRST_INSTANCE, processId: 41001, drawing: FIRST_DRAWING, reportsDrawing: false }],
    async ([older]) => {
      const refused = await captureError(
        withApplicationConnection(query, { requiredFeatures: ["writeSafety"] })
      );
      assert.ok(refused instanceof Civil3dMcpError);
      assert.equal(refused.code, "CIVIL3D.PLUGIN_UPDATE_REQUIRED");
      assert.equal(refused.outcome, "not_started");
      assert.match(refused.message, /writeSafety/);
      assert.equal(scriptCalls(older), 0);

      // Without the requirement the same plugin serves the call as before.
      assert.deepEqual(await withApplicationConnection(query), { servedBy: FIRST_INSTANCE });

      // A pinned port is used without a probe, so the plugin is asked first.
      process.env.CIVIL3D_PORT = String(older.port);
      const pinned = await captureError(
        withApplicationConnection(query, { requiredFeatures: ["writeSafety"] })
      );
      assert.equal(pinned.code, "CIVIL3D.PLUGIN_UPDATE_REQUIRED");
      assert.equal(scriptCalls(older), 1);
    }
  );

  await withInstances(
    [{ instanceId: FIRST_INSTANCE, processId: 41001, drawing: FIRST_DRAWING, reportsDrawing: true }],
    async ([current]) => {
      const served = await withApplicationConnection(query, { requiredFeatures: ["writeSafety"] });
      assert.deepEqual(served, { servedBy: FIRST_INSTANCE });

      process.env.CIVIL3D_PORT = String(current.port);
      const pinned = await withApplicationConnection(query, { requiredFeatures: ["writeSafety"] });
      assert.deepEqual(pinned, { servedBy: FIRST_INSTANCE });
    }
  );
});

test("the instance list shows each plugin build and whether it matches the server", async () => {
  await withInstances(
    [
      {
        instanceId: FIRST_INSTANCE,
        processId: 41001,
        drawing: FIRST_DRAWING,
        reportsDrawing: true,
        build: { commit: OTHER_COMMIT, dirty: false, builtAtUtc: "2026-11-11T08:00:00Z" },
      },
      {
        instanceId: SECOND_INSTANCE,
        processId: 41002,
        drawing: SECOND_DRAWING,
        reportsDrawing: true,
        build: { ...SERVER_BUILD },
      },
    ],
    async ([first, second]) => {
      const instances = await listInstances();
      assert.deepEqual(instances.map((instance) => instance.instanceId), [FIRST_INSTANCE, SECOND_INSTANCE]);
      const [other, same] = instances;
      assert.equal(other.pluginBuild.commit, OTHER_COMMIT);
      assert.equal(other.processId, 41001);
      assert.equal(other.port, first.port);
      assert.deepEqual(other.activeDrawing, { ...FIRST_DRAWING, observedAtUtc: "2026-11-11T09:00:00Z" });
      assert.equal(other.activeDrawingKnown, true);
      assert.deepEqual(other.features, ["activeDrawing", "writeSafety"]);
      // The test may run from a build without git; then nothing can be compared.
      const serverKnowsItsCommit = SERVER_BUILD.commit !== "unknown";
      assert.equal(other.buildMatchesServer, serverKnowsItsCommit ? false : null);
      assert.equal(same.buildMatchesServer, serverKnowsItsCommit ? true : null);
      assert.equal(identityCommands(first) + identityCommands(second), 0);
      assert.equal(scriptCalls(first) + scriptCalls(second), 0);
    }
  );

  await withInstances(
    [{ instanceId: FIRST_INSTANCE, processId: 41001, drawing: FIRST_DRAWING, reportsDrawing: false }],
    async () => {
      const [older] = await listInstances();
      assert.equal(older.pluginBuild, null);
      assert.equal(older.buildMatchesServer, null);
      assert.equal(older.activeDrawingKnown, false);
      assert.equal(older.activeDrawing, null);
      assert.deepEqual(older.features, []);
    }
  );

  await withInstances([], async () => {
    assert.deepEqual(await listInstances(), []);
  });
});

async function withInstances(definitions, run) {
  const endpointDirectory = await mkdtemp(path.join(os.tmpdir(), "civil3d-mcp-health-routing-"));
  const saved = Object.fromEntries(ENVIRONMENT_NAMES.map((name) => [name, process.env[name]]));
  const plugins = [];
  try {
    for (const definition of definitions) plugins.push(await startMockPlugin(definition));
    process.env.CIVIL3D_HOST = "127.0.0.1";
    delete process.env.CIVIL3D_PORT;
    process.env.CIVIL3D_CONNECT_TIMEOUT = "1000";
    process.env.CIVIL3D_DISCOVERY_TIMEOUT = "1000";
    process.env.CIVIL3D_MCP_ENDPOINT_DIR = endpointDirectory;
    for (const plugin of plugins) {
      await writeFile(
        path.join(endpointDirectory, `${plugin.processId}-${plugin.instanceId}.json`),
        JSON.stringify({
          schemaVersion: 1,
          instanceId: plugin.instanceId,
          processId: plugin.processId,
          port: plugin.port,
          startedAtUtc: "2026-08-26T00:00:00Z",
          processName: "acad",
        }),
        "utf8"
      );
    }
    await run(plugins);
  } finally {
    for (const plugin of plugins) await plugin.close();
    for (const [name, value] of Object.entries(saved)) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
    await rm(endpointDirectory, { recursive: true, force: true });
  }
}

/**
 * reportsDrawing=false models a plugin from before the health reply carried
 * the active drawing, the build and the feature list.
 */
async function startMockPlugin({ instanceId, processId, drawing, reportsDrawing, drawingKnown = true, build }) {
  const methods = [];
  let port = 0;
  const server = net.createServer((socket) => {
    const chunks = [];
    socket.on("data", (chunk) => {
      chunks.push(chunk);
      const frame = Buffer.concat(chunks);
      const delimiter = frame.indexOf(0x0a);
      if (delimiter < 0) return;

      const request = JSON.parse(frame.subarray(0, delimiter).toString("utf8"));
      methods.push(request.method);
      let result;
      if (request.method === "getCivil3DHealth") {
        result = {
          connected: true,
          listenerRunning: true,
          operationInProgress: false,
          currentOperation: null,
          queueDepth: 0,
          instanceId,
          processId,
          port,
          startedAtUtc: "2026-08-26T00:00:00Z",
          mode: "code_execution",
          roslyn: true,
          executionBackend: "modal",
          ...(reportsDrawing
            ? {
                activeDrawingKnown: drawingKnown,
                activeDrawing:
                  drawingKnown && drawing ? { ...drawing, observedAtUtc: "2026-11-11T09:00:00Z" } : null,
                build: build ?? { commit: "unknown", dirty: null, builtAtUtc: null },
                features: ["activeDrawing", "writeSafety"],
              }
            : {}),
        };
      } else if (request.method === "getActiveDrawingIdentity") {
        result = { instanceId, ...(drawing ?? { databaseFilename: "", fingerprintGuid: "" }) };
      } else {
        result = { servedBy: instanceId };
      }
      socket.end(`${JSON.stringify({ jsonrpc: "2.0", id: request.id, result })}\n`);
    });
  });

  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  port = server.address().port;

  return {
    instanceId,
    processId,
    port,
    methods,
    close: () => new Promise((resolve, reject) =>
      server.close((error) => error ? reject(error) : resolve())
    ),
  };
}

async function captureError(promise) {
  try {
    await promise;
    assert.fail("operation must fail");
  } catch (error) {
    return error;
  }
}
