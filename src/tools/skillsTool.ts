import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import * as fs from "fs";
import * as path from "path";
import { createLogger } from "../utils/logger.js";
import { fileURLToPath } from "url";
import { InstanceStatus, listInstances, withApplicationConnection } from "../utils/ConnectionManager.js";
import { SERVER_BUILD, SERVER_VERSION, UNKNOWN_COMMIT } from "../buildInfo.js";
import { Civil3dMcpError, createStructuredToolErrorResult } from "../errors/structuredError.js";
import { instanceIdSchema } from "./instanceSelection.js";
import { isRunnableByName, omitCodeTemplate } from "./skillBinding.js";
import { GUIDE_POINTER, GUIDE_TOPICS, GUIDE_UNAVAILABLE_TEXT, usageGuideText } from "../usageGuide.js";

const log = createLogger("SkillsTool");

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
export const SKILLS_DIR = path.resolve(__dirname, "..", "..", "skills");
const DEFAULT_PAGE_LIMIT = 20;
const MAX_PAGE_LIMIT = 50;
const MAX_CURSOR_LENGTH = 256;
const CURSOR_VERSION = 1;

interface ApiLookupParameters extends Record<string, unknown> {
  query: string;
  assembly?: string;
  namespace?: string;
  limit?: number;
}

interface SkillsToolOptions {
  // Narrow test seam; production always uses the private apiLookup JSON-RPC method.
  apiLookup?: (parameters: ApiLookupParameters, instanceId?: string) => Promise<unknown>;
  // Internal fixture directory only; this is not a public MCP input.
  skillsDirectory?: string;
  // Narrow test seam; production asks the running Civil 3D instances for their health.
  listInstances?: () => Promise<InstanceStatus[]>;
}

interface SkillMetadata {
  name: string;
  category: string;
  description: string;
  requires_write: boolean;
  aliases: string[];
  workflow_tags: string[];
  tested_civil_version: string | null;
  validation_summary: string | null;
  parameters: Array<{
    name: string;
    type: string;
    required: boolean;
    description?: string;
  }>;
}

export interface SkillFile {
  metadata: SkillMetadata;
  content: string;
  filePath: string;
}

type PaginatedAction = "list" | "search";

interface PaginationOptions {
  action: PaginatedAction;
  category?: string;
  query?: string;
  limit?: number;
  cursor?: string;
}

const cursorSchema = z
  .object({
    version: z.literal(CURSOR_VERSION),
    offset: z.number().int().min(0).max(Number.MAX_SAFE_INTEGER),
    action: z.enum(["list", "search"]),
    category: z.string().nullable(),
    query: z.string().nullable(),
  })
  .strict();

/**
 * Parse a .skill.md file into metadata + content.
 * Format: YAML frontmatter between --- delimiters, then markdown body.
 */
function parseSkillFile(filePath: string): SkillFile | null {
  try {
    const raw = fs.readFileSync(filePath, "utf-8");
    const frontmatterMatch = raw.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n([\s\S]*)$/);

    if (!frontmatterMatch) {
      log.warn("Skill file missing frontmatter", { filePath });
      return null;
    }

    const yamlBlock = frontmatterMatch[1];
    const content = frontmatterMatch[2].trim();

    // Simple YAML parsing (no dependency needed for our format)
    const metadata: SkillMetadata = {
      name: extractYamlValue(yamlBlock, "name") ?? path.basename(filePath, ".skill.md"),
      category: extractYamlValue(yamlBlock, "category") ?? "general",
      description: extractYamlValue(yamlBlock, "description") ?? "",
      requires_write: extractYamlValue(yamlBlock, "requires_write") === "true",
      aliases: extractDiscoveryStrings(yamlBlock, "aliases"),
      workflow_tags: extractDiscoveryStrings(yamlBlock, "workflow_tags"),
      tested_civil_version: extractDiscoveryString(yamlBlock, "tested_civil_version", 40),
      validation_summary: extractDiscoveryString(yamlBlock, "validation_summary", 280),
      parameters: extractYamlParameters(yamlBlock),
    };

    return { metadata, content, filePath };
  } catch (error) {
    log.error("Error parsing skill file", { filePath, error: String(error) });
    return null;
  }
}

