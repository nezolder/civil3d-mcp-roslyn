import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtemp, mkdir, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const script = path.join(root, "scripts", "install.mjs");

function run(args) {
  return spawnSync(process.execPath, [script, ...args], { cwd: root, encoding: "utf8" });
}

test("the server is copied to a folder that runs on its own and keeps the previous copy", async () => {
  const work = await mkdtemp(path.join(os.tmpdir(), "civil3d-mcp-install-server-"));
  const target = path.join(work, "server");
  try {
    const first = run(["--server", "--target", target]);
    assert.equal(first.status, 0, first.stderr);
    // It prints the settings that start the copy, for JSON clients and for Codex.
    const entry = path.join(target, "build", "index.js");
    assert.ok(first.stdout.includes(JSON.stringify(entry)), "the JSON settings name the installed entry");
    assert.ok(first.stdout.includes(`args = ['${entry}']`), "the Codex settings name the installed entry");
    assert.match(first.stdout, /\[mcp_servers\.civil3d\]/);
    for (const expected of [
      "build/index.js",
      "build/build-info.json",
      "skills",
      "package.json",
      "node_modules/@modelcontextprotocol/sdk/package.json",
      "node_modules/zod/package.json",
    ]) {
      assert.ok(existsSync(path.join(target, expected)), `${expected} must be installed`);
    }
    assert.equal(existsSync(path.join(target, "node_modules", "typescript")), false,
      "build tools are not part of the installed server");
    assert.equal(existsSync(path.join(target, "src")), false);

    const built = JSON.parse(await readFile(path.join(root, "build", "build-info.json"), "utf8"));
    const installed = JSON.parse(await readFile(path.join(target, "install-info.json"), "utf8"));
    assert.equal(installed.commit, built.commit);
    assert.equal(installed.builtAtUtc, built.builtAtUtc);

    // The copy answers an MCP client without the development folder.
    const answer = spawnSync(process.execPath, [path.join(target, "build", "index.js")], {
      cwd: work,
      encoding: "utf8",
      env: { ...process.env, LOG_LEVEL: "error" },
      input:
        JSON.stringify({
          jsonrpc: "2.0",
          id: 1,
          method: "initialize",
          params: {
            protocolVersion: "2025-06-18",
            capabilities: {},
            clientInfo: { name: "install-test", version: "1" },
          },
        }) + "\n",
      timeout: 20_000,
    });
    const reply = JSON.parse(answer.stdout.split("\n")[0]);
    assert.equal(reply.result.serverInfo.name, "civil3d-mcp");
    assert.match(reply.result.serverInfo.version, /^1\.0\.0(\+[0-9a-f]{7})?$/);

    await writeFile(path.join(target, "marker.txt"), "first installation");
    const second = run(["--server", "--target", target]);
    assert.equal(second.status, 0, second.stderr);
    assert.equal(existsSync(path.join(target, "marker.txt")), false, "the new copy replaces the folder");
    assert.equal(
      await readFile(path.join(`${target}.previous`, "marker.txt"), "utf8"),
      "first installation",
      "the replaced copy is kept"
    );
    assert.deepEqual((await readdir(work)).sort(), ["server", "server.previous"], "no staging folder is left behind");

    const inside = run(["--server", "--target", path.join(root, "build", "installed")]);
    assert.equal(inside.status, 1);
    assert.match(inside.stderr, /outside the development folder/);
  } finally {
    await rm(work, { recursive: true, force: true });
  }
});

