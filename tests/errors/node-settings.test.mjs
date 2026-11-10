import assert from "node:assert/strict";
import net from "node:net";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";

function restoreEnvironment(saved) {
  for (const [name, value] of Object.entries(saved)) {
    if (value === undefined) delete process.env[name];
    else process.env[name] = value;
  }
}

test("logger falls back for invalid and inherited names while honoring valid levels", async () => {
  const saved = { LOG_LEVEL: process.env.LOG_LEVEL };
  const originalError = console.error;
  const cases = [
    [undefined, ["info", "warn", "error"]],
    ["", ["info", "warn", "error"]],
    ["unknown", ["info", "warn", "error"]],
    ["constructor", ["info", "warn", "error"]],
    ["__proto__", ["info", "warn", "error"]],
    ["debug", ["debug", "info", "warn", "error"]],
    [" INFO ", ["info", "warn", "error"]],
    ["Warn", ["warn", "error"]],
    ["error", ["error"]],
  ];
  try {
    for (const [index, [setting, expected]] of cases.entries()) {
      restoreEnvironment({ LOG_LEVEL: setting });
      const lines = [];
      console.error = (line) => lines.push(String(line));
      const { createLogger } = await import(`../../build/utils/logger.js?settings-case=${index}`);
      const logger = createLogger("SettingsTest");
      for (const level of ["debug", "info", "warn", "error"]) logger[level](level);
      assert.deepEqual(lines.map((line) => line.split(" ").at(-1)), expected, String(setting));
    }
  } finally {
    console.error = originalError;
    restoreEnvironment(saved);
  }
});

test("timeouts reject invalid timer delays and omit raw values from warnings", async () => {
  const saved = {
    CIVIL3D_COMMAND_TIMEOUT: process.env.CIVIL3D_COMMAND_TIMEOUT,
    CIVIL3D_TEST_TIMEOUT: process.env.CIVIL3D_TEST_TIMEOUT,
  };
  const originalError = console.error;
  const warnings = [];
  try {
    process.env.CIVIL3D_COMMAND_TIMEOUT = "not-a-number";
    console.error = (line) => warnings.push(String(line));
    const { readTimeoutEnvironment } = await import("../../build/utils/SocketClient.js");
    for (const value of [undefined, "", "  ", "0", "-1", "1.5", "Infinity", "NaN", "2147483648", "123ms", "PRIVATE_TIMEOUT_MARKER"]) {
      restoreEnvironment({ CIVIL3D_TEST_TIMEOUT: value });
      assert.equal(readTimeoutEnvironment("CIVIL3D_TEST_TIMEOUT", 120_000), 120_000, String(value));
    }
    for (const [value, expected] of [["1", 1], [" 600000 ", 600_000], ["2147483647", 2_147_483_647]]) {
      process.env.CIVIL3D_TEST_TIMEOUT = value;
      assert.equal(readTimeoutEnvironment("CIVIL3D_TEST_TIMEOUT", 120_000), expected);
    }
    assert.ok(warnings.some((line) => line.includes("CIVIL3D_TEST_TIMEOUT")));
    assert.ok(warnings.every((line) => !line.includes("PRIVATE_TIMEOUT_MARKER")));
  } finally {
    console.error = originalError;
    restoreEnvironment(saved);
  }
});

test("default IPv4 routing and invalid timeouts preserve compact query/execute/save results", async () => {
  const keys = ["CIVIL3D_HOST", "CIVIL3D_PORT", "CIVIL3D_COMMAND_TIMEOUT", "CIVIL3D_SAVE_TIMEOUT"];
  const saved = Object.fromEntries(keys.map((key) => [key, process.env[key]]));
  const payload = { ok: true, name: "Unicode: őű", values: [1, 2, { z: 3.25 }] };
  const requests = [];
  const sockets = new Set();
  const mock = net.createServer((socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
    let buffered = Buffer.alloc(0);
    socket.on("data", (chunk) => {
      buffered = Buffer.concat([buffered, chunk]);
      const delimiter = buffered.indexOf(0x0a);
      if (delimiter < 0) return;
      const request = JSON.parse(buffered.subarray(0, delimiter).toString("utf8"));
      buffered = Buffer.alloc(0);
      requests.push(request);
      setTimeout(() => {
        if (!socket.destroyed) socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: payload }) + "\n");
      }, 40);
    });
  });
  await new Promise((resolve, reject) => {
    mock.once("error", reject);
    mock.listen(0, "127.0.0.1", resolve);
  });
  let client;
  let server;
  try {
    delete process.env.CIVIL3D_HOST;
    process.env.CIVIL3D_PORT = String(mock.address().port);
    process.env.CIVIL3D_COMMAND_TIMEOUT = "not-a-number";
    process.env.CIVIL3D_SAVE_TIMEOUT = "not-a-number";
    const { registerQueryTool } = await import("../../build/tools/queryTool.js");
    const { registerExecuteTool } = await import("../../build/tools/executeTool.js");
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    server = new McpServer({ name: "settings-test", version: "1" });
    registerQueryTool(server);
    registerExecuteTool(server);
    client = new Client({ name: "settings-test", version: "1" });
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    const expectedDrawing = {
      databaseFilename: String.raw`C:\Tests\Settings.dwg`,
      fingerprintGuid: "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
    };
    for (const [name, extra] of [["civil3d_query", {}], ["civil3d_execute", {}], ["civil3d_execute", { saveDrawing: true }]]) {
      const result = await client.callTool({ name, arguments: { code: "return 1;", expectedDrawing, ...extra } });
      assert.notEqual(result.isError, true, JSON.stringify(result));
      assert.deepEqual(JSON.parse(result.content[0].text), payload);
      assert.equal(result.content[0].text, JSON.stringify(payload));
      assert.ok(result.content[0].text.length < JSON.stringify(payload, null, 2).length);
    }
    assert.equal(requests.length, 3);
    assert.deepEqual(requests.map((request) => request.params.readOnly), [true, false, false]);
    assert.ok(requests.every((request) => request.params.expectedDrawing.databaseFilename === expectedDrawing.databaseFilename));
    assert.equal(requests[2].params.saveDrawing, true);
  } finally {
    await client?.close();
    await server?.close();
    restoreEnvironment(saved);
    for (const socket of sockets) socket.destroy();
    await new Promise((resolve) => mock.close(resolve));
  }
});

