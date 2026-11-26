import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import {
  CODEX_APPROVALS,
  CODEX_TIMEOUTS,
  LAUNCHER,
  LAUNCHER_SOURCE,
  PACKAGING_DIR,
  PLUGIN_NAME,
  RECEIPT,
  SERVER_NAME,
  buildConnector,
  buildPlugin,
  checkConnector,
  checkPluginSource,
  pluginFiles,
  privacyProblems,
  updateMarketplace,
} from "../../scripts/package-connector.mjs";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const STATE = { commit: "abc", dirty: false };

function tempDir(t) {
  const dir = mkdtempSync(join(tmpdir(), "c3dmcp-plugin-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  return dir;
}

function git(cwd, ...args) {
  return execFileSync("git", args, { cwd, encoding: "utf8" });
}

function writeJson(path, data) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, JSON.stringify(data, null, 2) + "\n");
}

// A marketplace repository with another plugin in both marketplaces, committed, as the release workflow clones it.
function fakeMarketplace(t) {
  const dir = tempDir(t);
  git(dir, "init", "-q");
  git(dir, "config", "user.email", "test@example.invalid");
  git(dir, "config", "user.name", "test");
  const claude = {
    name: "shared-market",
    owner: { name: "Owner" },
    metadata: { description: "Ékezetes leírás marad." },
    plugins: [{ name: "other-plugin", source: "./plugins/claude/other-plugin", description: "Other", version: "0.8.8" }],
  };
  const codex = {
    name: "shared-market",
    interface: { displayName: "Shared" },
    plugins: [{
      name: "other-plugin",
      source: { source: "local", path: "./plugins/codex/other-plugin" },
      policy: { installation: "AVAILABLE", authentication: "ON_INSTALL" },
      category: "Productivity",
    }],
  };
  writeJson(join(dir, ".claude-plugin", "marketplace.json"), claude);
  writeJson(join(dir, ".agents", "plugins", "marketplace.json"), codex);
  for (const platform of ["claude", "codex"]) {
    mkdirSync(join(dir, "plugins", platform, "other-plugin"), { recursive: true });
    writeFileSync(join(dir, "plugins", platform, "other-plugin", "file.txt"), "other\n");
  }
  writeFileSync(join(dir, "README.md"), "hand written\n");
  git(dir, "add", "-A");
  git(dir, "commit", "-q", "-m", "start");
  return { dir, claude, codex };
}

function built(t) {
  const { version, built } = buildConnector(tempDir(t), { state: STATE });
  const dirs = { claude: built.claude.dir, codex: built.codex.dir };
  const manifests = { claude: built.claude.manifest, codex: built.codex.manifest };
  return { version, dirs, manifests };
}

test("both plugin sources are complete, named nezolder-civil3d-mcp, agree and are free of private paths", () => {
  const { version, manifests } = checkConnector();
  assert.match(version, /^\d+\.\d+\.\d+$/);
  assert.equal(manifests.claude.name, PLUGIN_NAME);
  assert.equal(manifests.codex.name, PLUGIN_NAME);
  assert.equal(manifests.codex.version, version);
  assert.equal(manifests.codex.mcpServers, "./.mcp.json");
});

test("the Codex .mcp.json starts the launcher relative to the plugin folder, not through a Claude variable", () => {
  const mcp = JSON.parse(readFileSync(join(PACKAGING_DIR, "codex-plugin", ".mcp.json"), "utf8"));
  const server = mcp.mcpServers[SERVER_NAME];
  assert.deepEqual(server.args, [LAUNCHER]);
  assert.equal(server.cwd, ".");
  assert.ok(!JSON.stringify(mcp).includes("CLAUDE_PLUGIN_ROOT"));
});

test("the Codex .mcp.json carries Codex's own time limits, and the check refuses a plugin without them", (t) => {
  const mcp = JSON.parse(readFileSync(join(PACKAGING_DIR, "codex-plugin", ".mcp.json"), "utf8"));
  assert.equal(mcp.mcpServers[SERVER_NAME].tool_timeout_sec, CODEX_TIMEOUTS.tool_timeout_sec);
  assert.equal(mcp.mcpServers[SERVER_NAME].startup_timeout_sec, CODEX_TIMEOUTS.startup_timeout_sec);
  const codex = join(tempDir(t), "codex-plugin");
  cpSync(join(PACKAGING_DIR, "codex-plugin"), codex, { recursive: true });
  delete mcp.mcpServers[SERVER_NAME].tool_timeout_sec;
  writeJson(join(codex, ".mcp.json"), mcp);
  assert.throws(() => checkPluginSource("codex", codex), /tool_timeout_sec must be 660/);
});

