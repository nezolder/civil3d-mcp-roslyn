#!/usr/bin/env node
// Installs the connector's Codex plugin with a real Codex CLI, in a throw-away Codex home, and reads the
// resulting MCP server configuration back: what a colleague gets on another PC.
//
//   node scripts/check-codex-install.mjs --codex-js <node_modules/@openai/codex/bin/codex.js> [--marketplace <dir>]
//
// Without --marketplace it checks the plugin built by `node scripts/package-connector.mjs` (dist/connector)
// in a temporary one-plugin marketplace; with --marketplace it checks a copy of that marketplace folder
// (for example the refreshed clone in the release workflow), so the folder itself is never touched.
// Then it proves the approval rules in real tool calls: the installed plugin's server command is pointed at
// a stand-in server with the same three tools and annotations, and `codex exec` (approval policy "never",
// a local stand-in model that calls one tool) must run only the tools that need no approval.
// The real Codex setup of this computer is never read or changed, and the real MCP server is never started.

import { spawn, spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { dirname, join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { CODEX_APPROVALS, CODEX_TIMEOUTS, LAUNCHER, PACKAGING_DIR, PLUGIN_NAME, SERVER_NAME, checkConnector } from "./package-connector.mjs";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");

function parseArgs(argv) {
  const args = { codexJs: null, marketplace: null, built: join(repoRoot, "dist", "connector") };
  for (let i = 0; i < argv.length; i++) {
    const value = argv[i + 1];
    if (argv[i] === "--codex-js" && value) { args.codexJs = resolve(value); i++; }
    else if (argv[i] === "--marketplace" && value) { args.marketplace = resolve(value); i++; }
    else if (argv[i] === "--built" && value) { args.built = resolve(value); i++; }
    else throw new Error(`Unknown or incomplete argument: ${argv[i]}`);
  }
  if (!args.codexJs || !existsSync(args.codexJs)) throw new Error("--codex-js must name the bin/codex.js of an installed @openai/codex");
  return args;
}

function git(cwd, ...args) {
  const run = spawnSync("git", args, { cwd, encoding: "utf8" });
  if (run.status !== 0) throw new Error(`git ${args.join(" ")} failed: ${run.stderr}`);
}

// A marketplace with only this plugin, from the built folders.
function singlePluginMarketplace(dir, built) {
  const codexPlugin = join(built, "codex", PLUGIN_NAME);
  if (!existsSync(codexPlugin)) throw new Error(`No built Codex plugin in ${codexPlugin}; run node scripts/package-connector.mjs first.`);
  cpSync(codexPlugin, join(dir, "plugins", "codex", PLUGIN_NAME), { recursive: true });
  mkdirSync(join(dir, ".agents", "plugins"), { recursive: true });
  writeFileSync(join(dir, ".agents", "plugins", "marketplace.json"), JSON.stringify({
    name: "connector-check",
    plugins: [{
      name: PLUGIN_NAME,
      source: { source: "local", path: `./plugins/codex/${PLUGIN_NAME}` },
      policy: { installation: "AVAILABLE", authentication: "ON_INSTALL" },
      category: "Productivity",
    }],
  }, null, 2) + "\n");
  git(dir, "init", "-q");
  git(dir, "-c", "user.name=check", "-c", "user.email=check@example.invalid", "add", "-A");
  git(dir, "-c", "user.name=check", "-c", "user.email=check@example.invalid", "commit", "-q", "-m", "check");
  return "connector-check";
}

// A stand-in MCP server: the real tool names and annotations; each call is appended to FAKE_LOG.
const FAKE_SERVER = `import { appendFileSync } from "node:fs";
import { createInterface } from "node:readline";
const tools = [
  { name: "civil3d_query", description: "query", inputSchema: { type: "object" }, annotations: { destructiveHint: false, openWorldHint: false } },
  { name: "civil3d_execute", description: "execute", inputSchema: { type: "object" }, annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false } },
  { name: "civil3d_skills", description: "skills", inputSchema: { type: "object" }, annotations: { readOnlyHint: true, openWorldHint: false } },
];
createInterface({ input: process.stdin }).on("line", (line) => {
  const message = JSON.parse(line);
  if (message.id === undefined) return;
  let result = {};
  if (message.method === "initialize") {
    result = { protocolVersion: message.params?.protocolVersion ?? "2025-06-18", capabilities: { tools: {} }, serverInfo: { name: "stand-in", version: "1" } };
  } else if (message.method === "tools/list") {
    result = { tools };
  } else if (message.method === "tools/call") {
    appendFileSync(process.env.FAKE_LOG, message.params.name + "\\n");
    result = { content: [{ type: "text", text: "ran " + message.params.name }] };
  }
  process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id: message.id, result }) + "\\n");
});
`;

// A stand-in Responses API: the first request of a turn gets one call of the chosen tool, the request that
// carries the tool's output gets a closing message. The outputs are collected per tool.
function startModel(outputs) {
  const state = { tool: null };
  const server = createServer((req, res) => {
    let body = "";
    req.on("data", (chunk) => { body += chunk; });
    req.on("end", () => {
      const input = JSON.parse(body || "{}").input ?? [];
      const output = input.find((item) => item?.type === "function_call_output");
      const event = (type, extra) => `event: ${type}\ndata: ${JSON.stringify({ type, ...extra })}\n\n`;
      let text = event("response.created", { response: { id: "r" } });
      if (output) {
        outputs[state.tool] = JSON.stringify(output.output);
        text += event("response.output_item.done", { item: { type: "message", role: "assistant", id: "m", content: [{ type: "output_text", text: "done" }] } });
      } else {
        text += event("response.output_item.done", { item: { type: "function_call", id: "f", call_id: "c", name: state.tool, namespace: `mcp__${SERVER_NAME}`, arguments: "{}" } });
      }
      text += event("response.completed", { response: { id: "r", usage: { input_tokens: 1, output_tokens: 1, total_tokens: 2 } } });
      res.writeHead(200, { "Content-Type": "text/event-stream" });
      res.end(text);
    });
  });
  return new Promise((done) => server.listen(0, "127.0.0.1", () => done({ server, state, port: server.address().port })));
}

function runAsync(command, args, options) {
  return new Promise((done) => {
    const child = spawn(command, args, { ...options, stdio: ["ignore", "pipe", "pipe"] });
    let out = "";
    child.stdout.on("data", (chunk) => { out += chunk; });
    child.stderr.on("data", (chunk) => { out += chunk; });
    const timer = setTimeout(() => child.kill(), 120000);
    child.on("close", (status) => { clearTimeout(timer); done({ status, out }); });
  });
}

// Which tools run without approval, in real calls through the installed plugin's rules.
async function checkApprovals({ codexJs, work, env, pluginRoot }) {
  const fake = join(work, "stand-in-server.mjs");
  const log = join(work, "tool-calls.log");
  writeFileSync(fake, FAKE_SERVER);
  const mcpPath = join(pluginRoot, ".mcp.json");
  const mcp = JSON.parse(readFileSync(mcpPath, "utf8"));
  const server = mcp.mcpServers[SERVER_NAME];
  server.command = process.execPath;
  server.args = [fake];
  server.env = { ...server.env, FAKE_LOG: log };
  writeFileSync(mcpPath, JSON.stringify(mcp, null, 2));

  const outputs = {};
  const model = await startModel(outputs);
  const problems = [];
  try {
    const provider = `{ name = "stand-in", base_url = "http://127.0.0.1:${model.port}/v1", wire_api = "responses" }`;
    for (const tool of ["civil3d_skills", "civil3d_query", "civil3d_execute"]) {
      model.state.tool = tool;
      const run = await runAsync(process.execPath, [codexJs, "exec", "--skip-git-repo-check",
        "-c", 'model="stand-in"', "-c", 'model_provider="stand-in"', "-c", `model_providers.stand-in=${provider}`,
        "-c", 'approval_policy="never"', `call ${tool}`], { cwd: work, env });
      if (run.status !== 0) problems.push(`codex exec for ${tool} failed (${run.status}):\n${run.out.slice(-1500)}`);
    }
  } finally {
    model.server.close();
  }
  const called = existsSync(log) ? readFileSync(log, "utf8").split(/\r?\n/).filter(Boolean) : [];
  const result = {};
  for (const tool of ["civil3d_skills", "civil3d_query", "civil3d_execute"]) {
    const free = (CODEX_APPROVALS.tools[tool] ?? CODEX_APPROVALS.default) === "approve";
    const ran = called.includes(tool);
    const output = outputs[tool] ?? "";
    result[tool] = ran ? "runs" : "asks";
    if (free && !(ran && output.includes(`ran ${tool}`))) problems.push(`${tool} should run without approval; ran: ${ran}, output: ${output || "none"}`);
    if (!free && (ran || !output.includes("requires approval"))) problems.push(`${tool} should need approval; ran: ${ran}, output: ${output || "none"}`);
  }
  return { problems, result };
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const { version } = checkConnector();
  const expected = JSON.parse(readFileSync(join(PACKAGING_DIR, "codex-plugin", ".mcp.json"), "utf8")).mcpServers[SERVER_NAME];
  const work = mkdtempSync(join(tmpdir(), "codex-connector-check-"));
  try {
    const home = join(work, "home");
    const codexHome = join(home, ".codex");
    mkdirSync(codexHome, { recursive: true });
    const market = join(work, "marketplace");
    let marketName;
    if (args.marketplace) {
      cpSync(args.marketplace, market, { recursive: true });
      marketName = JSON.parse(readFileSync(join(market, ".agents", "plugins", "marketplace.json"), "utf8")).name;
    } else {
      mkdirSync(market, { recursive: true });
      marketName = singlePluginMarketplace(market, args.built);
    }
    const env = { ...process.env, HOME: home, USERPROFILE: home, CODEX_HOME: codexHome };
    const codex = (...codexArgs) => {
      const run = spawnSync(process.execPath, [args.codexJs, ...codexArgs], { cwd: work, env, encoding: "utf8" });
      if (run.status !== 0) throw new Error(`codex ${codexArgs.join(" ")} failed (${run.status}):\n${run.stderr}${run.stdout}`);
      return run.stdout;
    };
    console.log(codex("--version").trim());
    codex("plugin", "marketplace", "add", market);
    codex("plugin", "add", `${PLUGIN_NAME}@${marketName}`);
    const servers = JSON.parse(codex("mcp", "list", "--json"));

    const problems = [];
    const ours = servers.filter((server) => server.name === SERVER_NAME);
    if (ours.length !== 1) problems.push(`codex mcp list has ${ours.length} servers named ${SERVER_NAME}, expected 1`);
    const server = ours[0] ?? {};
    const transport = server.transport ?? {};
    const pluginRoot = join(codexHome, "plugins", "cache", marketName, PLUGIN_NAME, version);
    if (transport.type !== "stdio") problems.push(`transport is ${transport.type}, expected stdio`);
    if (transport.command !== expected.command) problems.push(`command is ${transport.command}, expected ${expected.command}`);
    if (JSON.stringify(transport.args) !== JSON.stringify([LAUNCHER])) problems.push(`args are ${JSON.stringify(transport.args)}`);
    // Compare real paths: on Windows the temporary folder can come as a short 8.3 name (RUNNER~1).
    const real = (path) => {
      try { return realpathSync.native(path); } catch { return resolve(path); }
    };
    const same = (a, b) => (process.platform === "win32" ? a.toLowerCase() === b.toLowerCase() : a === b);
    const cwd = real(resolve(transport.cwd ?? ""));
    if (!same(cwd, real(pluginRoot))) problems.push(`cwd is ${transport.cwd}, expected the installed plugin folder ${pluginRoot}`);
    if (!existsSync(join(cwd, LAUNCHER))) problems.push(`the launcher is not in the working folder ${cwd}`);
    for (const [key, value] of Object.entries(CODEX_TIMEOUTS)) {
      if (server[key] !== value) problems.push(`${key} is ${server[key]}, expected ${value}`);
    }
    for (const [key, value] of Object.entries(expected.env ?? {})) {
      if (transport.env?.[key] !== value) problems.push(`env ${key} is ${transport.env?.[key]}, expected ${value}`);
    }
    if (server.enabled !== true) problems.push("the server is not enabled");
    if (problems.length) throw new Error(`Codex install check failed:\n  ${problems.join("\n  ")}`);
    const approvals = await checkApprovals({ codexJs: args.codexJs, work, env, pluginRoot });
    if (approvals.problems.length) throw new Error(`Codex approval check failed:\n  ${approvals.problems.join("\n  ")}`);
    console.log(`Codex install check passed: ${PLUGIN_NAME} ${version} from ${marketName}, ${SERVER_NAME} once, cwd ${cwd.split(sep).slice(-4).join("/")}, ` +
      Object.entries(CODEX_TIMEOUTS).map(([k, v]) => `${k} ${v}`).join(", ") + "; " +
      Object.entries(approvals.result).map(([tool, how]) => `${tool} ${how}`).join(", "));
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

main().catch((error) => {
  console.error(error.message);
  process.exit(1);
});
