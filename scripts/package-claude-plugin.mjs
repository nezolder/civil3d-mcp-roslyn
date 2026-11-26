#!/usr/bin/env node
// Packages the Claude connector plugin (packaging/claude-plugin) and, optionally, refreshes it in a
// clone of a shared plugin marketplace repository.
//
//   node scripts/package-claude-plugin.mjs                      # check and build dist/claude-plugin/<name>/
//   node scripts/package-claude-plugin.mjs --release            # same, but refuses uncommitted changes
//   node scripts/package-claude-plugin.mjs --marketplace <dir>  # also refresh the plugin in that clone
//   node scripts/package-claude-plugin.mjs --print-version      # print the plugin version and exit
//
// With --marketplace only two things change in the clone: the folder plugins/claude/<name>/ is
// replaced, and this plugin's entry in .claude-plugin/marketplace.json is replaced (or added last).
// Other plugins, their entries, the marketplace's name and owner and every other file stay as they
// are; the script fails if anything else would change.
//
// Every package carries csomag.json (the name the shared marketplace uses for its receipts): source
// repository and commit, whether the worktree had uncommitted changes, and the SHA-256 of each file.

import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

export const PLUGIN_NAME = "nezolder-civil3d-mcp";
export const SOURCE_REPOSITORY = "nezolder/civil3d-mcp-roslyn";
export const LAUNCHER = "start-civil3d-mcp.mjs";
export const PLUGIN_FILES = [".claude-plugin/plugin.json", ".mcp.json", "README.md", LAUNCHER];
export const RECEIPT = "csomag.json";
const ALLOWED_DRIVE_PATH = "C:\\Program Files\\nodejs\\node.exe";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
export const SOURCE_DIR = join(repoRoot, "packaging", "claude-plugin");

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