test("the Codex .mcp.json asks before every tool except civil3d_skills, and the check refuses other rules", (t) => {
  const mcp = JSON.parse(readFileSync(join(PACKAGING_DIR, "codex-plugin", ".mcp.json"), "utf8"));
  const server = mcp.mcpServers[SERVER_NAME];
  assert.equal(server.default_tools_approval_mode, "prompt");
  assert.deepEqual(server.tools, { civil3d_skills: { approval_mode: "approve" } });
  assert.deepEqual(CODEX_APPROVALS, { default: "prompt", tools: { civil3d_skills: "approve" } });
  const codex = join(tempDir(t), "codex-plugin");
  cpSync(join(PACKAGING_DIR, "codex-plugin"), codex, { recursive: true });
  writeJson(join(codex, ".mcp.json"), { mcpServers: { [SERVER_NAME]: { ...server, tools: { ...server.tools, civil3d_execute: { approval_mode: "approve" } } } } });
  assert.throws(() => checkPluginSource("codex", codex), /tools approval rules must be/);
  const { default_tools_approval_mode: _, ...withoutDefault } = server;
  writeJson(join(codex, ".mcp.json"), { mcpServers: { [SERVER_NAME]: withoutDefault } });
  assert.throws(() => checkPluginSource("codex", codex), /default_tools_approval_mode must be prompt/);
});

test("the check refuses a version mismatch between the platforms", (t) => {
  const codex = join(tempDir(t), "codex-plugin");
  cpSync(join(PACKAGING_DIR, "codex-plugin"), codex, { recursive: true });
  const manifestPath = join(codex, ".codex-plugin", "plugin.json");
  const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  writeJson(manifestPath, { ...manifest, version: "0.0.1" });
  assert.throws(() => checkConnector({ codex }), /versions differ/);
});

