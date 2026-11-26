#!/usr/bin/env node
// Installs the connector's Codex plugin with a real Codex CLI, in a throw-away Codex home, and reads the
// resulting MCP server configuration back: what a colleague gets on another PC.
//
//   node scripts/check-codex-install.mjs --codex-js <node_modules/@openai/codex/bin/codex.js> [--marketplace <dir>]
//
// Without --marketplace it checks the plugin built by `node scripts/package-connector.mjs` (dist/connector)
// in a temporary one-plugin marketplace; with --marketplace it checks a copy of that marketplace folder
// (for example the refreshed clone in the release workflow), so the folder itself is never touched.
// The real Codex setup of this computer is never read or changed, and no MCP server is started.

import { spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { CODEX_TIMEOUTS, LAUNCHER, PACKAGING_DIR, PLUGIN_NAME, SERVER_NAME, checkConnector } from "./package-connector.mjs";

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

function main() {
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
    console.log(`Codex install check passed: ${PLUGIN_NAME} ${version} from ${marketName}, ${SERVER_NAME} once, cwd ${cwd.split(sep).slice(-4).join("/")}, ` +
      Object.entries(CODEX_TIMEOUTS).map(([k, v]) => `${k} ${v}`).join(", "));
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

try {
  main();
} catch (error) {
  console.error(error.message);
  process.exit(1);
}
