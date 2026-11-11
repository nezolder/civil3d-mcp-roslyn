// Copies a finished build to the place it runs from, so that the running MCP
// does not depend on what happens in the development folder afterwards.
//
//   node scripts/install.mjs --server [--target <folder>]
//   node scripts/install.mjs --plugin [--bundle <folder>] [--from <build output>] [--backup <folder>]
//
// --server copies the compiled server, the skills and the runtime packages to
//   %LOCALAPPDATA%\Civil3dMcpRoslyn\server (the previous copy is kept next to
//   it as server.previous). Point the MCP client at <target>\build\index.js.
// --plugin copies the built plugin into the Civil 3D autoload bundle. It
//   refuses while any Civil 3D is running, and first copies the installed
//   bundle to a time-stamped backup folder.
//
// Build first: `npm run build`, and for the plugin
// `dotnet build plugin/Civil3dMcpPlugin -c Release`.
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const BUNDLE_NAME = "Civil3dMcpPlugin.bundle";
const PLUGIN_DLL = "Civil3dMcpPlugin.dll";

class InstallError extends Error {}

function parseArguments(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];
    if (argument === "--server" || argument === "--plugin" || argument === "--help") {
      options[argument.slice(2)] = true;
    } else if (["--target", "--bundle", "--from", "--backup"].includes(argument)) {
      const value = argv[++index];
      if (!value) throw new InstallError(`${argument} needs a folder.`);
      options[argument.slice(2)] = path.resolve(value);
    } else {
      throw new InstallError(`Unknown argument: ${argument}`);
    }
  }
  return options;
}

function localApplicationData() {
  if (process.env.LOCALAPPDATA) return process.env.LOCALAPPDATA;
  if (process.platform === "win32") return path.join(os.homedir(), "AppData", "Local");
  return null;
}

function timeStamp(date = new Date()) {
  const pad = (value) => String(value).padStart(2, "0");
  return (
    `${date.getFullYear()}${pad(date.getMonth() + 1)}${pad(date.getDate())}-` +
    `${pad(date.getHours())}${pad(date.getMinutes())}${pad(date.getSeconds())}`
  );
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, "utf8"));
}

function sha256(file) {
  return createHash("sha256").update(fs.readFileSync(file)).digest("hex");
}

// ── Server ──

/** Folders of the packages the server needs at run time (no build tools). */
function collectRuntimePackages() {
  const modulesRoot = path.join(root, "node_modules");
  const found = new Set();
  const queue = Object.keys(readJson(path.join(root, "package.json")).dependencies ?? {}).map(
    (name) => ({ name, from: root, optional: false })
  );
  while (queue.length > 0) {
    const { name, from, optional } = queue.pop();
    const directory = resolvePackage(name, from);
    if (!directory) {
      if (optional) continue;
      throw new InstallError(`Package '${name}' is not installed. Run 'npm ci' first.`);
    }
    if (found.has(directory)) continue;
    found.add(directory);
    const manifest = readJson(path.join(directory, "package.json"));
    for (const dependency of Object.keys(manifest.dependencies ?? {})) {
      queue.push({ name: dependency, from: directory, optional: false });
    }
    for (const dependency of [
      ...Object.keys(manifest.optionalDependencies ?? {}),
      ...Object.keys(manifest.peerDependencies ?? {}),
    ]) {
      queue.push({ name: dependency, from: directory, optional: true });
    }
  }
  return [...found].map((directory) => path.relative(modulesRoot, directory)).sort();

  /** The same upward search Node uses when a package asks for another. */
  function resolvePackage(name, from) {
    let current = from;
    while (true) {
      const candidate = path.join(current, "node_modules", name);
      if (fs.existsSync(path.join(candidate, "package.json"))) return candidate;
      if (current === root) return null;
      const parent = path.dirname(current);
      if (parent === current) return null;
      current = parent;
    }
  }
}

