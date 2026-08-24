import path from "node:path";
import process from "node:process";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import {
  TOKEN_ESTIMATOR_FORMULA,
  TOKEN_ESTIMATOR_ID,
  countCharacters,
  countUtf8Bytes,
  estimateTokensFromUtf8Bytes,
  sha256Utf8,
} from "./measure.js";
import { TOOLS_LIST_SCHEMA_VERSION } from "./schema.js";

interface DescriptionEntry {
  path: string;
  text: string;
}

export interface DescriptionSummary {
  field_count: number;
  characters: number;
  utf8_bytes: number;
  estimated_tokens: number;
  sha256: string;
}

export interface ToolDescriptionSnapshot {
  name: string;
  top_level_description: DescriptionSummary;
  all_description_fields: DescriptionSummary;
}

export interface ToolsListSnapshot {
  schema_version: typeof TOOLS_LIST_SCHEMA_VERSION;
  character_unit: "unicode_code_points";
  token_estimator: {
    id: typeof TOKEN_ESTIMATOR_ID;
    formula: typeof TOKEN_ESTIMATOR_FORMULA;
  };
  public_tool_count: number;
  tools: ToolDescriptionSnapshot[];
  totals: DescriptionSummary;
}

function collectDescriptionEntries(
  value: unknown,
  currentPath = "$",
  output: DescriptionEntry[] = []
): DescriptionEntry[] {
  if (Array.isArray(value)) {
    value.forEach((item, index) =>
      collectDescriptionEntries(item, `${currentPath}[${index}]`, output)
    );
    return output;
  }
  if (typeof value !== "object" || value === null) return output;

  const record = value as Record<string, unknown>;
  for (const key of Object.keys(record).sort()) {
    const childPath = `${currentPath}.${key}`;
    if (key === "description" && typeof record[key] === "string") {
      output.push({ path: childPath, text: record[key] as string });
    } else {
      collectDescriptionEntries(record[key], childPath, output);
    }
  }
  return output;
}

function summarizeDescriptions(entries: readonly DescriptionEntry[]): DescriptionSummary {
  let characters = 0;
  let utf8Bytes = 0;
  let estimatedTokens = 0;
  for (const entry of entries) {
    const bytes = countUtf8Bytes(entry.text);
    characters += countCharacters(entry.text);
    utf8Bytes += bytes;
    estimatedTokens += estimateTokensFromUtf8Bytes(bytes);
  }

  return {
    field_count: entries.length,
    characters,
    utf8_bytes: utf8Bytes,
    estimated_tokens: estimatedTokens,
    sha256: sha256Utf8(JSON.stringify(entries)),
  };
}

export function createToolsListSnapshot(rawTools: readonly unknown[]): ToolsListSnapshot {
  const tools = rawTools.map((rawTool) => {
    if (typeof rawTool !== "object" || rawTool === null || Array.isArray(rawTool)) {
      throw new Error("tools/list entries must be objects");
    }
    const tool = rawTool as Record<string, unknown>;
    if (typeof tool.name !== "string" || tool.name.length === 0) {
      throw new Error("tools/list entry is missing a name");
    }
    const topLevelDescription = typeof tool.description === "string" ? tool.description : "";
    const allEntries = collectDescriptionEntries(tool);
    return {
      name: tool.name,
      top_level_description: summarizeDescriptions([
        { path: "$.description", text: topLevelDescription },
      ]),
      all_description_fields: summarizeDescriptions(allEntries),
      allEntries,
    };
  });
  tools.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));

  const names = tools.map((tool) => tool.name);
  if (new Set(names).size !== names.length) {
    throw new Error("tools/list contains duplicate tool names");
  }

  const totalEntries = tools.flatMap((tool) =>
    tool.allEntries.map((entry) => ({
      path: `${tool.name}:${entry.path}`,
      text: entry.text,
    }))
  );

  return {
    schema_version: TOOLS_LIST_SCHEMA_VERSION,
    character_unit: "unicode_code_points",
    token_estimator: {
      id: TOKEN_ESTIMATOR_ID,
      formula: TOKEN_ESTIMATOR_FORMULA,
    },
    public_tool_count: tools.length,
    tools: tools.map(({ allEntries: _allEntries, ...tool }) => tool),
    totals: summarizeDescriptions(totalEntries),
  };
}

export async function captureToolsListFromBuiltServer(
  serverPath = path.resolve(process.cwd(), "build", "index.js")
): Promise<ToolsListSnapshot> {
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [serverPath],
    cwd: process.cwd(),
    env: {
      ...process.env,
      LOG_LEVEL: "error",
    },
  });
  const client = new Client({
    name: "civil3d-mcp-benchmark-tools-list",
    version: "1.0.0",
  });

  try {
    await client.connect(transport);
    const response = await client.listTools();
    return createToolsListSnapshot(response.tools);
  } finally {
    await client.close();
  }
}
