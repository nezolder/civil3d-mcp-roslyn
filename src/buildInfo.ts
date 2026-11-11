import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";

/** Which source state a program was built from. */
export interface BuildInfo {
  /** Full commit hash, or "unknown" when the build could not tell. */
  commit: string;
  /** True when tracked files had uncommitted changes at build time; null when unknown. */
  dirty: boolean | null;
  builtAtUtc: string | null;
}

export const UNKNOWN_COMMIT = "unknown";
export const SERVER_VERSION = "1.0.0";

/**
 * Written next to the compiled files by scripts/write-build-info.mjs at the
 * end of `npm run build`. A missing or broken file means "unknown", never an
 * error: the server must start either way.
 */
function readServerBuild(): BuildInfo {
  try {
    const file = path.join(path.dirname(fileURLToPath(import.meta.url)), "build-info.json");
    const parsed = JSON.parse(fs.readFileSync(file, "utf8")) as Record<string, unknown>;
    return {
      commit:
        typeof parsed.commit === "string" && /^[0-9a-f]{40}$/.test(parsed.commit)
          ? parsed.commit
          : UNKNOWN_COMMIT,
      dirty: typeof parsed.dirty === "boolean" ? parsed.dirty : null,
      builtAtUtc: typeof parsed.builtAtUtc === "string" ? parsed.builtAtUtc : null,
    };
  } catch {
    return { commit: UNKNOWN_COMMIT, dirty: null, builtAtUtc: null };
  }
}

export const SERVER_BUILD: BuildInfo = readServerBuild();

/** "1.0.0+abc1234" when the commit is known, plain "1.0.0" otherwise. */
export function serverVersionLabel(build: BuildInfo = SERVER_BUILD): string {
  return build.commit === UNKNOWN_COMMIT
    ? SERVER_VERSION
    : `${SERVER_VERSION}+${build.commit.slice(0, 7)}`;
}

/** Null when either side does not know its commit, so nothing can be said. */
export function buildsDiffer(left: BuildInfo, right: BuildInfo): boolean | null {
  if (left.commit === UNKNOWN_COMMIT || right.commit === UNKNOWN_COMMIT) return null;
  return left.commit.toLowerCase() !== right.commit.toLowerCase();
}
