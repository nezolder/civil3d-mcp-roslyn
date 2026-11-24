// Live smoke test: builds a small generated Civil 3D drawing through the MCP
// server and runs a fixed set of read and write cases on it. This module holds
// everything that does not touch the machine (no files, no processes), so that
// it can be tested without Civil 3D; scripts/live-smoke.mjs does the rest.

import path from "node:path";

export const REPORT_SCHEMA = "civil3d-mcp-live-smoke/v1";
/** The fixture drawing's file name always starts with this; nothing else is written to. */
export const FIXTURE_PREFIX = "mcp_smoke";

export const NAMES = Object.freeze({
  surface: "MCP_Ground",
  alignment: "MCP_Centerline",
  groundProfile: "MCP_Ground_profile",
  profileView: "MCP_Profile_view",
  designProfile: "MCP_Design_profile",
  sampleLineGroup: "MCP_Sample_lines",
  assembly: "MCP_Assembly",
  corridor: "MCP_Corridor",
});

/** Origin of the generated site, in drawing units. */
const ORIGIN = Object.freeze({ x: 1000, y: 2000 });

/**
 * Ground elevation of the generated surface. A plane, so that the TIN gives
 * back exactly this value anywhere inside the grid.
 */
export function groundElevation(x, y) {
  return round3(100 + 0.015 * (x - ORIGIN.x) + 0.02 * (y - ORIGIN.y));
}

/** Grid of (x, y, z) rows, 400 x 160 drawing units, 20 apart: 21 x 9 = 189 points. */
export function surfacePoints() {
  const rows = [];
  for (let dx = 0; dx <= 400; dx += 20) {
    for (let dy = -80; dy <= 80; dy += 20) {
      const x = ORIGIN.x + dx;
      const y = ORIGIN.y + dy;
      rows.push([x, y, groundElevation(x, y)]);
    }
  }
  return rows;
}

/** The centerline polyline: three straight legs inside the surface grid. */
export function centerlineVertices() {
  return [
    [ORIGIN.x + 20, ORIGIN.y, 0],
    [ORIGIN.x + 150, ORIGIN.y + 25, 0],
    [ORIGIN.x + 260, ORIGIN.y - 10, 0],
    [ORIGIN.x + 380, ORIGIN.y + 15, 0],
  ];
}

/** Design profile PVIs (station, elevation, curve length) for an alignment of this range. */
export function designPvis(startStation, endStation) {
  const length = endStation - startStation;
  const middle = startStation + length / 2;
  return [
    [round3(startStation), 100.8, 0],
    [round3(middle), 104.2, 60],
    [round3(endStation), 106.1, 0],
  ];
}

/** Probe line segments, outside the corridor and the surface. */
export const PROBE_LINE = Object.freeze({ x1: 900, y1: 1850, x2: 960, y2: 1850 });
export const DIRTY_LINE = Object.freeze({ x1: 900, y1: 1840, x2: 960, y2: 1840 });

function round3(value) {
  return Math.round(value * 1000) / 1000;
}

// ---------------------------------------------------------------------------
// C# bodies

const NAME_PATTERN = /^[\p{L}\p{N} _.\-()]{1,255}$/u;

/** A C# literal for a parameter value; strings are limited to plain names. */
export function csharpLiteral(value) {
  if (typeof value === "boolean") return value ? "true" : "false";
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new Error("A script parameter must be a finite number.");
    const text = String(value);
    return /[.eE]/.test(text) ? text : `${text}.0`;
  }
  if (typeof value === "string") {
    if (!NAME_PATTERN.test(value)) {
      throw new Error(`A script parameter is not a plain name: ${JSON.stringify(value)}`);
    }
    return `"${value}"`;
  }
  throw new Error("A script parameter must be a string, a number or a boolean.");
}

/**
 * Fills the lines marked "// smoke-param" of a script body. Every marked line
 * must get a value and every value must find its line, so a renamed variable
 * cannot slip through.
 */
export function fillScript(source, values) {
  const used = new Set();
  const filled = source.replace(
    /^(var\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*)([^;]+)(;\s*\/\/ smoke-param)$/gm,
    (_line, head, name, _literal, tail) => {
      if (!Object.prototype.hasOwnProperty.call(values, name)) {
        throw new Error(`Script parameter '${name}' has no value.`);
      }
      used.add(name);
      return `${head}${csharpLiteral(values[name])}${tail}`;
    }
  );
  const unused = Object.keys(values).filter((name) => !used.has(name));
  if (unused.length > 0) throw new Error(`No script line for parameter(s): ${unused.join(", ")}`);
  return filled;
}

