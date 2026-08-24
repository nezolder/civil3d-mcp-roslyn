import assert from "node:assert/strict";
import net from "node:net";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";

const SECRET_MARKERS = [
  "QUERY_CODE_SECRET",
  "EXECUTE_CODE_SECRET",
  "DESCRIPTION_SECRET",
  "DRAWING_SECRET",
  "11111111-2222-4333-8444-555555555555",
  "RAW_PLUGIN_ERROR_SECRET",
];

async function startMockPlugin() {
  const server = net.createServer((socket) => {
    let buffered = Buffer.alloc(0);
    socket.on("data", (chunk) => {
      buffered = Buffer.concat([buffered, chunk]);
      const delimiter = buffered.indexOf(0x0a);
      if (delimiter < 0) return;

      const request = JSON.parse(buffered.subarray(0, delimiter).toString("utf8"));
      const isQuery = request.params.readOnly === true;
      socket.write(
        JSON.stringify(
          isQuery
            ? { jsonrpc: "2.0", id: request.id, result: { ok: true } }
            : {
                jsonrpc: "2.0",
                id: request.id,
                error: {
                  code: "CIVIL3D.COMPILATION_ERROR",
                  message: "RAW_PLUGIN_ERROR_SECRET",
                },
              }
        ) + "\n"
      );
    });
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const address = server.address();
  assert.ok(address && typeof address === "object");
  return {
    port: address.port,
    close: () => new Promise((resolve, reject) => server.close((error) => (error ? reject(error) : resolve()))),
  };
}

function auditRecordFromLine(line) {
  const payloadStart = line.indexOf("{");
  assert.ok(payloadStart >= 0, "operation audit log must contain a JSON object");
  return JSON.parse(line.slice(payloadStart));
}

test("query and execute emit only bounded, secret-free operation audit fields", async () => {
  const mockPlugin = await startMockPlugin();
  const previousEnvironment = {
    CIVIL3D_HOST: process.env.CIVIL3D_HOST,
    CIVIL3D_PORT: process.env.CIVIL3D_PORT,
    CIVIL3D_CONNECT_TIMEOUT: process.env.CIVIL3D_CONNECT_TIMEOUT,
    LOG_LEVEL: process.env.LOG_LEVEL,
  };
  process.env.CIVIL3D_HOST = "127.0.0.1";
  process.env.CIVIL3D_PORT = String(mockPlugin.port);
  process.env.CIVIL3D_CONNECT_TIMEOUT = "1000";
  process.env.LOG_LEVEL = "debug";

  const stderrLines = [];
  const originalConsoleError = console.error;
  console.error = (line) => stderrLines.push(String(line));

  let client;
  let server;
  try {
    const { registerExecuteTool } = await import("../../build/tools/executeTool.js");
    const { registerQueryTool } = await import("../../build/tools/queryTool.js");
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    server = new McpServer({ name: "operation-audit-test", version: "1.0.0" });
    registerQueryTool(server);
    registerExecuteTool(server);
    client = new Client({ name: "operation-audit-client", version: "1.0.0" });
    await server.connect(serverTransport);
    await client.connect(clientTransport);

    const query = await client.callTool({
      name: "civil3d_query",
      arguments: { code: 'return "QUERY_CODE_SECRET";' },
    });
    assert.equal(query.isError, undefined);

    const execute = await client.callTool({
      name: "civil3d_execute",
      arguments: {
        code: 'return "EXECUTE_CODE_SECRET";',
        description: "DESCRIPTION_SECRET",
        expectedDrawing: {
          databaseFilename: "C:\\Sensitive\\DRAWING_SECRET.dwg",
          fingerprintGuid: "11111111-2222-4333-8444-555555555555",
        },
      },
    });
    assert.equal(execute.isError, true);

    const auditRecords = stderrLines
      .filter((line) => line.includes(" Operation audit "))
      .map(auditRecordFromLine);
    assert.equal(auditRecords.length, 2);
    assert.deepEqual(Object.keys(auditRecords[0]).sort(), [
      "codeSha256",
      "codeUtf8Bytes",
      "durationMs",
      "operationId",
      "status",
      "tool",
    ]);
    assert.deepEqual(Object.keys(auditRecords[1]).sort(), [
      "category",
      "code",
      "codeSha256",
      "codeUtf8Bytes",
      "durationMs",
      "operationId",
      "outcome",
      "source",
      "status",
      "tool",
    ]);
    assert.equal(auditRecords[0].tool, "civil3d_query");
    assert.equal(auditRecords[0].status, "success");
    assert.equal(auditRecords[1].tool, "civil3d_execute");
    assert.equal(auditRecords[1].status, "error");
    assert.deepEqual(
      {
        code: auditRecords[1].code,
        category: auditRecords[1].category,
        source: auditRecords[1].source,
        outcome: auditRecords[1].outcome,
      },
      {
        code: "CIVIL3D.COMPILATION_ERROR",
        category: "compilation",
        source: "plugin",
        outcome: "reported_error",
      }
    );
    for (const record of auditRecords) {
      assert.match(record.operationId, /^op-[a-f0-9]{16}$/);
      assert.match(record.codeSha256, /^[a-f0-9]{64}$/);
      assert.equal(Number.isSafeInteger(record.codeUtf8Bytes), true);
      assert.equal(Number.isSafeInteger(record.durationMs), true);
      assert.ok(record.durationMs >= 0);
    }
    assert.notEqual(auditRecords[0].operationId, auditRecords[1].operationId);

    const allLogText = stderrLines.join("\n");
    for (const marker of SECRET_MARKERS) {
      assert.equal(allLogText.includes(marker), false, `log leaked ${marker}`);
    }
  } finally {
    if (client) await client.close();
    if (server) await server.close();
    console.error = originalConsoleError;
    await mockPlugin.close();
    for (const [name, value] of Object.entries(previousEnvironment)) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
  }
});
