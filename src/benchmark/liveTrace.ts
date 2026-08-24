import {
  EVENT_SCHEMA_VERSION,
  InternalPartialReason,
  OPAQUE_BENCHMARK_RUN_ID_PATTERN,
} from "./schema.js";
import { sha256Utf8 } from "./measure.js";

export const MCP_BENCHMARK_META_KEY = "civil3d-mcp/benchmark";
export const INVALID_BENCHMARK_METADATA_MESSAGE = "Invalid benchmark metadata.";
export const INTERNAL_MEASUREMENT_SCHEMA_VERSION =
  "civil3d-mcp-internal-measurement/v1";
export const INTERNAL_MEASUREMENT_PARAMETER = "_benchmarkMeasurement";
export const INTERNAL_MEASUREMENT_RESPONSE_PROPERTY = "_benchmarkMeasurement";

export interface BenchmarkTraceRequest {
  correlationId: string;
  requestId: string;
}

export interface InternalMeasurementCoreEvent {
  schema_version: typeof EVENT_SCHEMA_VERSION;
  type: "internal_measurement";
  correlation_id: string;
  request_id: string;
  code_sha256: string;
  measurement_status: "complete" | "partial";
  partial_reason: InternalPartialReason | null;
  roslyn_cache_hits: number | null;
  roslyn_cache_misses: number | null;
  compilation_attempts: number | null;
  compilation_errors: number | null;
  command_context_wait_ms: number | null;
  execution_ms: number | null;
  end_to_end_ms: number;
}

export interface InternalMeasurementEvent extends InternalMeasurementCoreEvent {
  returned_payload_utf8_bytes: number;
}

export interface MeasuredCommandResult<T = unknown> {
  result: T;
  benchmarkEvent: InternalMeasurementCoreEvent;
}

interface ToolHandlerExtra {
  _meta?: Record<string, unknown>;
  requestId: string | number;
}

interface PluginMeasurement {
  correlationId: string;
  codeSha256: string;
  cacheHits: number;
  cacheMisses: number;
  compilationAttempts: number;
  compilationErrors: number;
  commandContextWaitMs: number | null;
  executionMs: number | null;
}

const ERROR_BENCHMARK_EVENT = Symbol("civil3d-mcp-benchmark-event");

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function assertExactKeys(
  value: Record<string, unknown>,
  expected: readonly string[],
  label: string
): void {
  const actual = Object.keys(value).sort();
  const allowed = [...expected].sort();
  if (JSON.stringify(actual) !== JSON.stringify(allowed)) {
    throw new Error(`${label} has unexpected or missing fields`);
  }
}

function requireOpaqueRunIdentifier(value: unknown, label: string): string {
  if (typeof value !== "string" || !OPAQUE_BENCHMARK_RUN_ID_PATTERN.test(value)) {
    throw new Error(`${label} must be a generated opaque run identifier`);
  }
  return value;
}

function requireNonNegativeInteger(value: unknown, label: string): number {
  if (!Number.isSafeInteger(value) || (value as number) < 0) {
    throw new Error(`${label} must be a non-negative safe integer`);
  }
  return value as number;
}

function requireOptionalDuration(value: unknown, label: string): number | null {
  if (value === null) return null;
  return requireDuration(value, label);
}

function requireDuration(value: unknown, label: string): number {
  if (typeof value !== "number" || !Number.isFinite(value) || value < 0) {
    throw new Error(`${label} must be a non-negative finite number`);
  }
  return value;
}

function requireSha256(value: unknown, label: string): string {
  if (typeof value !== "string" || !/^[a-f0-9]{64}$/.test(value)) {
    throw new Error(`${label} must be a lowercase SHA-256 digest`);
  }
  return value;
}

/**
 * Missing metadata and explicit enabled=false preserve normal behavior. Once
 * enabled=true is present, invalid metadata fails closed before tool execution.
 */
