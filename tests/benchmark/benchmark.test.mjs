import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readdirSync, readFileSync } from "node:fs";
import test from "node:test";
import {
  aggregateBenchmarkRuns,
  median,
  nearestRankP95,
} from "../../build/benchmark/aggregate.js";
import {
  bindBenchmarkManifest,
  parseBenchmarkManifest,
  summarizeBenchmarkManifest,
} from "../../build/benchmark/manifest.js";
import {
  countCharacters,
  countLines,
  countUtf8Bytes,
  estimateTokensFromUtf8Bytes,
  measureText,
  sha256Utf8,
} from "../../build/benchmark/measure.js";
import { recordBenchmarkEvents } from "../../build/benchmark/recorder.js";
import {
  BOOLEAN_METRIC_KEYS,
  EVENT_SCHEMA_VERSION,
  EVENT_SCHEMA_VERSION_V1,
  NUMERIC_METRIC_KEYS,
  RUNTIME_ERROR_CATEGORIES,
  V1_NUMERIC_METRIC_KEYS,
} from "../../build/benchmark/schema.js";
import { registerExecuteTool } from "../../build/tools/executeTool.js";
import { ApplicationClientConnection } from "../../build/utils/SocketClient.js";
import { createToolsListSnapshot } from "../../build/benchmark/toolsList.js";
import {
  INTERNAL_MEASUREMENT_PARAMETER,
  INVALID_BENCHMARK_METADATA_MESSAGE,
  MCP_BENCHMARK_META_KEY,
  addInternalMeasurementRequest,
  attachBenchmarkEventToError,
  createInternalMeasurementEvent,
  createMeasurementEventFromResponse,
  createPartialInternalMeasurementEvent,
  finalizeReturnedPayloadMeasurement,
  getBenchmarkEventFromError,
  getBenchmarkTraceRequest,
  getOrCreateTransportFailureEvent,
  parsePluginMeasurement,
  withBenchmarkEventMeta,
} from "../../build/benchmark/liveTrace.js";

const MANIFEST_TEXT = readFileSync("benchmark/scenarios.v1.json", "utf8");
const BINDING = bindBenchmarkManifest(MANIFEST_TEXT);
const MANIFEST = BINDING.manifest;
const SKILLS_SCENARIO = "skills.catalog.synthetic.v1";
const QUERY_SCENARIO = "query.bounded-summary.synthetic.v1";
const EXECUTE_SCENARIO = "execute.single-entity.synthetic.v1";
const INTERNAL_RUN_ID = "run-0123456789abcdef0123456789abcdef";
const INTERNAL_CODE = "return 7;";
const INTERNAL_CODE_SHA256 = sha256Utf8(INTERNAL_CODE);

const event = (type, values = {}) => ({
  schema_version: EVENT_SCHEMA_VERSION,
  type,
  ...values,
});

function start(values = {}) {
  return event("run_start", {
    run_id: "run-1",
    suite_id: MANIFEST.suite_id,
    variant_id: "baseline-v1",
    scenario_id: QUERY_SCENARIO,
    iteration: 1,
    temperature_state: "cold",
    measurement_mode: "external",
    observed_counts: [
      "tool_calls",
      "model_tool_rounds",
      "compilation_attempts",
      "runtime_errors",
    ],
    ...values,
  });
}

function internalMeasurement(values = {}) {
  return event("internal_measurement", {
    correlation_id: INTERNAL_RUN_ID,
    request_id: "request-0123456789abcdef",
    code_sha256: INTERNAL_CODE_SHA256,
    measurement_status: "complete",
    partial_reason: null,
    roslyn_cache_hits: 1,
    roslyn_cache_misses: 0,
    compilation_attempts: 0,
    compilation_errors: 0,
    command_context_wait_ms: 1.25,
    execution_ms: 2.5,
    end_to_end_ms: 4,
    returned_payload_utf8_bytes: 17,
    ...values,
  });
}

const v1Event = (type, values = {}) => ({
  schema_version: EVENT_SCHEMA_VERSION_V1,
  type,
  ...values,
});

function v1Start(values = {}) {
  return v1Event("run_start", {
    run_id: "legacy-v1-run",
    suite_id: MANIFEST.suite_id,
    variant_id: "baseline-v1",
    scenario_id: QUERY_SCENARIO,
    iteration: 1,
    temperature_state: "cold",
    observed_counts: [
      "tool_calls",
      "model_tool_rounds",
      "compilation_attempts",
      "runtime_errors",
    ],
    ...values,
  });
}

function internalStart(values = {}) {
  return start({
    run_id: INTERNAL_RUN_ID,
    measurement_mode: "plugin_internal",
    observed_counts: ["tool_calls", "model_tool_rounds", "runtime_errors"],
    ...values,
  });
}

function record(events, binding = BINDING) {
  return recordBenchmarkEvents(events, binding);
}

function timedRun({ iteration, temperature, queue, scenario = QUERY_SCENARIO }) {
  return record([
    start({
      run_id: `${scenario}.${temperature}.${iteration}`,
      scenario_id: scenario,
      iteration,
      temperature_state: temperature,
    }),
    event("tool_call"),
    event("model_tool_round"),
    event("generated_csharp", { attempt: 1, code: `return ${iteration};` }),
    event("compile_attempt", { attempt: 1, outcome: "success" }),
    event("timing", {
      queue_ms: queue,
      execution_ms: queue + 10,
      end_to_end_ms: queue + 20,
    }),
    event("outcome", { success: true }),
  ]);
}

function readSchema(name) {
  return JSON.parse(readFileSync(`benchmark/schemas/${name}`, "utf8"));
}

