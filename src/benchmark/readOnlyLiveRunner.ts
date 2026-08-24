import path from "node:path";
import { lstat as nodeLstat } from "node:fs/promises";
import type { BenchmarkManifestBinding } from "./manifest.js";
import { assertManifestRunPosition } from "./manifest.js";
import { sha256Utf8 } from "./measure.js";
import { recordBenchmarkEvents } from "./recorder.js";
import {
  EVENT_SCHEMA_VERSION,
  OPAQUE_BENCHMARK_RUN_ID_PATTERN,
  RUN_SCHEMA_VERSION,
  SAFE_IDENTIFIER_PATTERN,
  type AnyBenchmarkRunRecord,
  type TemperatureState,
} from "./schema.js";
import { MCP_BENCHMARK_META_KEY } from "./liveTrace.js";

export const READ_ONLY_LIVE_SCENARIO_ID = "query.bounded-summary.synthetic.v1";
export const EXPECTED_DRAWING_ENV = "CIVIL3D_BENCHMARK_EXPECTED_DRAWING";
export const READ_ONLY_SDK_TIMEOUT_GRACE_MS = 10_000;

const EXPECTED_PUBLIC_TOOLS = [
  "civil3d_execute",
  "civil3d_query",
  "civil3d_skills",
] as const;

/**
 * Fixed, reviewed query used by the 2A.2 live runner. The runner never accepts
 * caller-supplied C# or prompts.
 */
export const READ_ONLY_LIVE_QUERY_CODE = `
var surfaceCount = CivilDoc.GetSurfaceIds().Count;
var alignmentCount = CivilDoc.GetAlignmentIds().Count;
var cogoPointCount = CivilDoc.CogoPoints.Count;
var dbmod = System.Convert.ToInt32(
  Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("DBMOD")
);

return new {
  schemaVersion = "civil3d-mcp-readonly-live-payload/v1",
  databaseFilename = Database.Filename,
  dbmod,
  alignmentCount,
  surfaceCount,
  cogoPointCount
};
`.trim();

export type ReadOnlyLiveProbeErrorCode =
  | "PROBE.INVALID_INPUT"
  | "PROBE.INVALID_CONNECTION_CONFIG"
  | "PROBE.INVALID_OUTPUT"
  | "PROBE.SCENARIO_NOT_ALLOWED"
  | "PROBE.CONNECTION_FAILED"
  | "PROBE.TOOL_SURFACE_MISMATCH"
  | "PROBE.SKILLS_PREFLIGHT_FAILED"
  | "PROBE.QUERY_FAILED"
  | "PROBE.INVALID_QUERY_RESPONSE"
  | "PROBE.DRAWING_GUARD_FAILED"
  | "PROBE.DRAWING_NOT_CLEAN"
  | "PROBE.MEASUREMENT_INCOMPLETE"
  | "PROBE.CLIENT_CLOSE_FAILED";

export class ReadOnlyLiveProbeError extends Error {
  constructor(readonly code: ReadOnlyLiveProbeErrorCode) {
    super(code);
    this.name = "ReadOnlyLiveProbeError";
  }
}

export interface ReadOnlyProbeToolResult {
  content: Array<{ type: string; text?: string }>;
  isError?: boolean;
  _meta?: Record<string, unknown>;
  [key: string]: unknown;
}

export interface ReadOnlyProbeClient {
  listTools(): Promise<{ tools: Array<{ name: string }> }>;
  callTool(params: {
    name: string;
    arguments?: Record<string, unknown>;
    _meta?: Record<string, unknown>;
  }): Promise<ReadOnlyProbeToolResult>;
  close(): Promise<void>;
}

export interface ReadOnlyLiveRunInput {
  binding: BenchmarkManifestBinding;
  serverPath: string;
  scenarioId: string;
  variantId: string;
  iteration: number;
  temperatureState: TemperatureState;
  expectedDrawingPath: string;
}

export interface ReadOnlyLiveRunDependencies {
  connect(serverPath: string): Promise<ReadOnlyProbeClient>;
  createRunId(): string;
}

export interface ReadOnlyConnectionSettings {
  host: "localhost" | "127.0.0.1" | "::1";
  port: number;
  connectTimeoutMs: number;
  commandTimeoutMs: number;
  sdkRequestTimeoutMs: number;
}

interface OutputPathInfo {
  isDirectory(): boolean;
  isFile(): boolean;
  isSymbolicLink(): boolean;
}

