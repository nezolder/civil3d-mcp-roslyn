import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import * as fs from "fs";
import * as path from "path";
import { createLogger } from "../utils/logger.js";
import { fileURLToPath } from "url";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
import { Civil3dMcpError, createStructuredToolErrorResult } from "../errors/structuredError.js";
import { instanceIdSchema } from "./instanceSelection.js";
import { isRunnableByName, omitCodeTemplate } from "./skillBinding.js";

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

  if (query) {
    // Every whitespace-separated word must occur somewhere in the skill's
    // metadata. Underscores count as spaces so "surface volume" finds
    // surface_volume, and word order does not matter.
    const tokens = normalizeSearchText(query).split(" ").filter(Boolean);
    if (tokens.length === 0) return [];
    skills = skills.filter((s) => {
      const haystack = normalizeSearchText(
        [
          s.metadata.name,
          s.metadata.category,
          s.metadata.description,
          ...s.metadata.aliases,
          ...s.metadata.workflow_tags,
          ...s.metadata.parameters.map((p) => `${p.name} ${p.description ?? ""}`),
        ].join(" ")
      );
      return tokens.every((token) => haystack.includes(token));
    });
  }

  return skills;
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

export function registerSkillsTool(server: McpServer, options: SkillsToolOptions = {}) {
  const skillsDirectory = options.skillsDirectory ?? SKILLS_DIR;
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
      "'get' to read the full skill with code template, or 'api_lookup' to search public metadata from already-loaded Civil 3D host assemblies. " +
      "Search matches every word across metadata, Hungarian/English aliases and workflow tags, ignoring accents and hyphens. " +
      "List/search include write scope, tested Civil version and a scoped validation summary; missing evidence stays null. " +
      "Skills are pre-built C# patterns you can adapt and execute via civil3d_execute or civil3d_query. " +
      "When run_by_name is true, read it with get and includeCode=false, then pass skill and params to " +
      "civil3d_query (read skills) or civil3d_execute (write skills) instead of sending the code.",
    {
      action: z
        .enum(["list", "search", "get", "api_lookup"])
        .describe("list = browse skill metadata, search = find by keyword, get = read full skill, api_lookup = read-only public API metadata search"),
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

            const page = paginateSkills(getSkills(args.category, args.query, skillsDirectory), {
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
        }
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        log.error("Skills operation failed", { error: message });
        if (args.action === "api_lookup") {
          return createStructuredToolErrorResult(error, "API lookup failed: ");
        }
        return {
          content: [{ type: "text" as const, text: `Skills error: ${message}` }],
          isError: true,
        };
      }
    }
  );
}