function normalizedFileSha256(name) {
  const normalized = readFileSync(`benchmark/schemas/${name}`, "utf8").replace(
    /\r\n/g,
    "\n"
  );
  return createHash("sha256").update(normalized, "utf8").digest("hex");
}

test("text measurement uses Unicode code points, exact UTF-8 bytes, and SHA-256", () => {
  assert.equal(countCharacters("é😀"), 2);
  assert.equal(countUtf8Bytes("é😀"), 6);
  assert.equal(countLines("a\r\nb\nc\rd"), 4);
  assert.equal(countLines(""), 0);
  assert.equal(estimateTokensFromUtf8Bytes(5), 2);
  assert.equal(
    sha256Utf8("abc"),
    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
  );
  assert.deepEqual(measureText("é😀"), {
    characters: 2,
    lines: 1,
    utf8_bytes: 6,
    sha256: sha256Utf8("é😀"),
  });
});

test("benchmark tracing is opt-in through namespaced MCP metadata and uses opaque IDs", () => {
  assert.equal(getBenchmarkTraceRequest({ requestId: 1 }), undefined);
  assert.throws(
    () =>
      getBenchmarkTraceRequest({
        requestId: 1,
        _meta: {
          [MCP_BENCHMARK_META_KEY]: {
            enabled: true,
            run_id: "client-name-or-path",
          },
        },
      }),
    new RegExp(INVALID_BENCHMARK_METADATA_MESSAGE.replace(".", "\\."))
  );
  for (const invalidRequest of [
    { enabled: true },
    { enabled: true, run_id: INTERNAL_RUN_ID, extra: "not-allowed" },
  ]) {
    assert.throws(
      () =>
        getBenchmarkTraceRequest({
          requestId: 1,
          _meta: { [MCP_BENCHMARK_META_KEY]: invalidRequest },
        }),
      /Invalid benchmark metadata/
    );
  }

  const trace = getBenchmarkTraceRequest({
    requestId: "MCP request / PRIVATE_MARKER",
    _meta: {
      [MCP_BENCHMARK_META_KEY]: {
        enabled: true,
        run_id: INTERNAL_RUN_ID,
      },
    },
  });
  assert.equal(trace.correlationId, INTERNAL_RUN_ID);
  assert.match(trace.requestId, /^request-[a-f0-9]{16}$/);
  assert.equal(JSON.stringify(trace).includes("PRIVATE_MARKER"), false);

  const params = addInternalMeasurementRequest({ code: INTERNAL_CODE }, trace);
  assert.deepEqual(params[INTERNAL_MEASUREMENT_PARAMETER], {
    schemaVersion: "civil3d-mcp-internal-measurement/v1",
    correlationId: INTERNAL_RUN_ID,
  });
  assert.equal(
    JSON.stringify(params[INTERNAL_MEASUREMENT_PARAMETER]).includes("return 7"),
    false
  );
});

test("invalid enabled benchmark metadata fails closed before an execute connection", async () => {
  let callback;
  const fakeServer = {
    tool: (...args) => {
      callback = args.at(-1);
    },
  };
  registerExecuteTool(fakeServer);

  const result = await callback(
    { code: "return 1;", description: "write must not run" },
    {
      requestId: 7,
      _meta: {
        [MCP_BENCHMARK_META_KEY]: {
          enabled: true,
          run_id: "SENSITIVE_INVALID_RUN_ID",
        },
      },
    }
  );

  assert.equal(result.isError, true);
  assert.equal(result.content[0].text, INVALID_BENCHMARK_METADATA_MESSAGE);
  assert.equal(JSON.stringify(result).includes("SENSITIVE_INVALID_RUN_ID"), false);
});

test("plugin sidecar becomes a recorder event without retaining raw response data", () => {
  const trace = getBenchmarkTraceRequest({
    requestId: 42,
    _meta: {
      [MCP_BENCHMARK_META_KEY]: { enabled: true, run_id: INTERNAL_RUN_ID },
    },
  });
  const coreEvent = createInternalMeasurementEvent(
    trace,
    INTERNAL_CODE,
    {
      schemaVersion: "civil3d-mcp-internal-measurement/v1",
      correlationId: INTERNAL_RUN_ID,
      codeSha256: INTERNAL_CODE_SHA256,
      cacheHits: 1,
      cacheMisses: 0,
      compilationAttempts: 0,
      compilationErrors: 0,
      commandContextWaitMs: 1.25,
      executionMs: 2.5,
    },
    4
  );
  const normalResult = {
    content: [{ type: "text", text: "RAW_RESPONSE_MARKER" }],
  };
  const eventWithPayload = finalizeReturnedPayloadMeasurement(coreEvent, normalResult);
  const tracedResult = withBenchmarkEventMeta(normalResult, eventWithPayload);

  assert.deepEqual(tracedResult.content, normalResult.content);
  assert.equal(
    eventWithPayload.returned_payload_utf8_bytes,
    Buffer.byteLength(JSON.stringify(normalResult), "utf8")
  );
  assert.equal(
    JSON.stringify(tracedResult._meta).includes("RAW_RESPONSE_MARKER"),
    false
  );
  assert.deepEqual(tracedResult._meta[MCP_BENCHMARK_META_KEY], eventWithPayload);

  const normalErrorResult = {
    content: [{ type: "text", text: "Execution failed: RAW_ERROR_MARKER" }],
    isError: true,
  };
  const tracedErrorResult = withBenchmarkEventMeta(
    normalErrorResult,
    finalizeReturnedPayloadMeasurement(coreEvent, normalErrorResult)
  );
  assert.equal(tracedErrorResult.isError, true);
  assert.deepEqual(tracedErrorResult.content, normalErrorResult.content);
  assert.equal(
    JSON.stringify(tracedErrorResult._meta).includes("RAW_ERROR_MARKER"),
    false
  );

  const firstError = new Error("RAW_ERROR_MARKER");
  const secondError = new Error("other");
  attachBenchmarkEventToError(firstError, coreEvent);
  assert.strictEqual(getBenchmarkEventFromError(firstError), coreEvent);
  assert.equal(getBenchmarkEventFromError(secondError), undefined);
  assert.equal(JSON.stringify(firstError).includes("code_sha256"), false);

  assert.throws(
    () =>
      parsePluginMeasurement({
        schemaVersion: "civil3d-mcp-internal-measurement/v1",
        correlationId: INTERNAL_RUN_ID,
        codeSha256: INTERNAL_CODE_SHA256,
        cacheHits: 1,
        cacheMisses: 0,
        compilationAttempts: 0,
        compilationErrors: 0,
        commandContextWaitMs: 1,
        executionMs: 2,
        raw_code: "RAW_CODE_MARKER",
      }),
    /unexpected or missing fields/
  );
});