interface QueryPayload {
  databaseFilename: string;
  dbmod: number;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function exactKeys(value: Record<string, unknown>, expected: readonly string[]): boolean {
  return (
    JSON.stringify(Object.keys(value).sort()) === JSON.stringify([...expected].sort())
  );
}

function fail(code: ReadOnlyLiveProbeErrorCode): never {
  throw new ReadOnlyLiveProbeError(code);
}

function parseBoundedInteger(
  value: string | undefined,
  fallback: number,
  minimum: number,
  maximum: number
): number {
  const text = value ?? String(fallback);
  if (!/^\d+$/.test(text)) fail("PROBE.INVALID_CONNECTION_CONFIG");
  const parsed = Number(text);
  if (!Number.isSafeInteger(parsed) || parsed < minimum || parsed > maximum) {
    fail("PROBE.INVALID_CONNECTION_CONFIG");
  }
  return parsed;
}

/** Enforces the accepted localhost-only Node-to-plugin architecture. */
export function parseReadOnlyConnectionSettings(
  environment: Readonly<Record<string, string | undefined>>
): ReadOnlyConnectionSettings {
  const rawHost = (environment.CIVIL3D_HOST ?? "localhost").trim().toLowerCase();
  const host = rawHost === "[::1]" ? "::1" : rawHost;
  if (host !== "localhost" && host !== "127.0.0.1" && host !== "::1") {
    return fail("PROBE.INVALID_CONNECTION_CONFIG");
  }
  const port = parseBoundedInteger(environment.CIVIL3D_PORT, 8080, 1, 65_535);
  const connectTimeoutMs = parseBoundedInteger(
    environment.CIVIL3D_CONNECT_TIMEOUT,
    5_000,
    1,
    60_000
  );
  const commandTimeoutMs = parseBoundedInteger(
    environment.CIVIL3D_COMMAND_TIMEOUT,
    120_000,
    1,
    600_000
  );
  return {
    host,
    port,
    connectTimeoutMs,
    commandTimeoutMs,
    sdkRequestTimeoutMs:
      connectTimeoutMs + commandTimeoutMs + READ_ONLY_SDK_TIMEOUT_GRACE_MS,
  };
}

/** Restricts live output to a JSONL file under this workspace's benchmark-output. */
export function resolveReadOnlyOutputPath(
  outputPath: string | undefined,
  append: boolean,
  workspacePath: string
): string | undefined {
  if (!outputPath || outputPath === "-") {
    if (append) fail("PROBE.INVALID_OUTPUT");
    return undefined;
  }
  const outputRoot = path.resolve(workspacePath, "benchmark-output");
  const resolved = path.resolve(workspacePath, outputPath);
  const relative = path.relative(outputRoot, resolved);
  if (
    relative.length === 0 ||
    relative.startsWith(`..${path.sep}`) ||
    relative === ".." ||
    path.isAbsolute(relative) ||
    /[\\/]/.test(relative) ||
    path.extname(resolved).toLowerCase() !== ".jsonl"
  ) {
    return fail("PROBE.INVALID_OUTPUT");
  }
  return resolved;
}

function isMissingPathError(error: unknown): boolean {
  return (
    error instanceof Error &&
    "code" in error &&
    (error as NodeJS.ErrnoException).code === "ENOENT"
  );
}

/** Checks the output directory and final file before any live connection/write. */
export async function assertReadOnlyOutputTargetIsSafe(
  outputPath: string | undefined,
  append: boolean,
  inspect: (target: string) => Promise<OutputPathInfo> = nodeLstat
): Promise<void> {
  if (!outputPath) return;
  const outputRoot = path.dirname(outputPath);
  try {
    const rootInfo = await inspect(outputRoot);
    if (!rootInfo.isDirectory() || rootInfo.isSymbolicLink()) {
      fail("PROBE.INVALID_OUTPUT");
    }
  } catch (error) {
    if (!isMissingPathError(error)) {
      if (error instanceof ReadOnlyLiveProbeError) throw error;
      fail("PROBE.INVALID_OUTPUT");
    }
  }

  try {
    const fileInfo = await inspect(outputPath);
    if (!append || !fileInfo.isFile() || fileInfo.isSymbolicLink()) {
      fail("PROBE.INVALID_OUTPUT");
    }
  } catch (error) {
    if (isMissingPathError(error)) return;
    if (error instanceof ReadOnlyLiveProbeError) throw error;
    fail("PROBE.INVALID_OUTPUT");
  }
}

/** Windows-only comparison used in memory; the raw path is never recorded. */
export function normalizeDrawingPath(value: string): string {
  if (typeof value !== "string" || value.trim().length === 0 || /[\r\n\0]/.test(value)) {
    return fail("PROBE.INVALID_INPUT");
  }
  let normalized = path.win32.normalize(value.trim());
  if (normalized.startsWith("\\\\?\\UNC\\")) {
    normalized = `\\\\${normalized.slice(8)}`;
  } else if (normalized.startsWith("\\\\?\\")) {
    normalized = normalized.slice(4);
  }
  if (!path.win32.isAbsolute(normalized) || path.win32.extname(normalized).toLowerCase() !== ".dwg") {
    return fail("PROBE.INVALID_INPUT");
  }
  return normalized.replace(/[\\/]+$/, "").toLowerCase();
}

function validateInput(input: ReadOnlyLiveRunInput): void {
  if (input.scenarioId !== READ_ONLY_LIVE_SCENARIO_ID) {
    fail("PROBE.SCENARIO_NOT_ALLOWED");
  }
  const scenario = assertManifestRunPosition(input.binding, {
    suite_id: input.binding.manifest.suite_id,
    scenario_id: input.scenarioId,
    iteration: input.iteration,
    temperature_state: input.temperatureState,
  });
  if (
    scenario.public_tool !== "civil3d_query" ||
    scenario.operation_kind !== "read_only" ||
    scenario.live_required !== true
  ) {
    fail("PROBE.SCENARIO_NOT_ALLOWED");
  }
  if (!SAFE_IDENTIFIER_PATTERN.test(input.variantId) || input.serverPath.length === 0) {
    fail("PROBE.INVALID_INPUT");
  }
  normalizeDrawingPath(input.expectedDrawingPath);
}

function assertToolSurface(result: { tools: Array<{ name: string }> }): void {
  const names = result.tools.map((tool) => tool.name).sort();
  if (JSON.stringify(names) !== JSON.stringify([...EXPECTED_PUBLIC_TOOLS].sort())) {
    fail("PROBE.TOOL_SURFACE_MISMATCH");
  }
}

function parseSingleTextJson(result: ReadOnlyProbeToolResult, errorCode: ReadOnlyLiveProbeErrorCode): unknown {
  if (result.isError === true) fail(errorCode);
  const textBlocks = result.content.filter(
    (item): item is { type: string; text: string } =>
      item.type === "text" && typeof item.text === "string"
  );
  if (textBlocks.length !== 1) fail(errorCode);
  try {
    return JSON.parse(textBlocks[0].text);
  } catch {
    return fail(errorCode);
  }
}

function assertSkillsPreflight(result: ReadOnlyProbeToolResult): void {
  const payload = parseSingleTextJson(result, "PROBE.SKILLS_PREFLIGHT_FAILED");
  if (
    !isPlainObject(payload) ||
    !Number.isSafeInteger(payload.count) ||
    (payload.count as number) < 1 ||
    !Array.isArray(payload.skills) ||
    payload.skills.length !== payload.count
  ) {
    fail("PROBE.SKILLS_PREFLIGHT_FAILED");
  }
}

function parseQueryPayload(result: ReadOnlyProbeToolResult): QueryPayload {
  const payload = parseSingleTextJson(result, "PROBE.QUERY_FAILED");
  if (
    !isPlainObject(payload) ||
    !exactKeys(payload, [
      "schemaVersion",
      "databaseFilename",
      "dbmod",
      "alignmentCount",
      "surfaceCount",
      "cogoPointCount",
    ]) ||
    payload.schemaVersion !== "civil3d-mcp-readonly-live-payload/v1" ||
    typeof payload.databaseFilename !== "string" ||
    !Number.isSafeInteger(payload.dbmod) ||
    !Number.isSafeInteger(payload.alignmentCount) ||
    !Number.isSafeInteger(payload.surfaceCount) ||
    !Number.isSafeInteger(payload.cogoPointCount) ||
    (payload.alignmentCount as number) < 0 ||
    (payload.surfaceCount as number) < 0 ||
    (payload.cogoPointCount as number) < 0
  ) {
    fail("PROBE.INVALID_QUERY_RESPONSE");
  }
  return {
    databaseFilename: payload.databaseFilename,
    dbmod: payload.dbmod as number,
  };
}

function requireCompleteMeasurement(result: ReadOnlyProbeToolResult, runId: string): Record<string, unknown> {
  const measurement = result._meta?.[MCP_BENCHMARK_META_KEY];
  if (
    !isPlainObject(measurement) ||
    measurement.schema_version !== EVENT_SCHEMA_VERSION ||
    measurement.type !== "internal_measurement" ||
    measurement.correlation_id !== runId ||
    measurement.measurement_status !== "complete" ||
    measurement.partial_reason !== null ||
    measurement.code_sha256 !== sha256Utf8(READ_ONLY_LIVE_QUERY_CODE)
  ) {
    fail("PROBE.MEASUREMENT_INCOMPLETE");
  }
  return measurement;
}

function isMeasuredNumber(value: unknown): value is {
  value: number;
  availability: "measured";
  source: string;
} {
  return (
    isPlainObject(value) &&
    typeof value.value === "number" &&
    Number.isFinite(value.value) &&
    value.value >= 0 &&
    value.availability === "measured" &&
    typeof value.source === "string"
  );
}

function assertAcceptanceMetrics(record: AnyBenchmarkRunRecord): void {
  if (
    record.schema_version !== RUN_SCHEMA_VERSION ||
    record.metrics.first_pass_success.value !== true ||
    record.metrics.outcome_success.value !== true ||
    !isMeasuredNumber(record.metrics.roslyn_cache_hits) ||
    !isMeasuredNumber(record.metrics.roslyn_cache_misses) ||
    record.metrics.roslyn_cache_hits.value + record.metrics.roslyn_cache_misses.value !== 1 ||
    !isMeasuredNumber(record.metrics.queue_ms) ||
    !isMeasuredNumber(record.metrics.execution_ms) ||
    !isMeasuredNumber(record.metrics.end_to_end_ms) ||
    !isMeasuredNumber(record.metrics.returned_payload_utf8_bytes) ||
    record.metrics.returned_payload_utf8_bytes.value < 1
  ) {
    fail("PROBE.MEASUREMENT_INCOMPLETE");
  }
}

/**
 * Runs one fixed read-only query. Skills and tool-list checks are preflight and
 * are intentionally not counted in the benchmark run.
 */
export async function runReadOnlyLiveBenchmark(
  input: ReadOnlyLiveRunInput,
  dependencies: ReadOnlyLiveRunDependencies
): Promise<AnyBenchmarkRunRecord> {
  // Everything that can be validated locally is checked before starting the MCP child.
  validateInput(input);
  const expectedDrawing = normalizeDrawingPath(input.expectedDrawingPath);
  const runId = dependencies.createRunId();
  if (!OPAQUE_BENCHMARK_RUN_ID_PATTERN.test(runId)) fail("PROBE.INVALID_INPUT");

  let client: ReadOnlyProbeClient;
  try {
    client = await dependencies.connect(input.serverPath);
  } catch {
    return fail("PROBE.CONNECTION_FAILED");
  }

  let queryResult: ReadOnlyProbeToolResult;
  let primaryError: unknown;
  try {
    assertToolSurface(await client.listTools());
    assertSkillsPreflight(
      await client.callTool({ name: "civil3d_skills", arguments: { action: "list" } })
    );
    queryResult = await client.callTool({
      name: "civil3d_query",
      arguments: { code: READ_ONLY_LIVE_QUERY_CODE },
      _meta: {
        [MCP_BENCHMARK_META_KEY]: { enabled: true, run_id: runId },
      },
    });
  } catch (error) {
    primaryError = error;
  }

  try {
    await client.close();
  } catch {
    if (!primaryError) fail("PROBE.CLIENT_CLOSE_FAILED");
  }
  if (primaryError) {
    if (primaryError instanceof ReadOnlyLiveProbeError) throw primaryError;
    return fail("PROBE.QUERY_FAILED");
  }

  const payload = parseQueryPayload(queryResult!);
  if (normalizeDrawingPath(payload.databaseFilename) !== expectedDrawing) {
    fail("PROBE.DRAWING_GUARD_FAILED");
  }
  if (payload.dbmod !== 0) fail("PROBE.DRAWING_NOT_CLEAN");
  const internalMeasurement = requireCompleteMeasurement(queryResult!, runId);

  const record = recordBenchmarkEvents(
    [
      {
        schema_version: EVENT_SCHEMA_VERSION,
        type: "run_start",
        run_id: runId,
        suite_id: input.binding.manifest.suite_id,
        variant_id: input.variantId,
        scenario_id: input.scenarioId,
        iteration: input.iteration,
        temperature_state: input.temperatureState,
        measurement_mode: "plugin_internal",
        observed_counts: ["tool_calls", "model_tool_rounds", "runtime_errors"],
      },
      {
        schema_version: EVENT_SCHEMA_VERSION,
        type: "generated_csharp",
        attempt: 1,
        code: READ_ONLY_LIVE_QUERY_CODE,
      },
      { schema_version: EVENT_SCHEMA_VERSION, type: "tool_call" },
      internalMeasurement,
      { schema_version: EVENT_SCHEMA_VERSION, type: "outcome", success: true },
    ],
    input.binding
  );
  assertAcceptanceMetrics(record);
  return record;
}
