import assert from "node:assert/strict";
import net from "node:net";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  SkillBindingError,
  bindSkillCode,
  isRunnableByName,
} from "../../build/tools/skillBinding.js";
import { findSkill } from "../../build/tools/skillsTool.js";

const TEMPLATE = [
  "## Code Template",
  "",
  "```csharp",
  'var handle = "HANDLE";',
  "var limit = 100;",
  "var tolerance = double.NaN;",
  "var station = 0.0;  // Replace",
  "bool onlyReferences = true;",
  'string filter = "";',
  "var points = new (double x, double y)[] { };",
  "    var handle2 = handle; // indented lines are never bound",
  "return new { handle, limit, tolerance, station, onlyReferences, filter, points };",
  "```",
].join("\n");

const PARAMETERS = [
  { name: "handle", type: "string", required: true },
  { name: "limit", type: "int", required: false },
  { name: "tolerance", type: "double", required: true },
  { name: "station", type: "double", required: false },
  { name: "onlyReferences", type: "bool", required: false },
  { name: "filter", type: "string", required: false },
  { name: "points", type: "array", required: false },
];

function bind(values) {
  return bindSkillCode("fixture", TEMPLATE, PARAMETERS, values);
}

function line(code, prefix) {
  return code.split("\n").find((text) => text.startsWith(prefix));
}

test("binds scalar values as C# literals and keeps omitted defaults", () => {
  const code = bind({ handle: "1A2B", tolerance: 2, station: -12.5, onlyReferences: false });
  assert.equal(line(code, "var handle ="), 'var handle = "1A2B";');
  assert.equal(line(code, "var tolerance ="), "var tolerance = 2d;", "whole numbers stay doubles");
  assert.equal(line(code, "var station ="), "var station = -12.5d;  // Replace", "trailing comments are kept");
  assert.equal(line(code, "bool onlyReferences ="), "bool onlyReferences = false;");
  assert.equal(line(code, "var limit ="), "var limit = 100;", "an omitted optional parameter keeps its default");
  assert.equal(line(code, 'string filter ='), 'string filter = "";');
  assert.ok(code.includes("    var handle2 = handle;"), "indented code is untouched");
  assert.equal(code.split("\n").length, TEMPLATE.split("\n").length - 3, "only the code block is returned");
});

test("strings can never break out of their literal", () => {
  const hostile = '"; System.IO.File.Delete("x"); //\\\nnext line ő 🚲';
  const code = bind({ handle: hostile, tolerance: 0 });
  const literal = line(code, "var handle =").slice("var handle = ".length, -1);
  assert.equal(literal,
    '"\\"; System.IO.File.Delete(\\"x\\"); //\\\\\\u000anext line \\u0151 \\ud83d\\udeb2"');
  assert.equal(code.split("\n").length, TEMPLATE.split("\n").length - 3, "no line break is introduced");
});

test("binds tuple arrays from objects or positional arrays", () => {
  const code = bind({ handle: "A", tolerance: 1, points: [{ x: 1, y: 2.5 }, [3, -4]] });
  assert.equal(line(code, "var points ="), "var points = new (double x, double y)[] { (1d, 2.5d), (3d, -4d) };");
  assert.equal(line(bind({ handle: "A", tolerance: 1, points: [] }), "var points ="),
    "var points = new (double x, double y)[] {  };");
});

