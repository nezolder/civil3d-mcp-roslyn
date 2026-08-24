import { createHash } from "node:crypto";

export const EVENT_SCHEMA_VERSION_V1 = "civil3d-mcp-benchmark-event/v1";
export const EVENT_SCHEMA_VERSION = "civil3d-mcp-benchmark-event/v2";
export const RUN_SCHEMA_VERSION_V1 = "civil3d-mcp-benchmark-run/v1";
export const RUN_SCHEMA_VERSION = "civil3d-mcp-benchmark-run/v2";
export const AGGREGATE_SCHEMA_VERSION_V1 = "civil3d-mcp-benchmark-aggregate/v1";
export const AGGREGATE_SCHEMA_VERSION = "civil3d-mcp-benchmark-aggregate/v2";
export const TOOLS_LIST_SCHEMA_VERSION = "civil3d-mcp-tools-list-snapshot/v1";
export const MANIFEST_SCHEMA_VERSION = "civil3d-mcp-benchmark-manifest/v1";

export const TEMPERATURE_STATES = ["cold", "warm"] as const;
export type TemperatureState = (typeof TEMPERATURE_STATES)[number];

export const MEASUREMENT_MODES = ["external", "plugin_internal"] as const;
export type MeasurementMode = (typeof MEASUREMENT_MODES)[number];

export const INTERNAL_MEASUREMENT_STATUSES = ["complete", "partial"] as const;
export type InternalMeasurementStatus = (typeof INTERNAL_MEASUREMENT_STATUSES)[number];

export const INTERNAL_PARTIAL_REASONS = [
  "transport_timeout",
  "transport_error",
  "missing_sidecar",
  "invalid_sidecar",
] as const;
export type InternalPartialReason = (typeof INTERNAL_PARTIAL_REASONS)[number];

export const RUNTIME_ERROR_CATEGORIES = [
  "runtime_exception",
  "timeout",
  "transport_error",
  "unknown",
] as const;
export type RuntimeErrorCategory = (typeof RUNTIME_ERROR_CATEGORIES)[number];

export const AVAILABILITY = ["measured", "external", "derived", "not_available"] as const;
export type Availability = (typeof AVAILABILITY)[number];

export interface Measurement<T> {
  value: T | null;
  availability: Availability;
  source: string;
}

export const V1_NUMERIC_METRIC_KEYS = [
  "generated_csharp_attempts",
  "generated_csharp_characters_total",
  "generated_csharp_lines_total",
  "generated_csharp_utf8_bytes_total",
  "tool_calls",
  "model_tool_rounds",
  "compilation_attempts",
  "compilation_retries",
  "compilation_errors",
  "runtime_errors",
  "returned_payload_utf8_bytes",
  "queue_ms",
  "execution_ms",
  "end_to_end_ms",
] as const;

export const NUMERIC_METRIC_KEYS = [
  "generated_csharp_attempts",
  "generated_csharp_characters_total",
  "generated_csharp_lines_total",
  "generated_csharp_utf8_bytes_total",
  "tool_calls",
  "model_tool_rounds",
  "roslyn_cache_hits",
  "roslyn_cache_misses",
  "compilation_attempts",
  "compilation_retries",
  "compilation_errors",
  "runtime_errors",
  "returned_payload_utf8_bytes",
  "queue_ms",
  "execution_ms",
  "end_to_end_ms",
] as const;

export type V1NumericMetricKey = (typeof V1_NUMERIC_METRIC_KEYS)[number];
export type NumericMetricKey = (typeof NUMERIC_METRIC_KEYS)[number];

export const V1_INTEGER_NUMERIC_METRIC_KEYS = [
  "generated_csharp_attempts",
  "generated_csharp_characters_total",
  "generated_csharp_lines_total",
  "generated_csharp_utf8_bytes_total",
  "tool_calls",
  "model_tool_rounds",
  "compilation_attempts",
  "compilation_retries",
  "compilation_errors",
  "runtime_errors",
  "returned_payload_utf8_bytes",
] as const satisfies readonly V1NumericMetricKey[];

export const INTEGER_NUMERIC_METRIC_KEYS = [
  ...V1_INTEGER_NUMERIC_METRIC_KEYS,
  "roslyn_cache_hits",
  "roslyn_cache_misses",
] as const satisfies readonly NumericMetricKey[];

export const BOOLEAN_METRIC_KEYS = ["outcome_success", "first_pass_success"] as const;
export type BooleanMetricKey = (typeof BOOLEAN_METRIC_KEYS)[number];