test("normal MCP result shape is unchanged when internal measurement is absent", () => {
  const normalSuccess = { content: [{ type: "text", text: "{}" }] };
  const normalError = {
    content: [{ type: "text", text: "Execution failed: synthetic" }],
    isError: true,
  };
  assert.strictEqual(withBenchmarkEventMeta(normalSuccess, undefined), normalSuccess);
  assert.strictEqual(withBenchmarkEventMeta(normalError, undefined), normalError);
  assert.equal(Object.hasOwn(normalSuccess, "_meta"), false);
  assert.equal(Object.hasOwn(normalError, "_meta"), false);
});

test("proven internal cache hit with zero compile attempts can be first-pass success", () => {
  const result = record([
    internalStart(),
    event("generated_csharp", { attempt: 1, code: INTERNAL_CODE }),
    internalMeasurement(),
    event("outcome", { success: true }),
  ]);

  assert.equal(result.metrics.roslyn_cache_hits.value, 1);
  assert.equal(result.metrics.roslyn_cache_misses.value, 0);
  assert.equal(result.metrics.compilation_attempts.value, 0);
  assert.equal(result.metrics.compilation_errors.value, 0);
  assert.equal(result.metrics.compilation_retries.value, 0);
  assert.equal(result.metrics.first_pass_success.value, true);
  assert.deepEqual(result.internal_requests, [
    {
      request_id: "request-0123456789abcdef",
      code_sha256: INTERNAL_CODE_SHA256,
      measurement_status: "complete",
      partial_reason: null,
    },
  ]);
});

test("internal cache miss and compile diagnostics are counted with measured timings", () => {
  const result = record([
    internalStart(),
    internalMeasurement({
      roslyn_cache_hits: 0,
      roslyn_cache_misses: 1,
      compilation_attempts: 1,
      compilation_errors: 1,
    }),
    event("outcome", { success: false }),
  ]);

  assert.equal(result.metrics.roslyn_cache_hits.value, 0);
  assert.equal(result.metrics.roslyn_cache_misses.value, 1);
  assert.equal(result.metrics.compilation_attempts.value, 1);
  assert.equal(result.metrics.compilation_errors.value, 1);
  assert.equal(result.metrics.queue_ms.value, 1.25);
  assert.equal(result.metrics.execution_ms.value, 2.5);
  assert.equal(result.metrics.end_to_end_ms.value, 4);
  assert.equal(result.metrics.returned_payload_utf8_bytes.value, 17);
  assert.equal(result.metrics.queue_ms.source, "plugin_internal_sum");
  assert.equal(result.metrics.first_pass_success.value, false);
});

test("two internal requests aggregate compile retry, errors, cache lookups, bytes, and timings", () => {
  const secondCode = "return 8;";
  const result = record([
    internalStart(),
    event("generated_csharp", { attempt: 1, code: INTERNAL_CODE }),
    event("generated_csharp", { attempt: 2, code: secondCode }),
    internalMeasurement({
      request_id: "request-1111111111111111",
      roslyn_cache_hits: 0,
      roslyn_cache_misses: 1,
      compilation_attempts: 1,
      compilation_errors: 1,
      command_context_wait_ms: 1,
      execution_ms: 2,
      end_to_end_ms: 3,
      returned_payload_utf8_bytes: 10,
    }),
    internalMeasurement({
      request_id: "request-2222222222222222",
      code_sha256: sha256Utf8(secondCode),
      roslyn_cache_hits: 0,
      roslyn_cache_misses: 1,
      compilation_attempts: 1,
      compilation_errors: 0,
      command_context_wait_ms: 4,
      execution_ms: 5,
      end_to_end_ms: 6,
      returned_payload_utf8_bytes: 20,
    }),
    event("outcome", { success: true }),
  ]);

  assert.equal(result.metrics.roslyn_cache_hits.value, 0);
  assert.equal(result.metrics.roslyn_cache_misses.value, 2);
  assert.equal(result.metrics.compilation_attempts.value, 2);
  assert.equal(result.metrics.compilation_retries.value, 1);
  assert.equal(result.metrics.compilation_errors.value, 1);
  assert.equal(result.metrics.returned_payload_utf8_bytes.value, 30);
  assert.equal(result.metrics.queue_ms.value, 5);
  assert.equal(result.metrics.execution_ms.value, 7);
  assert.equal(result.metrics.end_to_end_ms.value, 9);
  assert.equal(result.metrics.first_pass_success.value, false);
  assert.deepEqual(
    result.internal_requests.map(({ request_id, code_sha256 }) => ({
      request_id,
      code_sha256,
    })),
    [
      {
        request_id: "request-1111111111111111",
        code_sha256: INTERNAL_CODE_SHA256,
      },
      {
        request_id: "request-2222222222222222",
        code_sha256: sha256Utf8(secondCode),
      },
    ]
  );
});