test("rejects unknown, missing and mistyped values before any code is produced", () => {
  const cases = [
    [{ tolerance: 1 }, /requires parameter 'handle'/],
    [{ handle: "A", tolerance: 1, extra: 1 }, /Unknown parameter\(s\) for skill 'fixture': extra/],
    [{ handle: 7, tolerance: 1 }, /'handle' must be a string/],
    [{ handle: "A", tolerance: Number.NaN }, /'tolerance' must be a finite number/],
    [{ handle: "A", tolerance: "1" }, /'tolerance' must be a finite number/],
    [{ handle: "A", tolerance: 1, limit: 1.5 }, /'limit' must be a 32-bit integer/],
    [{ handle: "A", tolerance: 1, limit: 2 ** 31 }, /'limit' must be a 32-bit integer/],
    [{ handle: "A", tolerance: 1, onlyReferences: "true" }, /'onlyReferences' must be a boolean/],
    [{ handle: "A", tolerance: 1, points: {} }, /'points' must be an array/],
    [{ handle: "A", tolerance: 1, points: [{ x: 1 }] }, /'points\[0\]' must have exactly the fields x, y/],
    [{ handle: "A", tolerance: 1, points: [{ x: 1, y: 2, z: 3 }] }, /exactly the fields x, y/],
    [{ handle: "A", tolerance: 1, points: [[1]] }, /'points\[0\]' must have 2 values/],
    [{ handle: "A", tolerance: 1, points: [[1, "2"]] }, /'points\[0\]\.y' must be a finite number/],
  ];
  for (const [values, message] of cases) {
    assert.throws(() => bind(values), (error) => error instanceof SkillBindingError && message.test(error.message));
  }
});

test("templates with ambiguous or unsupported declarations are not runnable", () => {
  const duplicate = TEMPLATE.replace("var limit = 100;", "var limit = 100;\nvar limit = 5;");
  assert.equal(isRunnableByName(duplicate, PARAMETERS), false);
  const multiple = TEMPLATE.replace("var station = 0.0;  // Replace", "double station = 0, other = 1;");
  assert.equal(isRunnableByName(multiple, PARAMETERS), false);
  assert.equal(isRunnableByName(TEMPLATE, [...PARAMETERS, { name: "missing", type: "string", required: false }]), false);
  assert.equal(isRunnableByName("no code block", []), false);
  assert.equal(isRunnableByName(TEMPLATE, PARAMETERS), true);
});

test("engineering recipes run by name; the free-form replacement recipe does not", () => {
  for (const name of [
    "drawing_info",
    "alignment_geometry_audit",
    "create_design_profile_from_pvis",
    "create_section_views",
    "material_quantity_report",
    "compare_surface_elevations",
    "data_reference_audit",
    "corridor_target_audit",
    "create_surface_profile_view",
  ]) {
    const skill = findSkill(name);
    assert.ok(skill, name);
    assert.equal(isRunnableByName(skill.content, skill.metadata.parameters), true, name);
  }
  const replacement = findSkill("replace_alignment_with_fixed_primitives");
  assert.equal(isRunnableByName(replacement.content, replacement.metadata.parameters), false);
});

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
      socket.write(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { ok: true } }) + "\n");
    });
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  return {
    port: server.address().port,
    executed: () => requests.filter((request) => request.method === "executeCode"),
    close: () => new Promise((resolve) => server.close(resolve)),
  };
}