export function getBenchmarkTraceRequest(
  extra: ToolHandlerExtra
): BenchmarkTraceRequest | undefined {
  const requested = extra._meta?.[MCP_BENCHMARK_META_KEY];
  if (!isPlainObject(requested) || requested.enabled !== true) {
    return undefined;
  }

  if (
    Object.keys(requested).sort().join("\u0000") !==
      ["enabled", "run_id"].sort().join("\u0000") ||
    typeof requested.run_id !== "string" ||
    !OPAQUE_BENCHMARK_RUN_ID_PATTERN.test(requested.run_id)
  ) {
    throw new Error(INVALID_BENCHMARK_METADATA_MESSAGE);
  }

  return {
    correlationId: requested.run_id,
    requestId: `request-${sha256Utf8(
      `${requested.run_id}\u0000${String(extra.requestId)}`
    ).slice(0, 16)}`,
  };
}

/** Only the sanitized run correlation is forwarded over the private TCP hop. */
export function addInternalMeasurementRequest(
  params: Record<string, unknown>,
  request: BenchmarkTraceRequest
): Record<string, unknown> {
  return {
    ...params,
    [INTERNAL_MEASUREMENT_PARAMETER]: {
      schemaVersion: INTERNAL_MEASUREMENT_SCHEMA_VERSION,
      correlationId: request.correlationId,
    },
  };
}

export function parsePluginMeasurement(value: unknown): PluginMeasurement {
  if (!isPlainObject(value)) {
    throw new Error("Internal benchmark measurement must be an object");
  }
  assertExactKeys(
    value,
    [
      "schemaVersion",
      "correlationId",
      "codeSha256",
      "cacheHits",
      "cacheMisses",
      "compilationAttempts",
      "compilationErrors",
      "commandContextWaitMs",
      "executionMs",
    ],
    "Internal benchmark measurement"
  );
  if (value.schemaVersion !== INTERNAL_MEASUREMENT_SCHEMA_VERSION) {
    throw new Error("Unsupported internal benchmark measurement schema");
  }

  const cacheHits = requireNonNegativeInteger(value.cacheHits, "cacheHits");
  const cacheMisses = requireNonNegativeInteger(value.cacheMisses, "cacheMisses");
  const compilationAttempts = requireNonNegativeInteger(
    value.compilationAttempts,
    "compilationAttempts"
  );
  const compilationErrors = requireNonNegativeInteger(
    value.compilationErrors,
    "compilationErrors"
  );
  if (cacheHits + cacheMisses > 1) {
    throw new Error("A measured request can contain at most one Roslyn cache lookup");
  }
  if (cacheHits > 0 && compilationAttempts !== 0) {
    throw new Error("A Roslyn cache hit cannot contain a compilation attempt");
  }
  if (compilationErrors > compilationAttempts) {
    throw new Error("Compilation errors cannot exceed compilation attempts");
  }

  return {
    correlationId: requireOpaqueRunIdentifier(value.correlationId, "correlationId"),
    codeSha256: requireSha256(value.codeSha256, "codeSha256"),
    cacheHits,
    cacheMisses,
    compilationAttempts,
    compilationErrors,
    commandContextWaitMs: requireOptionalDuration(
      value.commandContextWaitMs,
      "commandContextWaitMs"
    ),
    executionMs: requireOptionalDuration(value.executionMs, "executionMs"),
  };
}

/** Converts a valid private plugin sidecar into a complete recorder event. */
export function createInternalMeasurementEvent(
  request: BenchmarkTraceRequest,
  forwardedCode: string,
  pluginSidecar: unknown,
  endToEndMs: number
): InternalMeasurementCoreEvent {
  const measurement = parsePluginMeasurement(pluginSidecar);
  if (measurement.correlationId !== request.correlationId) {
    throw new Error("Internal benchmark correlation does not match the MCP run");
  }
  const codeSha256 = sha256Utf8(forwardedCode);
  if (measurement.codeSha256 !== codeSha256) {
    throw new Error("Internal benchmark code hash does not match the forwarded C#");
  }

  return {
    schema_version: EVENT_SCHEMA_VERSION,
    type: "internal_measurement",
    correlation_id: request.correlationId,
    request_id: request.requestId,
    code_sha256: codeSha256,
    measurement_status: "complete",
    partial_reason: null,
    roslyn_cache_hits: measurement.cacheHits,
    roslyn_cache_misses: measurement.cacheMisses,
    compilation_attempts: measurement.compilationAttempts,
    compilation_errors: measurement.compilationErrors,
    command_context_wait_ms: measurement.commandContextWaitMs,
    execution_ms: measurement.executionMs,
    end_to_end_ms: requireDuration(endToEndMs, "endToEndMs"),
  };
}