// ---------------------------------------------------------------------------
// Civil 3D start

/** Path of the fixture drawing in the output folder. */
export function fixtureDrawingPath(outDir) {
  return path.win32.join(outDir, `${FIXTURE_PREFIX}.dwg`);
}

/**
 * Startup script for the first acad.exe start (/b): saves the new drawing at
 * once under the fixture name and quits. The run then opens that file in a
 * second start, the way a drawing is normally opened: in the first live run a
 * drawing named by SAVEAS in the same session failed its second saved write
 * (eFileAccessErr).
 */
export function startupScript(drawingPath) {
  if (/["\r\n]/.test(drawingPath)) throw new Error("The drawing path cannot hold quotes or line breaks.");
  return ["FILEDIA", "0", "_.SAVEAS", "", `"${drawingPath}"`, "FILEDIA", "1", "_.QUIT", ""].join("\r\n");
}

/**
 * acad.exe arguments: with scriptPath a new drawing from the template running
 * the startup script, with drawing that existing file opened.
 */
export function acadArguments({ acadPath, template, profile, language, scriptPath, drawing }) {
  const args = [];
  if (drawing) {
    args.push(drawing);
  } else {
    if (template) args.push("/t", template);
    args.push("/b", scriptPath);
  }
  args.push("/ld", path.win32.join(path.win32.dirname(acadPath), "AecBase.dbx"));
  if (profile) args.push("/p", profile);
  args.push("/product", "C3D");
  if (language) args.push("/language", language);
  return args;
}

/**
 * Picks the Civil 3D instance the run talks to from a status reply: the one
 * this run started (by process id), or else the only one started after it.
 */
export function pickStartedInstance(status, processId, launchedAtMs) {
  const instances = Array.isArray(status?.instances) ? status.instances : [];
  const byProcess = instances.filter((instance) => instance.processId === processId);
  if (byProcess.length === 1) return byProcess[0];
  const newer = instances.filter((instance) => {
    const started = Date.parse(instance.startedAtUtc ?? "");
    return Number.isFinite(started) && started >= launchedAtMs - 1000;
  });
  return newer.length === 1 ? newer[0] : null;
}

export function samePath(a, b) {
  if (typeof a !== "string" || typeof b !== "string") return false;
  const norm = (value) => path.win32.normalize(value).toLowerCase();
  return norm(a) === norm(b);
}

/** Whether a drawing path can be the fixture of this run: named mcp_smoke*.dwg. */
export function isFixturePath(drawingPath, expectedPath) {
  if (typeof drawingPath !== "string" || drawingPath.length === 0) return false;
  if (expectedPath) return samePath(drawingPath, expectedPath);
  const base = path.win32.basename(drawingPath).toLowerCase();
  return base.startsWith(FIXTURE_PREFIX) && base.endsWith(".dwg");
}

// ---------------------------------------------------------------------------
// Tool results

/** Thrown to stop the whole run; no later call is sent. */
export class StopRun extends Error {
  constructor(reason) {
    super(reason);
    this.name = "StopRun";
  }
}

/** Reads a tool reply: the result, the receipt and the structured error. */
export function readToolResult(result) {
  const blocks = Array.isArray(result?.content) ? result.content : [];
  const texts = blocks.filter((block) => block?.type === "text").map((block) => block.text ?? "");
  let data;
  let receipt = result?.structuredContent?.receipt;
  for (const [index, text] of texts.entries()) {
    let parsed;
    try {
      parsed = JSON.parse(text);
    } catch {
      continue;
    }
    if (index > 0 && parsed && typeof parsed === "object" && "receipt" in parsed) {
      receipt = receipt ?? parsed.receipt;
    } else if (index === 0) {
      data = parsed;
    }
  }
  return {
    isError: result?.isError === true,
    data,
    text: texts[0] ?? "",
    receipt,
    error: result?.structuredContent?.error,
  };
}

function check(condition, message, problems) {
  if (!condition) problems.push(message);
}

// ---------------------------------------------------------------------------
// The run

/**
 * Runs the fixture build and the cases in a fixed order and returns the report.
 *
 * deps.client          MCP client pinned to the test instance (callTool).
 * deps.wrongTokenClient optional; an MCP client that presents a wrong token.
 * deps.scripts         { name: C# body } from scripts/live-smoke/csharp.
 * deps.fileExists      (path) => boolean, for the backup copy.
 * deps.expectedPath    the fixture's full path when this run made it.
 * deps.port            the pinned port.
 * deps.timeouts        { callMs, writeMs }.
 * deps.now             () => ms; deps.log (line) => void.
 *
 * A call whose outcome is unknown (timeout, lost connection) stops the run at
 * once. No call is ever sent twice, except the one idempotency case, which
 * sends the same save on purpose and expects the refusal.
 */
export async function runSmoke(deps) {
  const now = deps.now ?? (() => Date.now());
  const log = deps.log ?? (() => {});
  const startedAt = now();
  const steps = [];
  const state = { instance: null, expectedDrawing: null, handles: {}, styles: {}, corridor: false, marker: null };
  let stopReason = null;

  async function callTool(client, name, args, timeoutMs) {
    let raw;
    try {
      raw = await client.callTool({ name, arguments: args }, undefined, {
        timeout: timeoutMs,
        maxTotalTimeout: timeoutMs,
      });
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      throw new StopRun(`${name}: no reply (${message}); the outcome is unknown, nothing more is sent.`);
    }
    const result = readToolResult(raw);
    if (result.isError && result.error?.outcome === "unknown") {
      throw new StopRun(
        `${name}: ${result.error.code ?? "error"} with unknown outcome; nothing more is sent.`
      );
    }
    return result;
  }

  const query = (args, client = deps.client) =>
    callTool(client, "civil3d_query", args, deps.timeouts.callMs);
  const execute = (args) =>
    callTool(deps.client, "civil3d_execute", { changeItems: 20, ...args }, deps.timeouts.writeMs);
  const guarded = (args) => ({ expectedDrawing: state.expectedDrawing, ...args });
  const script = (name, values = {}) => fillScript(deps.scripts[name], values);

  async function step(id, title, body, { optional = false } = {}) {
    if (stopReason) {
      steps.push({ id, title, status: "skip", problems: [], details: { reason: "the run stopped earlier" } });
      return null;
    }
    const begin = now();
    const problems = [];
    const details = {};
    let value = null;
    try {
      value = await body({ problems, details });
    } catch (error) {
      if (error instanceof StopRun) {
        problems.push(error.message);
        stopReason = error.message;
      } else {
        problems.push(error instanceof Error ? error.message : String(error));
      }
    }
    const failed = problems.length > 0;
    const status = failed ? (optional && !stopReason ? "warn" : "fail") : "pass";
    steps.push({ id, title, status, ms: Math.round(now() - begin), problems, details });
    log(`${status.toUpperCase().padEnd(4)} ${id} ${title}${failed ? `: ${problems.join(" | ")}` : ""}`);
    return failed ? null : value ?? true;
  }

  /** A step that the rest of the run depends on: its failure stops the run. */
  async function required(id, title, body) {
    const value = await step(id, title, body);
    if (value === null && !stopReason) stopReason = `${id} failed; the later cases need it.`;
    return value;
  }

  function expectWrite(result, problems, { saved }) {
    if (result.isError) {
      problems.push(`error ${result.error?.code ?? ""}: ${result.text}`.trim());
      return false;
    }
    const receipt = result.receipt;
    check(receipt?.committed === true, "receipt.committed is not true", problems);
    if (saved) {
      check(receipt?.saved === true, "receipt.saved is not true", problems);
      check(receipt?.dbmodAfter === 0, `receipt.dbmodAfter is ${receipt?.dbmodAfter}, not 0`, problems);
    }
    if (result.data && typeof result.data === "object" && result.data.success === false) {
      problems.push(`the script reported: ${result.data.error ?? "success false"}`);
    }
    return problems.length === 0;
  }

  const writeSwitches = { requireClean: true, saveDrawing: true };

  // --- Connection and target drawing ------------------------------------
  await required("S1", "Connection and build (status)", async ({ problems, details }) => {
    const result = await callTool(
      deps.client,
      "civil3d_skills",
      { action: "status" },
      deps.timeouts.callMs
    );
    const status = result.data;
    const instance = (status?.instances ?? []).find((item) => item.port === deps.port);
    details.serverCommit = status?.server?.commit;
    details.pluginCommit = instance?.pluginBuild?.commit;
    check(instance, `no instance on port ${deps.port}`, problems);
    if (!instance) return;
    state.instance = instance;
    check(instance.authorized === true, "the plugin did not accept the server's token", problems);
    check(instance.tokenRequired === true, "the plugin runs requests without a token", problems);
    check(instance.operationInProgress !== true, "an operation is running in this instance", problems);
  });

  // A development run uses a server built from a branch next to the installed
  // plugin, so differing builds are a warning here, not a stop.
  await step(
    "S1b",
    "Plugin and server from the same build",
    async ({ problems, details }) => {
      details.pluginCommit = state.instance?.pluginBuild?.commit;
      check(state.instance?.buildMatchesServer === true, "plugin and server builds differ", problems);
    },
    { optional: true }
  );

  await required("S2", "Target drawing identified and still empty", async ({ problems, details }) => {
    const result = await query({ code: script("discover") });
    if (result.isError) throw new Error(`discover failed: ${result.text}`);
    const found = result.data;
    details.drawing = found?.databaseFilename;
    details.counts = found?.counts;
    details.styles = found?.styles;
    details.drawingUnits = found?.drawingUnits;
    if (!isFixturePath(found?.databaseFilename, deps.expectedPath)) {
      throw new StopRun(
        `The active drawing (${found?.databaseFilename}) is not this run's ${FIXTURE_PREFIX}*.dwg; nothing is written.`
      );
    }
    const counts = found.counts ?? {};
    if (counts.surfaces || counts.alignments || counts.corridors || counts.assemblies) {
      throw new StopRun("The fixture drawing already holds Civil 3D objects; use a fresh one.");
    }
    if (found.dbmod !== 0) {
      throw new StopRun(`The fixture drawing has unsaved changes (DBMOD ${found.dbmod}).`);
    }
    for (const key of ["alignmentStyle", "alignmentLabelSet", "profileStyle", "profileLabelSet", "profileViewStyle", "profileViewBandSet"]) {
      check(typeof found.styles?.[key] === "string", `the drawing has no ${key}`, problems);
    }
    state.styles = found.styles ?? {};
    state.expectedDrawing = {
      databaseFilename: found.databaseFilename,
      fingerprintGuid: found.fingerprintGuid,
    };
  });

  // --- Fixture -------------------------------------------------------------
  await required("F1", "Ground surface (create_tin_surface)", async ({ problems, details }) => {
    const points = surfacePoints();
    const result = await execute(guarded({
      ...writeSwitches,
      skill: "create_tin_surface",
      params: { surfaceName: NAMES.surface, points, description: "MCP live smoke test" },
      description: "MCP smoke: ground surface",
    }));
    if (!expectWrite(result, problems, { saved: true })) return;
    state.handles.surface = result.data?.handle;
    details.handle = state.handles.surface;
    details.added = result.receipt?.changes?.added?.byType;
    check(typeof state.handles.surface === "string", "no surface handle in the result", problems);
  });

  await required("F2", "Centerline polyline (create_polyline)", async ({ problems }) => {
    const result = await execute(guarded({
      ...writeSwitches,
      skill: "create_polyline",
      params: { vertices: centerlineVertices() },
      description: "MCP smoke: centerline polyline",
    }));
    if (!expectWrite(result, problems, { saved: true })) return;
    state.handles.polyline = result.data?.handle;
    check(typeof state.handles.polyline === "string", "no polyline handle in the result", problems);
  });

  await required("F3", "Alignment from the polyline", async ({ problems, details }) => {
    const result = await execute(guarded({
      ...writeSwitches,
      code: script("create_alignment", {
        polylineHandle: state.handles.polyline,
        alignmentName: NAMES.alignment,
        styleName: state.styles.alignmentStyle,
        labelSetName: state.styles.alignmentLabelSet,
      }),
      description: "MCP smoke: alignment",
    }));
    if (!expectWrite(result, problems, { saved: true })) return;
    state.handles.alignment = result.data?.handle;
    state.alignment = result.data;
    details.length = result.data?.length;
    details.entityCount = result.data?.entityCount;
    check(result.data?.entityCount === 3, `the alignment has ${result.data?.entityCount} elements, not 3`, problems);
  });

  await required("F4", "Ground profile and profile view (create_surface_profile_view)", async ({ problems, details }) => {
    const result = await execute(guarded({
      ...writeSwitches,
      skill: "create_surface_profile_view",
      params: {
        alignmentHandle: state.handles.alignment,
        surfaceHandle: state.handles.surface,
        profileName: NAMES.groundProfile,
        profileViewName: NAMES.profileView,
        insertionX: 1000,
        insertionY: 2300,
        profileStyleName: state.styles.profileStyle,
        profileViewStyleName: state.styles.profileViewStyle,
        profileLabelSetName: state.styles.profileLabelSet,
        bandSetStyleName: state.styles.profileViewBandSet,
      },
      description: "MCP smoke: ground profile",
    }));
    if (!expectWrite(result, problems, { saved: true })) return;
    details.added = result.receipt?.changes?.added?.byType;
  });

  await required("F5", "Design profile (create_design_profile_from_pvis)", async ({ problems }) => {
    const result = await execute(guarded({
      ...writeSwitches,
      skill: "create_design_profile_from_pvis",
      params: {
        alignmentHandle: state.handles.alignment,
        profileName: NAMES.designProfile,
        profileStyleName: state.styles.profileStyle,
        profileLabelSetName: state.styles.profileLabelSet,
        pviData: designPvis(state.alignment.startStation, state.alignment.endStation),
      },
      description: "MCP smoke: design profile",
    }));
    if (!expectWrite(result, problems, { saved: true })) return;
    state.handles.designProfile = result.data?.handle;
    check(typeof state.handles.designProfile === "string", "no profile handle in the result", problems);
  });

  await required("F6", "Sample lines every 20 units (create_sample_lines)", async ({ problems, details }) => {
    const result = await execute(guarded({
      ...writeSwitches,
      skill: "create_sample_lines",
      params: {
        alignmentHandle: state.handles.alignment,
        groupName: NAMES.sampleLineGroup,
        leftWidth: 30,
        rightWidth: 30,
        interval: 20,
        sampledSources: [[state.handles.surface, state.styles.sectionStyle ?? ""]],
      },
      description: "MCP smoke: sample lines",
    }));
    if (!expectWrite(result, problems, { saved: true })) return;
    details.created = result.data?.createdCount ?? result.data?.created?.length;
  });

  await step(
    "F7",
    "Assembly and corridor (BasicLane, rebuilt)",
    async ({ problems, details }) => {
      const result = await execute(guarded({
        ...writeSwitches,
        code: script("create_corridor", {
          alignmentHandle: state.handles.alignment,
          profileHandle: state.handles.designProfile,
          assemblyName: NAMES.assembly,
          corridorName: NAMES.corridor,
          assemblyX: 900,
          assemblyY: 2150,
        }),
        description: "MCP smoke: corridor",
      }));
      if (!expectWrite(result, problems, { saved: true })) return;
      state.corridor = true;
      state.handles.corridor = result.data?.corridorHandle;
      details.subassemblies = result.data?.subassemblies;
      details.subassemblyWarning = result.data?.subassemblyWarning;
      details.outOfDate = result.data?.outOfDate;
      details.added = result.receipt?.changes?.added?.byType;
      if (result.data?.subassemblyWarning) {
        problems.push(`no stock subassembly: ${result.data.subassemblyWarning}`);
      }
    },
    { optional: true }
  );

  // --- Reading -------------------------------------------------------------
  async function readRecipe(id, title, skill, params, verify) {
    await step(id, title, async ({ problems, details }) => {
      const result = await query(guarded({ skill, params }));
      if (result.isError) {
        problems.push(`error ${result.error?.code ?? ""}: ${result.text}`.trim());
        return;
      }
      if (result.data && typeof result.data === "object" && "error" in result.data && result.data.error) {
        problems.push(`the recipe reported: ${result.data.error}`);
        return;
      }
      verify(result.data, problems, details);
    });
  }

  await readRecipe("R1", "Drawing info (drawing_info)", "drawing_info", {}, (data, problems) => {
    check(data && typeof data === "object", "empty reply", problems);
  });
  await readRecipe("R2", "Alignments (list_alignments)", "list_alignments", {}, (data, problems) => {
    check(data?.alignments?.some((item) => item.name === NAMES.alignment), "the alignment is not listed", problems);
  });
  await readRecipe("R3", "Surfaces (list_surfaces)", "list_surfaces", {}, (data, problems) => {
    check(JSON.stringify(data).includes(NAMES.surface), "the surface is not listed", problems);
  });
  await readRecipe("R4", "Profiles (list_profiles)", "list_profiles", { alignmentName: NAMES.alignment }, (data, problems) => {
    const text = JSON.stringify(data);
    check(text.includes(NAMES.groundProfile), "the ground profile is not listed", problems);
    check(text.includes(NAMES.designProfile), "the design profile is not listed", problems);
  });
  await readRecipe("R5", "Sample lines (section_inventory)", "section_inventory", { alignmentName: NAMES.alignment }, (data, problems, details) => {
    details.total = data?.total;
    check(data?.total >= 10, `only ${data?.total} sample lines`, problems);
  });
  const probe = { x: 1200, y: 2030 };
  await readRecipe("R6", "Ground elevation at a point (surface_elevation)", "surface_elevation", { surfaceName: NAMES.surface, ...probe }, (data, problems, details) => {
    const expected = groundElevation(probe.x, probe.y);
    details.elevation = data?.elevation;
    details.expected = expected;
    check(Math.abs((data?.elevation ?? NaN) - expected) < 0.001, `elevation ${data?.elevation}, expected ${expected}`, problems);
  });
  await readRecipe("R7", "Design elevation at the start (profile_elevation_at_station)", "profile_elevation_at_station", {
    alignmentName: NAMES.alignment,
    profileName: NAMES.designProfile,
    station: state.alignment?.startStation ?? 0,
  }, (data, problems, details) => {
    details.elevation = data?.elevation;
    check(Math.abs((data?.elevation ?? NaN) - 100.8) < 0.001, `elevation ${data?.elevation}, expected 100.8`, problems);
  });
  const [first, second] = centerlineVertices();
  await readRecipe("R8", "Station and offset of a vertex (station_offset)", "station_offset", {
    alignmentName: NAMES.alignment,
    x: second[0],
    y: second[1],
  }, (data, problems, details) => {
    const expected = (state.alignment?.startStation ?? 0) + Math.hypot(second[0] - first[0], second[1] - first[1]);
    details.station = data?.station;
    details.offset = data?.offset;
    check(Math.abs((data?.station ?? NaN) - expected) < 0.01, `station ${data?.station}, expected ${round3(expected)}`, problems);
    check(Math.abs(data?.offset ?? NaN) < 0.01, `offset ${data?.offset}, expected 0`, problems);
  });
  if (state.corridor) {
    await readRecipe("R9", "Corridor (corridor_summary)", "corridor_summary", { corridorName: NAMES.corridor }, (data, problems, details) => {
      const text = JSON.stringify(data);
      details.summary = text.length > 400 ? `${text.slice(0, 400)}...` : text;
      check(text.includes(NAMES.corridor), "the corridor is not listed", problems);
    });
  }

  // --- Writing -------------------------------------------------------------
  const probeValues = (line, marker, fail) => ({
    alignmentHandle: state.handles.alignment,
    marker,
    ...line,
    failAfterChange: fail,
  });
  const readProbe = async (line) => {
    const result = await query(guarded({
      code: script("probe_read", { alignmentHandle: state.handles.alignment, ...line }),
    }));
    if (result.isError) throw new Error(`readback failed: ${result.text}`);
    return result.data;
  };

  await required("W1", "Preview: nothing stays, the drawing stays clean", async ({ problems, details }) => {
    const result = await execute(guarded({
      ...writeSwitches,
      backup: true,
      preview: true,
      code: script("probe_write", probeValues(PROBE_LINE, "smoke-preview", false)),
      description: "MCP smoke: preview",
    }));
    if (result.isError) {
      problems.push(`error ${result.error?.code ?? ""}: ${result.text}`.trim());
      return;
    }
    const receipt = result.receipt ?? {};
    const changes = receipt.changes ?? {};
    details.changes = { added: changes.added?.byType, modified: changes.modified?.byType, rolledBack: changes.rolledBack };
    check(receipt.preview === true, "receipt.preview is not true", problems);
    check(receipt.committed === false, "receipt.committed is not false", problems);
    check(receipt.saved !== true, "a preview saved the drawing", problems);
    check(!receipt.backupPath, "a preview made a backup", problems);
    check(changes.rolledBack === true, "changes.rolledBack is not true", problems);
    check(changes.added?.byType?.Line === 1, "the preview does not list the one new line", problems);
    check((changes.modified?.count ?? 0) >= 1, "the preview does not list the changed alignment", problems);
    check(receipt.dbmodAfter === 0, `dbmodAfter is ${receipt.dbmodAfter}, not 0`, problems);
    const after = await readProbe(PROBE_LINE);
    details.after = after;
    check(after.lines === 0, `${after.lines} probe lines stayed in the drawing`, problems);
    check(after.description !== "smoke-preview", "the alignment description stayed changed", problems);
    check(after.dbmod === 0, `DBMOD is ${after.dbmod} after the preview`, problems);
  });

  await required("W2", "Real write with save and backup", async ({ problems, details }) => {
    state.marker = `smoke-${new Date(startedAt).toISOString().slice(0, 19).replace(/[:T]/g, "-")}`;
    const result = await execute(guarded({
      ...writeSwitches,
      backup: true,
      code: script("probe_write", probeValues(PROBE_LINE, state.marker, false)),
      description: "MCP smoke: real write",
    }));
    if (!expectWrite(result, problems, { saved: true })) return;
    const receipt = result.receipt ?? {};
    const changes = receipt.changes ?? {};
    details.backupPath = receipt.backupPath;
    details.changes = { added: changes.added?.byType, modified: changes.modified?.byType };
    check(typeof receipt.backupPath === "string" && deps.fileExists(receipt.backupPath), "the backup copy is missing", problems);
    check(changes.added?.byType?.Line === 1, "the receipt does not list the one new line", problems);
    check(
      (changes.added?.items ?? []).some((item) => item.handle === result.data?.lineHandle),
      "the receipt does not name the new line's handle",
      problems
    );
    check((changes.modified?.byType?.Alignment ?? 0) >= 1, "the receipt does not list the changed alignment", problems);
    const after = await readProbe(PROBE_LINE);
    details.after = after;
    check(after.lines === 1, `${after.lines} probe lines, expected 1`, problems);
    check(after.description === state.marker, "the alignment description was not kept", problems);
  });

  await step("W3", "Failing script: rolled back, the drawing stays clean", async ({ problems, details }) => {
    const result = await execute(guarded({
      ...writeSwitches,
      code: script("probe_write", probeValues(PROBE_LINE, "smoke-failed", true)),
      description: "MCP smoke: planned failure",
    }));
    details.code = result.error?.code;
    details.outcome = result.error?.outcome;
    details.changes = result.receipt?.changes;
    check(result.isError, "the failing script was reported as success", problems);
    check(result.error?.outcome === "reported_error", `outcome ${result.error?.outcome}, expected reported_error`, problems);
    check(result.receipt?.committed !== true, "the failed write was committed", problems);
    check(result.receipt?.changes?.rolledBack === true, "changes.rolledBack is not true", problems);
    const after = await readProbe(PROBE_LINE);
    details.after = after;
    check(after.lines === 1, `${after.lines} probe lines, expected 1`, problems);
    check(after.description === state.marker, "the alignment description changed", problems);
    check(after.dbmod === 0, `DBMOD is ${after.dbmod} after the failed script`, problems);
  });

  await required("W4", "Write without save: the drawing becomes modified", async ({ problems, details }) => {
    const result = await execute(guarded({
      requireClean: true,
      code: script("probe_write", probeValues(DIRTY_LINE, state.marker, false)),
      description: "MCP smoke: write without save",
    }));
    if (!expectWrite(result, problems, { saved: false })) return;
    details.dbmodAfter = result.receipt?.dbmodAfter;
    check(result.receipt?.saved !== true, "the write saved without saveDrawing", problems);
    check((result.receipt?.dbmodAfter ?? 0) !== 0, "DBMOD stayed 0 after an unsaved write", problems);
  });

  await step("W5", "requireClean refuses a modified drawing", async ({ problems, details }) => {
    const result = await execute(guarded({
      ...writeSwitches,
      code: script("save_only"),
      description: "MCP smoke: refusal expected",
    }));
    details.code = result.error?.code;
    details.outcome = result.error?.outcome;
    check(result.error?.code === "CIVIL3D.DRAWING_NOT_CLEAN", `code ${result.error?.code}, expected CIVIL3D.DRAWING_NOT_CLEAN`, problems);
    check(result.error?.outcome === "not_started", `outcome ${result.error?.outcome}, expected not_started`, problems);
  });

  const idempotencyKey = `mcp-smoke-${startedAt}`;
  await required("W6", "Save: the drawing is clean again", async ({ problems, details }) => {
    const result = await execute(guarded({
      requireClean: false,
      saveDrawing: true,
      idempotencyKey,
      code: script("save_only"),
      description: "MCP smoke: save",
    }));
    if (!expectWrite(result, problems, { saved: true })) return;
    details.changes = result.receipt?.changes?.added?.count ?? 0;
  });

  await step("W7", "Same idempotency key again: not run", async ({ problems, details }) => {
    const result = await execute(guarded({
      requireClean: false,
      saveDrawing: true,
      idempotencyKey,
      code: script("save_only"),
      description: "MCP smoke: save",
    }));
    details.code = result.error?.code;
    check(result.error?.code === "CIVIL3D.IDEMPOTENCY_COMPLETED", `code ${result.error?.code}, expected CIVIL3D.IDEMPOTENCY_COMPLETED`, problems);
    check(result.error?.outcome === "not_started", `outcome ${result.error?.outcome}, expected not_started`, problems);
  });

  if (state.corridor) {
    await step("W8", "Corridor rebuild with save", async ({ problems, details }) => {
      const result = await execute(guarded({
        ...writeSwitches,
        code: script("rebuild_corridor", { corridorHandle: state.handles.corridor }),
        description: "MCP smoke: corridor rebuild",
      }));
      if (!expectWrite(result, problems, { saved: true })) return;
      details.modified = result.receipt?.changes?.modified?.byType;
      details.outOfDate = result.data?.outOfDate;
    });
  }

  // --- Guards ----------------------------------------------------------------
  await step("G1", "Wrong target drawing: nothing runs", async ({ problems, details }) => {
    const wrong = {
      databaseFilename: state.expectedDrawing.databaseFilename.replace(/\.dwg$/i, "_masik.dwg"),
      fingerprintGuid: state.expectedDrawing.fingerprintGuid,
    };
    const result = await query({ expectedDrawing: wrong, code: "return 1;" });
    details.code = result.error?.code;
    check(result.error?.code === "CIVIL3D.DRAWING_MISMATCH", `code ${result.error?.code}, expected CIVIL3D.DRAWING_MISMATCH`, problems);
    check(result.error?.outcome === "not_started", `outcome ${result.error?.outcome}, expected not_started`, problems);
  });

  if (deps.wrongTokenClient) {
    await step("G2", "Wrong session token: nothing runs", async ({ problems, details }) => {
      const result = await query(guarded({ code: "return 1;" }), deps.wrongTokenClient);
      details.code = result.error?.code;
      check(result.error?.code === "CIVIL3D.UNAUTHORIZED", `code ${result.error?.code}, expected CIVIL3D.UNAUTHORIZED`, problems);
      check(result.error?.outcome === "not_started", `outcome ${result.error?.outcome}, expected not_started`, problems);
    });
  }

  await step("E1", "Final state", async ({ problems, details }) => {
    const result = await query(guarded({ code: script("discover") }));
    if (result.isError) throw new Error(`final read failed: ${result.text}`);
    const found = result.data;
    details.counts = found?.counts;
    check(found?.dbmod === 0, `DBMOD is ${found?.dbmod} at the end`, problems);
    check(samePath(found?.databaseFilename, state.expectedDrawing.databaseFilename), "the active drawing changed", problems);
    check(found?.counts?.surfaces === 1 && found?.counts?.alignments === 1, "surface or alignment count is off", problems);
    check(found?.counts?.corridors === (state.corridor ? 1 : 0), "corridor count is off", problems);
  });

  const counts = { pass: 0, warn: 0, fail: 0, skip: 0 };
  for (const item of steps) counts[item.status] += 1;
  return {
    schema: REPORT_SCHEMA,
    startedAtUtc: new Date(startedAt).toISOString(),
    durationMs: Math.round(now() - startedAt),
    drawing: state.expectedDrawing?.databaseFilename ?? null,
    port: deps.port,
    ok: counts.fail === 0 && counts.skip === 0 && !stopReason,
    stopped: stopReason,
    counts,
    steps,
  };
}

// ---------------------------------------------------------------------------
// Report

const STATUS_LABEL = { pass: "pass", warn: "warning", fail: "FAIL", skip: "skipped" };

export function renderMarkdown(report, extra = {}) {
  const lines = [];
  lines.push("# MCP live smoke test");
  lines.push("");
  lines.push(`- Result: **${report.ok ? "pass" : "FAIL"}** (${report.counts.pass} pass, ${report.counts.warn} warning, ${report.counts.fail} fail, ${report.counts.skip} skipped)`);
  if (report.stopped) lines.push(`- Stopped: ${report.stopped}`);
  lines.push(`- Drawing: \`${report.drawing ?? "-"}\``);
  lines.push(`- Port: ${report.port}; started: ${report.startedAtUtc}; duration: ${(report.durationMs / 1000).toFixed(1)} s`);
  for (const [key, value] of Object.entries(extra)) lines.push(`- ${key}: ${value}`);
  lines.push("");
  lines.push("| Case | What | Status | Time (s) | Notes |");
  lines.push("| --- | --- | --- | --- | --- |");
  for (const item of report.steps) {
    const note = item.problems.join("; ").replace(/\|/g, "/").replace(/\r?\n/g, " ");
    const seconds = typeof item.ms === "number" ? (item.ms / 1000).toFixed(1) : "-";
    lines.push(`| ${item.id} | ${item.title} | ${STATUS_LABEL[item.status]} | ${seconds} | ${note} |`);
  }
  lines.push("");
  return lines.join("\n");
}