test("transport timeout creates a partial measurement without false plugin zeroes", () => {
  const trace = getBenchmarkTraceRequest({
    requestId: 99,
    _meta: {
      [MCP_BENCHMARK_META_KEY]: { enabled: true, run_id: INTERNAL_RUN_ID },
    },
  });
  const partial = createPartialInternalMeasurementEvent(
    trace,
    INTERNAL_CODE,
    120001,
    "transport_timeout"
  );
  const normalError = {
    content: [{ type: "text", text: "RAW_TIMEOUT_ERROR" }],
    isError: true,
  };
  const measured = finalizeReturnedPayloadMeasurement(partial, normalError);

  assert.equal(measured.measurement_status, "partial");
  assert.equal(measured.partial_reason, "transport_timeout");
  for (const key of [
    "roslyn_cache_hits",
    "roslyn_cache_misses",
    "compilation_attempts",
    "compilation_errors",
    "command_context_wait_ms",
    "execution_ms",
  ]) {
    assert.equal(measured[key], null);
  }
  assert.equal(measured.code_sha256, INTERNAL_CODE_SHA256);
  assert.equal(JSON.stringify(measured).includes("RAW_TIMEOUT_ERROR"), false);

  const result = record([
    internalStart(),
    event("generated_csharp", { attempt: 1, code: INTERNAL_CODE }),
    measured,
    event("runtime_error", { category: "timeout" }),
    event("outcome", { success: false }),
  ]);
  assert.equal(result.metrics.roslyn_cache_hits.value, null);
  assert.equal(result.metrics.roslyn_cache_hits.availability, "not_available");
  assert.equal(result.metrics.compilation_attempts.value, null);
  assert.equal(result.metrics.queue_ms.value, null);
  assert.equal(result.metrics.execution_ms.value, null);
  assert.equal(result.metrics.end_to_end_ms.value, 120001);
  assert.equal(
    result.metrics.returned_payload_utf8_bytes.value,
    Buffer.byteLength(JSON.stringify(normalError), "utf8")
  );
  assert.deepEqual(result.internal_requests[0], {
    request_id: trace.requestId,
    code_sha256: INTERNAL_CODE_SHA256,
    measurement_status: "partial",
    partial_reason: "transport_timeout",
  });
});

test("SocketClient timeout attaches the partial event to the normal error path", async () => {
  const connection = new ApplicationClientConnection("127.0.0.1", 65535);
  connection.isConnected = true;
  connection.socket.write = () => true;
  const trace = getBenchmarkTraceRequest({
    requestId: 101,
    _meta: {
      [MCP_BENCHMARK_META_KEY]: { enabled: true, run_id: INTERNAL_RUN_ID },
    },
  });
  const originalSetTimeout = globalThis.setTimeout;
  globalThis.setTimeout = (callback, _delay, ...args) => {
    queueMicrotask(() => callback(...args));
    return 0;
  };

  try {
    const error = await connection
      .sendCommand("executeCode", { code: INTERNAL_CODE, readOnly: true }, trace)
      .then(
        () => assert.fail("timeout must reject"),
        (reason) => reason
      );
    const measured = getBenchmarkEventFromError(error);
    assert.equal(measured.measurement_status, "partial");
    assert.equal(measured.partial_reason, "transport_timeout");
    assert.equal(measured.code_sha256, INTERNAL_CODE_SHA256);
    assert.equal(measured.compilation_attempts, null);
    assert.ok(measured.end_to_end_ms >= 0);
  } finally {
    globalThis.setTimeout = originalSetTimeout;
    connection.socket.destroy();
  }
});

test("missing and invalid plugin sidecars produce machine-readable partial events", () => {
  const trace = getBenchmarkTraceRequest({
    requestId: 100,
    _meta: {
      [MCP_BENCHMARK_META_KEY]: { enabled: true, run_id: INTERNAL_RUN_ID },
    },
  });
  const missing = createMeasurementEventFromResponse(trace, INTERNAL_CODE, {}, 5);
  const invalid = createMeasurementEventFromResponse(
    trace,
    INTERNAL_CODE,
    { _benchmarkMeasurement: { raw_error: "RAW_SIDECAR_ERROR" } },
    6
  );
  const connectionFailure = getOrCreateTransportFailureEvent(
    new Error("RAW_CONNECT_ERROR"),
    trace,
    INTERNAL_CODE,
    7
  );

  assert.equal(missing.measurement_status, "partial");
  assert.equal(missing.partial_reason, "missing_sidecar");
  assert.equal(invalid.measurement_status, "partial");
  assert.equal(invalid.partial_reason, "invalid_sidecar");
  assert.equal(invalid.compilation_attempts, null);
  assert.equal(JSON.stringify(invalid).includes("RAW_SIDECAR_ERROR"), false);
  assert.equal(connectionFailure.partial_reason, "transport_error");
  assert.equal(JSON.stringify(connectionFailure).includes("RAW_CONNECT_ERROR"), false);
});

