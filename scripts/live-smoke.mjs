// Live smoke test with one command. Builds a generated test drawing in a
// Civil 3D instance through this checkout's MCP server (build/index.js) and
// runs fixed read and write cases on it. See docs/LIVE_SMOKE.md.
//
//   npm run smoke:live -- --start-civil --template "<dwt>"
//   npm run smoke:live -- --port <port>          (a Civil 3D you prepared)
//
// It writes only to its own drawing (mcp_smoke*.dwg, still empty of Civil 3D
// objects when the run starts), never installs anything and never closes
// Civil 3D.

import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import {
  acadArguments,
  fixtureDrawingPath,
  isFixturePath,
  pickStartedInstance,
  readToolResult,
  renderMarkdown,
  runSmoke,
  samePath,
  startupScript,
} from "./live-smoke/lib.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const DEFAULT_ACAD = "C:\\Program Files\\Autodesk\\AutoCAD 2025\\acad.exe";
const WRONG_TOKEN = "mcp-smoke-wrong-token-00000000000000000000";
const USAGE = `Usage:
  node scripts/live-smoke.mjs --start-civil [--template <dwt>] [--acad <acad.exe>]
                              [--profile <name>] [--language <code>] [--out <folder>]
  node scripts/live-smoke.mjs --port <port> [--drawing <full path of mcp_smoke*.dwg>] [--out <folder>]
Options: --wait-minutes <n> (default 10)`;

function parseArgs(argv) {
  const options = { startCivil: false, waitMinutes: 10 };
  const takesValue = new Set(["--template", "--acad", "--profile", "--language", "--out", "--port", "--drawing", "--wait-minutes"]);
  for (let index = 0; index < argv.length; index++) {
    const name = argv[index];
    if (name === "--start-civil") {
      options.startCivil = true;
      continue;
    }
    if (name === "--help" || name === "-h") {
      options.help = true;
      continue;
    }
    if (!takesValue.has(name) || index + 1 >= argv.length) throw new Error(`Unknown or incomplete option: ${name}`);
    const value = argv[++index];
    const key = name.slice(2).replace(/-([a-z])/g, (_m, letter) => letter.toUpperCase());
    options[key] = value;
  }
  if (options.port !== undefined) {
    const port = Number(options.port);
    if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error("--port must be a port number.");
    options.port = port;
  }
  options.waitMinutes = Number(options.waitMinutes);
  if (!(options.waitMinutes > 0 && options.waitMinutes <= 60)) throw new Error("--wait-minutes must be 1 to 60.");
  if (options.startCivil === (options.port !== undefined)) {
    throw new Error("Give either --start-civil or --port.");
  }
  if (options.drawing && !isFixturePath(options.drawing)) {
    throw new Error("--drawing must be a mcp_smoke*.dwg file.");
  }
  return options;
}

function stamp() {
  return new Date().toISOString().slice(0, 19).replace(/[:T]/g, "").replace(/-/g, "");
}

async function connect(env) {
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [path.join(repositoryRoot, "build", "index.js")],
    cwd: repositoryRoot,
    env: { ...process.env, LOG_LEVEL: "error", ...env },
  });
  const client = new Client({ name: "civil3d-mcp-live-smoke", version: "1.0.0" });
  await client.connect(transport);
  return client;
}

async function status(client) {
  const result = await client.callTool({ name: "civil3d_skills", arguments: { action: "status" } });
  return readToolResult(result).data;
}

const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Finds the template Civil 3D ships when none is given. */
function defaultTemplate() {
  const base = path.join(process.env.LOCALAPPDATA ?? "", "Autodesk", "C3D 2025");
  if (!fs.existsSync(base)) return null;
  for (const language of fs.readdirSync(base)) {
    const candidate = path.join(base, language, "Template", "_AutoCAD Civil 3D (Metric) NCS.dwt");
    if (fs.existsSync(candidate)) return candidate;
  }
  return null;
}