function extractYamlValue(yaml: string, key: string): string | undefined {
  const match = yaml.match(new RegExp(`^${key}:\\s*(.+)$`, "m"));
  return match ? match[1].trim() : undefined;
}

// Discovery fields use JSON-compatible inline YAML values. Missing or invalid
// fields stay unknown; they must never manufacture a validation claim.
function extractDiscoveryJson(yaml: string, key: string): unknown {
  const match = yaml.match(new RegExp(`^${key}:[ \\t]*(.*)$`, "m"));
  if (!match) return undefined;
  try {
    return JSON.parse(match[1]);
  } catch {
    log.warn("Invalid skill discovery field", { key });
    return undefined;
  }
}

function extractDiscoveryString(yaml: string, key: string, maxLength: number): string | null {
  const value = extractDiscoveryJson(yaml, key);
  if (typeof value !== "string" || /\p{Cc}/u.test(value)) return null;
  const text = value.trim();
  return text.length > 0 && text.length <= maxLength ? text : null;
}

function extractDiscoveryStrings(yaml: string, key: string): string[] {
  const value = extractDiscoveryJson(yaml, key);
  if (!Array.isArray(value) || value.length > 8 || value.some((item) =>
    typeof item !== "string" || item.trim().length === 0 || item.length > 100 || /\p{Cc}/u.test(item)
  )) return [];
  return [...new Set(value.map((item: string) => item.trim()))];
}

function discoverySummary(skill: SkillFile) {
  const metadata = skill.metadata;
  // Aliases are a search index and are available in get; keep pages compact.
  return {
    run_by_name: isRunnableByName(skill.content, metadata.parameters),
    workflow_tags: metadata.workflow_tags,
    tested_civil_version: metadata.tested_civil_version,
    validation_summary: metadata.validation_summary,
  };
}

function extractYamlParameters(yaml: string): SkillMetadata["parameters"] {
  const params: SkillMetadata["parameters"] = [];
  const paramSection = yaml.match(/parameters:\r?\n((?:\s+-[\s\S]*?)*)(?:\r?\n\w|$)/);
  if (!paramSection) return params;

  const paramBlocks = paramSection[1].split(/\r?\n\s+-\s+/).filter(Boolean);
  for (const block of paramBlocks) {
    const lines = (block.includes("- ") ? block.replace(/^\s*-\s*/, "") : block)
      .split(/\r?\n/)
      .map((line) => line.trimStart())
      .join("\n");
    const name = extractYamlValue(lines, "name");
    const type = extractYamlValue(lines, "type") ?? "string";
    const required = extractYamlValue(lines, "required") === "true";
    const description = extractYamlValue(lines, "description");
    if (name) {
      params.push({ name, type, required, description });
    }
  }

  return params;
}

/**
 * Recursively find all .skill.md files in the skills directory.
 */
function findSkillFiles(dir: string): string[] {
  if (!fs.existsSync(dir)) return [];

  const results: string[] = [];
  const entries = fs.readdirSync(dir, { withFileTypes: true });

  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      results.push(...findSkillFiles(fullPath));
    } else if (entry.name.endsWith(".skill.md")) {
      results.push(fullPath);
    }
  }

  return results;
}

/** Find one skill by case-insensitive name. */
export function findSkill(name: string, skillsDirectory = SKILLS_DIR): SkillFile | undefined {
  return getSkills(undefined, undefined, skillsDirectory).find(
    (skill) => skill.metadata.name.toLowerCase() === name.toLowerCase()
  );
}

/**
 * Get all available skills, optionally filtered by category or search query.
 */
