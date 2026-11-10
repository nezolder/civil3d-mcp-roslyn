import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import * as net from "node:net";
import * as os from "node:os";
import * as path from "node:path";
import test from "node:test";

import { Civil3dMcpError } from "../../build/errors/structuredError.js";
import { withApplicationConnection } from "../../build/utils/ConnectionManager.js";

const FIRST_INSTANCE = "11111111111141118111111111111111";
const SECOND_INSTANCE = "22222222222242228222222222222222";
const FIRST_FINGERPRINT = "{AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE}";
const SECOND_FINGERPRINT = "{BBBBBBBB-CCCC-4DDD-8EEE-FFFFFFFFFFFF}";

test("multiple Civil instances fail closed and route by instance or drawing identity", async () => {
  const endpointDirectory = await mkdtemp(path.join(os.tmpdir(), "civil3d-mcp-routing-"));
  const savedEnvironment = {
    CIVIL3D_HOST: process.env.CIVIL3D_HOST,
    CIVIL3D_PORT: process.env.CIVIL3D_PORT,
    CIVIL3D_CONNECT_TIMEOUT: process.env.CIVIL3D_CONNECT_TIMEOUT,
    CIVIL3D_DISCOVERY_TIMEOUT: process.env.CIVIL3D_DISCOVERY_TIMEOUT,
    CIVIL3D_MCP_ENDPOINT_DIR: process.env.CIVIL3D_MCP_ENDPOINT_DIR,
  };
  const first = await startMockPlugin({
    instanceId: FIRST_INSTANCE,
    processId: 41001,
    databaseFilename: String.raw`C:\Projects\First.dwg`,
    fingerprintGuid: FIRST_FINGERPRINT,
  });
  const second = await startMockPlugin({
    instanceId: SECOND_INSTANCE,
    processId: 41002,
    databaseFilename: String.raw`C:\Projects\Second.dwg`,
    fingerprintGuid: SECOND_FINGERPRINT,
  });

  try {
    process.env.CIVIL3D_HOST = "127.0.0.1";
    delete process.env.CIVIL3D_PORT;
    process.env.CIVIL3D_CONNECT_TIMEOUT = "1000";
    process.env.CIVIL3D_DISCOVERY_TIMEOUT = "1000";
    process.env.CIVIL3D_MCP_ENDPOINT_DIR = endpointDirectory;
    await writeEndpointRecord(endpointDirectory, first);
    await writeEndpointRecord(endpointDirectory, second);
    await writeFile(path.join(endpointDirectory, "invalid.json"), "not json", "utf8");

    const ambiguous = await captureError(
      withApplicationConnection((client) =>
        client.sendCommand("executeCode", { code: "return 1;", readOnly: true })
      )
    );
    assert.ok(ambiguous instanceof Civil3dMcpError);
    assert.equal(ambiguous.code, "CIVIL3D.INSTANCE_SELECTION_REQUIRED");
    assert.match(ambiguous.message, new RegExp(FIRST_INSTANCE));
    assert.match(ambiguous.message, new RegExp(SECOND_INSTANCE));
    assert.match(ambiguous.message, /First\.dwg/);
    assert.match(ambiguous.message, /Second\.dwg/);
    assert.equal(first.methods.filter((method) => method === "executeCode").length, 0);
    assert.equal(second.methods.filter((method) => method === "executeCode").length, 0);

    const explicitResult = await withApplicationConnection(
      (client) => client.sendCommand("executeCode", { code: "return 2;", readOnly: true }),
      { instanceId: SECOND_INSTANCE }
    );
    assert.deepEqual(explicitResult, { servedBy: SECOND_INSTANCE });
    assert.equal(first.methods.filter((method) => method === "executeCode").length, 0);
    assert.equal(second.methods.filter((method) => method === "executeCode").length, 1);

    const guardedResult = await withApplicationConnection(
      (client) => client.sendCommand("executeCode", { code: "return 3;", readOnly: true }),
      {
        expectedDrawing: {
          databaseFilename: String.raw`c:\projects\archive\..\FIRST.dwg`,
          fingerprintGuid: "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
        },
      }
    );
    assert.deepEqual(guardedResult, { servedBy: FIRST_INSTANCE });
    assert.equal(first.methods.filter((method) => method === "executeCode").length, 1);
    assert.equal(second.methods.filter((method) => method === "executeCode").length, 1);

    const mismatch = await captureError(
      withApplicationConnection(
        (client) => client.sendCommand("executeCode", { code: "return 4;", readOnly: true }),
        {
          expectedDrawing: {
            databaseFilename: String.raw`C:\Projects\Missing.dwg`,
            fingerprintGuid: FIRST_FINGERPRINT,
          },
        }
      )
    );
    assert.ok(mismatch instanceof Civil3dMcpError);
    assert.equal(mismatch.code, "CIVIL3D.DRAWING_MISMATCH");
    assert.equal(first.methods.filter((method) => method === "executeCode").length, 1);
    assert.equal(second.methods.filter((method) => method === "executeCode").length, 1);
  } finally {
    await first.close();
    await second.close();
    restoreEnvironment(savedEnvironment);
    await rm(endpointDirectory, { recursive: true, force: true });
  }
});

