import assert from "node:assert/strict";
import net from "node:net";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";

import {
  Civil3dMcpError,
  civil3dErrorFromPlugin,
  createStructuredToolErrorResult,
} from "../../build/errors/structuredError.js";
import { MCP_BENCHMARK_META_KEY } from "../../build/benchmark/liveTrace.js";
import { registerExecuteTool } from "../../build/tools/executeTool.js";
import { registerQueryTool } from "../../build/tools/queryTool.js";
import { ApplicationClientConnection } from "../../build/utils/SocketClient.js";

async function getUnusedLoopbackPort() {
  const server = net.createServer();
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const address = server.address();
  assert.ok(address && typeof address === "object");
  await new Promise((resolve, reject) => server.close((error) => (error ? reject(error) : resolve())));
  return address.port;
}

test("plugin error codes survive the private JSON-RPC hop", async () => {
  const connection = new ApplicationClientConnection("127.0.0.1", 65535);
  connection.isConnected = true;
  connection.socket.write = () => true;

  try {
    const pending = connection.sendCommand("executeCode", {
      code: "return missingSymbol;",
      readOnly: true,
    });
    const [requestId] = connection.responseCallbacks.keys();
    assert.ok(requestId);

    connection.socket.emit(
      "data",
      Buffer.from(
        JSON.stringify({
          jsonrpc: "2.0",
          id: requestId,
          error: {
            code: "CIVIL3D.COMPILATION_ERROR",
            message: "C# compilation failed",
          },
        }) + "\n"
      )
    );

    const error = await pending.then(
      () => assert.fail("plugin error must reject"),
      (reason) => reason
    );
    assert.ok(error instanceof Civil3dMcpError);
    assert.equal(error.code, "CIVIL3D.COMPILATION_ERROR");
    assert.equal(error.category, "compilation");
    assert.equal(error.source, "plugin");
    assert.equal(error.outcome, "reported_error");
  } finally {
    connection.socket.destroy();
  }
});

test("structured tool errors preserve the legacy text response", () => {
  const error = civil3dErrorFromPlugin({
    code: "CIVIL3D.COMPILATION_ERROR",
    message: "C# compilation failed",
  });
  const result = createStructuredToolErrorResult(error, "Query failed: ");

  assert.equal(result.isError, true);
  assert.equal(result.content[0].text, "Query failed: C# compilation failed");
  assert.deepEqual(result.structuredContent, {
    schema_version: "civil3d-mcp-error/v1",
    ok: false,
    error: {
      code: "CIVIL3D.COMPILATION_ERROR",
      category: "compilation",
      message: "C# compilation failed",
      source: "plugin",
      outcome: "reported_error",
      retryable: false,
    },
  });
});

test("command timeout is explicit and never marked retryable", async () => {
  const connection = new ApplicationClientConnection("127.0.0.1", 65535);
  connection.isConnected = true;
  connection.socket.write = () => true;
  const originalSetTimeout = globalThis.setTimeout;
  globalThis.setTimeout = (callback, _delay, ...args) => {
    queueMicrotask(() => callback(...args));
    return 0;
  };

  try {
    const error = await connection.sendCommand("executeCode", { code: "return 1;" }).then(
      () => assert.fail("timeout must reject"),
      (reason) => reason
    );
    const result = createStructuredToolErrorResult(error, "Execution failed: ");

    assert.ok(error instanceof Civil3dMcpError);
    assert.equal(error.code, "CIVIL3D.COMMAND_TIMEOUT");
    assert.equal(result.structuredContent.error.outcome, "unknown");
    assert.equal(result.structuredContent.error.retryable, false);
  } finally {
    globalThis.setTimeout = originalSetTimeout;
    connection.socket.destroy();
  }
});

test("malformed plugin errors receive a stable fallback code", () => {
  const error = civil3dErrorFromPlugin({ code: 500, message: "plugin failed" });
  assert.equal(error.code, "CIVIL3D.PLUGIN_ERROR");
  assert.equal(error.category, "execution");
  assert.equal(error.source, "plugin");
  assert.equal(error.outcome, "reported_error");
});

test("drawing guard plugin errors have stable non-retryable classifications", () => {
  const cases = [
    ["CIVIL3D.DRAWING_GUARD_REQUIRED", "drawing"],
    ["CIVIL3D.DRAWING_GUARD_INVALID", "validation"],
    ["CIVIL3D.DRAWING_MISMATCH", "drawing"],
  ];

  for (const [code, category] of cases) {
    const error = civil3dErrorFromPlugin({ code, message: "guard rejected" });
    assert.equal(error.code, code);
    assert.equal(error.category, category);
    assert.equal(error.outcome, "not_started");
    assert.equal(error.toStructuredError().retryable, false);
  }
});