function getSkills(category?: string, query?: string, skillsDirectory = SKILLS_DIR): SkillFile[] {
  const files = findSkillFiles(skillsDirectory);
  let skills = files.map(parseSkillFile).filter((s): s is SkillFile => s !== null);

  if (category) {
    skills = skills.filter(
      (s) => s.metadata.category.toLowerCase() === category.toLowerCase()
    );
  }

  if (query) return searchSkills(skills, query).skills;

  return skills;
}

/** Shorter words are too likely to match inside unrelated words to rank a partial match. */
const MIN_PARTIAL_WORD_LENGTH = 3;

interface SkillSearchResult {
  skills: SkillFile[];
  /** Set when no skill contains every word and the closest matches are returned instead. */
  partial?: { matchedWords: number; queryWords: number };
}

/**
 * Every whitespace-separated word must occur somewhere in the skill's
 * metadata. Underscores count as spaces so "surface volume" finds
 * surface_volume, and word order does not matter. When no skill has every
 * word, the closest skills are returned and flagged as partial, so one extra
 * or unknown word does not hide the whole catalog. Closeness is the total
 * length of the matched words: a long domain word outweighs short filler
 * words, which match by accident inside other words.
 */
function searchSkills(skills: SkillFile[], query: string): SkillSearchResult {
  const tokens = [...new Set(normalizeSearchText(query).split(" ").filter(Boolean))];
  if (tokens.length === 0) return { skills: [] };

  const scored = skills.map((skill) => {
    const haystack = normalizeSearchText(
      [
        skill.metadata.name,
        skill.metadata.category,
        skill.metadata.description,
        ...skill.metadata.aliases,
        ...skill.metadata.workflow_tags,
        ...skill.metadata.parameters.map((p) => `${p.name} ${p.description ?? ""}`),
      ].join(" ")
    );
    const matched = tokens.filter((token) => haystack.includes(token));
    return {
      skill,
      matched: matched.length,
      weight: matched.reduce(
        (sum, token) => sum + (token.length >= MIN_PARTIAL_WORD_LENGTH ? token.length : 0),
        0
      ),
    };
  });

  const complete = scored.filter((entry) => entry.matched === tokens.length);
  if (complete.length > 0) return { skills: complete.map((entry) => entry.skill) };

  const bestWeight = Math.max(0, ...scored.map((entry) => entry.weight));
  if (bestWeight === 0) return { skills: [] };
  const closest = scored.filter((entry) => entry.weight === bestWeight);
  return {
    skills: closest.map((entry) => entry.skill),
    partial: {
      matchedWords: Math.max(...closest.map((entry) => entry.matched)),
      queryWords: tokens.length,
    },
  };
}

function normalizeSearchText(value: string): string {
  return value.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase()
    .replace(/[_\s\-\u2010-\u2015]+/g, " ").trim();
}

function sortSkills(skills: SkillFile[]): SkillFile[] {
  return [...skills].sort((left, right) => {
    const leftJson = JSON.stringify(left.metadata);
    const rightJson = JSON.stringify(right.metadata);
    const leftKey = `${leftJson.toLowerCase()}\0${leftJson}`;
    const rightKey = `${rightJson.toLowerCase()}\0${rightJson}`;
    return leftKey < rightKey ? -1 : leftKey > rightKey ? 1 : 0;
  });
}

function normalizeCursorFilter(value: string | undefined): string | null {
  return value ? value.toLowerCase() : null;
}

function encodeCursor(offset: number, options: PaginationOptions): string {
  const payload = {
    version: CURSOR_VERSION,
    offset,
    action: options.action,
    category: normalizeCursorFilter(options.category),
    query:
      options.action === "search" ? normalizeCursorFilter(options.query) : null,
  };

  return Buffer.from(JSON.stringify(payload), "utf8").toString("base64url");
}