function installServer(options) {
  const buildDirectory = path.join(root, "build");
  const buildInfoFile = path.join(buildDirectory, "build-info.json");
  if (!fs.existsSync(path.join(buildDirectory, "index.js")) || !fs.existsSync(buildInfoFile)) {
    throw new InstallError("The server is not built. Run 'npm run build' first.");
  }
  const dataRoot = localApplicationData();
  const target = options.target ?? (dataRoot && path.join(dataRoot, "Civil3dMcpRoslyn", "server"));
  if (!target) throw new InstallError("Say where to install with --target <folder>.");
  if (target === root || target.startsWith(root + path.sep)) {
    throw new InstallError("The target must be outside the development folder.");
  }

  const buildInfo = readJson(buildInfoFile);
  const staging = `${target}.staging-${process.pid}`;
  const previous = `${target}.previous`;
  fs.rmSync(staging, { recursive: true, force: true });
  try {
    fs.mkdirSync(staging, { recursive: true });
    fs.cpSync(buildDirectory, path.join(staging, "build"), { recursive: true });
    fs.cpSync(path.join(root, "skills"), path.join(staging, "skills"), { recursive: true });
    for (const file of ["package.json", "package-lock.json"]) {
      fs.copyFileSync(path.join(root, file), path.join(staging, file));
    }
    const packages = collectRuntimePackages();
    for (const relative of packages) {
      const source = path.join(root, "node_modules", relative);
      fs.cpSync(source, path.join(staging, "node_modules", relative), {
        recursive: true,
        // Nested packages are copied on their own, and only if they are needed.
        filter: (entry) => entry === source || !path.relative(source, entry).split(path.sep).includes("node_modules"),
      });
    }
    fs.writeFileSync(
      path.join(staging, "install-info.json"),
      JSON.stringify(
        {
          commit: buildInfo.commit,
          dirty: buildInfo.dirty,
          builtAtUtc: buildInfo.builtAtUtc,
          installedAtUtc: new Date().toISOString(),
          packages: packages.length,
        },
        null,
        2
      ) + "\n"
    );

    // Proof that the copy stands on its own: load the tools from it.
    const probe = spawnSync(
      process.execPath,
      [
        "--input-type=module",
        "-e",
        `await import(${JSON.stringify(pathToFileURL(path.join(staging, "build", "tools", "register.js")).href)});`,
      ],
      { cwd: staging, encoding: "utf8", env: { ...process.env, LOG_LEVEL: "error" } }
    );
    if (probe.status !== 0) {
      throw new InstallError(`The copied server does not load:\n${probe.stderr || probe.stdout}`);
    }

    try {
      fs.rmSync(previous, { recursive: true, force: true });
      if (fs.existsSync(target)) fs.renameSync(target, previous);
      fs.renameSync(staging, target);
    } catch (error) {
      if (!fs.existsSync(target) && fs.existsSync(previous)) fs.renameSync(previous, target);
      throw new InstallError(
        `The installed folder could not be replaced (${error.code ?? error.message}). ` +
          "A running MCP server probably uses it: close the AI clients that use the Civil 3D MCP, then run this again. " +
          "The existing installation was left as it was."
      );
    }
  } finally {
    fs.rmSync(staging, { recursive: true, force: true });
  }

  console.log(`Server installed: ${target}`);
  console.log(`  commit ${buildInfo.commit}${buildInfo.dirty ? " (built with uncommitted changes)" : ""}`);
  console.log(`  start it with: node "${path.join(target, "build", "index.js")}"`);
  if (fs.existsSync(previous)) console.log(`  previous copy kept: ${previous}`);
}

// ── Plugin ──

function readPluginStamp(outputDirectory) {
  try {
    // bin/<configuration>/<framework> -> obj/<configuration>/<framework>
    const framework = path.basename(outputDirectory);
    const configuration = path.basename(path.dirname(outputDirectory));
    const stamp = fs.readFileSync(
      path.join(root, "plugin", "Civil3dMcpPlugin", "obj", configuration, framework, "Civil3dMcpBuildStamp.g.cs"),
      "utf8"
    );
    return stamp.match(/"Civil3dMcpCommit", "([^"]+)"/)?.[1] ?? null;
  } catch {
    return null;
  }
}