/** Starts Civil 3D on a new drawing saved at once as the fixture; returns its instance. */
async function startCivil(options, outDir, drawingPath) {
  if (process.platform !== "win32") throw new Error("--start-civil works on Windows only.");
  const acadPath = options.acad ?? process.env.CIVIL3D_ACAD_EXE ?? DEFAULT_ACAD;
  if (!fs.existsSync(acadPath)) throw new Error(`acad.exe was not found: ${acadPath} (use --acad).`);
  const template = options.template ?? defaultTemplate();
  if (!template || !fs.existsSync(template)) {
    throw new Error("No template was found; give one with --template.");
  }
  const scriptPath = path.join(outDir, "mcp_smoke_start.scr");
  fs.writeFileSync(scriptPath, startupScript(drawingPath), "utf8");
  const args = acadArguments({ acadPath, template, profile: options.profile, language: options.language, scriptPath });
  console.log(`Starting Civil 3D: ${acadPath} ${args.map((arg) => JSON.stringify(arg)).join(" ")}`);
  const launchedAt = Date.now();
  const child = spawn(acadPath, args, { detached: true, stdio: "ignore", windowsHide: false });
  child.unref();
  console.log("Once its window is open, click into it once: the first command can wait for that.");

  const client = await connect({});
  try {
    const deadline = launchedAt + options.waitMinutes * 60_000;
    let instance = null;
    while (Date.now() < deadline) {
      const current = await status(client);
      instance = pickStartedInstance(current, child.pid, launchedAt);
      if (instance && fs.existsSync(drawingPath)) {
        const active = instance.activeDrawing?.databaseFilename;
        if (!instance.activeDrawingKnown || samePath(active, drawingPath)) return { instance, template, acadPath };
      }
      await pause(5_000);
    }
    throw new Error(
      instance
        ? `Civil 3D started (port ${instance.port}) but the fixture drawing was not saved in time: ${drawingPath}`
        : "The started Civil 3D did not register its MCP plugin in time."
    );
  } finally {
    await client.close();
  }
}

async function main() {
  let options;
  try {
    options = parseArgs(process.argv.slice(2));
  } catch (error) {
    console.error(`${error.message}\n${USAGE}`);
    return 2;
  }
  if (options.help) {
    console.log(USAGE);
    return 0;
  }
  if (!fs.existsSync(path.join(repositoryRoot, "build", "index.js"))) {
    console.error("build/index.js is missing; run npm run build first.");
    return 2;
  }

  const outDir = path.resolve(options.out ?? path.join(os.tmpdir(), "c3dmcp-smoke", stamp()));
  fs.mkdirSync(outDir, { recursive: true });
  const scripts = {};
  const scriptDir = path.join(repositoryRoot, "scripts", "live-smoke", "csharp");
  for (const file of fs.readdirSync(scriptDir).filter((name) => name.endsWith(".cs"))) {
    scripts[path.basename(file, ".cs")] = fs.readFileSync(path.join(scriptDir, file), "utf8");
  }

  let port = options.port;
  let expectedPath = options.drawing;
  const extra = {};
  if (options.startCivil) {
    expectedPath = fixtureDrawingPath(outDir);
    if (fs.existsSync(expectedPath)) {
      console.error(`The fixture drawing already exists: ${expectedPath}; use a new --out folder.`);
      return 2;
    }
    try {
      const started = await startCivil(options, outDir, expectedPath);
      port = started.instance.port;
      extra["Started Civil 3D"] = `process ${started.instance.processId}, template \`${started.template}\``;
    } catch (error) {
      console.error(error.message);
      return 2;
    }
  }

  const client = await connect({ CIVIL3D_PORT: String(port) });
  const wrongTokenClient = await connect({ CIVIL3D_PORT: String(port), CIVIL3D_MCP_TOKEN: WRONG_TOKEN });
  let report;
  try {
    report = await runSmoke({
      client,
      wrongTokenClient,
      scripts,
      port,
      expectedPath,
      fileExists: (file) => fs.existsSync(file),
      timeouts: { callMs: 180_000, writeMs: 900_000 },
      log: (line) => console.log(line),
    });
  } finally {
    await client.close().catch(() => {});
    await wrongTokenClient.close().catch(() => {});
  }

  const serverBuild = JSON.parse(fs.readFileSync(path.join(repositoryRoot, "build", "build-info.json"), "utf8"));
  extra["MCP server"] = `${serverBuild.commit ?? "?"}${serverBuild.dirty ? " (uncommitted changes)" : ""}`;
  fs.writeFileSync(path.join(outDir, "smoke-report.json"), `${JSON.stringify({ ...report, extra }, null, 2)}\n`, "utf8");
  fs.writeFileSync(path.join(outDir, "smoke-report.md"), renderMarkdown(report, extra), "utf8");
  console.log(`\n${report.ok ? "PASS" : "FAIL"}: ${report.counts.pass} pass, ${report.counts.warn} warning, ${report.counts.fail} fail, ${report.counts.skip} skipped`);
  console.log(`Report: ${path.join(outDir, "smoke-report.md")}`);
  console.log("Civil 3D stays open. The test drawing and its folder can be deleted by hand when no longer needed.");
  return report.ok ? 0 : 1;
}

process.exitCode = await main();
