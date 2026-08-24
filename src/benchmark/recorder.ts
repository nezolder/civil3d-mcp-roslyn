import { measureText } from "./measure.js";
import {
  BenchmarkManifestBinding,
  BenchmarkScenario,
  assertBenchmarkRunMatchesManifest,
  assertManifestRunPosition,
} from "./manifest.js";
import {
  AnyBenchmarkRunRecord,
  BenchmarkRunRecord,
  BenchmarkRunRecordV1,
  EVENT_SCHEMA_VERSION,
  EVENT_SCHEMA_VERSION_V1,
  GeneratedCSharpAttempt,
  INTERNAL_PARTIAL_REASONS,
  InternalMeasurementStatus,
  InternalPartialReason,
  InternalRequestRecord,
  MeasurementMode,
  OPAQUE_BENCHMARK_REQUEST_ID_PATTERN,
  OPAQUE_BENCHMARK_RUN_ID_PATTERN,
  RUNTIME_ERROR_CATEGORIES,
  RUN_SCHEMA_VERSION,
  RUN_SCHEMA_VERSION_V1,
  RuntimeErrorCategory,
  RuntimeErrorCategoryMeasurements,
  assertBenchmarkRunRecord,
  measurement,
  notAvailable,
  sanitizeIdentifier,
} from "./schema.js";

const V1_EVENT_TYPES = [
  "run_start",
  "tool_call",
  "model_tool_round",
  "generated_csharp",
  "compile_attempt",
  "runtime_error",
  "returned_payload",
  "timing",
  "outcome",
] as const;

const V2_EVENT_TYPES = [...V1_EVENT_TYPES, "internal_measurement"] as const;

const OBSERVED_COUNT_KEYS = [
  "tool_calls",
  "model_tool_rounds",
  "compilation_attempts",
  "runtime_errors",
] as const;

type EventSchemaVersion = typeof EVENT_SCHEMA_VERSION_V1 | typeof EVENT_SCHEMA_VERSION;
type EventType = (typeof V2_EVENT_TYPES)[number];
type ObservedCountKey = (typeof OBSERVED_COUNT_KEYS)[number];
type EventObject = Record<string, unknown>;

interface InternalMeasurement {
  correlation_id: string;
  request_id: string;
  code_sha256: string;
  measurement_status: InternalMeasurementStatus;
  partial_reason: InternalPartialReason | null;
  roslyn_cache_hits: number | null;
  roslyn_cache_misses: number | null;
  compilation_attempts: number | null;
  compilation_errors: number | null;
  command_context_wait_ms: number | null;
  execution_ms: number | null;
  end_to_end_ms: number;
  returned_payload_utf8_bytes: number;
}

function isPlainObject(value: unknown): value is EventObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function assertAllowedKeys(
  value: EventObject,
  allowed: readonly string[],
  label: string
): void {
  const unexpected = Object.keys(value).filter((key) => !allowed.includes(key));
  if (unexpected.length > 0) {
    throw new Error(`${label} contains unsupported fields`);
  }
  const missing = allowed.filter((key) => !Object.hasOwn(value, key));
  if (missing.length > 0) {
    throw new Error(`${label} is missing required fields`);
  }
}

function requireEventSchemaVersion(value: unknown): EventSchemaVersion {
  if (value !== EVENT_SCHEMA_VERSION_V1 && value !== EVENT_SCHEMA_VERSION) {
    throw new Error(`Unsupported benchmark event schema: ${String(value)}`);
  }
  return value;
}

function assertEventHeader(event: EventObject, streamVersion: EventSchemaVersion): void {
  if (event.schema_version !== streamVersion) {
    throw new Error("Benchmark event stream cannot mix schema versions");
  }
  const eventTypes =
    streamVersion === EVENT_SCHEMA_VERSION_V1 ? V1_EVENT_TYPES : V2_EVENT_TYPES;
  if (!eventTypes.includes(event.type as never)) {
    throw new Error(`Unsupported benchmark event type: ${String(event.type)}`);
  }
}

function requireString(value: unknown, label: string): string {
  if (typeof value !== "string") {
    throw new Error(`${label} must be a string`);
  }
  return value;
}