test("the reply wait always outlasts the script limit the plugin applies", async () => {
  const { commandWaitMs } = await import("../../build/tools/scriptTimeout.js");
  // Default script limit (120 s): wait past it so the plugin's answer arrives.
  assert.equal(commandWaitMs(120_000, undefined), 150_000);
  // A configured longer wait is kept; a shorter one is raised to the floor.
  assert.equal(commandWaitMs(300_000, undefined), 300_000);
  assert.equal(commandWaitMs(1_000, undefined), 150_000);
  // A longer script limit extends the wait by the same amount.
  assert.equal(commandWaitMs(120_000, 600), 630_000);
  assert.equal(commandWaitMs(600_000, 600), 1_080_000);
  // A shorter limit never shortens the configured wait.
  assert.equal(commandWaitMs(120_000, 10), 120_000);
  assert.equal(commandWaitMs(600_000, 10), 600_000);
});

test("timeoutSeconds is validated, forwarded to the plugin and reflected in tool annotations", async () => {
  const keys = ["CIVIL3D_HOST", "CIVIL3D_PORT"];
  const saved = Object.fromEntries(keys.map((key) => [key, process.env[key]]));
  const requests = [];
  const sockets = new Set();
  const mock = net.createServer((socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
    let buffered = Buffer.alloc(0);
    socket.on("data", (chunk) => {
      buffered = Buffer.concat([buffered, chunk]);
      const delimiter = buffered.indexOf(0x0a);
      if (delimiter < 0) return;
      const request = JSON.parse(buffered.subarray(0, delimiter).toString("utf8"));
      buffered = Buffer.alloc(0);
      requests.push(request);
      socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: 1 }) + "\n");
    });
  });
  await new Promise((resolve, reject) => {
    mock.once("error", reject);
    mock.listen(0, "127.0.0.1", resolve);
  });
  let client;
  let server;
  try {
    delete process.env.CIVIL3D_HOST;
    process.env.CIVIL3D_PORT = String(mock.address().port);
    const { registerQueryTool } = await import("../../build/tools/queryTool.js");
    const { registerExecuteTool } = await import("../../build/tools/executeTool.js");
    const { registerSkillsTool } = await import("../../build/tools/skillsTool.js");
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    server = new McpServer({ name: "timeout-test", version: "1" });
    registerQueryTool(server);
    registerExecuteTool(server);
    registerSkillsTool(server);
    client = new Client({ name: "timeout-test", version: "1" });
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    const expectedDrawing = {
      databaseFilename: String.raw`C:\Tests\Settings.dwg`,
      fingerprintGuid: "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
    };

    for (const name of ["civil3d_query", "civil3d_execute"]) {
      const plain = await client.callTool({ name, arguments: { code: "return 1;", expectedDrawing } });
      assert.notEqual(plain.isError, true, JSON.stringify(plain));
      assert.equal(Object.hasOwn(requests.at(-1).params, "timeoutSeconds"), false);

      const long = await client.callTool({
        name,
        arguments: { code: "return 1;", expectedDrawing, timeoutSeconds: 900 },
      });
      assert.notEqual(long.isError, true, JSON.stringify(long));
      assert.equal(requests.at(-1).params.timeoutSeconds, 900);

      const sent = requests.length;
      for (const timeoutSeconds of [0, -5, 1.5, 1801, "600"]) {
        const rejected = await client.callTool({
          name,
          arguments: { code: "return 1;", expectedDrawing, timeoutSeconds },
        }).catch((error) => ({ isError: true, thrown: error }));
        assert.equal(rejected.isError, true, String(timeoutSeconds));
      }
      assert.equal(requests.length, sent, "an invalid limit must not reach the plugin");
    }

    const tools = new Map((await client.listTools()).tools.map((tool) => [tool.name, tool]));
    assert.equal(tools.get("civil3d_skills").annotations.readOnlyHint, true);
    assert.equal(tools.get("civil3d_execute").annotations.readOnlyHint, false);
    assert.equal(tools.get("civil3d_execute").annotations.destructiveHint, true);
    assert.equal(tools.get("civil3d_execute").annotations.idempotentHint, false);
    // Arbitrary code is never advertised as read-only, even when rolled back.
    assert.equal(Object.hasOwn(tools.get("civil3d_query").annotations, "readOnlyHint"), false);
    assert.equal(tools.get("civil3d_query").annotations.destructiveHint, false);
    for (const tool of tools.values()) assert.equal(tool.annotations.openWorldHint, false, tool.name);
  } finally {
    await client?.close();
    await server?.close();
    restoreEnvironment(saved);
    for (const socket of sockets) socket.destroy();
    await new Promise((resolve) => mock.close(resolve));
  }
});