function decodeCursor(cursor: string, options: PaginationOptions): number {
  if (
    cursor.length === 0 ||
    cursor.length > MAX_CURSOR_LENGTH ||
    !/^[A-Za-z0-9_-]+$/.test(cursor)
  ) {
    throw new Error("Parameter 'cursor' is invalid.");
  }

  let payload: z.infer<typeof cursorSchema>;
  try {
    const decoded = Buffer.from(cursor, "base64url");
    if (decoded.toString("base64url") !== cursor) {
      throw new Error();
    }
    payload = cursorSchema.parse(JSON.parse(decoded.toString("utf8")));
  } catch {
    throw new Error("Parameter 'cursor' is invalid.");
  }

  const expectedQuery =
    options.action === "search" ? normalizeCursorFilter(options.query) : null;
  if (
    payload.action !== options.action ||
    payload.category !== normalizeCursorFilter(options.category) ||
    payload.query !== expectedQuery
  ) {
    throw new Error("Parameter 'cursor' does not match the current action and filters.");
  }

  return payload.offset;
}

function getPageLimit(limit: number | undefined): number {
  if (limit === undefined) return DEFAULT_PAGE_LIMIT;
  if (!Number.isInteger(limit) || limit < 1 || limit > MAX_PAGE_LIMIT) {
    throw new Error(
      `Parameter 'limit' must be an integer between 1 and ${MAX_PAGE_LIMIT}.`
    );
  }
  return limit;
}

function paginateSkills(skills: SkillFile[], options: PaginationOptions) {
  const sortedSkills = sortSkills(skills);
  const limit = getPageLimit(options.limit);
  const offset = options.cursor ? decodeCursor(options.cursor, options) : 0;

  if (options.cursor && offset >= sortedSkills.length) {
    throw new Error("Parameter 'cursor' is outside the filtered result set.");
  }

  const items = sortedSkills.slice(offset, offset + limit);
  const nextOffset = offset + items.length;
  const truncated = nextOffset < sortedSkills.length;

  return {
    items,
    total: sortedSkills.length,
    truncated,
    nextCursor: truncated ? encodeCursor(nextOffset, options) : null,
  };
}

const shortCommit = (commit: string) => (commit === UNKNOWN_COMMIT ? commit : commit.slice(0, 7));

/**
 * Which server is answering, from which folder, and which Civil 3D instances
 * it can reach with which plugin build. Reads health replies only.
 */
function buildStatus(instances: InstanceStatus[], skillsDirectory: string) {
  const skills = getSkills(undefined, undefined, skillsDirectory);
  const warnings: string[] = [];
  if (SERVER_BUILD.commit === UNKNOWN_COMMIT) {
    warnings.push("The MCP server does not know which commit it was built from.");
  } else if (SERVER_BUILD.dirty) {
    warnings.push("The MCP server was built from a folder with uncommitted changes.");
  }
  if (instances.length === 0) {
    warnings.push("No running Civil 3D with the MCP plugin was found.");
  }
  for (const instance of instances) {
    const label = `Instance ${instance.instanceId} (process ${instance.processId ?? "unknown"})`;
    if (!instance.pluginBuild) {
      warnings.push(`${label} runs an older plugin that does not report its build; update the plugin.`);
    } else if (instance.buildMatchesServer === false) {
      warnings.push(
        `${label} runs a plugin built from ${shortCommit(instance.pluginBuild.commit)}, ` +
          `the MCP server from ${shortCommit(SERVER_BUILD.commit)}; update the older side.`
      );
    } else if (instance.pluginBuild.dirty) {
      warnings.push(`${label} runs a plugin built from a folder with uncommitted changes.`);
    }
  }
  return {
    server: {
      name: "civil3d-mcp",
      version: SERVER_VERSION,
      commit: SERVER_BUILD.commit,
      dirty: SERVER_BUILD.dirty,
      builtAtUtc: SERVER_BUILD.builtAtUtc,
      skillsDirectory,
      skillCount: skills.length,
      runByNameCount: skills.filter((skill) =>
        isRunnableByName(skill.content, skill.metadata.parameters)
      ).length,
    },
    instances,
    warnings,
    guide: GUIDE_POINTER,
  };
}

