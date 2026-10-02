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
