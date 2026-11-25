import assert from "node:assert/strict";
import net from "node:net";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { apiNotesContentBlocks, readApiNotes } from "../../build/tools/apiNotes.js";

const expectedDrawing = {
  databaseFilename: "C:\\Projects\\Plans\\Target.dwg",
  fingerprintGuid: "{11111111-2222-4333-8444-555555555555}",
};
const png = { mimeType: "image/png", data: Buffer.from([0x89, 0x50, 0x4e, 0x47, 1, 2, 3]).toString("base64") };
const jpeg = { mimeType: "image/jpeg", data: Buffer.from([0xff, 0xd8, 0xff, 9]).toString("base64") };

async function startMockPlugin(reply) {
  const server = net.createServer((socket) => {
    let buffered = Buffer.alloc(0);
    socket.on("data", (chunk) => {
      buffered = Buffer.concat([buffered, chunk]);
      const delimiter = buffered.indexOf(0x0a);
      if (delimiter < 0) return;
      const request = JSON.parse(buffered.subarray(0, delimiter).toString("utf8"));
      const body =
        request.method === "getCivil3DHealth"
          ? { result: { connected: true, features: ["activeDrawing", "writeSafety", "images"] } }
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
    close: () =>
      new Promise((resolve, reject) => server.close((error) => (error ? reject(error) : resolve()))),
  };
}

async function withTools(mockPlugin, run) {
  const names = ["CIVIL3D_HOST", "CIVIL3D_PORT", "CIVIL3D_CONNECT_TIMEOUT", "LOG_LEVEL"];
  const saved = Object.fromEntries(names.map((name) => [name, process.env[name]]));
  process.env.CIVIL3D_HOST = "127.0.0.1";
  process.env.CIVIL3D_PORT = String(mockPlugin.port);
  process.env.CIVIL3D_CONNECT_TIMEOUT = "1000";
  process.env.LOG_LEVEL = "error";

  const { registerExecuteTool } = await import("../../build/tools/executeTool.js");
  const { registerQueryTool } = await import("../../build/tools/queryTool.js");
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "api-notes-test", version: "1.0.0" });
  registerExecuteTool(server);
  registerQueryTool(server);
  const client = new Client({ name: "api-notes-client", version: "1.0.0" });
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

const note = {
  id: "bounded-volumes-raw",
  member: "Autodesk.Civil.DatabaseServices.Surface.GetBoundedVolumes",
  line: 3,
  source: "live",
  note: "GetBoundedVolumes on a volume surface returns raw cut and fill.",
  guideTopic: null,
};
const { guideTopic: _unused, ...expectedNote } = note;

test("a query hands the plugin's API notes on in their own block after the result", async () => {
  const mockPlugin = await startMockPlugin(() => ({ result: { cut: 1 }, apiNotes: [note] }));
  await withTools(mockPlugin, async (client) => {
    const reply = await client.callTool({ name: "civil3d_query", arguments: { code: "return 1;" } });
    assert.notEqual(reply.isError, true);
    assert.equal(reply.content.length, 2);
    assert.deepEqual(JSON.parse(reply.content[0].text), { cut: 1 }, "the result keeps its own block and shape");
    assert.deepEqual(JSON.parse(reply.content[1].text), { apiNotes: [expectedNote] });
  });
});

test("a write keeps its order: result, receipt, notes; a failed write has them too", async () => {
  const receipt = { drawing: expectedDrawing, committed: true, saved: false };
  let mockPlugin = await startMockPlugin(() => ({ result: { ok: true }, receipt, apiNotes: [note] }));
  await withTools(mockPlugin, async (client) => {
    const reply = await client.callTool({ name: "civil3d_execute", arguments: { code: "return 1;", expectedDrawing } });
    assert.notEqual(reply.isError, true);
    assert.deepEqual(JSON.parse(reply.content[1].text), { receipt });
    assert.deepEqual(JSON.parse(reply.content[2].text), { apiNotes: [expectedNote] });
  });

  mockPlugin = await startMockPlugin(() => ({
    error: { code: "CIVIL3D.TRANSACTION_FAILED", message: "eNotOpenForWrite" },
    receipt: { ...receipt, committed: false },
    apiNotes: [note],
  }));
  await withTools(mockPlugin, async (client) => {
    const reply = await client.callTool({ name: "civil3d_execute", arguments: { code: "return 1;", expectedDrawing } });
    assert.equal(reply.isError, true);
    assert.deepEqual(reply.structuredContent.apiNotes, [expectedNote]);
    assert.equal(reply.structuredContent.receipt.committed, false);
    assert.ok(reply.content.some((block) => block.text === JSON.stringify({ apiNotes: [expectedNote] })));
  });
});

test("a failed query carries its notes in the error reply", async () => {
  const mockPlugin = await startMockPlugin(() => ({
    error: { code: "CIVIL3D.TRANSACTION_FAILED", message: "fatal" },
    apiNotes: [note],
  }));
  await withTools(mockPlugin, async (client) => {
    const reply = await client.callTool({ name: "civil3d_query", arguments: { code: "return 1;" } });
    assert.equal(reply.isError, true);
    assert.deepEqual(reply.structuredContent.apiNotes, [expectedNote]);
  });
});

test("a catalog recipe run by name gets no notes: the recipe already handles them", async () => {
  const mockPlugin = await startMockPlugin(() => ({ result: { ok: true }, apiNotes: [note] }));
  await withTools(mockPlugin, async (client) => {
    const reply = await client.callTool({ name: "civil3d_query", arguments: { skill: "drawing_info" } });
    assert.notEqual(reply.isError, true);
    assert.equal(reply.content.length, 1);
  });
});

test("only well-formed notes are accepted, at most eight, texts bounded", () => {
  const accepted = readApiNotes({
    apiNotes: [
      note,
      null,
      "text",
      { ...note, line: "3" },
      { ...note, line: 2.5 },
      { ...note, member: undefined },
      { ...note, id: "long", note: "x".repeat(2000), guideTopic: "civil" },
    ],
  });
  assert.deepEqual(accepted.map((item) => item.id), ["bounded-volumes-raw", "long"]);
  assert.equal(accepted[1].note.length, 600);
  assert.equal(accepted[1].guideTopic, "civil");
  assert.equal("guideTopic" in accepted[0], false);
  assert.equal(readApiNotes({ apiNotes: Array.from({ length: 20 }, () => note) }).length, 8);
  assert.deepEqual(readApiNotes({}), []);
  assert.deepEqual(apiNotesContentBlocks([]), []);
});
