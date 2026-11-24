// Offline tests of the live smoke runner (scripts/live-smoke). A small
// simulated Civil 3D answers the MCP calls, so the order of the run, its
// checks and its stop rules are tested without Civil 3D. Whether the cases
// pass in a real Civil 3D is only shown by a live run.

import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  NAMES,
  acadArguments,
  csharpLiteral,
  designPvis,
  fillScript,
  groundElevation,
  isFixturePath,
  pickStartedInstance,
  readToolResult,
  renderMarkdown,
  runSmoke,
  startupScript,
  surfacePoints,
} from "../../scripts/live-smoke/lib.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const scriptDir = path.join(root, "scripts", "live-smoke", "csharp");
const SCRIPTS = Object.fromEntries(
  fs.readdirSync(scriptDir)
    .filter((name) => name.endsWith(".cs"))
    .map((name) => [path.basename(name, ".cs"), fs.readFileSync(path.join(scriptDir, name), "utf8")])
);
const FIXTURE = "C:\\Temp\\c3dmcp-smoke\\20261124\\mcp_smoke.dwg";
const PORT = 50123;

const text = (value) => ({ type: "text", text: JSON.stringify(value) });
const ok = (data, receipt) => ({ content: [text(data), ...(receipt ? [text({ receipt })] : [])] });
const failure = (code, outcome, receipt) => ({
  isError: true,
  content: [{ type: "text", text: `Execution failed: ${code}` }, ...(receipt ? [text({ receipt })] : [])],
  structuredContent: { ok: false, error: { code, outcome }, ...(receipt ? { receipt } : {}) },
});

function param(code, name) {
  const match = new RegExp(`^var ${name} = (.+); // smoke-param$`, "m").exec(code);
  if (!match) return undefined;
  const literal = match[1];
  if (literal.startsWith('"')) return literal.slice(1, -1);
  if (literal === "true" || literal === "false") return literal === "true";
  return Number(literal);
}

