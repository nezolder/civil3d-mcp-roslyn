#!/usr/bin/env node
// Packages the connector plugin nezolder-civil3d-mcp for Claude (packaging/claude-plugin) and Codex
// (packaging/codex-plugin) and, optionally, refreshes both in a clone of a shared plugin marketplace.
//
//   node scripts/package-connector.mjs                      # check and build dist/connector/{claude,codex}/<name>/
//   node scripts/package-connector.mjs --release            # same, but refuses uncommitted changes
//   node scripts/package-connector.mjs --marketplace <dir>  # also refresh the plugin in that clone
//   node scripts/package-connector.mjs --print-version      # print the plugin version and exit
//
// With --marketplace only these change in the clone: the folders plugins/claude/<name>/ and
// plugins/codex/<name>/ are replaced, and this plugin's entry in .claude-plugin/marketplace.json and in
// .agents/plugins/marketplace.json is replaced (or added last). Other plugins, their entries, the
// marketplaces' names and every other file stay as they are; the script fails if anything else changed.
//
// Both platforms ship the same launcher (packaging/claude-plugin/start-civil3d-mcp.mjs) and the same
// version. Every package carries csomag.json (the name the shared marketplace uses for its receipts):
// source repository and commit, whether the worktree had uncommitted changes, and the SHA-256 of each file.

import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

export const PLUGIN_NAME = "nezolder-civil3d-mcp";
export const SERVER_NAME = "civil3d_roslyn";
export const SOURCE_REPOSITORY = "nezolder/civil3d-mcp-roslyn";
export const LAUNCHER = "start-civil3d-mcp.mjs";
export const RECEIPT = "csomag.json";
const ALLOWED_DRIVE_PATH = "C:\\Program Files\\nodejs\\node.exe";
// Codex's own limits for this server: a saved write may take the server's 10-minute save limit plus a
// margin; without them Codex waits 60 s for a tool call and 10 s for the start.
export const CODEX_TIMEOUTS = { tool_timeout_sec: 660, startup_timeout_sec: 30 };
// Codex's approval rules for this server: every tool asks before it runs (civil3d_query runs C# that can
// act outside the rolled-back transaction, civil3d_execute writes, and tools added later inherit the rule),
// except the catalog and status tool civil3d_skills. Without them Codex decides from the tool annotations.
export const CODEX_APPROVALS = { default: "prompt", tools: { civil3d_skills: "approve" } };

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
export const PACKAGING_DIR = join(repoRoot, "packaging");
export const LAUNCHER_SOURCE = join(PACKAGING_DIR, "claude-plugin", LAUNCHER);

// Per platform: source folder, manifest, the files in the source folder, the files of the built
// plugin, how .mcp.json must start the launcher, and the marketplace file and entry.
export const PLATFORMS = {
  claude: {
    source: "claude-plugin",
    manifest: ".claude-plugin/plugin.json",
    sourceFiles: [".claude-plugin/plugin.json", ".mcp.json", "README.md", LAUNCHER],
    launch: (server) => JSON.stringify(server.args) === JSON.stringify([`\${CLAUDE_PLUGIN_ROOT}/${LAUNCHER}`]),
    launchRule: `args [\${CLAUDE_PLUGIN_ROOT}/${LAUNCHER}]`,
    marketplace: ".claude-plugin/marketplace.json",
    entry: (manifest) => ({
      name: PLUGIN_NAME,
      source: `./plugins/claude/${PLUGIN_NAME}`,
      description: manifest.description,
      version: manifest.version,
    }),
  },
  codex: {
    source: "codex-plugin",
    manifest: ".codex-plugin/plugin.json",
    sourceFiles: [".codex-plugin/plugin.json", ".mcp.json", "README.md"],
    // Codex has no plugin-root variable for MCP servers; "cwd": "." is resolved to the plugin folder.
    launch: (server) => JSON.stringify(server.args) === JSON.stringify([LAUNCHER]) && server.cwd === ".",
    launchRule: `args [${LAUNCHER}] with cwd "."`,
    marketplace: ".agents/plugins/marketplace.json",
    entry: (manifest) => ({
      name: PLUGIN_NAME,
      source: { source: "local", path: `./plugins/codex/${PLUGIN_NAME}` },
      policy: { installation: "AVAILABLE", authentication: "ON_INSTALL" },
      category: manifest.interface?.category ?? "Productivity",
    }),
  },
};