test("internal mode rejects missing, duplicate request IDs, mixed, mismatched, and unsafe traces", () => {
  assert.throws(
    () => record([internalStart()]),
    /requires internal_measurement/
  );
  assert.throws(
    () => record([internalStart(), internalMeasurement(), internalMeasurement()]),
    /request_id values must be unique/
  );
  assert.throws(
    () =>
      record([
        internalStart(),
        event("compile_attempt", { attempt: 1, outcome: "success" }),
        internalMeasurement(),
      ]),
    /cannot be mixed/
  );
  assert.throws(
    () =>
      record([
        internalStart(),
        internalMeasurement({ correlation_id: "run-ffffffffffffffffffffffffffffffff" }),
      ]),
    /does not match run_id/
  );
  assert.throws(
    () => record([internalStart({ run_id: "user supplied drawing name" })]),
    /generated opaque identifier/
  );
  assert.throws(
    () =>
      record([
        internalStart(),
        event("generated_csharp", { attempt: 1, code: "return 8;" }),
        internalMeasurement(),
      ]),
    /code SHA-256 does not match/
  );
});

test("tools/list snapshot measures nested descriptions without retaining text", () => {
  const snapshot = createToolsListSnapshot([
    {
      name: "tool_b",
      description: "Á",
      inputSchema: { properties: { code: { description: "PAYLOAD_MARKER_😀" } } },
    },
    { name: "tool_a", description: "abc", inputSchema: { type: "object" } },
  ]);
  assert.deepEqual(snapshot.tools.map((tool) => tool.name), ["tool_a", "tool_b"]);
  assert.equal(snapshot.public_tool_count, 2);
  assert.equal(snapshot.tools[1].top_level_description.characters, 1);
  assert.equal(snapshot.tools[1].top_level_description.utf8_bytes, 2);
  assert.equal(snapshot.tools[1].all_description_fields.field_count, 2);
  assert.equal(snapshot.token_estimator.id, "utf8_bytes_div_4_ceil_v1");
  assert.equal(JSON.stringify(snapshot).includes("PAYLOAD_MARKER"), false);
});

test("manifest SHA-256 is semantic across LF, CRLF, key order, and whitespace", () => {
  const parsed = JSON.parse(MANIFEST_TEXT);
  const crlf = MANIFEST_TEXT.replace(/\r?\n/g, "\r\n");
  const compact = JSON.stringify(parsed);
  const reordered = JSON.stringify({
    scenarios: parsed.scenarios,
    temperature_states: parsed.temperature_states,
    repetitions_per_state: parsed.repetitions_per_state,
    drawing_fixture_id: parsed.drawing_fixture_id,
    runner_config_id: parsed.runner_config_id,
    model_config_id: parsed.model_config_id,
    suite_id: parsed.suite_id,
    schema_version: parsed.schema_version,
  }, null, 7);
  const hashes = [MANIFEST_TEXT, crlf, compact, reordered].map(
    (text) => bindBenchmarkManifest(text).manifest_sha256
  );
  assert.equal(new Set(hashes).size, 1);

  const changed = structuredClone(parsed);
  changed.drawing_fixture_id = "different-synthetic-drawing-v1";
  assert.notEqual(bindBenchmarkManifest(JSON.stringify(changed)).manifest_sha256, hashes[0]);
});

test("manifest exposes separate model, runner, and disposable drawing fixture IDs", () => {
  const parsed = parseBenchmarkManifest(JSON.parse(MANIFEST_TEXT));
  const summary = summarizeBenchmarkManifest(MANIFEST_TEXT);
  assert.equal(parsed.model_config_id, "fixed-model-config-v1");
  assert.equal(parsed.runner_config_id, "fixed-runner-config-v1");
  assert.equal(parsed.drawing_fixture_id, "disposable-synthetic-drawing-v1");
  assert.equal(summary.manifest_sha256, BINDING.manifest_sha256);
  assert.equal(summary.planned_run_count, 30);
});

test("recorder binds every run to the supplied manifest", () => {
  const result = record([
    start(),
    event("compile_attempt", { attempt: 1, outcome: "success" }),
    event("outcome", { success: true }),
  ]);
  assert.equal(result.manifest_sha256, BINDING.manifest_sha256);
  assert.equal(result.model_config_id, MANIFEST.model_config_id);
  assert.equal(result.runner_config_id, MANIFEST.runner_config_id);
  assert.equal(result.drawing_fixture_id, MANIFEST.drawing_fixture_id);

  assert.throws(
    () => record([start({ suite_id: "other-suite-v1" })]),
    /suite_id does not match/
  );
  assert.throws(
    () => record([start({ scenario_id: "unknown.synthetic.v1" })]),
    /scenario_id is not defined/
  );
  assert.throws(
    () => record([start({ iteration: MANIFEST.repetitions_per_state + 1 })]),
    /outside the benchmark manifest repetition range/
  );
});

