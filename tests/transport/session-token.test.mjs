import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import * as net from "node:net";
import * as os from "node:os";
import * as path from "node:path";
import test from "node:test";

import { Civil3dMcpError } from "../../build/errors/structuredError.js";
import { listInstances, withApplicationConnection } from "../../build/utils/ConnectionManager.js";

const INSTANCE = "33333333333343338333333333333333";
const OTHER_INSTANCE = "44444444444444448444444444444444";
const TOKEN = "a".repeat(64);
const OTHER_TOKEN = "b".repeat(64);
const DRAWING = {
  databaseFilename: String.raw`C:\Projects\Token.dwg`,
  fingerprintGuid: "{CCCCCCCC-DDDD-4EEE-8FFF-000000000000}",
};
const ENVIRONMENT_NAMES = [
  "CIVIL3D_HOST",
  "CIVIL3D_PORT",
  "CIVIL3D_CONNECT_TIMEOUT",
  "CIVIL3D_DISCOVERY_TIMEOUT",
  "CIVIL3D_MCP_ENDPOINT_DIR",
  "CIVIL3D_MCP_DEFAULT_PORT_FALLBACK",
  "CIVIL3D_MCP_TOKEN",
  "LOG_LEVEL",
];

const query = (client) => client.sendCommand("executeCode", { code: "return 1;", readOnly: true });
const scripts = (plugin) => plugin.requests.filter((request) => request.method === "executeCode");

test("the token of the endpoint record travels with every request, also the health probe", async () => {
  await withInstances([{ instanceId: INSTANCE, processId: 42001, token: TOKEN }], async ([plugin]) => {
    assert.deepEqual(await withApplicationConnection(query), { servedBy: INSTANCE });
    assert.ok(plugin.requests.length >= 2);
    for (const request of plugin.requests) {
      assert.equal(request.auth, TOKEN, `${request.method} must present the session token`);
      assert.equal("auth" in (request.params ?? {}), false, "the token is not a script input");
    }

    const [instance] = await listInstances();
    assert.equal(instance.authorized, true);
    assert.equal(instance.tokenRequired, true);
    assert.deepEqual(instance.activeDrawing, { ...DRAWING, observedAtUtc: "2026-11-24T09:00:00Z" });
  });
});

test("a pinned port finds the token in the endpoint records; a configured token wins", async () => {
  await withInstances([{ instanceId: INSTANCE, processId: 42001, token: TOKEN }], async ([plugin]) => {
    process.env.CIVIL3D_PORT = String(plugin.port);
    assert.deepEqual(await withApplicationConnection(query), { servedBy: INSTANCE });
    assert.equal(scripts(plugin).at(-1).auth, TOKEN);

    process.env.CIVIL3D_MCP_TOKEN = OTHER_TOKEN;
    const refused = await captureError(withApplicationConnection(query));
    assert.equal(refused.code, "CIVIL3D.UNAUTHORIZED", "the configured token is the one that is sent");
    assert.equal(plugin.requests.at(-1).auth, OTHER_TOKEN);

    // A value that cannot be a token is ignored, not sent.
    process.env.CIVIL3D_MCP_TOKEN = "not a token";
    assert.deepEqual(await withApplicationConnection(query), { servedBy: INSTANCE });
    assert.equal(scripts(plugin).at(-1).auth, TOKEN);
  });
});

test("without the right token nothing runs and the refusal says that nothing started", async () => {
  await withInstances(
    [{ instanceId: INSTANCE, processId: 42001, token: TOKEN, recordToken: OTHER_TOKEN }],
    async ([plugin]) => {
      const refused = await captureError(withApplicationConnection(query));
      assert.ok(refused instanceof Civil3dMcpError);
      assert.equal(refused.code, "CIVIL3D.UNAUTHORIZED");
      assert.equal(refused.category, "connection");
      assert.equal(refused.outcome, "not_started");
      assert.equal(refused.source, "plugin");
      assert.equal(plugin.served, 0, "the plugin ran nothing");

      // The instance is still listed, as one this server cannot use.
      const [instance] = await listInstances();
      assert.equal(instance.instanceId, INSTANCE);
      assert.equal(instance.authorized, false);
      assert.equal(instance.tokenRequired, true);
      assert.equal(instance.activeDrawing, null, "nothing about the drawing is told without the token");
      assert.equal(instance.activeDrawingKnown, false);
    }
  );

  // A record that carries no token at all (a mismatched endpoint folder, an older record).
  await withInstances(
    [{ instanceId: INSTANCE, processId: 42001, token: TOKEN, recordToken: null }],
    async ([plugin]) => {
      const refused = await captureError(withApplicationConnection(query));
      assert.equal(refused.code, "CIVIL3D.UNAUTHORIZED");
      assert.equal(plugin.requests.every((request) => !("auth" in request)), true);
      assert.equal(plugin.served, 0);
    }
  );
});

