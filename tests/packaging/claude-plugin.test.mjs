import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import {
  LAUNCHER,
  PLUGIN_NAME,
  RECEIPT,
  SOURCE_DIR,
  buildPlugin,
  checkPluginSource,
  privacyProblems,
  updateMarketplace,
} from "../../scripts/package-claude-plugin.mjs";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const launcher = join(SOURCE_DIR, LAUNCHER);

function tempDir(t) {
  const dir = mkdtempSync(join(tmpdir(), "c3dmcp-plugin-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  return dir;
}

function git(cwd, ...args) {
  return execFileSync("git", args, { cwd, encoding: "utf8" });
}

// A marketplace repository with another plugin in it, committed, as the release workflow clones it.
function fakeMarketplace(t) {
  const dir = tempDir(t);
  git(dir, "init", "-q");
  git(dir, "config", "user.email", "test@example.invalid");
  git(dir, "config", "user.name", "test");
  const market = {
    name: "shared-market",
    owner: { name: "Owner" },
    metadata: { description: "Ékezetes leírás marad." },
    plugins: [
      { name: "other-plugin", source: "./plugins/claude/other-plugin", description: "Other", version: "0.8.8" },
    ],
  };
  mkdirSync(join(dir, ".claude-plugin"), { recursive: true });
  writeFileSync(join(dir, ".claude-plugin", "marketplace.json"), JSON.stringify(market, null, 2) + "\n");
  mkdirSync(join(dir, "plugins", "claude", "other-plugin"), { recursive: true });
  writeFileSync(join(dir, "plugins", "claude", "other-plugin", "file.txt"), "other\n");
  writeFileSync(join(dir, "README.md"), "hand written\n");
  git(dir, "add", "-A");
  git(dir, "commit", "-q", "-m", "start");
  return { dir, market };
}

test("the plugin source is complete, named nezolder-civil3d-mcp and free of private paths", () => {
  const manifest = checkPluginSource();
  assert.equal(manifest.name, PLUGIN_NAME);
  assert.match(manifest.version, /^\d+\.\d+\.\d+$/);
});

test("the privacy check finds local and network paths, user folders and e-mail addresses", () => {
  assert.deepEqual(privacyProblems("README.md", "Node is C:\\Program Files\\nodejs\\node.exe; see https://example.com"), []);
  assert.equal(privacyProblems("README.md", "C:\\Users\\someone\\build\\index.js").length, 1);
  assert.equal(privacyProblems("README.md", "M:\\projects\\x").length, 1);
  assert.equal(privacyProblems("README.md", "\\\\server\\share\\x").length, 1);
  assert.equal(privacyProblems("a.mjs", 'join(home, "Documents", "x")').length, 1);
  assert.equal(privacyProblems("README.md", "write to someone@example.com").length, 1);
  assert.equal(privacyProblems("x.json", JSON.stringify({ args: ["D:\\work\\index.js"] })).length, 1);
});

test("a build copies the four plugin files and records their hashes", (t) => {
  const out = join(tempDir(t), PLUGIN_NAME);
  const { receipt } = buildPlugin(out, { state: { commit: "abc", dirty: false } });
  assert.deepEqual(Object.keys(receipt.files), [".claude-plugin/plugin.json", ".mcp.json", "README.md", LAUNCHER]);
  assert.equal(receipt.plugin, PLUGIN_NAME);
  assert.equal(receipt.source_commit, "abc");
  const written = JSON.parse(readFileSync(join(out, RECEIPT), "utf8"));
  assert.deepEqual(written, receipt);
  assert.equal(readFileSync(join(out, ".mcp.json"), "utf8"), readFileSync(join(SOURCE_DIR, ".mcp.json"), "utf8"));
});

test("the marketplace update changes only this plugin's folder and entry", (t) => {
  const { dir, market } = fakeMarketplace(t);
  const out = join(tempDir(t), PLUGIN_NAME);
  const { manifest } = buildPlugin(out, { state: { commit: "abc", dirty: false } });

  const changed = updateMarketplace(dir, out, manifest);
  assert.ok(changed.includes(".claude-plugin/marketplace.json"));
  assert.ok(changed.every((p) => p === ".claude-plugin/marketplace.json" || p.startsWith(`plugins/claude/${PLUGIN_NAME}/`)));

  const after = JSON.parse(readFileSync(join(dir, ".claude-plugin", "marketplace.json"), "utf8"));
  assert.equal(after.name, market.name);
  assert.deepEqual(after.owner, market.owner);
  assert.deepEqual(after.metadata, market.metadata);
  assert.deepEqual(after.plugins[0], market.plugins[0]);
  assert.deepEqual(after.plugins[1], {
    name: PLUGIN_NAME,
    source: `./plugins/claude/${PLUGIN_NAME}`,
    description: manifest.description,
    version: manifest.version,
  });
  assert.ok(existsSync(join(dir, "plugins", "claude", PLUGIN_NAME, LAUNCHER)));
  assert.equal(readFileSync(join(dir, "README.md"), "utf8"), "hand written\n");

  // A later release replaces its own entry in place and removes files it no longer ships.
  git(dir, "add", "-A");
  git(dir, "commit", "-q", "-m", "first release");
  writeFileSync(join(dir, "plugins", "claude", PLUGIN_NAME, "stale.txt"), "old\n");
  git(dir, "add", "-A");
  git(dir, "commit", "-q", "-m", "stale file");
  updateMarketplace(dir, out, { ...manifest, version: "9.9.9" });
  const again = JSON.parse(readFileSync(join(dir, ".claude-plugin", "marketplace.json"), "utf8"));
  assert.equal(again.plugins.length, 2);
  assert.equal(again.plugins[1].version, "9.9.9");
  assert.ok(!existsSync(join(dir, "plugins", "claude", PLUGIN_NAME, "stale.txt")));
});

test("the marketplace update refuses a clone with changes outside its own paths", (t) => {
  const { dir } = fakeMarketplace(t);
  const out = join(tempDir(t), PLUGIN_NAME);
  const { manifest } = buildPlugin(out, { state: { commit: "abc", dirty: false } });
  writeFileSync(join(dir, "README.md"), "changed\n");
  assert.throws(() => updateMarketplace(dir, out, manifest), /does not own:\s+README\.md/);
});

test("the marketplace update needs an existing marketplace clone", (t) => {
  const out = join(tempDir(t), PLUGIN_NAME);
  const { manifest } = buildPlugin(out, { state: { commit: "abc", dirty: false } });
  assert.throws(() => updateMarketplace(tempDir(t), out, manifest), /no \.git/);
  const bare = tempDir(t);
  git(bare, "init", "-q");
  assert.throws(() => updateMarketplace(bare, out, manifest), /marketplace must exist first/);
});

test("the launcher reports where it looked when no server is installed", (t) => {
  const empty = tempDir(t);
  const env = { ...process.env, LOCALAPPDATA: empty };
  delete env.CIVIL3D_MCP_SERVER_ENTRY;
  const run = spawnSync(process.execPath, [launcher], { env, encoding: "utf8" });
  assert.equal(run.status, 1);
  assert.match(run.stderr, /was not found on this PC/);
  assert.ok(run.stderr.includes(join(empty, "Civil3dMcpRoslyn", "server", "build", "index.js")));
});

test("the launcher loads the server from the standard place", (t) => {
  const local = tempDir(t);
  const entry = join(local, "Civil3dMcpRoslyn", "server", "build", "index.js");
  mkdirSync(dirname(entry), { recursive: true });
  writeFileSync(entry, 'process.stdout.write("standard server loaded");\n');
  const env = { ...process.env, LOCALAPPDATA: local };
  delete env.CIVIL3D_MCP_SERVER_ENTRY;
  const run = spawnSync(process.execPath, [launcher], { env, encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
  assert.equal(run.stdout, "standard server loaded");
});

test("the launcher answers an MCP handshake with the three tools through CIVIL3D_MCP_SERVER_ENTRY", async (t) => {
  const entry = join(repoRoot, "build", "index.js");
  assert.ok(existsSync(entry), "run `npm run build` first");
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [launcher],
    env: { ...process.env, LOCALAPPDATA: tempDir(t), CIVIL3D_MCP_SERVER_ENTRY: entry, LOG_LEVEL: "error" },
  });
  const client = new Client({ name: "plugin-launcher-test", version: "1.0.0" });
  try {
    await client.connect(transport);
    const { tools } = await client.listTools();
    assert.deepEqual(tools.map((tool) => tool.name).sort(), ["civil3d_execute", "civil3d_query", "civil3d_skills"]);
  } finally {
    await client.close();
  }
});
