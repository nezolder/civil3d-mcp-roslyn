import assert from "node:assert/strict";
import net from "node:net";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import fs from "node:fs";
import path from "node:path";
import {
  SkillBindingError,
  bindSkillCode,
  describeParameters,
  isRunnableByName,
} from "../../build/tools/skillBinding.js";
import { SKILLS_DIR, findSkill } from "../../build/tools/skillsTool.js";

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

test("binds templates stored with CRLF line endings and keeps them", () => {
  const code = bindSkillCode("fixture", TEMPLATE.replace(/\n/g, "\r\n"), PARAMETERS,
    { handle: "1A2B", tolerance: 2, station: 3 });
  assert.ok(code.includes('var handle = "1A2B";\r\n'));
  assert.ok(code.includes("var tolerance = 2d;\r\n"));
  assert.ok(code.includes("var station = 3d;  // Replace\r\n"));
  assert.equal(code.includes("\r\r"), false);
  assert.equal(isRunnableByName(TEMPLATE.replace(/\n/g, "\r\n"), PARAMETERS), true);
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

const LIST_TEMPLATE = [
  "```csharp",
  "var stations = new double[] { };",
  'var layers = new string[] { "0", "C-ROAD, edge" };',
  "int[] counts = new int[] { 1, 2 };",
  "var flags = new bool[] { true };",
  "return new { stations, layers, counts, flags };",
  "```",
].join("\n");

const LIST_PARAMETERS = [
  { name: "stations", type: "array", required: true, description: "Stations to sample" },
  { name: "layers", type: "array", required: false },
  { name: "counts", type: "array", required: false },
  { name: "flags", type: "array", required: false },
];

test("binds plain lists of numbers, texts and booleans", () => {
  const code = bindSkillCode("lists", LIST_TEMPLATE, LIST_PARAMETERS, {
    stations: [0, 12.5, -3],
    layers: ['a"b', "ő"],
    counts: [],
    flags: [false, true],
  });
  assert.equal(line(code, "var stations ="), "var stations = new double[] { 0d, 12.5d, -3d };");
  assert.equal(line(code, "var layers ="), 'var layers = new string[] { "a\\"b", "\\u0151" };');
  assert.equal(line(code, "int[] counts ="), "int[] counts = new int[] {  };");
  assert.equal(line(code, "var flags ="), "var flags = new bool[] { false, true };");

  const defaults = bindSkillCode("lists", LIST_TEMPLATE, LIST_PARAMETERS, { stations: [1] });
  assert.equal(line(defaults, "var layers ="), 'var layers = new string[] { "0", "C-ROAD, edge" };',
    "an omitted list keeps its default, commas inside texts included");

  for (const [values, message] of [
    [{ stations: 5 }, /'stations' must be an array of double values/],
    [{ stations: [1, "2"] }, /'stations\[1\]' must be a finite number/],
    [{ stations: [1], layers: [1] }, /'layers\[0\]' must be a string/],
    [{ stations: [1], counts: [1.5] }, /'counts\[0\]' must be a 32-bit integer/],
    [{ stations: [[1, 2]] }, /'stations\[0\]' must be a finite number/],
  ]) {
    assert.throws(() => bindSkillCode("lists", LIST_TEMPLATE, LIST_PARAMETERS, values),
      (error) => error instanceof SkillBindingError && message.test(error.message));
  }

  // A list whose default is not made of plain literals of its own type cannot be bound.
  for (const declaration of [
    "var stations = new double[] { Math.PI };",
    'var stations = new double[] { "1" };',
    "double[] stations = new int[] { 1 };",
    "var stations = new double[] { 1, };",
    "var stations = new double[] { 1 2 };",
  ]) {
    const template = LIST_TEMPLATE.replace("var stations = new double[] { };", declaration);
    assert.equal(isRunnableByName(template, LIST_PARAMETERS), false, declaration);
  }
});

test("a missing parameter is reported together with every parameter of the skill", () => {
  assert.throws(() => bind({ limit: 3 }), (error) => {
    assert.ok(error instanceof SkillBindingError);
    assert.match(error.message, /^Skill 'fixture' requires parameters 'handle', 'tolerance'\. Parameters: /);
    for (const part of [
      "handle (string, required)",
      "limit (int, optional, default 100)",
      "tolerance (double, required)",
      "station (double, optional, default 0)",
      "onlyReferences (bool, optional, default true)",
      "filter (string, optional)",
      "points (rows of {x: double, y: double}, optional)",
    ]) {
      assert.ok(error.message.includes(part), `${part} in: ${error.message}`);
    }
    return true;
  });
  assert.throws(() => bindSkillCode("lists", LIST_TEMPLATE, LIST_PARAMETERS, {}),
    /requires parameter 'stations'\. Parameters: stations \(double\[\], required\); layers \(string\[\], optional, default \["0","C-ROAD, edge"\]\)/);
});

test("parameter cards state type, default and row fields as the template has them", () => {
  assert.deepEqual(describeParameters(TEMPLATE, PARAMETERS), [
    { name: "handle", type: "string", required: true },
    { name: "limit", type: "int", required: false, default: 100 },
    { name: "tolerance", type: "double", required: true },
    { name: "station", type: "double", required: false, default: 0 },
    { name: "onlyReferences", type: "bool", required: false, default: true },
    { name: "filter", type: "string", required: false },
    { name: "points", type: "rows", required: false,
      fields: [{ name: "x", type: "double" }, { name: "y", type: "double" }] },
  ]);
  assert.deepEqual(describeParameters(LIST_TEMPLATE, LIST_PARAMETERS), [
    { name: "stations", type: "double[]", required: true, description: "Stations to sample" },
    { name: "layers", type: "string[]", required: false, default: ["0", "C-ROAD, edge"] },
    { name: "counts", type: "int[]", required: false, default: [1, 2] },
    { name: "flags", type: "bool[]", required: false, default: [true] },
  ]);
  // A parameter the template does not declare keeps the type of the skill's own metadata.
  assert.deepEqual(describeParameters("no code block", [{ name: "baseline", type: "object", required: true }]),
    [{ name: "baseline", type: "object", required: true }]);
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

function skillNames(directory = SKILLS_DIR) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) =>
    entry.isDirectory()
      ? skillNames(path.join(directory, entry.name))
      : entry.name.endsWith(".skill.md") ? [entry.name.slice(0, -".skill.md".length)] : []
  );
}

test("every recipe runs by name except the free-form replacement recipe", () => {
  const names = skillNames();
  assert.ok(names.length >= 39);
  for (const name of names) {
    const skill = findSkill(name);
    assert.ok(skill, name);
    // Its input is a table of anonymous records with points and infinite radii;
    // changing that shape would void the live validation of a write recipe.
    const expected = name !== "replace_alignment_with_fixed_primitives";
    assert.equal(isRunnableByName(skill.content, skill.metadata.parameters), expected, name);
  }
});

test("the recipes that became runnable by name bind their documented parameters", () => {
  const cases = {
    station_offset: [{ alignmentName: "Axis 1", station: 125.5, offset: -3 },
      ['var alignmentName = "Axis 1";', "var station = 125.5d;", "var offset = -3d;", "var x = double.NaN;"]],
    surface_elevation: [{ surfaceName: "EG", x: 650000.5, y: 237000.25 },
      ['var surfaceName = "EG";', "var x = 650000.5d;", "var y = 237000.25d;"]],
    surface_volume: [{ surfaceName: "Volume" }, ['var surfaceName = "Volume";']],
    earthwork_report: [{ surfaceName: "EG" }, ['var surfaceName = "EG";']],
    list_profiles: [{ alignmentName: "Axis 1", limit: 10 }, ['var alignmentName = "Axis 1";', "var limit = 10;"]],
    section_inventory: [{ limit: 5 }, ['var alignmentName = "";', "var limit = 5;"]],
    corridor_summary: [{ corridorName: "C1", baselineLimit: 2 },
      ['var corridorName = "C1";', "var limit = 20;", "var baselineLimit = 2;"]],
    pipe_network_qc: [{ networkName: "Storm", partLimit: 10 }, ['var networkName = "Storm";', "var partLimit = 10;"]],
    surface_definition_summary: [{ surfaceName: "EG" }, ['var surfaceName = "EG";', "var limit = 50;"]],
    create_polyline: [{ vertices: [[0, 0, 0], { x: 10, y: 0, z: 0 }], layer: "0" },
      ["var vertices = new (double x, double y, double z)[] { (0d, 0d, 0d), (10d, 0d, 0d) };",
        "var polyline3d = false;", 'var layer = "0";']],
    create_cogo_points: [{ points: [[650000.12, 237000.5, 101.25, "TOPO"]] },
      ["var points = new (double easting, double northing, double elevation, string description)[] " +
        '{ (650000.12d, 237000.5d, 101.25d, "TOPO") };']],
    create_tin_surface: [{ surfaceName: "New", points: [[0, 0, 1], [10, 0, 2], [0, 10, 3]] },
      ['var surfaceName = "New";',
        "var points = new (double x, double y, double z)[] { (0d, 0d, 1d), (10d, 0d, 2d), (0d, 10d, 3d) };",
        'var description = "";']],
  };
  for (const [name, [params, lines]] of Object.entries(cases)) {
    const skill = findSkill(name);
    const code = bindSkillCode(name, skill.content, skill.metadata.parameters, params).replace(/\r\n/g, "\n");
    for (const expected of lines) {
      assert.ok(code.split("\n").includes(expected), `${name}: ${expected}`);
    }
    // No placeholder of the old copy-and-edit templates is left behind.
    assert.doesNotMatch(code, /"[A-Z_]+_NAME(?:_HERE)?"|\/\/ Replace/, name);
  }
  // Read recipes refuse a write through civil3d_query by their metadata, not by their text.
  for (const name of ["create_polyline", "create_cogo_points", "create_tin_surface"]) {
    assert.equal(findSkill(name).metadata.requires_write, true, name);
  }
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
    // Windows checkouts can store recipes with CRLF line endings.
    assert.ok(sentRead.code.replace(/\r\n/g, "\n").startsWith('var surfaceAHandle = "1A";\nvar surfaceBHandle = "2B";\n' +
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
        /requires parameters 'surfaceBHandle', 'samplePoints', 'tolerance'\. Parameters: surfaceAHandle \(string, required\); /],
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

    // The card is what a run by name needs, and a fraction of the full text.
    const cardReply = await client.callTool({
      name: "civil3d_skills",
      arguments: { action: "get", skillName: "compare_surface_elevations", view: "card" },
    });
    const card = JSON.parse(cardReply.content[0].text);
    assert.deepEqual(Object.keys(card), ["name", "description", "requires_write", "run_by_name", "parameters", "next"]);
    assert.equal(card.run_by_name, true);
    assert.deepEqual(card.parameters.map((parameter) => [parameter.name, parameter.type, parameter.required]), [
      ["surfaceAHandle", "string", true],
      ["surfaceBHandle", "string", true],
      ["samplePoints", "rows", true],
      ["tolerance", "double", true],
      ["detailLimit", "int", false],
    ]);
    assert.deepEqual(card.parameters[2].fields, [{ name: "x", type: "double" }, { name: "y", type: "double" }]);
    assert.equal(card.parameters[4].default, 50);
    assert.ok(card.parameters.every((parameter) => typeof parameter.description === "string"));
    assert.match(card.next, /^Pass skill and params to civil3d_query\. .*get with includeCode=false\.$/);
    const cardLength = cardReply.content[0].text.length;
    assert.ok(cardLength * 2 < metadataOnly.content[0].text.length && cardLength * 5 < JSON.stringify(full).length,
      "the card is less than half of the text without code and a fifth of the full skill");

    const writeCard = JSON.parse((await client.callTool({
      name: "civil3d_skills",
      arguments: { action: "get", skillName: "create_polyline", view: "card" },
    })).content[0].text);
    assert.match(writeCard.next, /^Pass skill and params to civil3d_execute\. .*before the first write/);

    const freeForm = JSON.parse((await client.callTool({
      name: "civil3d_skills",
      arguments: { action: "get", skillName: "replace_alignment_with_fixed_primitives", view: "card" },
    })).content[0].text);
    assert.equal(freeForm.run_by_name, false);
    assert.match(freeForm.next, /does not run by name/);
    assert.equal(freeForm.parameters.find((parameter) => parameter.name === "expectedBaseline").type, "object");

    const { tools: listed } = await client.listTools();
    const skillsTool = listed.find((tool) => tool.name === "civil3d_skills");
    assert.deepEqual(skillsTool.inputSchema.properties.view.enum, ["card", "full"]);
    assert.match(skillsTool.description, /get and view='card'/);
  } finally {
    await client.close();
    await server.close();
    await plugin.close();
  }
});
