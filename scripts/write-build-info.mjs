// Runs at the end of `npm run build`: records which commit the compiled
// server came from, so that a running server can say so (see src/buildInfo.ts).
import { execFileSync } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const COMMIT = /^[0-9a-f]{40}$/;

function git(args) {
  try {
    return execFileSync("git", args, {
      cwd: root,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
      windowsHide: true,
    }).trim();
  } catch {
    return null;
  }
}

/** Without a usable git: follow .git/HEAD by hand. Enough for a plain checkout or worktree. */
function readHeadFromFiles() {
  try {
    let gitDir = path.join(root, ".git");
    if (fs.statSync(gitDir).isFile()) {
      const pointer = fs.readFileSync(gitDir, "utf8").match(/^gitdir:\s*(.+)$/m);
      if (!pointer) return null;
      gitDir = path.resolve(root, pointer[1].trim());
    }
    const head = fs.readFileSync(path.join(gitDir, "HEAD"), "utf8").trim();
    if (COMMIT.test(head)) return head;
    const ref = head.match(/^ref:\s*(.+)$/)?.[1];
    if (!ref) return null;
    const commonDirFile = path.join(gitDir, "commondir");
    const commonDir = fs.existsSync(commonDirFile)
      ? path.resolve(gitDir, fs.readFileSync(commonDirFile, "utf8").trim())
      : gitDir;
    for (const dir of [gitDir, commonDir]) {
      const refFile = path.join(dir, ref);
      if (fs.existsSync(refFile)) {
        const value = fs.readFileSync(refFile, "utf8").trim();
        if (COMMIT.test(value)) return value;
      }
    }
    const packed = path.join(commonDir, "packed-refs");
    if (fs.existsSync(packed)) {
      for (const line of fs.readFileSync(packed, "utf8").split(/\r?\n/)) {
        const [value, name] = line.split(" ");
        if (name === ref && COMMIT.test(value)) return value;
      }
    }
    return null;
  } catch {
    return null;
  }
}

const override = process.env.CIVIL3D_MCP_COMMIT?.trim().toLowerCase();
let commit = null;
let dirty = null;
if (override && COMMIT.test(override)) {
  commit = override;
} else {
  const head = git(["rev-parse", "HEAD"]);
  if (head && COMMIT.test(head)) {
    commit = head;
    const status = git(["status", "--porcelain", "--untracked-files=no"]);
    dirty = status === null ? null : status.length > 0;
  } else {
    commit = readHeadFromFiles();
  }
}

const info = {
  commit: commit ?? "unknown",
  dirty,
  builtAtUtc: new Date().toISOString(),
};
const target = path.join(root, "build", "build-info.json");
fs.mkdirSync(path.dirname(target), { recursive: true });
fs.writeFileSync(target, JSON.stringify(info, null, 2) + "\n");
console.log(`build-info: ${info.commit}${info.dirty ? " (uncommitted changes)" : ""}`);