test("the check refuses a Codex plugin that relies on a plugin-root variable", (t) => {
  const codex = join(tempDir(t), "codex-plugin");
  cpSync(join(PACKAGING_DIR, "codex-plugin"), codex, { recursive: true });
  const mcpPath = join(codex, ".mcp.json");
  const mcp = JSON.parse(readFileSync(mcpPath, "utf8"));
  mcp.mcpServers[SERVER_NAME].args = [`\${CLAUDE_PLUGIN_ROOT}/${LAUNCHER}`];
  delete mcp.mcpServers[SERVER_NAME].cwd;
  writeJson(mcpPath, mcp);
  assert.throws(() => checkPluginSource("codex", codex), /cwd "\."/);
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

test("a build gives each platform its files, the shared launcher and a receipt", (t) => {
  const out = tempDir(t);
  for (const platform of ["claude", "codex"]) {
    const dir = join(out, platform);
    const { receipt } = buildPlugin(platform, dir, { state: STATE });
    assert.deepEqual(Object.keys(receipt.files), pluginFiles(platform));
    assert.equal(receipt.platform, platform);
    assert.equal(receipt.source_commit, "abc");
    assert.deepEqual(JSON.parse(readFileSync(join(dir, RECEIPT), "utf8")), receipt);
    assert.equal(readFileSync(join(dir, LAUNCHER), "utf8"), readFileSync(LAUNCHER_SOURCE, "utf8"));
  }
  assert.ok(existsSync(join(out, "codex", ".codex-plugin", "plugin.json")));
  assert.ok(!existsSync(join(out, "codex", ".claude-plugin")));
});

test("the marketplace update changes only this plugin's folders and entries", (t) => {
  const { dir, claude, codex } = fakeMarketplace(t);
  const { dirs, manifests } = built(t);

  const changed = updateMarketplace(dir, dirs, manifests);
  assert.ok(changed.includes(".claude-plugin/marketplace.json"));
  assert.ok(changed.includes(".agents/plugins/marketplace.json"));
  assert.ok(changed.some((p) => p.startsWith(`plugins/codex/${PLUGIN_NAME}/`)));

  const claudeAfter = JSON.parse(readFileSync(join(dir, ".claude-plugin", "marketplace.json"), "utf8"));
  assert.equal(claudeAfter.name, claude.name);
  assert.deepEqual(claudeAfter.owner, claude.owner);
  assert.deepEqual(claudeAfter.metadata, claude.metadata);
  assert.deepEqual(claudeAfter.plugins[0], claude.plugins[0]);
  assert.deepEqual(claudeAfter.plugins[1], {
    name: PLUGIN_NAME,
    source: `./plugins/claude/${PLUGIN_NAME}`,
    description: manifests.claude.description,
    version: manifests.claude.version,
  });
  const codexAfter = JSON.parse(readFileSync(join(dir, ".agents", "plugins", "marketplace.json"), "utf8"));
  assert.equal(codexAfter.name, codex.name);
  assert.deepEqual(codexAfter.interface, codex.interface);
  assert.deepEqual(codexAfter.plugins[0], codex.plugins[0]);
  assert.deepEqual(codexAfter.plugins[1], {
    name: PLUGIN_NAME,
    source: { source: "local", path: `./plugins/codex/${PLUGIN_NAME}` },
    policy: { installation: "AVAILABLE", authentication: "ON_INSTALL" },
    category: "Productivity",
  });
  assert.ok(existsSync(join(dir, "plugins", "claude", PLUGIN_NAME, LAUNCHER)));
  assert.ok(existsSync(join(dir, "plugins", "codex", PLUGIN_NAME, ".codex-plugin", "plugin.json")));
  assert.equal(readFileSync(join(dir, "README.md"), "utf8"), "hand written\n");

  // A later release replaces its own entries in place and removes files it no longer ships.
  git(dir, "add", "-A");
  git(dir, "commit", "-q", "-m", "first release");
  writeFileSync(join(dir, "plugins", "codex", PLUGIN_NAME, "stale.txt"), "old\n");
  git(dir, "add", "-A");
  git(dir, "commit", "-q", "-m", "stale file");
  updateMarketplace(dir, dirs, { claude: { ...manifests.claude, version: "9.9.9" }, codex: manifests.codex });
  const again = JSON.parse(readFileSync(join(dir, ".claude-plugin", "marketplace.json"), "utf8"));
  assert.equal(again.plugins.length, 2);
  assert.equal(again.plugins[1].version, "9.9.9");
  assert.equal(JSON.parse(readFileSync(join(dir, ".agents", "plugins", "marketplace.json"), "utf8")).plugins.length, 2);
  assert.ok(!existsSync(join(dir, "plugins", "codex", PLUGIN_NAME, "stale.txt")));
});

test("the marketplace update refuses a clone with changes outside its own paths", (t) => {
  const { dir } = fakeMarketplace(t);
  const { dirs, manifests } = built(t);
  writeFileSync(join(dir, "plugins", "codex", "other-plugin", "file.txt"), "changed\n");
  assert.throws(() => updateMarketplace(dir, dirs, manifests), /does not own:\s+plugins\/codex\/other-plugin\/file\.txt/);
});

test("the marketplace update needs an existing clone with both marketplace files", (t) => {
  const { dirs, manifests } = built(t);
  assert.throws(() => updateMarketplace(tempDir(t), dirs, manifests), /no \.git/);
  const { dir } = fakeMarketplace(t);
  rmSync(join(dir, ".agents"), { recursive: true });
  assert.throws(() => updateMarketplace(dir, dirs, manifests), /\.agents\/plugins\/marketplace\.json.*must exist first/);
});

test("the launcher reports where it looked when no server is installed", (t) => {
  const empty = tempDir(t);
  const env = { ...process.env, LOCALAPPDATA: empty };
  delete env.CIVIL3D_MCP_SERVER_ENTRY;
  const run = spawnSync(process.execPath, [LAUNCHER_SOURCE], { env, encoding: "utf8" });
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
  const run = spawnSync(process.execPath, [LAUNCHER_SOURCE], { env, encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
  assert.equal(run.stdout, "standard server loaded");
});

async function handshake(t, options) {
  const entry = join(repoRoot, "build", "index.js");
  assert.ok(existsSync(entry), "run `npm run build` first");
  const transport = new StdioClientTransport({
    command: process.execPath,
    ...options,
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
}

test("the Claude launch line answers an MCP handshake with the three tools", async (t) => {
  await handshake(t, { args: [LAUNCHER_SOURCE] });
});

test("the Codex launch line (relative launcher, working folder = plugin folder) answers an MCP handshake", async (t) => {
  const dir = join(tempDir(t), "plugin root with space");
  buildPlugin("codex", dir, { state: STATE });
  const server = JSON.parse(readFileSync(join(dir, ".mcp.json"), "utf8")).mcpServers[SERVER_NAME];
  await handshake(t, { args: server.args, cwd: join(dir, server.cwd) });
});