function requirePositiveInteger(value: unknown, label: string): number {
  if (!Number.isSafeInteger(value) || (value as number) < 1) {
    throw new Error(`${label} must be a positive safe integer`);
  }
  return value as number;
}

function requireNonNegativeNumber(value: unknown, label: string): number {
  if (typeof value !== "number" || !Number.isFinite(value) || value < 0) {
    throw new Error(`${label} must be a non-negative finite number`);
  }
  return value;
}

function requireNonNegativeInteger(value: unknown, label: string): number {
  if (!Number.isSafeInteger(value) || (value as number) < 0) {
    throw new Error(`${label} must be a non-negative safe integer`);
  }
  return value as number;
}

function requireNullableNonNegativeNumber(value: unknown, label: string): number | null {
  return value === null ? null : requireNonNegativeNumber(value, label);
}

function requireSha256(value: unknown, label: string): string {
  if (typeof value !== "string" || !/^[a-f0-9]{64}$/.test(value)) {
    throw new Error(`${label} must be a lowercase SHA-256 digest`);
  }
  return value;
}

function parseObservedCounts(value: unknown): Set<ObservedCountKey> {
  if (!Array.isArray(value)) {
    throw new Error("run_start.observed_counts must be an array");
  }
  const result = new Set<ObservedCountKey>();
  for (const item of value) {
    if (!OBSERVED_COUNT_KEYS.includes(item as ObservedCountKey)) {
      throw new Error("run_start.observed_counts contains an unsupported metric");
    }
    if (result.has(item as ObservedCountKey)) {
      throw new Error("run_start.observed_counts contains a duplicate metric");
    }
    result.add(item as ObservedCountKey);
  }
  return result;
}

function sumKnown(values: readonly (number | null)[]): number | null {
  return values.some((value) => value === null)
    ? null
    : values.reduce<number>((sum, value) => sum + (value ?? 0), 0);
}

export function parseEventJsonLines(input: string): unknown[] {
  const events: unknown[] = [];
  for (const [index, line] of input.split(/\r?\n/).entries()) {
    if (line.trim().length === 0) continue;
    try {
      events.push(JSON.parse(line));
    } catch {
      throw new Error(`Invalid JSON on event line ${index + 1}`);
    }
  }
  if (events.length === 0) {
    throw new Error("Event stream is empty");
  }
  return events;
}