function assertNotInUse(file) {
  if (!fs.existsSync(file)) return;
  let handle;
  try {
    handle = fs.openSync(file, "r+");
  } catch (error) {
    throw new InstallError(
      `The installed plugin is in use (${error.code ?? error.message}). A running Civil 3D has it loaded. ` +
        "Close every Civil 3D on this computer, then run this again. Nothing was changed."
    );
  } finally {
    if (handle !== undefined) fs.closeSync(handle);
  }
}

function installPlugin(options) {
  if (process.platform !== "win32" && !(options.bundle && options.from)) {
    throw new InstallError("The plugin is installed on the Windows computer that runs Civil 3D.");
  }
  const output =
    options.from ?? path.join(root, "plugin", "Civil3dMcpPlugin", "bin", "Release", "net8.0-windows");
  if (!fs.existsSync(path.join(output, PLUGIN_DLL))) {
    throw new InstallError(
      `No built plugin in ${output}. Run 'dotnet build plugin/Civil3dMcpPlugin -c Release' first.`
    );
  }
  const bundle =
    options.bundle ??
    path.join(
      process.env.APPDATA ?? path.join(os.homedir(), "AppData", "Roaming"),
      "Autodesk",
      "ApplicationPlugins",
      BUNDLE_NAME
    );
  const contents = path.join(bundle, "Contents", "Windows");
  assertNotInUse(path.join(contents, PLUGIN_DLL));

  let backup = null;
  if (fs.existsSync(bundle)) {
    const dataRoot = localApplicationData();
    const backupRoot = options.backup ?? (dataRoot && path.join(dataRoot, "Civil3dMcpRoslyn", "backup"));
    if (!backupRoot) throw new InstallError("Say where to keep the backup with --backup <folder>.");
    backup = path.join(backupRoot, timeStamp(), BUNDLE_NAME);
    if (fs.existsSync(backup)) throw new InstallError(`The backup folder already exists: ${backup}`);
    fs.cpSync(bundle, backup, { recursive: true });
  }

  fs.mkdirSync(contents, { recursive: true });
  fs.copyFileSync(
    path.join(root, "deploy", BUNDLE_NAME, "PackageContents.xml"),
    path.join(bundle, "PackageContents.xml")
  );
  const files = fs
    .readdirSync(output, { withFileTypes: true })
    .filter((entry) => entry.isFile())
    .map((entry) => entry.name)
    .sort();
  for (const name of files) {
    const source = path.join(output, name);
    const destination = path.join(contents, name);
    fs.copyFileSync(source, destination);
    if (sha256(source) !== sha256(destination)) {
      throw new InstallError(
        `${name} was not copied correctly.` + (backup ? ` The previous bundle is in ${backup}.` : "")
      );
    }
  }

  const commit = readPluginStamp(output);
  console.log(`Plugin installed: ${bundle}`);
  console.log(`  ${files.length} files, ${PLUGIN_DLL} sha256 ${sha256(path.join(contents, PLUGIN_DLL))}`);
  if (commit) console.log(`  commit ${commit}`);
  if (backup) console.log(`  previous bundle kept: ${backup}`);
  console.log("  Civil 3D loads it at its next start and asks once whether to load the new file.");
  try {
    const serverCommit = readJson(path.join(root, "build", "build-info.json")).commit;
    if (commit && serverCommit !== "unknown" && commit !== "unknown" && commit !== serverCommit) {
      console.log(`  Note: the built server is from another commit (${serverCommit}).`);
    }
  } catch {
    // No built server next to it: nothing to compare.
  }
}

try {
  const options = parseArguments(process.argv.slice(2));
  if (options.help || (!options.server && !options.plugin)) {
    console.log(
      "Usage:\n" +
        "  node scripts/install.mjs --server [--target <folder>]\n" +
        "  node scripts/install.mjs --plugin [--bundle <folder>] [--from <build output>] [--backup <folder>]"
    );
    process.exit(options.help ? 0 : 2);
  }
  if (options.server) installServer(options);
  if (options.plugin) installPlugin(options);
} catch (error) {
  if (error instanceof InstallError) {
    console.error(error.message);
    process.exit(1);
  }
  throw error;
}
