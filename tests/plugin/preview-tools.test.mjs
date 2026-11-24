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
const changes = {
  added: {
    count: 2,
    byType: { Alignment: 1, Polyline: 1 },
    items: [{ type: "Alignment", handle: "2F3A", name: "Tengely-1", layer: "C-ROAD" }],
    more: 1,
  },
  modified: { count: 0 },
  erased: { count: 0 },
  rolledBack: true,
  complete: false,
  note: "a preview lists what the script itself did.",
};
const previewReceipt = {
  drawing: expectedDrawing,
  dbmodBefore: 0,
  dbmodAfter: 0,
  preview: true,
  committed: false,
  saved: false,
  fileLastWriteUtc: "2026-11-24T09:30:00.0000000+00:00",
  fileBytes: 123456,
  backupPath: null,
  changes,
};

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
  const names = ["CIVIL3D_HOST", "CIVIL3D_PORT", "CIVIL3D_CONNECT_TIMEOUT", "CIVIL3D_MCP_ENDPOINT_DIR", "LOG_LEVEL"];
  const saved = Object.fromEntries(names.map((name) => [name, process.env[name]]));
  process.env.CIVIL3D_HOST = "127.0.0.1";
  process.env.CIVIL3D_PORT = String(mockPlugin.port);
  process.env.CIVIL3D_CONNECT_TIMEOUT = "1000";
  process.env.LOG_LEVEL = "error";

  const { registerExecuteTool } = await import("../../build/tools/executeTool.js");
  const { registerQueryTool } = await import("../../build/tools/queryTool.js");
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "preview-test", version: "1.0.0" });
  registerExecuteTool(server);
  registerQueryTool(server);
  const client = new Client({ name: "preview-client", version: "1.0.0" });
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

test("a preview is forwarded with its change list size and the receipt comes back whole", async () => {
  const mockPlugin = await startMockPlugin({
    features: ["activeDrawing", "writeSafety", "images", "changeList", "preview", "sessionToken"],
    reply: () => ({ result: { created: "2F3A" }, receipt: previewReceipt }),
  });
  await withTools(mockPlugin, async (client) => {
    const { tools } = await client.listTools();
    const execute = tools.find((tool) => tool.name === "civil3d_execute");
    const query = tools.find((tool) => tool.name === "civil3d_query");
    assert.equal(execute.inputSchema.properties.preview.type, "boolean");
    assert.equal(execute.inputSchema.properties.changeItems.type, "integer");
    assert.equal(execute.inputSchema.properties.changeItems.minimum, 0);
    assert.equal(execute.inputSchema.properties.changeItems.maximum, 100);
    for (const name of ["preview", "changeItems"]) {
      assert.equal(execute.inputSchema.required.includes(name), false);
      assert.equal(query.inputSchema.properties[name], undefined, `${name} belongs to writes only`);
    }
    assert.match(execute.description, /preview=true/);
    assert.match(execute.description, /added, modified and erased/);

    const result = await client.callTool({
      name: "civil3d_execute",
      arguments: {
        code: "return 1;",
        expectedDrawing,
        preview: true,
        changeItems: 1,
        saveDrawing: true,
        idempotencyKey: "create-alignment-1",
      },
    });
    assert.notEqual(result.isError, true);
    assert.deepEqual(JSON.parse(result.content[0].text), { created: "2F3A" });
    assert.deepEqual(JSON.parse(result.content[1].text), { receipt: previewReceipt },
      "the receipt, with its change list, is passed on unchanged");

    assert.equal(mockPlugin.requests[0].method, "getCivil3DHealth",
      "the plugin is asked whether it knows preview before a preview is sent");
    const [sent] = mockPlugin.scripts();
    assert.equal(sent.params.preview, true);
    assert.equal(sent.params.changeItems, 1);
    assert.equal(sent.params.readOnly, false, "a preview runs with write access");
    assert.equal(sent.params.saveDrawing, true, "the other inputs travel as they are; the plugin keeps nothing");
    assert.equal(sent.params.idempotencyKey, "create-alignment-1");

    // A plain write sends neither input and asks nothing extra.
    const before = mockPlugin.requests.length;
    await client.callTool({ name: "civil3d_execute", arguments: { code: "return 2;", expectedDrawing } });
    assert.equal(mockPlugin.requests.length, before + 1);
    const plain = mockPlugin.scripts()[1];
    assert.equal("preview" in plain.params, false);
    assert.equal("changeItems" in plain.params, false);

    // The schema refuses a value outside the range before anything is sent.
    const scriptsBefore = mockPlugin.scripts().length;
    const tooMany = await client.callTool({
      name: "civil3d_execute",
      arguments: { code: "return 3;", expectedDrawing, changeItems: 101 },
    });
    assert.equal(tooMany.isError, true);
    assert.equal(mockPlugin.scripts().length, scriptsBefore);
  });
});

test("a preview is never sent to a plugin that would ignore it and commit", async () => {
  for (const features of [null, ["activeDrawing", "writeSafety", "images"]]) {
    const mockPlugin = await startMockPlugin({ features, reply: () => ({ result: { ok: true } }) });
    await withTools(mockPlugin, async (client) => {
      for (const inputs of [{ preview: true }, { changeItems: 5 }, { preview: true, requireClean: true }]) {
        const result = await client.callTool({
          name: "civil3d_execute",
          arguments: { code: "return 1;", expectedDrawing, ...inputs },
        });
        assert.equal(result.isError, true);
        assert.equal(result.structuredContent.error.code, "CIVIL3D.PLUGIN_UPDATE_REQUIRED");
        assert.equal(result.structuredContent.error.outcome, "not_started");
        if (inputs.preview) assert.match(result.content[0].text, /preview/);
      }
      assert.equal(mockPlugin.scripts().length, 0, "nothing may run when preview would be ignored");

      // preview: false is an ordinary write and needs nothing new.
      const plain = await client.callTool({
        name: "civil3d_execute",
        arguments: { code: "return 2;", expectedDrawing, preview: false },
      });
      assert.notEqual(plain.isError, true);
      assert.equal(mockPlugin.scripts().length, 1);
    });
  }
});
