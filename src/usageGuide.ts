import * as fs from "fs";
import * as path from "path";
import { fileURLToPath } from "url";
import { createLogger } from "./utils/logger.js";

const log = createLogger("UsageGuide");

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

/**
 * The working order an assistant follows with this server lives in one file,
 * docs/USAGE.md. The build copies it next to the compiled server, so every
 * installed build carries the text it was built with.
 */
const GUIDE_FILE_NAME = "USAGE.md";
const GUIDE_FILE_CANDIDATES = [
  path.join(__dirname, GUIDE_FILE_NAME),
  path.resolve(__dirname, "..", "docs", GUIDE_FILE_NAME),
];

/** Part sent as the server's MCP instructions and as the default guide reply. */
const CORE_TOPIC = "core";
/** Parts read on request with civil3d_skills action "guide" and a topic. */
export const GUIDE_TOPICS = ["drawing", "writing", "errors", "csharp"] as const;
export type GuideTopic = (typeof GUIDE_TOPICS)[number];

/** Always-delivered part; a client pays for it in every conversation. */
export const MAX_CORE_BYTES = 2048;
export const MAX_TOPIC_BYTES = 6144;

const TOPIC_MARKER = /^<!-- topic: ([a-z]+) -->[ \t]*\r?$/gm;

export interface UsageGuide {
  core: string;
  topics: Record<GuideTopic, string>;
}

/** Splits the guide file at its `<!-- topic: name -->` marker lines. */
export function parseUsageGuide(markdown: string): UsageGuide {
  const parts = new Map<string, string>();
  const markers = [...markdown.matchAll(TOPIC_MARKER)];
  markers.forEach((marker, index) => {
    const start = marker.index + marker[0].length;
    const end = index + 1 < markers.length ? markers[index + 1].index : markdown.length;
    const name = marker[1];
    if (parts.has(name)) throw new Error(`The usage guide names the topic '${name}' twice.`);
    parts.set(name, markdown.slice(start, end).replace(/\r\n/g, "\n").trim());
  });

  const read = (name: string): string => {
    const text = parts.get(name);
    if (!text) throw new Error(`The usage guide has no '${name}' part.`);
    return text;
  };
  const topics = Object.fromEntries(GUIDE_TOPICS.map((topic) => [topic, read(topic)])) as Record<
    GuideTopic,
    string
  >;
  return { core: read(CORE_TOPIC), topics };
}

let cached: UsageGuide | null | undefined;

/**
 * The guide of this build, or null when its file is missing or malformed.
 * A missing guide must not stop the server: the three tools still work.
 */
export function loadUsageGuide(): UsageGuide | null {
  if (cached !== undefined) return cached;
  cached = null;
  for (const filePath of GUIDE_FILE_CANDIDATES) {
    if (!fs.existsSync(filePath)) continue;
    try {
      cached = parseUsageGuide(fs.readFileSync(filePath, "utf-8"));
    } catch (error) {
      log.error("Usage guide could not be read", { filePath, error: String(error) });
    }
    break;
  }
  if (cached === null) log.warn("Usage guide not found next to the server");
  return cached;
}

export const GUIDE_UNAVAILABLE_TEXT =
  "The usage guide file (USAGE.md) is missing from this installation of the Civil 3D MCP server. " +
  "Reinstall or rebuild the server; the text is docs/USAGE.md in its repository.";

/** One sentence that leads from any reply to the guide. */
export const GUIDE_POINTER =
  `Working order for this server: civil3d_skills with action "guide" ` +
  `(optional topic: ${GUIDE_TOPICS.join(", ")}).`;

/** Text for the server's MCP instructions; undefined when there is no guide. */
export function usageInstructions(): string | undefined {
  return loadUsageGuide()?.core;
}

/** Reply of civil3d_skills action "guide". */
export function usageGuideText(topic?: GuideTopic): string | null {
  const guide = loadUsageGuide();
  if (!guide) return null;
  return topic ? guide.topics[topic] : guide.core;
}