test("each instance gets its own token, and a plugin from before the token gets none", async () => {
  await withInstances(
    [
      { instanceId: INSTANCE, processId: 42001, token: TOKEN, drawing: DRAWING },
      // An older plugin: no token in its record, and it asks for none.
      {
        instanceId: OTHER_INSTANCE,
        processId: 42002,
        token: null,
        drawing: { ...DRAWING, databaseFilename: String.raw`C:\Projects\Older.dwg` },
      },
    ],
    async ([current, older]) => {
      assert.deepEqual(
        await withApplicationConnection(query, { expectedDrawing: DRAWING }),
        { servedBy: INSTANCE }
      );
      assert.deepEqual(
        await withApplicationConnection(query, {
          expectedDrawing: { ...DRAWING, databaseFilename: String.raw`C:\Projects\Older.dwg` },
        }),
        { servedBy: OTHER_INSTANCE }
      );
      assert.equal(current.requests.every((request) => request.auth === TOKEN), true);
      assert.equal(older.requests.every((request) => !("auth" in request)), true,
        "no token is sent where the record has none");

      const instances = await listInstances();
      const olderStatus = instances.find((instance) => instance.instanceId === OTHER_INSTANCE);
      assert.equal(olderStatus.authorized, true);
      assert.equal(olderStatus.tokenRequired, null, "an older plugin cannot say whether it asks for a token");
    }
  );
});

test("a token of an unexpected form in a record is not sent on", async () => {
  await withInstances(
    [{ instanceId: INSTANCE, processId: 42001, token: null, recordToken: "x y\n{\"injected\":true}" }],
    async ([plugin]) => {
      assert.deepEqual(await withApplicationConnection(query), { servedBy: INSTANCE });
      assert.equal(plugin.requests.every((request) => !("auth" in request)), true);
    }
  );
});

async function withInstances(definitions, run) {
  const endpointDirectory = await mkdtemp(path.join(os.tmpdir(), "civil3d-mcp-session-token-"));
  const saved = Object.fromEntries(ENVIRONMENT_NAMES.map((name) => [name, process.env[name]]));
  const plugins = [];
  try {
    for (const definition of definitions) plugins.push(await startMockPlugin(definition));
    process.env.CIVIL3D_HOST = "127.0.0.1";
    delete process.env.CIVIL3D_PORT;
    delete process.env.CIVIL3D_MCP_TOKEN;
    process.env.CIVIL3D_CONNECT_TIMEOUT = "1000";
    process.env.CIVIL3D_DISCOVERY_TIMEOUT = "1000";
    process.env.CIVIL3D_MCP_ENDPOINT_DIR = endpointDirectory;
    // A Civil 3D that happens to run on this machine must never be reached.
    process.env.CIVIL3D_MCP_DEFAULT_PORT_FALLBACK = "0";
    process.env.LOG_LEVEL = "error";
    for (const plugin of plugins) {
      const record = {
        schemaVersion: 1,
        instanceId: plugin.instanceId,
        processId: plugin.processId,
        port: plugin.port,
        startedAtUtc: "2026-11-24T00:00:00Z",
        processName: "acad",
      };
      if (plugin.recordToken !== null) record.token = plugin.recordToken;
      await writeFile(
        path.join(endpointDirectory, `${plugin.processId}-${plugin.instanceId}.json`),
        JSON.stringify(record),
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
 * Behaves like the plugin: with a token it runs a request only when the
 * request presents that token, and answers health with the short reply
 * otherwise. token=null models a plugin from before the token existed.
 * recordToken is what its endpoint record says (default: the real token).
 */
async function startMockPlugin({ instanceId, processId, token, recordToken, drawing = DRAWING }) {
  const requests = [];
  const state = { served: 0 };
  let port = 0;
  const server = net.createServer((socket) => {
    const chunks = [];
    socket.on("data", (chunk) => {
      chunks.push(chunk);
      const frame = Buffer.concat(chunks);
      const delimiter = frame.indexOf(0x0a);
      if (delimiter < 0) return;

      const request = JSON.parse(frame.subarray(0, delimiter).toString("utf8"));
      requests.push(request);
      const authorized = token === null || request.auth === token;
      const identity = { connected: true, instanceId, processId, port, startedAtUtc: "2026-11-24T00:00:00Z" };
      const features = ["activeDrawing", "writeSafety", "images", "changeList", "preview", "sessionToken"];
      let body;
      if (request.method === "getCivil3DHealth") {
        body = authorized
          ? {
              result: {
                ...identity,
                listenerRunning: true,
                operationInProgress: false,
                queueDepth: 0,
                executionBackend: "modal",
                activeDrawingKnown: true,
                activeDrawing: { ...drawing, observedAtUtc: "2026-11-24T09:00:00Z" },
                build: { commit: "unknown", dirty: null, builtAtUtc: null },
                features,
                ...(token === null ? {} : { authorized: true, tokenRequired: true }),
              },
            }
          : {
              result: {
                ...identity,
                authorized: false,
                tokenRequired: true,
                activeDrawingKnown: false,
                build: { commit: "unknown", dirty: null, builtAtUtc: null },
                features,
              },
            };
      } else if (!authorized) {
        body = { error: { code: "CIVIL3D.UNAUTHORIZED", message: "This Civil 3D session only runs requests that present its session token." } };
      } else if (request.method === "getActiveDrawingIdentity") {
        state.served++;
        body = { result: { instanceId, ...drawing } };
      } else {
        state.served++;
        body = { result: { servedBy: instanceId } };
      }
      socket.end(`${JSON.stringify({ jsonrpc: "2.0", id: request.id, ...body })}\n`);
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
    requests,
    recordToken: recordToken === undefined ? token : recordToken,
    get served() {
      return state.served;
    },
    close: () => new Promise((resolve, reject) => server.close((error) => (error ? reject(error) : resolve()))),
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