export type BenchmarkMetricsV1 = Record<V1NumericMetricKey, Measurement<number>> &
  Record<BooleanMetricKey, Measurement<boolean>>;
export type BenchmarkMetrics = Record<NumericMetricKey, Measurement<number>> &
  Record<BooleanMetricKey, Measurement<boolean>>;

export type RuntimeErrorCategoryMeasurements = Record<
  RuntimeErrorCategory,
  Measurement<number>
>;

export interface GeneratedCSharpAttempt {
  attempt: number;
  characters: number;
  lines: number;
  utf8_bytes: number;
  sha256: string;
}

interface BenchmarkRunCommon {
  manifest_sha256: string;
  run_id: string;
  suite_id: string;
  model_config_id: string;
  runner_config_id: string;
  drawing_fixture_id: string;
  variant_id: string;
  scenario_id: string;
  iteration: number;
  temperature_state: TemperatureState;
  runtime_error_categories: RuntimeErrorCategoryMeasurements;
  generated_csharp: GeneratedCSharpAttempt[];
}

export interface BenchmarkRunRecordV1 extends BenchmarkRunCommon {
  schema_version: typeof RUN_SCHEMA_VERSION_V1;
  metrics: BenchmarkMetricsV1;
}

export interface InternalRequestRecord {
  request_id: string;
  code_sha256: string;
  measurement_status: InternalMeasurementStatus;
  partial_reason: InternalPartialReason | null;
}

export interface BenchmarkRunRecord extends BenchmarkRunCommon {
  schema_version: typeof RUN_SCHEMA_VERSION;
  measurement_mode: MeasurementMode;
  internal_requests: InternalRequestRecord[];
  metrics: BenchmarkMetrics;
}

export type AnyBenchmarkRunRecord = BenchmarkRunRecordV1 | BenchmarkRunRecord;

export const SAFE_IDENTIFIER_PATTERN = /^[a-z0-9][a-z0-9._-]{0,127}$/;
export const OPAQUE_BENCHMARK_RUN_ID_PATTERN = /^run-[a-f0-9]{32}$/;
export const OPAQUE_BENCHMARK_REQUEST_ID_PATTERN = /^request-[a-f0-9]{16}$/;
const SAFE_SOURCE_PATTERN = /^[a-z0-9][a-z0-9._:-]{0,127}$/;
const SHA256_PATTERN = /^[a-f0-9]{64}$/;

export function sanitizeIdentifier(value: string, prefix: string): string {
  if (SAFE_IDENTIFIER_PATTERN.test(value)) {
    return value;
  }

  const digest = createHash("sha256").update(value, "utf8").digest("hex").slice(0, 16);
  return `${prefix}-${digest}`;
}

export function notAvailable<T>(source = "not_available"): Measurement<T> {
  return { value: null, availability: "not_available", source };
}

export function measurement<T>(
  value: T,
  availability: Exclude<Availability, "not_available">,
  source: string
): Measurement<T> {
  return { value, availability, source };
}

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

function assertSafeIdentifier(value: unknown, label: string): asserts value is string {
  if (typeof value !== "string" || !SAFE_IDENTIFIER_PATTERN.test(value)) {
    throw new Error(`${label} must be a sanitized identifier`);
  }
}

function assertMeasurement(
  value: unknown,
  expectedType: "number" | "boolean",
  label: string,
  integer: boolean
): asserts value is Measurement<number | boolean> {
  if (!isPlainObject(value)) {
    throw new Error(`${label} must be an object`);
  }
  assertExactKeys(value, ["value", "availability", "source"], label);

  if (!AVAILABILITY.includes(value.availability as Availability)) {
    throw new Error(`${label}.availability is invalid`);
  }
  if (typeof value.source !== "string" || !SAFE_SOURCE_PATTERN.test(value.source)) {
    throw new Error(`${label}.source is invalid`);
  }

  if (value.value === null) {
    if (value.availability !== "not_available") {
      throw new Error(`${label} null values must be not_available`);
    }
    return;
  }

  if (value.availability === "not_available" || typeof value.value !== expectedType) {
    throw new Error(`${label} has an inconsistent value and availability`);
  }
  if (expectedType === "number") {
    const numberValue = value.value as number;
    if (
      !Number.isFinite(numberValue) ||
      numberValue < 0 ||
      (integer && !Number.isSafeInteger(numberValue))
    ) {
      throw new Error(`${label}.value is not a valid non-negative number`);
    }
  }
}