test("a record left behind by a dead process is skipped while a live session exists", async () => {
  const endpointDirectory = await mkdtemp(path.join(os.tmpdir(), "civil3d-mcp-stale-"));
  const savedEnvironment = {
    CIVIL3D_HOST: process.env.CIVIL3D_HOST,
    CIVIL3D_PORT: process.env.CIVIL3D_PORT,
    CIVIL3D_CONNECT_TIMEOUT: process.env.CIVIL3D_CONNECT_TIMEOUT,
    CIVIL3D_DISCOVERY_TIMEOUT: process.env.CIVIL3D_DISCOVERY_TIMEOUT,
    CIVIL3D_MCP_ENDPOINT_DIR: process.env.CIVIL3D_MCP_ENDPOINT_DIR,
  };
  const deadProcessId = await exitedProcessId();
  const live = await startMockPlugin({
    instanceId: FIRST_INSTANCE,
    processId: process.pid,
    databaseFilename: String.raw`C:\Projects\First.dwg`,
    fingerprintGuid: FIRST_FINGERPRINT,
  });
  // Something still answers on the stale record's port, so a probe would be visible.
  const stale = await startMockPlugin({
    instanceId: SECOND_INSTANCE,
    processId: deadProcessId,
    databaseFilename: String.raw`C:\Projects\Second.dwg`,
    fingerprintGuid: SECOND_FINGERPRINT,
  });

  try {
    process.env.CIVIL3D_HOST = "127.0.0.1";
    delete process.env.CIVIL3D_PORT;
    process.env.CIVIL3D_CONNECT_TIMEOUT = "1000";
    process.env.CIVIL3D_DISCOVERY_TIMEOUT = "1000";
    process.env.CIVIL3D_MCP_ENDPOINT_DIR = endpointDirectory;
    await writeEndpointRecord(endpointDirectory, live);
    await writeEndpointRecord(endpointDirectory, stale);

    // Without the check this is ambiguous (two answering records).
    const result = await withApplicationConnection((client) =>
      client.sendCommand("executeCode", { code: "return 1;", readOnly: true })
    );
    assert.deepEqual(result, { servedBy: FIRST_INSTANCE });
    assert.deepEqual(stale.methods, []);

    // An instance the caller names is always probed, whatever its record says.
    const named = await withApplicationConnection(
      (client) => client.sendCommand("executeCode", { code: "return 2;", readOnly: true }),
      { instanceId: SECOND_INSTANCE }
    );
    assert.deepEqual(named, { servedBy: SECOND_INSTANCE });

    // With no live session left, skipped records are probed after all.
    await live.close();
    const fallback = await withApplicationConnection((client) =>
      client.sendCommand("executeCode", { code: "return 3;", readOnly: true })
    );
    assert.deepEqual(fallback, { servedBy: SECOND_INSTANCE });
  } finally {
    await live.close().catch(() => {});
    await stale.close();
    restoreEnvironment(savedEnvironment);
    await rm(endpointDirectory, { recursive: true, force: true });
  }
});

test("a cancelled call is not sent to any Civil instance", async () => {
  const endpointDirectory = await mkdtemp(path.join(os.tmpdir(), "civil3d-mcp-cancel-"));
  const savedEnvironment = {
    CIVIL3D_HOST: process.env.CIVIL3D_HOST,
    CIVIL3D_PORT: process.env.CIVIL3D_PORT,
    CIVIL3D_MCP_ENDPOINT_DIR: process.env.CIVIL3D_MCP_ENDPOINT_DIR,
  };
  const plugin = await startMockPlugin({
    instanceId: FIRST_INSTANCE,
    processId: process.pid,
    databaseFilename: String.raw`C:\Projects\First.dwg`,
    fingerprintGuid: FIRST_FINGERPRINT,
  });
  const controller = new AbortController();
  controller.abort();

  try {
    process.env.CIVIL3D_HOST = "127.0.0.1";
    delete process.env.CIVIL3D_PORT;
    process.env.CIVIL3D_MCP_ENDPOINT_DIR = endpointDirectory;
    await writeEndpointRecord(endpointDirectory, plugin);

    const error = await captureError(
      withApplicationConnection(
        (client) => client.sendCommand("executeCode", { code: "return 1;" }),
        { signal: controller.signal }
      )
    );
    assert.ok(error instanceof Civil3dMcpError);
    assert.equal(error.code, "CIVIL3D.CANCELLED");
    assert.equal(error.outcome, "not_started");
    assert.deepEqual(plugin.methods, []);
  } finally {
    await plugin.close();
    restoreEnvironment(savedEnvironment);
    await rm(endpointDirectory, { recursive: true, force: true });
  }
});

/** A process id that existed a moment ago and is gone now. */
async function exitedProcessId() {
  const child = spawn(process.execPath, ["-e", ""], { stdio: "ignore" });
  await new Promise((resolve, reject) => {
    child.once("error", reject);
    child.once("exit", resolve);
  });
  return child.pid;
}

async function startMockPlugin({
  instanceId,
  processId,
  databaseFilename,
  fingerprintGuid,
}) {
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
        };
      } else if (request.method === "getActiveDrawingIdentity") {
        result = { instanceId, databaseFilename, fingerprintGuid };
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

async function writeEndpointRecord(directory, plugin) {
  const payload = {
    schemaVersion: 1,
    instanceId: plugin.instanceId,
    processId: plugin.processId,
    port: plugin.port,
    startedAtUtc: "2026-08-26T00:00:00Z",
  };
  await writeFile(
    path.join(directory, `${plugin.processId}-${plugin.instanceId}.json`),
    JSON.stringify(payload),
    "utf8"
  );
}

async function captureError(promise) {
  try {
    await promise;
    assert.fail("operation must fail");
  } catch (error) {
    return error;
  }
}

function restoreEnvironment(saved) {
  for (const [name, value] of Object.entries(saved)) {
    if (value === undefined) delete process.env[name];
    else process.env[name] = value;
  }
}