test("the plugin is copied into the bundle after the installed one was backed up", async () => {
  const work = await mkdtemp(path.join(os.tmpdir(), "civil3d-mcp-install-plugin-"));
  const output = path.join(work, "output");
  const bundle = path.join(work, "ApplicationPlugins", "Civil3dMcpPlugin.bundle");
  const backup = path.join(work, "backup");
  const contents = path.join(bundle, "Contents", "Windows");
  try {
    await mkdir(path.join(output, "de"), { recursive: true });
    await writeFile(path.join(output, "Civil3dMcpPlugin.dll"), "new plugin");
    await writeFile(path.join(output, "Microsoft.CodeAnalysis.dll"), "compiler");
    await writeFile(path.join(output, "de", "resources.dll"), "not needed");

    const missing = run(["--plugin", "--bundle", bundle, "--from", path.join(work, "nothing"), "--backup", backup]);
    assert.equal(missing.status, 1);
    assert.match(missing.stderr, /No built plugin/);
    assert.equal(existsSync(bundle), false, "a refused installation changes nothing");

    const first = run(["--plugin", "--bundle", bundle, "--from", output, "--backup", backup]);
    assert.equal(first.status, 0, first.stderr);
    assert.equal(await readFile(path.join(contents, "Civil3dMcpPlugin.dll"), "utf8"), "new plugin");
    assert.equal(await readFile(path.join(contents, "Microsoft.CodeAnalysis.dll"), "utf8"), "compiler");
    assert.ok(existsSync(path.join(bundle, "PackageContents.xml")));
    assert.equal(existsSync(path.join(contents, "de")), false, "language folders are not installed");
    assert.equal(existsSync(backup), false, "there was nothing to back up the first time");

    await writeFile(path.join(output, "Civil3dMcpPlugin.dll"), "newer plugin");
    const second = run(["--plugin", "--bundle", bundle, "--from", output, "--backup", backup]);
    assert.equal(second.status, 0, second.stderr);
    assert.equal(await readFile(path.join(contents, "Civil3dMcpPlugin.dll"), "utf8"), "newer plugin");
    const [stamp] = await readdir(backup);
    assert.match(stamp, /^\d{8}-\d{6}$/);
    assert.equal(
      await readFile(
        path.join(backup, stamp, "Civil3dMcpPlugin.bundle", "Contents", "Windows", "Civil3dMcpPlugin.dll"),
        "utf8"
      ),
      "new plugin",
      "the replaced bundle is kept as it was"
    );
  } finally {
    await rm(work, { recursive: true, force: true });
  }
});

test("only the newest plugin backups made by the script are kept", async () => {
  const work = await mkdtemp(path.join(os.tmpdir(), "civil3d-mcp-install-prune-"));
  const output = path.join(work, "output");
  const bundle = path.join(work, "ApplicationPlugins", "Civil3dMcpPlugin.bundle");
  const backup = path.join(work, "backup");
  try {
    await mkdir(output, { recursive: true });
    await writeFile(path.join(output, "Civil3dMcpPlugin.dll"), "plugin");
    await mkdir(path.join(bundle, "Contents", "Windows"), { recursive: true });
    await writeFile(path.join(bundle, "Contents", "Windows", "Civil3dMcpPlugin.dll"), "installed");
    for (const old of ["20200101-000000", "20200102-000000", "20200103-000000", "20200104-000000"]) {
      await mkdir(path.join(backup, old), { recursive: true });
    }
    await mkdir(path.join(backup, "kept by hand"), { recursive: true });

    const invalid = run(["--plugin", "--bundle", bundle, "--from", output, "--backup", backup, "--keep-backups", "x"]);
    assert.equal(invalid.status, 1);
    assert.match(invalid.stderr, /--keep-backups/);

    const all = run(["--plugin", "--bundle", bundle, "--from", output, "--backup", backup, "--keep-backups", "0"]);
    assert.equal(all.status, 0, all.stderr);
    assert.equal((await readdir(backup)).length, 6, "0 keeps every backup");

    // A second install in the same second would reuse the stamp; wait for a new one.
    await new Promise((resolve) => setTimeout(resolve, 1100));
    const pruned = run(["--plugin", "--bundle", bundle, "--from", output, "--backup", backup]);
    assert.equal(pruned.status, 0, pruned.stderr);
    const left = (await readdir(backup)).sort();
    assert.equal(left.length, 4, left.join(", "));
    assert.ok(left.includes("kept by hand"), "a folder the script did not name is left alone");
    assert.deepEqual(
      left.filter((name) => name.startsWith("2020")),
      ["20200104-000000"],
      "the two new backups and the newest old one stay, the older ones go"
    );
    assert.match(pruned.stdout, /older backup removed/);
  } finally {
    await rm(work, { recursive: true, force: true });
  }
});

test("without a choice the script explains itself and changes nothing", () => {
  const usage = run([]);
  assert.equal(usage.status, 2);
  assert.match(usage.stdout, /--server/);
  const unknown = run(["--everything"]);
  assert.equal(unknown.status, 1);
  assert.match(unknown.stderr, /Unknown argument/);
});