test("recorder counts calls, rounds, compile retries, errors, payload, and timings", () => {
  const sensitiveCode = "var marker = \"SENSITIVE_CODE_MARKER\"; return 42;";
  const result = record([
    start(),
    event("tool_call"),
    event("tool_call"),
    event("model_tool_round"),
    event("model_tool_round"),
    event("generated_csharp", { attempt: 1, code: sensitiveCode }),
    event("generated_csharp", { attempt: 2, code: "return 42;" }),
    event("compile_attempt", { attempt: 1, outcome: "error" }),
    event("compile_attempt", { attempt: 2, outcome: "success" }),
    event("runtime_error", { category: "runtime_exception" }),
    event("returned_payload", { serialized_payload: "CLIENT_PAYLOAD_é" }),
    event("returned_payload", { utf8_bytes: 7 }),
    event("timing", { queue_ms: 1.25, execution_ms: 2.5, end_to_end_ms: 4 }),
    event("outcome", { success: true }),
  ]);

  assert.equal(result.metrics.tool_calls.value, 2);
  assert.equal(result.metrics.model_tool_rounds.value, 2);
  assert.equal(result.metrics.compilation_attempts.value, 2);
  assert.equal(result.metrics.compilation_retries.value, 1);
  assert.equal(result.metrics.compilation_errors.value, 1);
  assert.equal(result.metrics.runtime_errors.value, 1);
  assert.equal(result.metrics.first_pass_success.value, false);
  assert.equal(
    result.metrics.returned_payload_utf8_bytes.value,
    countUtf8Bytes("CLIENT_PAYLOAD_é") + 7
  );
  assert.equal(result.runtime_error_categories.runtime_exception.value, 1);
  assert.equal(result.runtime_error_categories.timeout.value, 0);

  const serialized = JSON.stringify(result);
  assert.equal(serialized.includes("SENSITIVE_CODE_MARKER"), false);
  assert.equal(serialized.includes("CLIENT_PAYLOAD"), false);
  assert.equal(serialized.includes("return 42"), false);
});

test("runtime error events accept only sanitized benchmark categories", () => {
  assert.throws(
    () =>
      record([
        start(),
        event("runtime_error", {
          category: "runtime_exception",
          message: "SENSITIVE_ERROR_DETAIL",
        }),
      ]),
    /unsupported fields/
  );
  assert.throws(
    () => record([start(), event("runtime_error", { category: "raw.type.name" })]),
    /not a supported benchmark category/
  );
});

test("metadata first-pass succeeds with explicit zero compile and runtime coverage", () => {
  const result = record([
    start({
      scenario_id: SKILLS_SCENARIO,
      observed_counts: ["compilation_attempts", "runtime_errors"],
    }),
    event("outcome", { success: true }),
  ]);
  assert.equal(result.metrics.compilation_attempts.value, 0);
  assert.equal(result.metrics.compilation_retries.value, 0);
  assert.equal(result.metrics.compilation_errors.value, 0);
  assert.equal(result.metrics.runtime_errors.value, 0);
  assert.equal(result.metrics.first_pass_success.value, true);
});

test("query compile error and execute compile retry cannot be first-pass successes", () => {
  const queryError = record([
    start({ scenario_id: QUERY_SCENARIO }),
    event("compile_attempt", { attempt: 1, outcome: "error" }),
    event("outcome", { success: false }),
  ]);
  assert.equal(queryError.metrics.first_pass_success.value, false);
  assert.equal(queryError.metrics.compilation_errors.value, 1);

  const executeRetry = record([
    start({ scenario_id: EXECUTE_SCENARIO }),
    event("compile_attempt", { attempt: 1, outcome: "error" }),
    event("compile_attempt", { attempt: 2, outcome: "success" }),
    event("outcome", { success: true }),
  ]);
  assert.equal(executeRetry.metrics.first_pass_success.value, false);
  assert.equal(executeRetry.metrics.compilation_retries.value, 1);
});

test("code scenarios do not pass first-pass with observed zero compilation", () => {
  const result = record([
    start({ observed_counts: ["compilation_attempts", "runtime_errors"] }),
    event("outcome", { success: true }),
  ]);
  assert.equal(result.metrics.first_pass_success.value, false);
});

test("missing coverage remains explicit null/not_available", () => {
  const result = record([start({ observed_counts: [] })]);
  for (const metric of [
    "tool_calls",
    "model_tool_rounds",
    "compilation_attempts",
    "compilation_retries",
    "runtime_errors",
    "queue_ms",
    "execution_ms",
    "end_to_end_ms",
    "first_pass_success",
  ]) {
    assert.equal(result.metrics[metric].value, null, metric);
    assert.equal(result.metrics[metric].availability, "not_available", metric);
  }
  for (const category of RUNTIME_ERROR_CATEGORIES) {
    assert.equal(result.runtime_error_categories[category].value, null, category);
  }
});

test("observed zero counts come from explicit runner coverage", () => {
  const result = record([
    start({ observed_counts: ["tool_calls", "runtime_errors"] }),
  ]);
  assert.deepEqual(result.metrics.tool_calls, {
    value: 0,
    availability: "external",
    source: "external_runner_coverage",
  });
  assert.equal(result.runtime_error_categories.timeout.value, 0);
  assert.equal(result.runtime_error_categories.timeout.source, "external_runner_coverage");
  assert.equal(result.metrics.model_tool_rounds.value, null);
});

test("unsafe run labels are hashed and manifest scenario labels cannot be substituted", () => {
  const rawRunId = "Unsafe run / SENSITIVE_RUN_MARKER";
  const first = record([start({ run_id: rawRunId, observed_counts: [] })]);
  const second = record([start({ run_id: rawRunId, observed_counts: [] })]);
  assert.match(first.run_id, /^run-[a-f0-9]{16}$/);
  assert.equal(first.run_id, second.run_id);
  assert.equal(JSON.stringify(first).includes("SENSITIVE_RUN_MARKER"), false);
  assert.throws(
    () => record([start({ scenario_id: "Unsafe scenario / marker" })]),
    /scenario_id is not defined/
  );
});

test("median and nearest-rank p95 definitions are deterministic", () => {
  assert.equal(median([4, 1, 3, 2]), 2.5);
  assert.equal(median([9, 1, 5]), 5);
  assert.equal(nearestRankP95([1, 2, 3, 4, 100]), 100);
  assert.equal(nearestRankP95(Array.from({ length: 20 }, (_, index) => index + 1)), 19);
  assert.equal(median([]), null);
  assert.equal(nearestRankP95([]), null);
});