test("query and execute send the bound template; invalid sources never reach the plugin", async () => {
  const plugin = await startMockPlugin();
  process.env.CIVIL3D_HOST = "127.0.0.1";
  process.env.CIVIL3D_PORT = String(plugin.port);
  process.env.CIVIL3D_CONNECT_TIMEOUT = "500";
  process.env.LOG_LEVEL = "error";

  const { registerQueryTool } = await import("../../build/tools/queryTool.js");
  const { registerExecuteTool } = await import("../../build/tools/executeTool.js");
  const { registerSkillsTool } = await import("../../build/tools/skillsTool.js");
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "run-by-name-test", version: "1.0.0" });
  registerQueryTool(server);
  registerExecuteTool(server);
  registerSkillsTool(server);
  const client = new Client({ name: "run-by-name-client", version: "1.0.0" });
  const expectedDrawing = {
    databaseFilename: "C:\\Projects\\Plans\\Target.dwg",
    fingerprintGuid: "{11111111-2222-4333-8444-555555555555}",
  };

  try {
    await server.connect(serverTransport);
    await client.connect(clientTransport);

    const { tools } = await client.listTools();
    for (const name of ["civil3d_query", "civil3d_execute"]) {
      const schema = tools.find((tool) => tool.name === name).inputSchema;
      assert.equal(schema.required?.includes("code") ?? false, false, `${name} code is optional`);
      assert.ok(schema.properties.skill && schema.properties.params, `${name} accepts skill and params`);
    }

    const read = await client.callTool({
      name: "civil3d_query",
      arguments: {
        skill: "compare_surface_elevations",
        params: { surfaceAHandle: "1A", surfaceBHandle: "2B", samplePoints: [[1, 2]], tolerance: 0.1 },
        expectedDrawing,
      },
    });
    assert.notEqual(read.isError, true, JSON.stringify(read));
    const sentRead = plugin.executed().at(-1).params;
    assert.equal(sentRead.readOnly, true);
    assert.ok(sentRead.code.startsWith('var surfaceAHandle = "1A";\nvar surfaceBHandle = "2B";\n' +
      "var samplePoints = new (double x, double y)[] { (1d, 2d) };\nvar tolerance = 0.1d;\nvar detailLimit = 50;"));

    const write = await client.callTool({
      name: "civil3d_execute",
      arguments: {
        skill: "create_alignment_from_polyline",
        params: { sourceHandle: "3C", alignmentName: "Tengely 1" },
        expectedDrawing,
      },
    });
    assert.notEqual(write.isError, true, JSON.stringify(write));
    const sentWrite = plugin.executed().at(-1).params;
    assert.equal(sentWrite.readOnly, false);
    assert.ok(sentWrite.code.includes('var alignmentName = "Tengely 1";'));

    const before = plugin.executed().length;
    for (const [name, args, message] of [
      ["civil3d_query", { skill: "create_alignment_from_polyline", params: { sourceHandle: "3C", alignmentName: "A" } },
        /writes to the drawing; run it with civil3d_execute/],
      ["civil3d_query", { code: "return 1;", skill: "drawing_info" }, /either 'code' or 'skill', not both/],
      ["civil3d_query", {}, /Provide either 'code' or 'skill'/],
      ["civil3d_query", { code: "return 1;", params: {} }, /only valid together with 'skill'/],
      ["civil3d_query", { skill: "no_such_skill" }, /Skill 'no_such_skill' not found/],
      ["civil3d_query", { skill: "compare_surface_elevations", params: { surfaceAHandle: "1A" } },
        /requires parameter 'surfaceBHandle'/],
      ["civil3d_execute", { skill: "replace_alignment_with_fixed_primitives", params: {}, expectedDrawing },
        /cannot run by name/],
    ]) {
      const result = await client.callTool({ name, arguments: args });
      assert.equal(result.isError, true, JSON.stringify(args));
      assert.equal(result.structuredContent.error.code, "CIVIL3D.INVALID_INPUT");
      assert.equal(result.structuredContent.error.outcome, "not_started");
      assert.match(result.structuredContent.error.message, message);
    }
    assert.equal(plugin.executed().length, before, "invalid sources must fail before TCP");

    const metadataOnly = await client.callTool({
      name: "civil3d_skills",
      arguments: { action: "get", skillName: "compare_surface_elevations", includeCode: false },
    });
    const skill = JSON.parse(metadataOnly.content[0].text);
    assert.equal(skill.run_by_name, true);
    assert.equal(skill.content.includes("```csharp"), false, "includeCode=false omits the template");
    assert.ok(skill.content.includes("Code template omitted"));
    const full = JSON.parse((await client.callTool({
      name: "civil3d_skills",
      arguments: { action: "get", skillName: "compare_surface_elevations" },
    })).content[0].text);
    assert.ok(full.content.includes("```csharp"), "get keeps the code by default");
  } finally {
    await client.close();
    await server.close();
    await plugin.close();
  }
});