function assertCommonRunFields(value: Record<string, unknown>): void {
  if (
    typeof value.manifest_sha256 !== "string" ||
    !SHA256_PATTERN.test(value.manifest_sha256)
  ) {
    throw new Error("manifest_sha256 must be a lowercase SHA-256 hex digest");
  }
  assertSafeIdentifier(value.run_id, "run_id");
  assertSafeIdentifier(value.suite_id, "suite_id");
  assertSafeIdentifier(value.model_config_id, "model_config_id");
  assertSafeIdentifier(value.runner_config_id, "runner_config_id");
  assertSafeIdentifier(value.drawing_fixture_id, "drawing_fixture_id");
  assertSafeIdentifier(value.variant_id, "variant_id");
  assertSafeIdentifier(value.scenario_id, "scenario_id");
  if (!Number.isSafeInteger(value.iteration) || (value.iteration as number) < 1) {
    throw new Error("iteration must be a positive safe integer");
  }
  if (!TEMPERATURE_STATES.includes(value.temperature_state as TemperatureState)) {
    throw new Error("temperature_state must be cold or warm");
  }
}

function assertMetrics(
  value: unknown,
  keys: readonly (NumericMetricKey | V1NumericMetricKey)[],
  integerKeys: readonly string[]
): void {
  if (!isPlainObject(value)) {
    throw new Error("metrics must be an object");
  }
  assertExactKeys(value, [...keys, ...BOOLEAN_METRIC_KEYS], "metrics");
  const integers = new Set(integerKeys);
  for (const key of keys) {
    assertMeasurement(value[key], "number", `metrics.${key}`, integers.has(key));
  }
  for (const key of BOOLEAN_METRIC_KEYS) {
    assertMeasurement(value[key], "boolean", `metrics.${key}`, false);
  }
}

function assertRuntimeErrors(value: Record<string, unknown>): void {
  if (!isPlainObject(value.runtime_error_categories)) {
    throw new Error("runtime_error_categories must be an object");
  }
  assertExactKeys(
    value.runtime_error_categories,
    RUNTIME_ERROR_CATEGORIES,
    "runtime_error_categories"
  );
  for (const category of RUNTIME_ERROR_CATEGORIES) {
    assertMeasurement(
      value.runtime_error_categories[category],
      "number",
      `runtime_error_categories.${category}`,
      true
    );
  }
  const runtimeTotal = (value.metrics as { runtime_errors: Measurement<number> })
    .runtime_errors.value;
  const categoryValues = RUNTIME_ERROR_CATEGORIES.map(
    (category) =>
      (value.runtime_error_categories as unknown as RuntimeErrorCategoryMeasurements)[category]
        .value
  );
  if (runtimeTotal === null) {
    if (categoryValues.some((categoryValue) => categoryValue !== null)) {
      throw new Error("runtime error categories require runtime error coverage");
    }
  } else {
    if (categoryValues.some((categoryValue) => categoryValue === null)) {
      throw new Error(
        "runtime error category counts must be complete when runtime errors are available"
      );
    }
    const categoryTotal = categoryValues.reduce<number>(
      (sum, categoryValue) => sum + (categoryValue ?? 0),
      0
    );
    if (categoryTotal !== runtimeTotal) {
      throw new Error("runtime error category counts must sum to runtime_errors");
    }
  }
}

function assertGeneratedCSharp(value: Record<string, unknown>): void {
  if (!Array.isArray(value.generated_csharp)) {
    throw new Error("generated_csharp must be an array");
  }
  let previousAttempt = 0;
  for (const [index, item] of value.generated_csharp.entries()) {
    if (!isPlainObject(item)) {
      throw new Error(`generated_csharp[${index}] must be an object`);
    }
    assertExactKeys(
      item,
      ["attempt", "characters", "lines", "utf8_bytes", "sha256"],
      `generated_csharp[${index}]`
    );
    for (const key of ["attempt", "characters", "lines", "utf8_bytes"] as const) {
      if (
        !Number.isSafeInteger(item[key]) ||
        (item[key] as number) < (key === "attempt" ? 1 : 0)
      ) {
        throw new Error(`generated_csharp[${index}].${key} is invalid`);
      }
    }
    if ((item.attempt as number) <= previousAttempt) {
      throw new Error("generated_csharp attempts must be strictly increasing");
    }
    previousAttempt = item.attempt as number;
    if (typeof item.sha256 !== "string" || !SHA256_PATTERN.test(item.sha256)) {
      throw new Error(`generated_csharp[${index}].sha256 is invalid`);
    }
  }
}