test("legacy v1 external events and run records remain usable", () => {
  const legacyRun = record([
    v1Start(),
    v1Event("tool_call"),
    v1Event("compile_attempt", { attempt: 1, outcome: "success" }),
    v1Event("outcome", { success: true }),
  ]);
  assert.equal(legacyRun.schema_version, "civil3d-mcp-benchmark-run/v1");
  assert.equal(Object.hasOwn(legacyRun, "measurement_mode"), false);
  assert.deepEqual(
    Object.keys(legacyRun.metrics).sort(),
    [...V1_NUMERIC_METRIC_KEYS, ...BOOLEAN_METRIC_KEYS].sort()
  );

  const legacyAggregate = aggregateBenchmarkRuns([legacyRun], BINDING);
  assert.equal(legacyAggregate.schema_version, "civil3d-mcp-benchmark-aggregate/v1");
  assert.equal(Object.hasOwn(legacyAggregate, "measurement_mode"), false);
});

test("aggregator reports manifest completeness and separates cold/warm", () => {
  const cold = [1, 2, 3, 4, 100].map((queue, index) =>
    timedRun({ iteration: index + 1, temperature: "cold", queue })
  );
  const warm = [10, 20, 30, 40, 50].map((queue, index) =>
    timedRun({ iteration: index + 1, temperature: "warm", queue })
  );
  const forward = aggregateBenchmarkRuns([...cold, ...warm], BINDING);
  const reverse = aggregateBenchmarkRuns([...cold, ...warm].reverse(), BINDING);
  assert.deepEqual(forward, reverse);
  assert.equal(forward.manifest_sha256, BINDING.manifest_sha256);
  assert.equal(forward.measurement_mode, "external");
  assert.equal(forward.groups.every((group) => group.measurement_mode === "external"), true);
  assert.equal(forward.groups.length, MANIFEST.scenarios.length * 2);

  const queryCold = forward.groups.find(
    (group) => group.scenario_id === QUERY_SCENARIO && group.temperature_state === "cold"
  );
  assert.equal(queryCold.expected_repetitions, 5);
  assert.equal(queryCold.observed_repetitions, 5);
  assert.deepEqual(queryCold.missing_iterations, []);
  assert.equal(queryCold.complete, true);
  assert.equal(queryCold.numeric_metrics.queue_ms.median, 3);
  assert.equal(queryCold.numeric_metrics.queue_ms.p95, 100);

  const skillsCold = forward.groups.find(
    (group) => group.scenario_id === SKILLS_SCENARIO && group.temperature_state === "cold"
  );
  assert.equal(skillsCold.observed_repetitions, 0);
  assert.deepEqual(skillsCold.missing_iterations, [1, 2, 3, 4, 5]);
  assert.equal(skillsCold.complete, false);
});

test("aggregator reports missing observed iterations", () => {
  const runs = [1, 2, 4].map((iteration) =>
    timedRun({ iteration, temperature: "warm", queue: iteration })
  );
  const aggregate = aggregateBenchmarkRuns(runs, BINDING);
  const group = aggregate.groups.find(
    (candidate) =>
      candidate.scenario_id === QUERY_SCENARIO && candidate.temperature_state === "warm"
  );
  assert.equal(group.expected_repetitions, 5);
  assert.equal(group.observed_repetitions, 3);
  assert.deepEqual(group.missing_iterations, [3, 5]);
  assert.equal(group.complete, false);
});

test("aggregator rejects duplicate run IDs and duplicate group iterations", () => {
  const first = timedRun({ iteration: 1, temperature: "cold", queue: 1 });
  assert.throws(
    () => aggregateBenchmarkRuns([first, structuredClone(first)], BINDING),
    /Duplicate run_id/
  );
  const duplicateIteration = { ...structuredClone(first), run_id: "different-run-id" };
  assert.throws(
    () => aggregateBenchmarkRuns([first, duplicateIteration], BINDING),
    /Duplicate group temperature iteration/
  );
});