// Checks the plugin source; returns its manifest or throws with every problem found.
export function checkPluginSource(dir = SOURCE_DIR) {
  const problems = [];
  const files = existsSync(dir) ? listFiles(dir) : [];
  const expected = [...PLUGIN_FILES].sort();
  if (JSON.stringify(files) !== JSON.stringify(expected)) {
    problems.push(`plugin files are [${files.join(", ")}], expected [${expected.join(", ")}]`);
  }
  let manifest = {};
  let mcp = {};
  try { manifest = JSON.parse(readFileSync(join(dir, ".claude-plugin", "plugin.json"), "utf8")); } catch (e) { problems.push(`plugin.json: ${e.message}`); }
  try { mcp = JSON.parse(readFileSync(join(dir, ".mcp.json"), "utf8")); } catch (e) { problems.push(`.mcp.json: ${e.message}`); }
  if (manifest.name !== PLUGIN_NAME) problems.push(`plugin.json name must be ${PLUGIN_NAME}, is ${manifest.name}`);
  if (!/^\d+\.\d+\.\d+$/.test(manifest.version ?? "")) problems.push(`plugin.json version must be x.y.z, is ${manifest.version}`);
  if (typeof manifest.description !== "string" || manifest.description.trim() === "") problems.push("plugin.json needs a description");
  const servers = Object.keys(mcp.mcpServers ?? {});
  if (JSON.stringify(servers) !== JSON.stringify(["civil3d_roslyn"])) problems.push(`.mcp.json must define exactly the server civil3d_roslyn, has [${servers.join(", ")}]`);
  const args = mcp.mcpServers?.civil3d_roslyn?.args;
  if (JSON.stringify(args) !== JSON.stringify([`\${CLAUDE_PLUGIN_ROOT}/${LAUNCHER}`])) problems.push(`.mcp.json must start the launcher through \${CLAUDE_PLUGIN_ROOT}/${LAUNCHER}`);
  for (const file of files) {
    try { problems.push(...privacyProblems(file, readFileSync(join(dir, file), "utf8"))); } catch (e) { problems.push(`${file}: ${e.message}`); }
  }
  if (problems.length) throw new Error("The plugin source is not ready:\n  " + problems.join("\n  "));
  return manifest;
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

// Copies the plugin into out/ (the plugin root) and writes its receipt.
export function buildPlugin(out, { source = SOURCE_DIR, state = sourceState() } = {}) {
  const manifest = checkPluginSource(source);
  rmSync(out, { recursive: true, force: true });
  mkdirSync(out, { recursive: true });
  for (const file of PLUGIN_FILES) {
    mkdirSync(dirname(join(out, file)), { recursive: true });
    cpSync(join(source, file), join(out, file));
  }
  const files = Object.fromEntries(PLUGIN_FILES.slice().sort().map((file) => [file, sha256(join(out, file))]));
  const receipt = {
    source_repository: SOURCE_REPOSITORY,
    source_commit: state.commit,
    source_worktree_dirty: state.dirty,
    platform: "claude",
    plugin: PLUGIN_NAME,
    version: manifest.version,
    files,
  };
  writeFileSync(join(out, RECEIPT), JSON.stringify(receipt, null, 2) + "\n", "utf8");
  return { manifest, receipt };
}

function upsert(entries, entry) {
  const found = entries.some((e) => e?.name === entry.name);
  return found ? entries.map((e) => (e?.name === entry.name ? entry : e)) : [...entries, entry];
}

// Refreshes this plugin in a clone of the shared marketplace repository; returns the changed paths.
export function updateMarketplace(target, pluginDir, manifest) {
  if (!existsSync(join(target, ".git"))) throw new Error(`Not a clone of the marketplace repository (no .git): ${target}`);
  const marketPath = join(target, ".claude-plugin", "marketplace.json");
  if (!existsSync(marketPath)) throw new Error(`No .claude-plugin/marketplace.json in ${target}; the marketplace must exist first.`);
  const market = JSON.parse(readFileSync(marketPath, "utf8"));
  if (!Array.isArray(market.plugins)) throw new Error("marketplace.json has no plugins list");

  const own = join(target, "plugins", "claude", PLUGIN_NAME);
  rmSync(own, { recursive: true, force: true });
  mkdirSync(dirname(own), { recursive: true });
  cpSync(pluginDir, own, { recursive: true });

  market.plugins = upsert(market.plugins, {
    name: PLUGIN_NAME,
    source: `./plugins/claude/${PLUGIN_NAME}`,
    description: manifest.description,
    version: manifest.version,
  });
  writeFileSync(marketPath, JSON.stringify(market, null, 2) + "\n", "utf8");

  const changed = git(["status", "--porcelain", "--untracked-files=all"], target)
    .split("\n").filter(Boolean).map((line) => line.slice(3).replace(/^"|"$/g, ""));
  const ownPrefix = `plugins/claude/${PLUGIN_NAME}/`;
  const foreign = changed.filter((path) => path !== ".claude-plugin/marketplace.json" && !path.startsWith(ownPrefix));
  if (foreign.length) throw new Error("The marketplace update touched paths it does not own:\n  " + foreign.join("\n  "));
  return changed;
}

function parseArgs(argv) {
  const args = { out: join(repoRoot, "dist", "claude-plugin", PLUGIN_NAME), release: false, marketplace: null, printVersion: false };
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
    process.stdout.write(checkPluginSource().version + "\n");
    return;
  }
  const state = sourceState();
  if (args.release && (state.commit === null || state.dirty !== false)) {
    throw new Error("A release build needs a committed, clean worktree.");
  }
  const { manifest, receipt } = buildPlugin(args.out, { state });
  console.log(`${PLUGIN_NAME} ${manifest.version} built in ${args.out} (source ${receipt.source_commit ?? "unknown"}${state.dirty ? ", UNCOMMITTED CHANGES" : ""})`);
  if (args.marketplace) {
    const changed = updateMarketplace(args.marketplace, args.out, manifest);
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