/** Creates a safe Node-only observation when plugin measurements are unavailable. */
export function createPartialInternalMeasurementEvent(
  request: BenchmarkTraceRequest,
  forwardedCode: string,
  endToEndMs: number,
  partialReason: InternalPartialReason
): InternalMeasurementCoreEvent {
  return {
    schema_version: EVENT_SCHEMA_VERSION,
    type: "internal_measurement",
    correlation_id: request.correlationId,
    request_id: request.requestId,
    code_sha256: sha256Utf8(forwardedCode),
    measurement_status: "partial",
    partial_reason: partialReason,
    roslyn_cache_hits: null,
    roslyn_cache_misses: null,
    compilation_attempts: null,
    compilation_errors: null,
    command_context_wait_ms: null,
    execution_ms: null,
    end_to_end_ms: requireDuration(endToEndMs, "endToEndMs"),
  };
}

export function createMeasurementEventFromResponse(
  request: BenchmarkTraceRequest,
  forwardedCode: string,
  response: Record<string, unknown>,
  endToEndMs: number
): InternalMeasurementCoreEvent {
  if (!Object.hasOwn(response, INTERNAL_MEASUREMENT_RESPONSE_PROPERTY)) {
    return createPartialInternalMeasurementEvent(
      request,
      forwardedCode,
      endToEndMs,
      "missing_sidecar"
    );
  }
  try {
    return createInternalMeasurementEvent(
      request,
      forwardedCode,
      response[INTERNAL_MEASUREMENT_RESPONSE_PROPERTY],
      endToEndMs
    );
  } catch {
    return createPartialInternalMeasurementEvent(
      request,
      forwardedCode,
      endToEndMs,
      "invalid_sidecar"
    );
  }
}

export function attachBenchmarkEventToError(
  error: Error,
  event: InternalMeasurementCoreEvent
): void {
  Object.defineProperty(error, ERROR_BENCHMARK_EVENT, {
    value: event,
    configurable: false,
    enumerable: false,
    writable: false,
  });
}

export function getBenchmarkEventFromError(
  error: unknown
): InternalMeasurementCoreEvent | undefined {
  return error instanceof Error
    ? (error as Error & { [ERROR_BENCHMARK_EVENT]?: InternalMeasurementCoreEvent })[
        ERROR_BENCHMARK_EVENT
      ]
    : undefined;
}

/** Preserves a request when transport setup fails before SocketClient can tag the error. */
export function getOrCreateTransportFailureEvent(
  error: unknown,
  request: BenchmarkTraceRequest,
  forwardedCode: string,
  endToEndMs: number
): InternalMeasurementCoreEvent {
  return (
    getBenchmarkEventFromError(error) ??
    createPartialInternalMeasurementEvent(
      request,
      forwardedCode,
      endToEndMs,
      "transport_error"
    )
  );
}

/** Measures the normal MCP tool result before trace metadata is attached. */
export function finalizeReturnedPayloadMeasurement<T extends Record<string, unknown>>(
  event: InternalMeasurementCoreEvent | undefined,
  normalResult: T
): InternalMeasurementEvent | undefined {
  return event
    ? {
        ...event,
        returned_payload_utf8_bytes: Buffer.byteLength(JSON.stringify(normalResult), "utf8"),
      }
    : undefined;
}

export function withBenchmarkEventMeta<T extends Record<string, unknown>>(
  result: T,
  event: InternalMeasurementEvent | undefined
): T | (T & { _meta: Record<string, unknown> }) {
  return event
    ? {
        ...result,
        _meta: { [MCP_BENCHMARK_META_KEY]: event },
      }
    : result;
}