test("aggregator rejects mixing external and plugin_internal timing meanings", () => {
  const externalRun = timedRun({ iteration: 1, temperature: "cold", queue: 1 });
  const internalRun = record([
    internalStart({ iteration: 2, run_id: "run-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }),
    internalMeasurement({
      correlation_id: "run-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      request_id: "request-aaaaaaaaaaaaaaaa",
    }),
    event("outcome", { success: true }),
  ]);
  assert.throws(
    () => aggregateBenchmarkRuns([externalRun, internalRun], BINDING),
    /cannot mix measurement modes/
  );
});

test("aggregator rejects mismatched manifest, model, runner, or drawing bindings", () => {
  const base = timedRun({ iteration: 1, temperature: "cold", queue: 1 });
  for (const [field, value] of [
    ["manifest_sha256", "0".repeat(64)],
    ["model_config_id", "other-model-config-v1"],
    ["runner_config_id", "other-runner-config-v1"],
    ["drawing_fixture_id", "other-drawing-fixture-v1"],
  ]) {
    const changed = { ...structuredClone(base), run_id: `changed-${field}`, [field]: value };
    assert.throws(
      () => aggregateBenchmarkRuns([changed], BINDING),
      /does not match the supplied benchmark manifest/
    );
  }
});

test("runtime categories aggregate deterministically without raw error details", () => {
  const run = record([
    start({ run_id: "runtime-category-run" }),
    event("compile_attempt", { attempt: 1, outcome: "success" }),
    event("runtime_error", { category: "timeout" }),
    event("runtime_error", { category: "timeout" }),
    event("runtime_error", { category: "transport_error" }),
    event("outcome", { success: false }),
  ]);
  const aggregate = aggregateBenchmarkRuns([run], BINDING);
  const group = aggregate.groups.find(
    (candidate) =>
      candidate.scenario_id === QUERY_SCENARIO && candidate.temperature_state === "cold"
  );
  assert.equal(group.runtime_error_categories.timeout.total, 2);
  assert.equal(group.runtime_error_categories.transport_error.total, 1);
  assert.equal(group.runtime_error_categories.runtime_exception.total, 0);
});

test("published v1 schemas are unchanged and v2 schemas match the code contract", () => {
  const schemaFiles = readdirSync("benchmark/schemas")
    .filter((name) => name.endsWith(".schema.json"))
    .sort();
  assert.deepEqual(schemaFiles, [
    "aggregate-v1.schema.json",
    "aggregate-v2.schema.json",
    "event-v1.schema.json",
    "event-v2.schema.json",
    "run-v1.schema.json",
    "run-v2.schema.json",
    "scenario-manifest-v1.schema.json",
    "tools-list-v1.schema.json",
  ]);
  for (const schemaFile of schemaFiles) {
    assert.doesNotThrow(() => readSchema(schemaFile));
  }

  assert.deepEqual(
    {
      "event-v1.schema.json": normalizedFileSha256("event-v1.schema.json"),
      "run-v1.schema.json": normalizedFileSha256("run-v1.schema.json"),
      "aggregate-v1.schema.json": normalizedFileSha256("aggregate-v1.schema.json"),
    },
    {
      "event-v1.schema.json":
        "858abbb39f87ee52f5e6acdad5f6dfcbbe89e906e6cb9e4f4513bcd583c15445",
      "run-v1.schema.json":
        "79db23e986d322914a5d0c060a229663cc42cd349e87ab0d20f483fe6f3c5fcf",
      "aggregate-v1.schema.json":
        "85948150e8a9ba1171719829d13a7b5b7799476a2f61dc4dd720b2cb3524e234",
    }
  );

  const sampleRun = timedRun({ iteration: 1, temperature: "cold", queue: 1 });
  const sampleAggregate = aggregateBenchmarkRuns([sampleRun], BINDING);
  const legacyRun = record([
    v1Start(),
    v1Event("compile_attempt", { attempt: 1, outcome: "success" }),
    v1Event("outcome", { success: true }),
  ]);
  const legacyAggregate = aggregateBenchmarkRuns([legacyRun], BINDING);
  const sampleTools = createToolsListSnapshot([{ name: "tool", description: "description" }]);
  const runSchema = readSchema("run-v2.schema.json");
  const aggregateSchema = readSchema("aggregate-v2.schema.json");
  const legacyRunSchema = readSchema("run-v1.schema.json");
  const legacyAggregateSchema = readSchema("aggregate-v1.schema.json");
  const manifestSchema = readSchema("scenario-manifest-v1.schema.json");
  const toolsSchema = readSchema("tools-list-v1.schema.json");
  const eventSchema = readSchema("event-v2.schema.json");
  const legacyEventSchema = readSchema("event-v1.schema.json");

  assert.deepEqual([...runSchema.required].sort(), Object.keys(sampleRun).sort());
  assert.deepEqual(
    [...runSchema.properties.metrics.required].sort(),
    [...NUMERIC_METRIC_KEYS, ...BOOLEAN_METRIC_KEYS].sort()
  );
  assert.deepEqual(
    [...runSchema.properties.runtime_error_categories.required].sort(),
    [...RUNTIME_ERROR_CATEGORIES].sort()
  );
  assert.deepEqual([...aggregateSchema.required].sort(), Object.keys(sampleAggregate).sort());
  assert.deepEqual(
    [...aggregateSchema.properties.groups.items.required].sort(),
    Object.keys(sampleAggregate.groups[0]).sort()
  );
  assert.deepEqual([...legacyRunSchema.required].sort(), Object.keys(legacyRun).sort());
  assert.deepEqual(
    [...legacyRunSchema.properties.metrics.required].sort(),
    [...V1_NUMERIC_METRIC_KEYS, ...BOOLEAN_METRIC_KEYS].sort()
  );
  assert.deepEqual(
    [...legacyAggregateSchema.required].sort(),
    Object.keys(legacyAggregate).sort()
  );
  assert.deepEqual([...manifestSchema.required].sort(), Object.keys(MANIFEST).sort());
  assert.deepEqual([...toolsSchema.required].sort(), Object.keys(sampleTools).sort());
  assert.equal(
    legacyEventSchema.oneOf.some(
      (candidate) => candidate.properties?.type?.const === "internal_measurement"
    ),
    false
  );
  const runtimeEventSchema = eventSchema.oneOf.find(
    (candidate) => candidate.properties?.type?.const === "runtime_error"
  );
  const internalEventSchema = eventSchema.oneOf.find(
    (candidate) => candidate.properties?.type?.const === "internal_measurement"
  );
  assert.deepEqual([...runtimeEventSchema.required].sort(), [
    "category",
    "schema_version",
    "type",
  ]);
  assert.equal(runtimeEventSchema.additionalProperties, false);
  assert.deepEqual([...internalEventSchema.required].sort(), [
    "code_sha256",
    "command_context_wait_ms",
    "compilation_attempts",
    "compilation_errors",
    "correlation_id",
    "end_to_end_ms",
    "execution_ms",
    "measurement_status",
    "partial_reason",
    "request_id",
    "returned_payload_utf8_bytes",
    "roslyn_cache_hits",
    "roslyn_cache_misses",
    "schema_version",
    "type",
  ]);
  assert.equal(internalEventSchema.additionalProperties, false);
});