/** A simulated Civil 3D with just enough behaviour for the smoke run. */
function simulatedCivil({ drawing = FIXTURE, corridorWarning = null, throwOn = null } = {}) {
  const civil = {
    calls: [],
    dbmod: 0,
    counts: { surfaces: 0, alignments: 0, corridors: 0, assemblies: 0, modelSpaceEntities: 0 },
    lines: new Map(),
    description: "",
    keys: new Set(),
    backups: [],
    handle: 0x100,
  };
  const nextHandle = () => (civil.handle++).toString(16).toUpperCase();
  const lineKey = (code) => ["x1", "y1", "x2", "y2"].map((name) => param(code, name)).join(",");

  function receipt(args, { committed = true, added = {}, modified = {}, items = [], rolledBack } = {}) {
    const preview = args.preview === true;
    const keep = committed && !preview;
    const dbmodBefore = civil.dbmod;
    if (keep) civil.dbmod = 1;
    let saved = false;
    let backupPath = null;
    if (keep && args.saveDrawing) {
      civil.dbmod = 0;
      saved = true;
    }
    if (!preview && args.backup) {
      backupPath = `C:\\backups\\mcp_smoke-${civil.backups.length}.dwg`;
      civil.backups.push(backupPath);
    }
    const group = (byType) => ({
      count: Object.values(byType).reduce((sum, value) => sum + value, 0),
      byType,
    });
    const changes = { added: { ...group(added), items }, modified: group(modified), erased: group({}) };
    if (rolledBack !== undefined) changes.rolledBack = rolledBack;
    return { dbmodBefore, dbmodAfter: civil.dbmod, preview, committed: keep, saved, backupPath, changes };
  }

  function runWrite(args) {
    if (args.skill === "create_tin_surface") {
      civil.counts.surfaces++;
      return ok({ success: true, handle: nextHandle() }, receipt(args, { added: { TinSurface: 1 } }));
    }
    if (args.skill === "create_polyline") {
      assert.equal(args.params.vertices.length, 4);
      return ok({ success: true, handle: nextHandle() }, receipt(args, { added: { Polyline: 1 } }));
    }
    if (args.skill === "create_surface_profile_view" || args.skill === "create_sample_lines") {
      return ok({ success: true, createdCount: 19 }, receipt(args, { added: { Profile: 1 } }));
    }
    if (args.skill === "create_design_profile_from_pvis") {
      assert.deepEqual(args.params.pviData[1], [150, 104.2, 60]);
      return ok({ success: true, handle: nextHandle() }, receipt(args, { added: { Profile: 1 } }));
    }
    const code = args.code ?? "";
    if (code.includes("siteless alignment from the fixture polyline")) {
      assert.equal(param(code, "alignmentName"), NAMES.alignment);
      civil.counts.alignments++;
      return ok({ success: true, handle: nextHandle(), startStation: 0, endStation: 300, length: 300, entityCount: 3 },
        receipt(args, { added: { Alignment: 1 } }));
    }
    if (code.includes("a corridor along the fixture alignment")) {
      civil.counts.corridors++;
      civil.counts.assemblies++;
      return ok({ success: true, corridorHandle: nextHandle(), subassemblies: ["a"], subassemblyWarning: corridorWarning, outOfDate: false },
        receipt(args, { added: { Corridor: 1, Assembly: 1 } }));
    }
    if (code.includes("rebuilds the fixture corridor")) {
      return ok({ success: true, outOfDate: false }, receipt(args, { modified: { Corridor: 1 } }));
    }
    if (code.includes("adds one line and sets the alignment description")) {
      const handle = nextHandle();
      if (param(code, "failAfterChange")) {
        return failure("CIVIL3D.COMMAND_FAILED", "reported_error", receipt(args, { committed: false, rolledBack: true }));
      }
      const r = receipt(args, {
        added: { Line: 1 },
        modified: { Alignment: 1 },
        items: [{ type: "Line", handle }],
        rolledBack: args.preview ? true : undefined,
      });
      if (!args.preview) {
        const key = lineKey(code);
        civil.lines.set(key, (civil.lines.get(key) ?? 0) + 1);
        civil.description = param(code, "marker");
      }
      return ok({ success: true, lineHandle: handle }, r);
    }
    if (code.includes("changes nothing")) {
      return ok({ success: true }, receipt(args));
    }
    throw new Error(`unexpected write: ${code.slice(0, 80)}`);
  }

  function runQuery(args) {
    const code = args.code ?? "";
    if (code.includes("identity of the active drawing")) {
      return ok({
        databaseFilename: drawing,
        fingerprintGuid: "{F1}",
        dbmod: civil.dbmod,
        counts: { ...civil.counts },
        styles: {
          alignmentStyle: "Basic",
          alignmentLabelSet: "Major Minor",
          profileStyle: "Basic",
          profileLabelSet: "Complete",
          profileViewStyle: "Major Grids",
          profileViewBandSet: "Elevations",
          sectionStyle: "Basic",
        },
      });
    }
    if (code.includes("how many lines lie on the given segment")) {
      return ok({ lines: civil.lines.get(lineKey(code)) ?? 0, description: civil.description, dbmod: civil.dbmod });
    }
    if (code === "return 1;") return ok(1);
    switch (args.skill) {
      case "drawing_info":
        return ok({ name: "x" });
      case "list_alignments":
        return ok({ count: 1, alignments: [{ name: NAMES.alignment }] });
      case "list_surfaces":
        return ok({ surfaces: [{ name: NAMES.surface }] });
      case "list_profiles":
        return ok({ profiles: [{ name: NAMES.groundProfile }, { name: NAMES.designProfile }] });
      case "section_inventory":
        return ok({ total: 16 });
      case "surface_elevation":
        return ok({ elevation: groundElevation(args.params.x, args.params.y) });
      case "profile_elevation_at_station":
        return ok({ elevation: 100.8 });
      case "station_offset":
        return ok({ station: Math.hypot(130, 25), offset: 0 });
      case "corridor_summary":
        return ok({ corridors: [{ name: NAMES.corridor }] });
    }
    throw new Error(`unexpected query: ${JSON.stringify(args).slice(0, 120)}`);
  }

  function respond(name, args, wrongToken) {
    if (name === "civil3d_skills") {
      return ok({
        server: { commit: "abc" },
        instances: [{
          port: PORT, processId: 7, pluginBuild: { commit: "abc" }, buildMatchesServer: true,
          authorized: !wrongToken, tokenRequired: true, operationInProgress: false,
        }],
      });
    }
    if (wrongToken) return failure("CIVIL3D.UNAUTHORIZED", "not_started");
    if (args.expectedDrawing && args.expectedDrawing.databaseFilename !== drawing) {
      return failure("CIVIL3D.DRAWING_MISMATCH", "not_started");
    }
    if (name === "civil3d_query") return runQuery(args);
    if (args.idempotencyKey && civil.keys.has(args.idempotencyKey)) {
      return failure("CIVIL3D.IDEMPOTENCY_COMPLETED", "not_started");
    }
    if (args.requireClean && civil.dbmod !== 0) return failure("CIVIL3D.DRAWING_NOT_CLEAN", "not_started");
    if (args.idempotencyKey) civil.keys.add(args.idempotencyKey);
    return runWrite(args);
  }

  const makeClient = (wrongToken) => ({
    async callTool({ name, arguments: args }, _schema, options) {
      assert.ok(options?.timeout > 0, "every call carries a timeout");
      civil.calls.push({ name, args, wrongToken });
      if (throwOn && throwOn(name, args)) throw new Error("Request timed out");
      return respond(name, args, wrongToken);
    },
  });
  civil.client = makeClient(false);
  civil.wrongTokenClient = makeClient(true);
  return civil;
}