export function registerSkillsTool(server: McpServer, options: SkillsToolOptions = {}) {
  const skillsDirectory = options.skillsDirectory ?? SKILLS_DIR;
  const readInstances = options.listInstances ?? listInstances;
  const apiLookup = options.apiLookup ?? ((parameters: ApiLookupParameters, instanceId?: string) =>
    withApplicationConnection(
      (client) => client.sendCommand("apiLookup", parameters),
      { instanceId }
    )
  );

  server.tool(
    "civil3d_skills",
    "Browse and read Civil 3D code skills (documented C# code templates). " +
      "Use 'list' to see available skills, 'search' to find by keyword, " +
      "'get' to read the full skill with code template, 'api_lookup' to search public metadata from already-loaded Civil 3D host assemblies, " +
      "'status' to see the server's version and folder and every running Civil 3D instance with its plugin version, " +
      "active drawing and instanceId (runs no command in Civil 3D), " +
      "or 'guide' to read how to work with this server: the working order, and with a topic the details on " +
      "identifying the target drawing, writing, errors and C# scripts. Read the guide before the first write. " +
      "Search matches every word across metadata, Hungarian/English aliases and workflow tags, ignoring accents and hyphens; " +
      "when no skill has every word it returns the closest ones with partial=true. " +
      "List/search include write scope, tested Civil version and a scoped validation summary; missing evidence stays null. " +
      "Skills are pre-built C# patterns you can adapt and execute via civil3d_execute or civil3d_query. " +
      "When run_by_name is true, read it with get and includeCode=false, then pass skill and params to " +
      "civil3d_query (read skills) or civil3d_execute (write skills) instead of sending the code.",
    {
      action: z
        .enum(["list", "search", "get", "api_lookup", "status", "guide"])
        .describe(
          "list = browse skill metadata, search = find by keyword, get = read full skill, " +
            "api_lookup = read-only public API metadata search, status = server and plugin versions plus running instances, " +
            "guide = how to work with this server (working order; details with topic)"
        ),
      topic: z
        .enum(GUIDE_TOPICS)
        .optional()
        .describe(
          "For 'guide': drawing = identify the target drawing and read, writing = safety inputs, receipt and readback, " +
            "errors = outcomes, time limits and uncertain results, csharp = script rules and API pitfalls. " +
            "Omit it for the short working order."
        ),
      category: z.string().optional().describe("Filter by category (surfaces, alignments, points, etc.)"),
      query: z.string().optional().describe("Search query for 'search' or 'api_lookup' action"),
      skillName: z.string().optional().describe("Skill name for 'get' action"),
      includeCode: z
        .boolean()
        .optional()
        .describe("For 'get': false omits the code template (default true). Use it for skills you run by name."),
      assembly: z.string().optional().describe("Allowlisted loaded host assembly filter for api_lookup"),
      namespace: z.string().optional().describe("Namespace prefix filter for api_lookup"),
      instanceId: instanceIdSchema,
      limit: z
        .number()
        .optional()
        .describe(
          `Maximum list/search/api_lookup results to return (integer 1-${MAX_PAGE_LIMIT}; default ${DEFAULT_PAGE_LIMIT})`
        ),
      cursor: z
        .string()
        .optional()
        .describe("Opaque nextCursor from a prior list/search call with the same filters"),
    },
    { title: "Civil 3D skills and API lookup", readOnlyHint: true, openWorldHint: false },
    async (args) => {
      try {
        switch (args.action) {
          case "list": {
            const page = paginateSkills(getSkills(args.category, undefined, skillsDirectory), {
              action: "list",
              category: args.category,
              limit: args.limit,
              cursor: args.cursor,
            });
            const summary = page.items.map((s) => ({
              name: s.metadata.name,
              category: s.metadata.category,
              description: s.metadata.description,
              requires_write: s.metadata.requires_write,
              parameters: s.metadata.parameters.map((p) => p.name),
              ...discoverySummary(s),
            }));

            return {
              content: [
                {
                  type: "text" as const,
                  text: JSON.stringify(
                    {
                      count: summary.length,
                      skills: summary,
                      total: page.total,
                      returned: summary.length,
                      truncated: page.truncated,
                      nextCursor: page.nextCursor,
                    }),
                },
              ],
            };
          }

          case "search": {
            if (!args.query) {
              return {
                content: [{ type: "text" as const, text: "Parameter 'query' is required for search." }],
                isError: true,
              };
            }

            const found = searchSkills(
              getSkills(args.category, undefined, skillsDirectory),
              args.query
            );
            const page = paginateSkills(found.skills, {
              action: "search",
              category: args.category,
              query: args.query,
              limit: args.limit,
              cursor: args.cursor,
            });
            const results = page.items.map((s) => ({
              name: s.metadata.name,
              category: s.metadata.category,
              description: s.metadata.description,
              requires_write: s.metadata.requires_write,
              ...discoverySummary(s),
            }));

            return {
              content: [
                {
                  type: "text" as const,
                  text: JSON.stringify(
                    {
                      count: results.length,
                      results,
                      total: page.total,
                      returned: results.length,
                      truncated: page.truncated,
                      nextCursor: page.nextCursor,
                      ...(found.partial
                        ? {
                            partial: true,
                            matchedWords: found.partial.matchedWords,
                            queryWords: found.partial.queryWords,
                          }
                        : {}),
                    }),
                },
              ],
            };
          }

          case "get": {
            if (!args.skillName) {
              return {
                content: [{ type: "text" as const, text: "Parameter 'skillName' is required for get." }],
                isError: true,
              };
            }

            const allSkills = getSkills(undefined, undefined, skillsDirectory);
            const skill = allSkills.find(
              (s) => s.metadata.name.toLowerCase() === args.skillName!.toLowerCase()
            );

            if (!skill) {
              const available = allSkills.map((s) => s.metadata.name).join(", ");
              return {
                content: [
                  {
                    type: "text" as const,
                    text: `Skill '${args.skillName}' not found. Available: ${available}`,
                  },
                ],
                isError: true,
              };
            }

            return {
              content: [
                {
                  type: "text" as const,
                  text: JSON.stringify(
                    {
                      ...skill.metadata,
                      run_by_name: isRunnableByName(skill.content, skill.metadata.parameters),
                      content: args.includeCode === false ? omitCodeTemplate(skill.content) : skill.content,
                    }),
                },
              ],
            };
          }

          case "api_lookup": {
            if (!args.query) {
              return createStructuredToolErrorResult(
                new Civil3dMcpError(
                  "CIVIL3D.INVALID_INPUT",
                  "Parameter 'query' is required for api_lookup.",
                  "validation",
                  "node",
                  "not_started"
                ),
                "API lookup failed: "
              );
            }

            const result = await apiLookup(
              {
                query: args.query,
                assembly: args.assembly,
                namespace: args.namespace,
                limit: args.limit,
              },
              args.instanceId
            );
            return {
              content: [{ type: "text" as const, text: JSON.stringify(result) }],
            };
          }

          case "status": {
            return {
              content: [
                {
                  type: "text" as const,
                  text: JSON.stringify(buildStatus(await readInstances(), skillsDirectory)),
                },
              ],
            };
          }

          case "guide": {
            // Plain markdown, not JSON: the text is meant to be read as it is.
            const text = usageGuideText(args.topic);
            return text === null
              ? { content: [{ type: "text" as const, text: GUIDE_UNAVAILABLE_TEXT }], isError: true }
              : { content: [{ type: "text" as const, text }] };
          }
        }
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        log.error("Skills operation failed", { error: message });
        if (args.action === "api_lookup") {
          return createStructuredToolErrorResult(error, "API lookup failed: ");
        }
        if (args.action === "status") {
          return createStructuredToolErrorResult(error, "Status failed: ");
        }
        return {
          content: [{ type: "text" as const, text: `Skills error: ${message}` }],
          isError: true,
        };
      }
    }
  );
}