function assertInternalRequests(value: Record<string, unknown>): void {
  if (!Array.isArray(value.internal_requests)) {
    throw new Error("internal_requests must be an array");
  }
  const requestIds = new Set<string>();
  for (const [index, request] of value.internal_requests.entries()) {
    if (!isPlainObject(request)) {
      throw new Error(`internal_requests[${index}] must be an object`);
    }
    assertExactKeys(
      request,
      ["request_id", "code_sha256", "measurement_status", "partial_reason"],
      `internal_requests[${index}]`
    );
    if (
      typeof request.request_id !== "string" ||
      !OPAQUE_BENCHMARK_REQUEST_ID_PATTERN.test(request.request_id)
    ) {
      throw new Error(`internal_requests[${index}].request_id is invalid`);
    }
    if (requestIds.has(request.request_id)) {
      throw new Error("internal request IDs must be unique");
    }
    requestIds.add(request.request_id);
    if (typeof request.code_sha256 !== "string" || !SHA256_PATTERN.test(request.code_sha256)) {
      throw new Error(`internal_requests[${index}].code_sha256 is invalid`);
    }
    if (
      !INTERNAL_MEASUREMENT_STATUSES.includes(
        request.measurement_status as InternalMeasurementStatus
      )
    ) {
      throw new Error(`internal_requests[${index}].measurement_status is invalid`);
    }
    if (request.measurement_status === "complete") {
      if (request.partial_reason !== null) {
        throw new Error("complete internal requests cannot have a partial reason");
      }
    } else if (
      !INTERNAL_PARTIAL_REASONS.includes(request.partial_reason as InternalPartialReason)
    ) {
      throw new Error("partial internal requests require a supported partial reason");
    }
  }

  if (value.measurement_mode === "external" && value.internal_requests.length !== 0) {
    throw new Error("external runs cannot contain internal requests");
  }
  if (value.measurement_mode === "plugin_internal" && value.internal_requests.length === 0) {
    throw new Error("plugin_internal runs require at least one internal request");
  }
}

export function isBenchmarkRunRecordV2(
  value: AnyBenchmarkRunRecord
): value is BenchmarkRunRecord {
  return value.schema_version === RUN_SCHEMA_VERSION;
}

export function assertBenchmarkRunRecord(
  value: unknown
): asserts value is AnyBenchmarkRunRecord {
  if (!isPlainObject(value)) {
    throw new Error("Benchmark run record must be an object");
  }

  const commonKeys = [
    "schema_version",
    "manifest_sha256",
    "run_id",
    "suite_id",
    "model_config_id",
    "runner_config_id",
    "drawing_fixture_id",
    "variant_id",
    "scenario_id",
    "iteration",
    "temperature_state",
    "metrics",
    "runtime_error_categories",
    "generated_csharp",
  ];

  if (value.schema_version === RUN_SCHEMA_VERSION_V1) {
    assertExactKeys(value, commonKeys, "Benchmark run record");
    assertCommonRunFields(value);
    assertMetrics(value.metrics, V1_NUMERIC_METRIC_KEYS, V1_INTEGER_NUMERIC_METRIC_KEYS);
  } else if (value.schema_version === RUN_SCHEMA_VERSION) {
    assertExactKeys(
      value,
      [...commonKeys, "measurement_mode", "internal_requests"],
      "Benchmark run record"
    );
    assertCommonRunFields(value);
    if (!MEASUREMENT_MODES.includes(value.measurement_mode as MeasurementMode)) {
      throw new Error("measurement_mode must be external or plugin_internal");
    }
    if (
      value.measurement_mode === "plugin_internal" &&
      !OPAQUE_BENCHMARK_RUN_ID_PATTERN.test(value.run_id as string)
    ) {
      throw new Error("plugin_internal run_id must be a generated opaque identifier");
    }
    assertMetrics(value.metrics, NUMERIC_METRIC_KEYS, INTEGER_NUMERIC_METRIC_KEYS);
    assertInternalRequests(value);
  } else {
    throw new Error(`Unsupported benchmark run schema: ${String(value.schema_version)}`);
  }

  assertRuntimeErrors(value);
  assertGeneratedCSharp(value);
}