function run(civil, overrides = {}) {
  return runSmoke({
    client: civil.client,
    wrongTokenClient: civil.wrongTokenClient,
    scripts: SCRIPTS,
    port: PORT,
    expectedPath: FIXTURE,
    fileExists: (file) => civil.backups.includes(file),
    timeouts: { callMs: 1000, writeMs: 2000 },
    ...overrides,
  });
}

test("a full run against the simulated Civil 3D passes every case", async () => {
  const civil = simulatedCivil();
  const report = await run(civil);
  const failed = report.steps.filter((step) => step.status !== "pass");
  assert.deepEqual(failed, [], JSON.stringify(failed, null, 2));
  assert.equal(report.ok, true);
  assert.deepEqual(
    report.steps.map((step) => step.id),
    ["S1", "S2", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "R9",
      "W1", "W2", "W3", "W4", "W5", "W6", "W7", "W8", "G1", "G2", "E1"]
  );
  // Every write names the drawing and carries a description.
  for (const call of civil.calls.filter((item) => item.name === "civil3d_execute")) {
    assert.equal(call.args.expectedDrawing.databaseFilename, FIXTURE);
    assert.match(call.args.description, /^MCP smoke: /);
  }
  // Only the idempotency case sends a write a second time.
  const writes = civil.calls.filter((item) => item.name === "civil3d_execute").map((item) => JSON.stringify(item.args));
  const repeated = writes.filter((item, index) => writes.indexOf(item) !== index);
  assert.equal(repeated.length, 1);
  assert.match(repeated[0], /idempotencyKey/);
  const markdown = renderMarkdown(report, { "MCP server": "abc" });
  assert.match(markdown, /Result: \*\*pass\*\*/);
  assert.match(markdown, /\| W1 \| Preview/);
});

