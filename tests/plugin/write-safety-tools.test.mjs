import assert from "node:assert/strict";
import net from "node:net";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";

const expectedDrawing = {
  databaseFilename: "C:\\Projects\\Plans\\Target.dwg",
  fingerprintGuid: "{11111111-2222-4333-8444-555555555555}",
};
const receipt = {
  drawing: expectedDrawing,
  dbmodBefore: 0,
  dbmodAfter: 1,
  committed: true,
  saved: true,
  fileLastWriteUtc: "2026-11-11T09:30:00.0000000+00:00",
  fileBytes: 123456,
  backupPath: "C:\\Backups\\Target.20261111-103000.dwg",
};

/** features=null models a plugin from before the safety switches existed. */
async function startMockPlugin({ features, reply }) {
  const requests = [];
  const server = net.createServer((socket) => {
    let buffered = Buffer.alloc(0);
    socket.on("data", (chunk) => {
      buffered = Buffer.concat([buffered, chunk]);
      const delimiter = buffered.indexOf(0x0a);
      if (delimiter < 0) return;
      const request = JSON.parse(buffered.subarray(0, delimiter).toString("utf8"));
      requests.push(request);
      const body =
        request.method === "getCivil3DHealth"
          ? { result: { connected: true, ...(features ? { features } : {}) } }
          : reply(request);
      socket.write(JSON.stringify({ jsonrpc: "2.0", id: request.id, ...body }) + "\n");
    });
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  return {
    port: server.address().port,
    requests,
    scripts: () => requests.filter((request) => request.method === "executeCode"),
    close: () =>
      new Promise((resolve, reject) => server.close((error) => (error ? reject(error) : resolve()))),
  };
}

async function withTools(mockPlugin, run) {
  const saved = {
    CIVIL3D_HOST: process.env.CIVIL3D_HOST,
    CIVIL3D_PORT: process.env.CIVIL3D_PORT,
    CIVIL3D_CONNECT_TIMEOUT: process.env.CIVIL3D_CONNECT_TIMEOUT,
    LOG_LEVEL: process.env.LOG_LEVEL,
  };
  process.env.CIVIL3D_HOST = "127.0.0.1";
  process.env.CIVIL3D_PORT = String(mockPlugin.port);
  process.env.CIVIL3D_CONNECT_TIMEOUT = "1000";
  process.env.LOG_LEVEL = "error";

  const { registerExecuteTool } = await import("../../build/tools/executeTool.js");
  const { registerQueryTool } = await import("../../build/tools/queryTool.js");
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "write-safety-test", version: "1.0.0" });
  registerExecuteTool(server);
  registerQueryTool(server);
  const client = new Client({ name: "write-safety-client", version: "1.0.0" });
  try {
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    await run(client);
  } finally {
    await client.close();
    await server.close();
    await mockPlugin.close();
    for (const [name, value] of Object.entries(saved)) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
  }
}