test("a command can opt into a longer timeout for a synchronous drawing save", async () => {
  const connection = new ApplicationClientConnection("127.0.0.1", 65535);
  connection.isConnected = true;
  connection.socket.write = () => true;
  const originalSetTimeout = globalThis.setTimeout;
  let observedDelay;
  globalThis.setTimeout = (callback, delay, ...args) => {
    observedDelay = delay;
    queueMicrotask(() => callback(...args));
    return 0;
  };

  try {
    const error = await connection
      .sendCommand("executeCode", { code: "return 1;" }, undefined, 600_000)
      .then(
        () => assert.fail("timeout must reject"),
        (reason) => reason
      );
    assert.equal(observedDelay, 600_000);
    assert.equal(error.code, "CIVIL3D.COMMAND_TIMEOUT");
    assert.match(error.message, /600000ms/);
  } finally {
    globalThis.setTimeout = originalSetTimeout;
    connection.socket.destroy();
  }
});

test("post-commit save errors distinguish missing paths from failed disk writes", () => {
  const missingPath = civil3dErrorFromPlugin({
    code: "CIVIL3D.SAVE_PATH_REQUIRED",
    message: "drawing has no file path",
  });
  assert.equal(missingPath.category, "drawing");
  assert.equal(missingPath.outcome, "not_started");

  const failedSave = civil3dErrorFromPlugin({
    code: "CIVIL3D.SAVE_FAILED",
    message: "changes committed in memory, save failed",
  });
  assert.equal(failedSave.category, "execution");
  assert.equal(failedSave.outcome, "reported_error");
  assert.equal(failedSave.toStructuredError().retryable, false);
});

test("idempotency plugin outcomes are stable and never retryable", () => {
  for (const code of [
    "CIVIL3D.IDEMPOTENCY_CONFLICT",
    "CIVIL3D.IDEMPOTENCY_IN_PROGRESS",
    "CIVIL3D.IDEMPOTENCY_COMPLETED",
  ]) {
    const error = civil3dErrorFromPlugin({ code, message: "idempotency state" });
    assert.equal(error.category, "execution");
    assert.equal(error.outcome, "not_started");
    assert.equal(error.toStructuredError().retryable, false);
  }
});

test("result serialization failure reports post-script pre-commit execution failure", () => {
  const error = civil3dErrorFromPlugin({
    code: "CIVIL3D.RESULT_SERIALIZATION_FAILED",
    message: "Result type is not supported",
  });

  assert.equal(error.code, "CIVIL3D.RESULT_SERIALIZATION_FAILED");
  assert.equal(error.category, "execution");
  assert.equal(error.outcome, "reported_error");
  assert.equal(error.toStructuredError().retryable, false);
});

test("query and execute expose the same contract through MCP", async () => {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "structured-error-test", version: "1.0.0" });
  registerQueryTool(server);
  registerExecuteTool(server);
  const client = new Client({ name: "structured-error-client", version: "1.0.0" });

  try {
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    for (const name of ["civil3d_query", "civil3d_execute"]) {
      const result = await client.callTool({
        name,
        arguments: {
          code: "return 1;",
          ...(name === "civil3d_execute"
            ? {
                expectedDrawing: {
                  databaseFilename: "C:\\Tests\\Drawing.dwg",
                  fingerprintGuid: "11111111-2222-4333-8444-555555555555",
                },
              }
            : {}),
        },
        _meta: {
          [MCP_BENCHMARK_META_KEY]: {
            enabled: true,
            run_id: "INVALID_PRIVATE_MARKER",
          },
        },
      });

      assert.equal(result.isError, true);
      assert.equal(result.structuredContent.schema_version, "civil3d-mcp-error/v1");
      assert.equal(result.structuredContent.error.code, "CIVIL3D.INVALID_BENCHMARK_METADATA");
      assert.equal(result.structuredContent.error.outcome, "not_started");
      assert.equal(result.structuredContent.error.retryable, false);
      assert.equal(JSON.stringify(result).includes("INVALID_PRIVATE_MARKER"), false);
    }
  } finally {
    await client.close();
    await server.close();
  }
});

test("connection refusal is reported before command execution", async () => {
  const unusedPort = await getUnusedLoopbackPort();
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: ["build/index.js"],
    cwd: process.cwd(),
    env: {
      ...process.env,
      CIVIL3D_HOST: "127.0.0.1",
      CIVIL3D_PORT: String(unusedPort),
      CIVIL3D_CONNECT_TIMEOUT: "250",
      LOG_LEVEL: "error",
    },
  });
  const client = new Client({ name: "connection-error-client", version: "1.0.0" });

  try {
    await client.connect(transport);
    const result = await client.callTool({
      name: "civil3d_query",
      arguments: { code: "return 1;" },
    });

    assert.equal(result.isError, true);
    assert.equal(result.structuredContent.error.code, "CIVIL3D.CONNECTION_FAILED");
    assert.equal(result.structuredContent.error.category, "connection");
    assert.equal(result.structuredContent.error.outcome, "not_started");
    assert.equal(result.structuredContent.error.retryable, false);
  } finally {
    await client.close();
  }
});
