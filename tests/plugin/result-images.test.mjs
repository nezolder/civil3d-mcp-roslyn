import assert from "node:assert/strict";
import net from "node:net";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { imageContentBlocks, readResultImages } from "../../build/tools/resultImages.js";

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
  const server = new McpServer({ name: "result-images-test", version: "1.0.0" });
  registerExecuteTool(server);
  registerQueryTool(server);
  const client = new Client({ name: "result-images-client", version: "1.0.0" });
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

test("a query hands the plugin's pictures on as image blocks after the text", async () => {
  const result = {
    layout: "Model",
    picture: { image: 1, mimeType: "image/png", bytes: 7, width: 1600, height: 1200 },
    detail: { image: 2, mimeType: "image/jpeg", bytes: 4 },
  };
  const mockPlugin = await startMockPlugin(() => ({ result, images: [png, jpeg] }));
  await withTools(mockPlugin, async (client) => {
    const reply = await client.callTool({ name: "civil3d_query", arguments: { code: "return 1;" } });
    assert.notEqual(reply.isError, true);
    assert.deepEqual(reply.content.map((block) => block.type), ["text", "image", "image"]);
    assert.deepEqual(JSON.parse(reply.content[0].text), result, "the references stay in the JSON result");
    assert.deepEqual(reply.content[1], { type: "image", ...png });
    assert.deepEqual(reply.content[2], { type: "image", ...jpeg });
  });
});

test("a write keeps its order: result, receipt, then the pictures", async () => {
  const receipt = { drawing: expectedDrawing, committed: true, saved: false };
  const mockPlugin = await startMockPlugin(() => ({ result: { ok: true }, receipt, images: [png] }));
  await withTools(mockPlugin, async (client) => {
    const reply = await client.callTool({
      name: "civil3d_execute",
      arguments: { code: "return 1;", expectedDrawing },
    });
    assert.notEqual(reply.isError, true);
    assert.deepEqual(reply.content.map((block) => block.type), ["text", "text", "image"]);
    assert.deepEqual(JSON.parse(reply.content[1].text), { receipt });
    assert.deepEqual(reply.content[2], { type: "image", ...png });
  });
});

test("a reply without pictures, or with a broken list, stays a plain text reply", async () => {
  for (const images of [undefined, [], "not a list", [{ mimeType: "image/png" }]]) {
    const mockPlugin = await startMockPlugin(() => ({ result: { ok: true }, ...(images === undefined ? {} : { images }) }));
    await withTools(mockPlugin, async (client) => {
      const reply = await client.callTool({ name: "civil3d_query", arguments: { code: "return 1;" } });
      assert.notEqual(reply.isError, true);
      assert.equal(reply.content.length, 1);
    });
  }
});

test("only well-formed PNG and JPEG entries are accepted", () => {
  const accepted = readResultImages({
    images: [
      png,
      null,
      "text",
      { mimeType: "image/svg+xml", data: "PHN2Zz4=" },
      { mimeType: "image/png", data: "" },
      { mimeType: "image/png", data: "not base64!" },
      { mimeType: 5, data: png.data },
      { ...jpeg, extra: "ignored" },
    ],
  });
  assert.deepEqual(accepted, [png, jpeg]);
  assert.deepEqual(readResultImages({}), []);
  assert.deepEqual(imageContentBlocks(accepted), [
    { type: "image", ...png },
    { type: "image", ...jpeg },
  ]);
});