export function pluginFiles(platform) {
  const files = PLATFORMS[platform].sourceFiles;
  return (files.includes(LAUNCHER) ? files : [...files, LAUNCHER]).slice().sort();
}

function listFiles(root) {
  const out = [];
  const walk = (dir) => {
    for (const name of readdirSync(dir)) {
      const full = join(dir, name);
      if (statSync(full).isDirectory()) walk(full);
      else out.push(relative(root, full).split(sep).join("/"));
    }
  };
  walk(root);
  return out.sort();
}

function stringsOf(value, out = []) {
  if (typeof value === "string") out.push(value);
  else if (Array.isArray(value)) value.forEach((item) => stringsOf(item, out));
  else if (value && typeof value === "object") Object.entries(value).forEach(([k, v]) => { out.push(k); stringsOf(v, out); });
  return out;
}

// Text that must not appear in the public plugin: local or network paths, user folders, e-mail addresses.
export function privacyProblems(file, text) {
  const pieces = file.endsWith(".json") ? stringsOf(JSON.parse(text)) : [text];
  const problems = [];
  for (const piece of pieces) {
    for (const match of piece.matchAll(/(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\n`"']*/g)) {
      if (!match[0].startsWith(ALLOWED_DRIVE_PATH)) problems.push(`${file}: local path ${JSON.stringify(match[0])}`);
    }
    if (/\\\\[A-Za-z0-9._-]+\\/.test(piece)) problems.push(`${file}: network path`);
    if (/\b(Documents|Dokumentumok|OneDrive|Desktop|Asztal)\b/i.test(piece)) problems.push(`${file}: user folder name`);
    if (/[\w.+-]+@[\w-]+\.[A-Za-z]{2,}/.test(piece)) problems.push(`${file}: e-mail address`);
  }
  return problems;
}

function readJson(path, problems, label) {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch (e) {
    problems.push(`${label}: ${e.message}`);
    return {};
  }
}

// Checks one platform's plugin source; returns its manifest or throws with every problem found.
export function checkPluginSource(platform, dir = join(PACKAGING_DIR, PLATFORMS[platform].source)) {
  const spec = PLATFORMS[platform];
  const problems = [];
  const files = existsSync(dir) ? listFiles(dir) : [];
  const expected = spec.sourceFiles.slice().sort();
  if (JSON.stringify(files) !== JSON.stringify(expected)) {
    problems.push(`${platform}: plugin files are [${files.join(", ")}], expected [${expected.join(", ")}]`);
  }
  const manifest = readJson(join(dir, spec.manifest), problems, `${platform} ${spec.manifest}`);
  const mcp = readJson(join(dir, ".mcp.json"), problems, `${platform} .mcp.json`);
  if (manifest.name !== PLUGIN_NAME) problems.push(`${platform}: manifest name must be ${PLUGIN_NAME}, is ${manifest.name}`);
  if (!/^\d+\.\d+\.\d+$/.test(manifest.version ?? "")) problems.push(`${platform}: manifest version must be x.y.z, is ${manifest.version}`);
  if (typeof manifest.description !== "string" || manifest.description.trim() === "") problems.push(`${platform}: manifest needs a description`);
  if (platform === "codex" && manifest.mcpServers !== "./.mcp.json") problems.push(`codex: manifest mcpServers must be "./.mcp.json"`);
  const servers = Object.keys(mcp.mcpServers ?? {});
  if (JSON.stringify(servers) !== JSON.stringify([SERVER_NAME])) problems.push(`${platform}: .mcp.json must define exactly the server ${SERVER_NAME}, has [${servers.join(", ")}]`);
  const server = mcp.mcpServers?.[SERVER_NAME] ?? {};
  if (!spec.launch(server)) problems.push(`${platform}: .mcp.json must start the launcher with ${spec.launchRule}`);
  if (platform === "codex") {
    for (const [key, value] of Object.entries(CODEX_TIMEOUTS)) {
      if (server[key] !== value) problems.push(`codex: .mcp.json ${SERVER_NAME}.${key} must be ${value}, is ${server[key]}`);
    }
    if (server.default_tools_approval_mode !== CODEX_APPROVALS.default) {
      problems.push(`codex: .mcp.json ${SERVER_NAME}.default_tools_approval_mode must be ${CODEX_APPROVALS.default}, is ${server.default_tools_approval_mode}`);
    }
    const tools = Object.fromEntries(Object.entries(server.tools ?? {}).map(([name, rule]) => [name, rule?.approval_mode]));
    if (JSON.stringify(tools) !== JSON.stringify(CODEX_APPROVALS.tools)) {
      problems.push(`codex: .mcp.json ${SERVER_NAME}.tools approval rules must be ${JSON.stringify(CODEX_APPROVALS.tools)}, are ${JSON.stringify(tools)}`);
    }
  }
  for (const file of files) {
    try { problems.push(...privacyProblems(`${platform} ${file}`, readFileSync(join(dir, file), "utf8"))); } catch (e) { problems.push(`${platform} ${file}: ${e.message}`); }
  }
  if (problems.length) throw new Error("The plugin source is not ready:\n  " + problems.join("\n  "));
  return { manifest, mcp };
}

// Checks both platforms and that they agree; returns the version and the manifests.
export function checkConnector(dirs = {}) {
  const claude = checkPluginSource("claude", dirs.claude);
  const codex = checkPluginSource("codex", dirs.codex);
  const problems = [];
  if (claude.manifest.version !== codex.manifest.version) problems.push(`versions differ: claude ${claude.manifest.version}, codex ${codex.manifest.version}`);
  if (claude.manifest.description !== codex.manifest.description) problems.push("descriptions differ between the claude and codex manifests");
  const strip = (server) => ({ command: server.command, env: server.env });
  if (JSON.stringify(strip(claude.mcp.mcpServers[SERVER_NAME])) !== JSON.stringify(strip(codex.mcp.mcpServers[SERVER_NAME]))) {
    problems.push("the claude and codex .mcp.json differ in command or env");
  }
  if (problems.length) throw new Error("The plugin source is not ready:\n  " + problems.join("\n  "));
  return { version: claude.manifest.version, manifests: { claude: claude.manifest, codex: codex.manifest } };
}

function git(args, cwd) {
  return execFileSync("git", args, { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
}

function sourceState() {
  try {
    const commit = git(["rev-parse", "HEAD"], repoRoot).trim();
    const dirty = git(["status", "--porcelain"], repoRoot).trim() !== "";
    return { commit, dirty };
  } catch {
    return { commit: null, dirty: null };
  }
}

function sha256(path) {
  return createHash("sha256").update(readFileSync(path)).digest("hex");
}

// Copies one platform's plugin into out/ (the plugin root) and writes its receipt.
export function buildPlugin(platform, out, { source, launcher = LAUNCHER_SOURCE, state = sourceState() } = {}) {
  const spec = PLATFORMS[platform];
  const dir = source ?? join(PACKAGING_DIR, spec.source);
  const { manifest } = checkPluginSource(platform, dir);
  rmSync(out, { recursive: true, force: true });
  mkdirSync(out, { recursive: true });
  for (const file of pluginFiles(platform)) {
    mkdirSync(dirname(join(out, file)), { recursive: true });
    cpSync(spec.sourceFiles.includes(file) ? join(dir, file) : launcher, join(out, file));
  }
  const files = Object.fromEntries(pluginFiles(platform).map((file) => [file, sha256(join(out, file))]));
  const receipt = {
    source_repository: SOURCE_REPOSITORY,
    source_commit: state.commit,
    source_worktree_dirty: state.dirty,
    platform,
    plugin: PLUGIN_NAME,
    version: manifest.version,
    files,
  };
  writeFileSync(join(out, RECEIPT), JSON.stringify(receipt, null, 2) + "\n", "utf8");
  return { manifest, receipt };
}

// Builds both platforms into out/<platform>/<name>/.
export function buildConnector(out, { state = sourceState() } = {}) {
  const { version } = checkConnector();
  const built = {};
  for (const platform of Object.keys(PLATFORMS)) {
    const dir = join(out, platform, PLUGIN_NAME);
    built[platform] = { dir, ...buildPlugin(platform, dir, { state }) };
  }
  return { version, built };
}

function upsert(entries, entry) {
  const found = entries.some((e) => e?.name === entry.name);
  return found ? entries.map((e) => (e?.name === entry.name ? entry : e)) : [...entries, entry];
}

// Refreshes this plugin in a clone of the shared marketplace repository; returns the changed paths.
// pluginDirs: { claude: <built plugin folder>, codex: <built plugin folder> }.
export function updateMarketplace(target, pluginDirs, manifests) {
  if (!existsSync(join(target, ".git"))) throw new Error(`Not a clone of the marketplace repository (no .git): ${target}`);
  const platforms = Object.keys(PLATFORMS);
  const markets = {};
  for (const platform of platforms) {
    const path = join(target, PLATFORMS[platform].marketplace);
    if (!existsSync(path)) throw new Error(`No ${PLATFORMS[platform].marketplace} in ${target}; the marketplace must exist first.`);
    markets[platform] = JSON.parse(readFileSync(path, "utf8"));
    if (!Array.isArray(markets[platform].plugins)) throw new Error(`${PLATFORMS[platform].marketplace} has no plugins list`);
  }
  for (const platform of platforms) {
    const own = join(target, "plugins", platform, PLUGIN_NAME);
    rmSync(own, { recursive: true, force: true });
    mkdirSync(dirname(own), { recursive: true });
    cpSync(pluginDirs[platform], own, { recursive: true });
    const market = markets[platform];
    market.plugins = upsert(market.plugins, PLATFORMS[platform].entry(manifests[platform]));
    writeFileSync(join(target, PLATFORMS[platform].marketplace), JSON.stringify(market, null, 2) + "\n", "utf8");
  }

  const changed = git(["status", "--porcelain", "--untracked-files=all"], target)
    .split("\n").filter(Boolean).map((line) => line.slice(3).replace(/^"|"$/g, ""));
  const owned = (path) => platforms.some((platform) =>
    path === PLATFORMS[platform].marketplace || path.startsWith(`plugins/${platform}/${PLUGIN_NAME}/`));
  const foreign = changed.filter((path) => !owned(path));
  if (foreign.length) throw new Error("The marketplace update touched paths it does not own:\n  " + foreign.join("\n  "));
  return changed;
}

function parseArgs(argv) {
  const args = { out: join(repoRoot, "dist", "connector"), release: false, marketplace: null, printVersion: false };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--release") args.release = true;
    else if (arg === "--print-version") args.printVersion = true;
    else if (arg === "--out" || arg === "--marketplace") {
      const value = argv[++i];
      if (!value) throw new Error(`${arg} needs a folder`);
      args[arg === "--out" ? "out" : "marketplace"] = resolve(value);
    } else throw new Error(`Unknown argument: ${arg}`);
  }
  return args;
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.printVersion) {
    process.stdout.write(checkConnector().version + "\n");
    return;
  }
  const state = sourceState();
  if (args.release && (state.commit === null || state.dirty !== false)) {
    throw new Error("A release build needs a committed, clean worktree.");
  }
  const { version, built } = buildConnector(args.out, { state });
  for (const [platform, { dir }] of Object.entries(built)) console.log(`${platform}: ${dir}`);
  console.log(`${PLUGIN_NAME} ${version} built (source ${state.commit ?? "unknown"}${state.dirty ? ", UNCOMMITTED CHANGES" : ""})`);
  if (args.marketplace) {
    const dirs = Object.fromEntries(Object.entries(built).map(([p, b]) => [p, b.dir]));
    const manifests = Object.fromEntries(Object.entries(built).map(([p, b]) => [p, b.manifest]));
    const changed = updateMarketplace(args.marketplace, dirs, manifests);
    console.log(changed.length ? `Marketplace updated: ${changed.join(", ")}` : "Marketplace already holds this build.");
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main();
  } catch (error) {
    console.error(error.message);
    process.exit(1);
  }
}