export function recordBenchmarkEvents(
  rawEvents: readonly unknown[],
  binding: BenchmarkManifestBinding
): AnyBenchmarkRunRecord {
  if (rawEvents.length === 0 || !isPlainObject(rawEvents[0])) {
    throw new Error("Event stream is empty or its first event is not an object");
  }
  const streamVersion = requireEventSchemaVersion(rawEvents[0].schema_version);
  const isV2 = streamVersion === EVENT_SCHEMA_VERSION;

  let metadata:
    | {
        run_id: string;
        suite_id: string;
        variant_id: string;
        scenario_id: string;
        iteration: number;
        temperature_state: "cold" | "warm";
      }
    | undefined;
  let measurementMode: MeasurementMode = "external";
  let manifestScenario: BenchmarkScenario | undefined;
  let observedCounts = new Set<ObservedCountKey>();
  let toolCalls = 0;
  let modelToolRounds = 0;
  let runtimeErrors = 0;
  const runtimeErrorCounts = Object.fromEntries(
    RUNTIME_ERROR_CATEGORIES.map((category) => [category, 0])
  ) as Record<RuntimeErrorCategory, number>;
  const compileAttempts = new Map<number, "success" | "error">();
  const generatedAttempts = new Map<number, GeneratedCSharpAttempt>();
  let returnedPayloadUtf8Bytes = 0;
  let payloadObservationCount = 0;
  let payloadHasExternalByteCount = false;
  let payloadHasMeasuredText = false;
  const timings: Partial<Record<"queue_ms" | "execution_ms" | "end_to_end_ms", number>> = {};
  const internalMeasurements: InternalMeasurement[] = [];
  const internalRequestIds = new Set<string>();
  let outcomeSuccess: boolean | undefined;

  for (const [eventIndex, rawEvent] of rawEvents.entries()) {
    if (!isPlainObject(rawEvent)) {
      throw new Error(`Event ${eventIndex + 1} must be an object`);
    }
    assertEventHeader(rawEvent, streamVersion);
    const type = rawEvent.type as EventType;

    if (eventIndex === 0 && type !== "run_start") {
      throw new Error("The first event must be run_start");
    }
    if (type !== "run_start" && !metadata) {
      throw new Error("run_start must precede measurement events");
    }

    switch (type) {
      case "run_start": {
        assertAllowedKeys(
          rawEvent,
          [
            "schema_version",
            "type",
            "run_id",
            "suite_id",
            "variant_id",
            "scenario_id",
            "iteration",
            "temperature_state",
            "observed_counts",
            ...(isV2 ? ["measurement_mode"] : []),
          ],
          "run_start"
        );
        if (metadata) {
          throw new Error("Only one run_start event is allowed");
        }
        const temperature = rawEvent.temperature_state;
        if (temperature !== "cold" && temperature !== "warm") {
          throw new Error("run_start.temperature_state must be cold or warm");
        }
        if (isV2) {
          if (
            rawEvent.measurement_mode !== "external" &&
            rawEvent.measurement_mode !== "plugin_internal"
          ) {
            throw new Error("run_start.measurement_mode must be external or plugin_internal");
          }
          measurementMode = rawEvent.measurement_mode;
        }
        const rawRunId = requireString(rawEvent.run_id, "run_start.run_id");
        if (
          measurementMode === "plugin_internal" &&
          !OPAQUE_BENCHMARK_RUN_ID_PATTERN.test(rawRunId)
        ) {
          throw new Error("plugin_internal run_id must be a generated opaque identifier");
        }
        const suiteId = requireString(rawEvent.suite_id, "run_start.suite_id");
        const scenarioId = requireString(rawEvent.scenario_id, "run_start.scenario_id");
        const iteration = requirePositiveInteger(rawEvent.iteration, "run_start.iteration");
        manifestScenario = assertManifestRunPosition(binding, {
          suite_id: suiteId,
          scenario_id: scenarioId,
          iteration,
          temperature_state: temperature,
        });
        metadata = {
          run_id: sanitizeIdentifier(rawRunId, "run"),
          suite_id: binding.manifest.suite_id,
          variant_id: sanitizeIdentifier(
            requireString(rawEvent.variant_id, "run_start.variant_id"),
            "variant"
          ),
          scenario_id: manifestScenario.scenario_id,
          iteration,
          temperature_state: temperature,
        };
        observedCounts = parseObservedCounts(rawEvent.observed_counts);
        break;
      }

      case "tool_call":
        assertAllowedKeys(rawEvent, ["schema_version", "type"], "tool_call");
        toolCalls += 1;
        break;

      case "model_tool_round":
        assertAllowedKeys(rawEvent, ["schema_version", "type"], "model_tool_round");
        modelToolRounds += 1;
        break;

      case "generated_csharp": {
        assertAllowedKeys(
          rawEvent,
          ["schema_version", "type", "attempt", "code"],
          "generated_csharp"
        );
        const attempt = requirePositiveInteger(rawEvent.attempt, "generated_csharp.attempt");
        if (generatedAttempts.has(attempt)) {
          throw new Error("generated_csharp attempt numbers must be unique");
        }
        const measured = measureText(requireString(rawEvent.code, "generated_csharp.code"));
        generatedAttempts.set(attempt, { attempt, ...measured });
        break;
      }

      case "internal_measurement": {
        assertAllowedKeys(
          rawEvent,
          [
            "schema_version",
            "type",
            "correlation_id",
            "request_id",
            "code_sha256",
            "measurement_status",
            "partial_reason",
            "roslyn_cache_hits",
            "roslyn_cache_misses",
            "compilation_attempts",
            "compilation_errors",
            "command_context_wait_ms",
            "execution_ms",
            "end_to_end_ms",
            "returned_payload_utf8_bytes",
          ],
          "internal_measurement"
        );
        if (!isV2 || measurementMode !== "plugin_internal") {
          throw new Error("internal_measurement requires v2 plugin_internal measurement mode");
        }

        const correlationId = requireString(
          rawEvent.correlation_id,
          "internal_measurement.correlation_id"
        );
        if (correlationId !== metadata!.run_id) {
          throw new Error("internal_measurement correlation_id does not match run_id");
        }
        const requestId = requireString(
          rawEvent.request_id,
          "internal_measurement.request_id"
        );
        if (!OPAQUE_BENCHMARK_REQUEST_ID_PATTERN.test(requestId)) {
          throw new Error("internal_measurement.request_id must be a generated opaque identifier");
        }
        if (internalRequestIds.has(requestId)) {
          throw new Error("internal_measurement request_id values must be unique");
        }
        internalRequestIds.add(requestId);

        if (
          rawEvent.measurement_status !== "complete" &&
          rawEvent.measurement_status !== "partial"
        ) {
          throw new Error("internal_measurement.measurement_status is invalid");
        }
        const status = rawEvent.measurement_status;
        let partialReason: InternalPartialReason | null = null;
        let cacheHits: number | null = null;
        let cacheMisses: number | null = null;
        let compilationAttempts: number | null = null;
        let compilationErrors: number | null = null;
        let commandContextWaitMs: number | null = null;
        let executionMs: number | null = null;

        if (status === "complete") {
          if (rawEvent.partial_reason !== null) {
            throw new Error("complete internal measurements cannot have a partial reason");
          }
          cacheHits = requireNonNegativeInteger(
            rawEvent.roslyn_cache_hits,
            "internal_measurement.roslyn_cache_hits"
          );
          cacheMisses = requireNonNegativeInteger(
            rawEvent.roslyn_cache_misses,
            "internal_measurement.roslyn_cache_misses"
          );
          compilationAttempts = requireNonNegativeInteger(
            rawEvent.compilation_attempts,
            "internal_measurement.compilation_attempts"
          );
          compilationErrors = requireNonNegativeInteger(
            rawEvent.compilation_errors,
            "internal_measurement.compilation_errors"
          );
          commandContextWaitMs = requireNullableNonNegativeNumber(
            rawEvent.command_context_wait_ms,
            "internal_measurement.command_context_wait_ms"
          );
          executionMs = requireNullableNonNegativeNumber(
            rawEvent.execution_ms,
            "internal_measurement.execution_ms"
          );
          if (cacheHits + cacheMisses > 1) {
            throw new Error("internal_measurement permits at most one Roslyn cache lookup");
          }
          if (cacheHits > 0 && compilationAttempts !== 0) {
            throw new Error("A Roslyn cache hit cannot contain a compilation attempt");
          }
          if (compilationErrors > compilationAttempts) {
            throw new Error("Compilation errors cannot exceed compilation attempts");
          }
        } else {
          if (!INTERNAL_PARTIAL_REASONS.includes(rawEvent.partial_reason as InternalPartialReason)) {
            throw new Error("partial internal measurements require a supported partial reason");
          }
          partialReason = rawEvent.partial_reason as InternalPartialReason;
          for (const key of [
            "roslyn_cache_hits",
            "roslyn_cache_misses",
            "compilation_attempts",
            "compilation_errors",
            "command_context_wait_ms",
            "execution_ms",
          ]) {
            if (rawEvent[key] !== null) {
              throw new Error("partial internal measurements require unknown plugin fields to be null");
            }
          }
        }

        internalMeasurements.push({
          correlation_id: correlationId,
          request_id: requestId,
          code_sha256: requireSha256(
            rawEvent.code_sha256,
            "internal_measurement.code_sha256"
          ),
          measurement_status: status,
          partial_reason: partialReason,
          roslyn_cache_hits: cacheHits,
          roslyn_cache_misses: cacheMisses,
          compilation_attempts: compilationAttempts,
          compilation_errors: compilationErrors,
          command_context_wait_ms: commandContextWaitMs,
          execution_ms: executionMs,
          end_to_end_ms: requireNonNegativeNumber(
            rawEvent.end_to_end_ms,
            "internal_measurement.end_to_end_ms"
          ),
          returned_payload_utf8_bytes: requireNonNegativeInteger(
            rawEvent.returned_payload_utf8_bytes,
            "internal_measurement.returned_payload_utf8_bytes"
          ),
        });
        break;
      }

      case "compile_attempt": {
        assertAllowedKeys(
          rawEvent,
          ["schema_version", "type", "attempt", "outcome"],
          "compile_attempt"
        );
        const attempt = requirePositiveInteger(rawEvent.attempt, "compile_attempt.attempt");
        if (rawEvent.outcome !== "success" && rawEvent.outcome !== "error") {
          throw new Error("compile_attempt.outcome must be success or error");
        }
        if (compileAttempts.has(attempt)) {
          throw new Error("compile_attempt attempt numbers must be unique");
        }
        compileAttempts.set(attempt, rawEvent.outcome);
        break;
      }

      case "runtime_error": {
        assertAllowedKeys(rawEvent, ["schema_version", "type", "category"], "runtime_error");
        if (!RUNTIME_ERROR_CATEGORIES.includes(rawEvent.category as RuntimeErrorCategory)) {
          throw new Error("runtime_error.category is not a supported benchmark category");
        }
        runtimeErrors += 1;
        runtimeErrorCounts[rawEvent.category as RuntimeErrorCategory] += 1;
        break;
      }

      case "returned_payload": {
        const common = ["schema_version", "type"];
        const hasSerialized = Object.hasOwn(rawEvent, "serialized_payload");
        const hasBytes = Object.hasOwn(rawEvent, "utf8_bytes");
        if (hasSerialized === hasBytes) {
          throw new Error(
            "returned_payload requires exactly one of serialized_payload or utf8_bytes"
          );
        }
        assertAllowedKeys(
          rawEvent,
          hasSerialized ? [...common, "serialized_payload"] : [...common, "utf8_bytes"],
          "returned_payload"
        );
        if (hasSerialized) {
          returnedPayloadUtf8Bytes += Buffer.byteLength(
            requireString(rawEvent.serialized_payload, "returned_payload.serialized_payload"),
            "utf8"
          );
          payloadHasMeasuredText = true;
        } else {
          returnedPayloadUtf8Bytes += requireNonNegativeInteger(
            rawEvent.utf8_bytes,
            "returned_payload.utf8_bytes"
          );
          payloadHasExternalByteCount = true;
        }
        payloadObservationCount += 1;
        break;
      }

      case "timing": {
        const allowed = ["schema_version", "type", "queue_ms", "execution_ms", "end_to_end_ms"];
        const actualKeys = Object.keys(rawEvent);
        if (actualKeys.some((key) => !allowed.includes(key))) {
          throw new Error("timing contains unsupported fields");
        }
        const timingKeys = ["queue_ms", "execution_ms", "end_to_end_ms"] as const;
        const provided = timingKeys.filter((key) => Object.hasOwn(rawEvent, key));
        if (provided.length === 0) {
          throw new Error("timing must provide at least one timing value");
        }
        for (const key of provided) {
          if (timings[key] !== undefined) {
            throw new Error(`Duplicate timing value for ${key}`);
          }
          timings[key] = requireNonNegativeNumber(rawEvent[key], `timing.${key}`);
        }
        break;
      }

      case "outcome":
        assertAllowedKeys(rawEvent, ["schema_version", "type", "success"], "outcome");
        if (typeof rawEvent.success !== "boolean") {
          throw new Error("outcome.success must be a boolean");
        }
        if (outcomeSuccess !== undefined) {
          throw new Error("Only one outcome event is allowed");
        }
        outcomeSuccess = rawEvent.success;
        break;
    }
  }

  if (!metadata || !manifestScenario) {
    throw new Error("Missing run_start event");
  }
  if (measurementMode === "plugin_internal") {
    if (internalMeasurements.length === 0) {
      throw new Error("plugin_internal measurement mode requires internal_measurement events");
    }
    if (
      compileAttempts.size > 0 ||
      payloadObservationCount > 0 ||
      Object.keys(timings).length > 0 ||
      observedCounts.has("compilation_attempts")
    ) {
      throw new Error("plugin_internal measurements cannot be mixed with external metric events");
    }
  }

  const orderedCompileAttempts = [...compileAttempts.entries()].sort((a, b) => a[0] - b[0]);
  for (const [index, [attempt]] of orderedCompileAttempts.entries()) {
    if (attempt !== index + 1) {
      throw new Error("compile_attempt numbers must form a sequence starting at 1");
    }
  }
  const generatedCSharp = [...generatedAttempts.values()].sort((a, b) => a.attempt - b.attempt);
  if (
    internalMeasurements.length > 0 &&
    generatedCSharp.length > 0 &&
    !internalMeasurements.every((internal) =>
      generatedCSharp.some((attempt) => attempt.sha256 === internal.code_sha256)
    )
  ) {
    throw new Error("Internal executed code SHA-256 does not match generated_csharp events");
  }

  const countMeasurement = (key: ObservedCountKey, value: number) =>
    observedCounts.has(key) || value > 0
      ? measurement(
          value,
          "external" as const,
          value > 0 ? "external_trace_event" : "external_runner_coverage"
        )
      : notAvailable<number>();

  const internalMetric = (value: number | null) =>
    value === null
      ? notAvailable<number>("partial_plugin_measurement")
      : measurement(value, "measured" as const, "plugin_internal_sum");
  const internalSum = (select: (item: InternalMeasurement) => number | null) =>
    sumKnown(internalMeasurements.map(select));
  const hasInternal = internalMeasurements.length > 0;

  const compilationAttemptMetric = hasInternal
    ? internalMetric(internalSum((item) => item.compilation_attempts))
    : countMeasurement("compilation_attempts", orderedCompileAttempts.length);
  const cacheHitsMetric = hasInternal
    ? internalMetric(internalSum((item) => item.roslyn_cache_hits))
    : notAvailable<number>();
  const cacheMissesMetric = hasInternal
    ? internalMetric(internalSum((item) => item.roslyn_cache_misses))
    : notAvailable<number>();
  const runtimeErrorMetric = countMeasurement("runtime_errors", runtimeErrors);
  const generatedAvailable = generatedCSharp.length > 0;
  const generatedMetric = (value: number) =>
    generatedAvailable
      ? measurement(value, "measured" as const, "transient_csharp_event")
      : notAvailable<number>();

  const compilationRetries =
    compilationAttemptMetric.value === null
      ? notAvailable<number>()
      : measurement(
          Math.max(0, compilationAttemptMetric.value - 1),
          "derived",
          "derived_from_compile_attempts"
        );
  const compilationErrors = hasInternal
    ? internalMetric(internalSum((item) => item.compilation_errors))
    : compilationAttemptMetric.value === null
      ? notAvailable<number>()
      : measurement(
          orderedCompileAttempts.filter(([, outcome]) => outcome === "error").length,
          "derived",
          "derived_from_compile_attempts"
        );

  const outcomeMetric =
    outcomeSuccess === undefined
      ? notAvailable<boolean>()
      : measurement(outcomeSuccess, "external", "external_trace_event");

  let firstPassSuccess;
  if (
    outcomeSuccess === undefined ||
    compilationAttemptMetric.value === null ||
    compilationRetries.value === null ||
    compilationErrors.value === null ||
    runtimeErrorMetric.value === null
  ) {
    firstPassSuccess = notAvailable<boolean>("first_pass_inputs_not_available");
  } else {
    const compileCountAllowed =
      manifestScenario.operation_kind === "metadata" ||
      compilationAttemptMetric.value >= 1 ||
      (cacheHitsMetric.value !== null && cacheHitsMetric.value >= 1);
    firstPassSuccess = measurement(
      outcomeSuccess &&
        compileCountAllowed &&
        compilationRetries.value === 0 &&
        compilationErrors.value === 0 &&
        runtimeErrorMetric.value === 0,
      "derived",
      "derived_first_pass"
    );
  }

  const runtimeErrorCategories = Object.fromEntries(
    RUNTIME_ERROR_CATEGORIES.map((category) => [
      category,
      runtimeErrorMetric.value === null
        ? notAvailable<number>()
        : measurement(
            runtimeErrorCounts[category],
            "external" as const,
            runtimeErrors > 0 ? "external_trace_event" : "external_runner_coverage"
          ),
    ])
  ) as RuntimeErrorCategoryMeasurements;

  const payloadMetric = hasInternal
    ? internalMetric(
        internalMeasurements.reduce(
          (sum, item) => sum + item.returned_payload_utf8_bytes,
          0
        )
      )
    : payloadObservationCount === 0
      ? notAvailable<number>()
      : measurement(
          returnedPayloadUtf8Bytes,
          payloadHasExternalByteCount ? "external" : "measured",
          payloadHasExternalByteCount && payloadHasMeasuredText
            ? "mixed_payload_observations"
            : payloadHasExternalByteCount
              ? "runner_supplied_payload_bytes"
              : "transient_serialized_payload"
        );

  const timingMeasurement = (key: "queue_ms" | "execution_ms" | "end_to_end_ms") => {
    if (hasInternal) {
      if (key === "queue_ms") {
        return internalMetric(internalSum((item) => item.command_context_wait_ms));
      }
      if (key === "execution_ms") {
        return internalMetric(internalSum((item) => item.execution_ms));
      }
      return internalMetric(
        internalMeasurements.reduce((sum, item) => sum + item.end_to_end_ms, 0)
      );
    }
    return timings[key] === undefined
      ? notAvailable<number>()
      : measurement(timings[key], "external" as const, "external_trace_event");
  };

  const commonRecord = {
    manifest_sha256: binding.manifest_sha256,
    ...metadata,
    model_config_id: binding.manifest.model_config_id,
    runner_config_id: binding.manifest.runner_config_id,
    drawing_fixture_id: binding.manifest.drawing_fixture_id,
    runtime_error_categories: runtimeErrorCategories,
    generated_csharp: generatedCSharp,
  };
  const commonMetrics = {
    generated_csharp_attempts: generatedMetric(generatedCSharp.length),
    generated_csharp_characters_total: generatedMetric(
      generatedCSharp.reduce((sum, item) => sum + item.characters, 0)
    ),
    generated_csharp_lines_total: generatedMetric(
      generatedCSharp.reduce((sum, item) => sum + item.lines, 0)
    ),
    generated_csharp_utf8_bytes_total: generatedMetric(
      generatedCSharp.reduce((sum, item) => sum + item.utf8_bytes, 0)
    ),
    tool_calls: countMeasurement("tool_calls", toolCalls),
    model_tool_rounds: countMeasurement("model_tool_rounds", modelToolRounds),
    compilation_attempts: compilationAttemptMetric,
    compilation_retries: compilationRetries,
    compilation_errors: compilationErrors,
    runtime_errors: runtimeErrorMetric,
    returned_payload_utf8_bytes: payloadMetric,
    queue_ms: timingMeasurement("queue_ms"),
    execution_ms: timingMeasurement("execution_ms"),
    end_to_end_ms: timingMeasurement("end_to_end_ms"),
    outcome_success: outcomeMetric,
    first_pass_success: firstPassSuccess,
  };

  let record: AnyBenchmarkRunRecord;
  if (isV2) {
    const internalRequests: InternalRequestRecord[] = internalMeasurements.map((item) => ({
      request_id: item.request_id,
      code_sha256: item.code_sha256,
      measurement_status: item.measurement_status,
      partial_reason: item.partial_reason,
    }));
    record = {
      schema_version: RUN_SCHEMA_VERSION,
      ...commonRecord,
      measurement_mode: measurementMode,
      internal_requests: internalRequests,
      metrics: {
        ...commonMetrics,
        roslyn_cache_hits: cacheHitsMetric,
        roslyn_cache_misses: cacheMissesMetric,
      },
    } satisfies BenchmarkRunRecord;
  } else {
    record = {
      schema_version: RUN_SCHEMA_VERSION_V1,
      ...commonRecord,
      metrics: commonMetrics,
    } satisfies BenchmarkRunRecordV1;
  }

  assertBenchmarkRunRecord(record);
  assertBenchmarkRunMatchesManifest(record, binding);
  return record;
}

export function recordEventJsonLines(
  input: string,
  binding: BenchmarkManifestBinding
): AnyBenchmarkRunRecord {
  return recordBenchmarkEvents(parseEventJsonLines(input), binding);
}