test("a drawing that is not the run's own fixture gets no write", async () => {
  const civil = simulatedCivil({ drawing: "M:\\Projekt\\terv.dwg" });
  const report = await run(civil);
  assert.equal(report.ok, false);
  assert.match(report.stopped, /not this run's mcp_smoke/);
  assert.equal(civil.calls.filter((item) => item.name === "civil3d_execute").length, 0);
  assert.ok(report.steps.slice(2).every((step) => step.status === "skip"));
});

test("an attached run accepts only a drawing named mcp_smoke*.dwg", async () => {
  const civil = simulatedCivil({ drawing: "C:\\Temp\\other.dwg" });
  const report = await run(civil, { expectedPath: undefined });
  assert.equal(civil.calls.filter((item) => item.name === "civil3d_execute").length, 0);
  assert.equal(report.steps.find((step) => step.id === "S2").status, "fail");
});

test("a call without reply stops the run and nothing is sent after it", async () => {
  const civil = simulatedCivil({ throwOn: (_name, args) => args.code?.includes("siteless alignment") });
  const report = await run(civil);
  assert.equal(report.ok, false);
  assert.match(report.stopped, /outcome is unknown/);
  const index = civil.calls.findIndex((item) => item.args.code?.includes("siteless alignment"));
  assert.equal(index, civil.calls.length - 1, "no call after the one without reply");
  assert.equal(report.steps.find((step) => step.id === "F3").status, "fail");
  assert.ok(report.steps.filter((step) => step.id > "F3" && step.id.startsWith("F")).every((step) => step.status === "skip"));
});

test("a corridor without the stock subassembly is a warning, not a failure", async () => {
  const civil = simulatedCivil({ corridorWarning: "Exception: not found" });
  const report = await run(civil);
  assert.equal(report.steps.find((step) => step.id === "F7").status, "warn");
  assert.equal(report.counts.fail, 0);
  assert.equal(report.ok, true);
});

test("script parameters are filled in exactly", () => {
  const body = 'var name = "X"; // smoke-param\nvar width = 0.0; // smoke-param\nvar on = false; // smoke-param\nreturn 1;';
  const filled = fillScript(body, { name: "MCP_Tengely", width: 3, on: true });
  assert.match(filled, /var name = "MCP_Tengely"; \/\/ smoke-param/);
  assert.match(filled, /var width = 3\.0;/);
  assert.match(filled, /var on = true;/);
  assert.throws(() => fillScript(body, { name: "a", width: 1 }), /'on' has no value/);
  assert.throws(() => fillScript(body, { name: "a", width: 1, on: true, extra: 1 }), /extra/);
  assert.throws(() => csharpLiteral('a"; System.IO.File.Delete("x'), /not a plain name/);
  assert.throws(() => csharpLiteral(Number.NaN), /finite/);
  for (const [name, source] of Object.entries(SCRIPTS)) {
    assert.match(source, /^\/\/ Live smoke test/, `${name}.cs starts with its purpose`);
  }
});

test("generated geometry stays inside the surface and the profile fits", () => {
  const points = surfacePoints();
  assert.equal(points.length, 189);
  assert.equal(groundElevation(1200, 2030), 103.6);
  assert.deepEqual(designPvis(0, 300), [[0, 100.8, 0], [150, 104.2, 60], [300, 106.1, 0]]);
});

test("Civil 3D start helpers", () => {
  const script = startupScript("C:\\Temp\\s\\mcp_smoke.dwg");
  assert.equal(script, 'FILEDIA\r\n0\r\n_.SAVEAS\r\n\r\n"C:\\Temp\\s\\mcp_smoke.dwg"\r\nFILEDIA\r\n1\r\n');
  assert.throws(() => startupScript('C:\\a"b.dwg'), /quotes/);
  const args = acadArguments({
    acadPath: "C:\\Program Files\\Autodesk\\AutoCAD 2025\\acad.exe",
    template: "C:\\t.dwt",
    profile: "C3D_Hungary",
    scriptPath: "C:\\Temp\\s\\start.scr",
  });
  assert.deepEqual(args, [
    "/t", "C:\\t.dwt", "/b", "C:\\Temp\\s\\start.scr",
    "/ld", "C:\\Program Files\\Autodesk\\AutoCAD 2025\\AecBase.dbx",
    "/p", "C3D_Hungary", "/product", "C3D",
  ]);
  const status = { instances: [
    { processId: 1, startedAtUtc: "2026-11-24T08:00:00Z" },
    { processId: 2, startedAtUtc: "2026-11-24T15:00:05Z" },
  ] };
  assert.equal(pickStartedInstance(status, 2, Date.parse("2026-11-24T15:00:00Z")).processId, 2);
  assert.equal(pickStartedInstance(status, 99, Date.parse("2026-11-24T15:00:00Z")).processId, 2);
  assert.equal(pickStartedInstance(status, 99, Date.parse("2026-11-24T16:00:00Z")), null);
  assert.equal(isFixturePath("C:\\x\\MCP_SMOKE.dwg"), true);
  assert.equal(isFixturePath("C:\\x\\terv.dwg"), false);
  assert.equal(isFixturePath("c:/temp/c3dmcp-smoke/20261124/mcp_smoke.dwg", FIXTURE), true);
});

test("reading a tool reply finds the result, the receipt and the error", () => {
  const read = readToolResult(failure("CIVIL3D.TIMEOUT", "unknown", { committed: false }));
  assert.equal(read.isError, true);
  assert.equal(read.error.outcome, "unknown");
  assert.equal(read.receipt.committed, false);
  const good = readToolResult(ok({ a: 1 }, { saved: true }));
  assert.deepEqual(good.data, { a: 1 });
  assert.equal(good.receipt.saved, true);
});
