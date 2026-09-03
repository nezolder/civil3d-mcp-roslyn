import assert from "node:assert/strict";
import net from "node:net";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";

async function startMockPlugin() {
  const requests = [];
  const server = net.createServer((socket) => {
    let buffered = Buffer.alloc(0);
    socket.on("data", (chunk) => {
      buffered = Buffer.concat([buffered, chunk]);
      const delimiter = buffered.indexOf(0x0a);
      if (delimiter < 0) return;

      const request = JSON.parse(buffered.subarray(0, delimiter).toString("utf8"));
      requests.push(request);
      socket.write(
        JSON.stringify({
          jsonrpc: "2.0",
          id: request.id,
          result: { ok: true },
        }) + "\n"
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
    requests,
    close: () =>
      new Promise((resolve, reject) =>
        server.close((error) => (error ? reject(error) : resolve()))
      ),
  };
}

async function callMustFailValidation(client, params) {
  try {
    const result = await client.callTool(params);
    assert.equal(result.isError, true, "invalid tool input must fail");
  } catch {
    // The SDK may surface schema rejection as an MCP protocol error.
  }
}

test("tools expose drawing, instance, idempotency, and post-commit save safeguards", async () => {
  const mockPlugin = await startMockPlugin();
  process.env.CIVIL3D_HOST = "127.0.0.1";
  process.env.CIVIL3D_PORT = String(mockPlugin.port);
  process.env.CIVIL3D_CONNECT_TIMEOUT = "25";
  process.env.LOG_LEVEL = "error";

  const { registerExecuteTool } = await import("../../build/tools/executeTool.js");
  const { registerQueryTool } = await import("../../build/tools/queryTool.js");
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "drawing-guard-test", version: "1.0.0" });
  registerExecuteTool(server);
  registerQueryTool(server);
  const client = new Client({ name: "drawing-guard-client", version: "1.0.0" });

  const expectedDrawing = {
    databaseFilename: "C:\\Projects\\Plans\\Target.dwg",
    fingerprintGuid: "{11111111-2222-4333-8444-555555555555}",
  };

  try {
    await server.connect(serverTransport);
    await client.connect(clientTransport);

    const { tools } = await client.listTools();
    const execute = tools.find((tool) => tool.name === "civil3d_execute");
    const query = tools.find((tool) => tool.name === "civil3d_query");
    assert.ok(execute && query);
    assert.ok(execute.inputSchema.required.includes("expectedDrawing"));
    assert.equal(query.inputSchema.required.includes("expectedDrawing"), false);
    assert.ok(execute.inputSchema.properties.instanceId);
    assert.ok(query.inputSchema.properties.instanceId);
    assert.equal(execute.inputSchema.required.includes("instanceId"), false);
    assert.equal(query.inputSchema.required.includes("instanceId"), false);
    assert.equal(execute.inputSchema.properties.instanceId.pattern, "^[0-9a-fA-F]{32}$");
    assert.ok(execute.inputSchema.properties.idempotencyKey);
    assert.equal(execute.inputSchema.required.includes("idempotencyKey"), false);
    assert.equal(query.inputSchema.properties.idempotencyKey, undefined);
    assert.ok(execute.inputSchema.properties.saveDrawing);
    assert.equal(execute.inputSchema.properties.saveDrawing.type, "boolean");
    assert.equal(execute.inputSchema.required.includes("saveDrawing"), false);
    assert.equal(query.inputSchema.properties.saveDrawing, undefined);
    const keySchema = execute.inputSchema.properties.idempotencyKey;
    assert.equal(keySchema.minLength, 1);
    assert.equal(keySchema.maxLength, 128);
    assert.equal(keySchema.pattern, "^[A-Za-z0-9._:-]+$");
    for (const tool of [execute, query]) {
      const guardSchema = tool.inputSchema.properties.expectedDrawing;
      assert.deepEqual(
        [...guardSchema.required].sort(),
        ["databaseFilename", "fingerprintGuid"]
      );
      assert.equal(guardSchema.additionalProperties, false);
    }

    const beforeMissing = mockPlugin.requests.length;
    await callMustFailValidation(client, {
      name: "civil3d_execute",
      arguments: { code: "return 1;" },
    });
    assert.equal(mockPlugin.requests.length, beforeMissing, "missing write guard must fail before TCP");

    const beforeMalformed = mockPlugin.requests.length;
    await callMustFailValidation(client, {
      name: "civil3d_execute",
      arguments: {
        code: "return 1;",
        expectedDrawing: { databaseFilename: expectedDrawing.databaseFilename },
      },
    });
    assert.equal(mockPlugin.requests.length, beforeMalformed, "malformed write guard must fail before TCP");

    const beforeInvalidKey = mockPlugin.requests.length;
    await callMustFailValidation(client, {
      name: "civil3d_execute",
      arguments: {
        code: "return 1;",
        expectedDrawing,
        idempotencyKey: "contains space",
      },
    });
    assert.equal(mockPlugin.requests.length, beforeInvalidKey, "invalid idempotency key must fail before TCP");

    const beforeInvalidInstance = mockPlugin.requests.length;
    await callMustFailValidation(client, {
      name: "civil3d_query",
      arguments: {
        code: "return 1;",
        instanceId: "not-an-instance-id",
      },
    });
    assert.equal(mockPlugin.requests.length, beforeInvalidInstance,
      "invalid instance id must fail before TCP");

    await client.callTool({
      name: "civil3d_query",
      arguments: { code: "return 1;" },
    });
    await client.callTool({
      name: "civil3d_query",
      arguments: { code: "return 2;", expectedDrawing },
    });
    await client.callTool({
      name: "civil3d_execute",
      arguments: {
        code: "return 3;",
        expectedDrawing,
        idempotencyKey: "write:test-1",
      },
    });
    await client.callTool({
      name: "civil3d_execute",
      arguments: {
        code: "return 4;",
        expectedDrawing,
        idempotencyKey: "write:test-save",
        saveDrawing: true,
      },
    });

    assert.equal(mockPlugin.requests.length, 4);
    assert.equal("expectedDrawing" in mockPlugin.requests[0].params, false);
    assert.equal(mockPlugin.requests[0].params.readOnly, true);
    assert.deepEqual(mockPlugin.requests[1].params.expectedDrawing, expectedDrawing);
    assert.equal(mockPlugin.requests[1].params.readOnly, true);
    assert.deepEqual(mockPlugin.requests[2].params.expectedDrawing, expectedDrawing);
    assert.equal(mockPlugin.requests[2].params.readOnly, false);
    assert.equal(mockPlugin.requests[2].params.idempotencyKey, "write:test-1");
    assert.equal("saveDrawing" in mockPlugin.requests[2].params, false);
    assert.equal(mockPlugin.requests[3].params.readOnly, false);
    assert.equal(mockPlugin.requests[3].params.idempotencyKey, "write:test-save");
    assert.equal(mockPlugin.requests[3].params.saveDrawing, true);
  } finally {
    await client.close();
    await server.close();
    await mockPlugin.close();
  }
});