test("a write returns the plugin's receipt in a block of its own and forwards the safety switches", async () => {
  const mockPlugin = await startMockPlugin({
    features: ["activeDrawing", "writeSafety"],
    reply: () => ({ result: { created: 3 }, receipt }),
  });
  await withTools(mockPlugin, async (client) => {
    const { tools } = await client.listTools();
    const execute = tools.find((tool) => tool.name === "civil3d_execute");
    const query = tools.find((tool) => tool.name === "civil3d_query");
    for (const name of ["requireClean", "backup"]) {
      assert.equal(execute.inputSchema.properties[name].type, "boolean");
      assert.equal(execute.inputSchema.required.includes(name), false);
      assert.equal(query.inputSchema.properties[name], undefined, `${name} belongs to writes only`);
    }
    assert.equal(execute.inputSchema.properties.backupDirectory.type, "string");
    assert.equal(query.inputSchema.properties.backupDirectory, undefined);

    const result = await client.callTool({
      name: "civil3d_execute",
      arguments: {
        code: "return 1;",
        expectedDrawing,
        description: "Rename the alignments",
        requireClean: true,
        backup: true,
        backupDirectory: "C:\\Backups",
        saveDrawing: true,
      },
    });
    assert.notEqual(result.isError, true);
    assert.equal(result.content.length, 2);
    assert.deepEqual(JSON.parse(result.content[0].text), { created: 3 }, "the result keeps its own block and shape");
    assert.deepEqual(JSON.parse(result.content[1].text), { receipt });

    const [sent] = mockPlugin.scripts();
    assert.equal(sent.params.requireClean, true);
    assert.equal(sent.params.backup, true);
    assert.equal(sent.params.backupDirectory, "C:\\Backups");
    assert.equal(sent.params.description, "Rename the alignments");
    assert.equal(mockPlugin.requests[0].method, "getCivil3DHealth",
      "the plugin is asked whether it knows the switches before they are relied on");

    // A plain write asks nothing extra and sends no switches.
    const before = mockPlugin.requests.length;
    await client.callTool({ name: "civil3d_execute", arguments: { code: "return 2;", expectedDrawing } });
    assert.equal(mockPlugin.requests.length, before + 1);
    const plain = mockPlugin.scripts()[1];
    for (const name of ["requireClean", "backup", "backupDirectory"]) {
      assert.equal(name in plain.params, false);
    }

    // A query has no receipt even if a plugin were to send one.
    const queried = await client.callTool({ name: "civil3d_query", arguments: { code: "return 3;" } });
    assert.equal(queried.content.length, 1);
  });
});

test("a refused or failed write still reports what the plugin found", async () => {
  const refusedReceipt = { ...receipt, dbmodBefore: 5, dbmodAfter: 5, committed: false, saved: false, backupPath: null };
  const mockPlugin = await startMockPlugin({
    features: ["activeDrawing", "writeSafety"],
    reply: () => ({
      error: { code: "CIVIL3D.DRAWING_NOT_CLEAN", message: "The active drawing has unsaved changes (DBMOD=5), so nothing was run." },
      receipt: refusedReceipt,
    }),
  });
  await withTools(mockPlugin, async (client) => {
    const result = await client.callTool({
      name: "civil3d_execute",
      arguments: { code: "return 1;", expectedDrawing, requireClean: true },
    });
    assert.equal(result.isError, true);
    assert.match(result.content[0].text, /unsaved changes/);
    assert.deepEqual(JSON.parse(result.content[1].text), { receipt: refusedReceipt });
    assert.equal(result.structuredContent.error.code, "CIVIL3D.DRAWING_NOT_CLEAN");
    assert.equal(result.structuredContent.error.category, "drawing");
    assert.equal(result.structuredContent.error.outcome, "not_started");
    assert.equal(result.structuredContent.error.retryable, false);
    assert.deepEqual(result.structuredContent.receipt, refusedReceipt);
  });
});

test("the safety switches are never sent to a plugin that would ignore them", async () => {
  const mockPlugin = await startMockPlugin({ features: null, reply: () => ({ result: { ok: true } }) });
  await withTools(mockPlugin, async (client) => {
    for (const switches of [{ requireClean: true }, { backup: true }, { backup: true, backupDirectory: "C:\\Backups" }]) {
      const result = await client.callTool({
        name: "civil3d_execute",
        arguments: { code: "return 1;", expectedDrawing, ...switches },
      });
      assert.equal(result.isError, true);
      assert.equal(result.structuredContent.error.code, "CIVIL3D.PLUGIN_UPDATE_REQUIRED");
      assert.equal(result.structuredContent.error.outcome, "not_started");
    }
    assert.equal(mockPlugin.scripts().length, 0, "nothing may run when a safety switch would be ignored");

    // The same older plugin still serves writes that do not use the switches.
    const plain = await client.callTool({
      name: "civil3d_execute",
      arguments: { code: "return 2;", expectedDrawing, requireClean: false },
    });
    assert.notEqual(plain.isError, true);
    assert.equal(plain.content.length, 1, "an older plugin sends no receipt and the reply stays as it was");
    assert.equal(mockPlugin.scripts().length, 1);
  });
});
