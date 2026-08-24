import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { readFileSync } from "node:fs";
import test from "node:test";
import { z } from "zod";
import { MCP_BENCHMARK_META_KEY } from "../../build/benchmark/liveTrace.js";
import { bindBenchmarkManifest } from "../../build/benchmark/manifest.js";
import { sha256Utf8 } from "../../build/benchmark/measure.js";
import {
  READ_ONLY_LIVE_QUERY_CODE,
  READ_ONLY_LIVE_SCENARIO_ID,
  ReadOnlyLiveProbeError,
  assertReadOnlyOutputTargetIsSafe,
  normalizeDrawingPath,
  parseReadOnlyConnectionSettings,
  resolveReadOnlyOutputPath,
  runReadOnlyLiveBenchmark,
} from "../../build/benchmark/readOnlyLiveRunner.js";
import { EVENT_SCHEMA_VERSION, RUN_SCHEMA_VERSION } from "../../build/benchmark/schema.js";

const BINDING = bindBenchmarkManifest(
  readFileSync("benchmark/scenarios.v1.json", "utf8")
);
const RUN_ID = "run-0123456789abcdef0123456789abcdef";
const EXPECTED_DRAWING = "C:\\Teszt\\Árvíztűrő\\synthetic-benchmark.dwg";

function measurement(values = {}) {
  return {
    schema_version: EVENT_SCHEMA_VERSION,
    type: "internal_measurement",
    correlation_id: RUN_ID,
    request_id: "request-0123456789abcdef",
    code_sha256: sha256Utf8(READ_ONLY_LIVE_QUERY_CODE),
    measurement_status: "complete",
    partial_reason: null,
    roslyn_cache_hits: 0,
    roslyn_cache_misses: 1,
    compilation_attempts: 1,
    compilation_errors: 0,
    command_context_wait_ms: 2,
    execution_ms: 3,
    end_to_end_ms: 6,
    returned_payload_utf8_bytes: 240,
    ...values,
  };
}

function textResult(payload, extra = {}) {
  return {
    content: [{ type: "text", text: JSON.stringify(payload) }],
    ...extra,
  };
}

function queryPayload(values = {}) {
  return {
    schemaVersion: "civil3d-mcp-readonly-live-payload/v1",
    databaseFilename: "c:/teszt/árvíztűrő/SYNTHETIC-BENCHMARK.DWG",
    dbmod: 0,
    alignmentCount: 2,
    surfaceCount: 3,
    cogoPointCount: 5,
    ...values,
  };
}

function makeClient({ tools, skillsResult, queryResult, queryError } = {}) {
  const state = { listCalls: 0, toolCalls: [], closeCalls: 0 };
  const client = {
    async listTools() {
      state.listCalls += 1;
      return {
        tools:
          tools ??
          ["civil3d_execute", "civil3d_query", "civil3d_skills"].map((name) => ({ name })),
      };
    },
    async callTool(params) {
      state.toolCalls.push(params);
      if (params.name === "civil3d_skills") {
        return skillsResult ?? textResult({ count: 11, skills: Array.from({ length: 11 }, () => ({})) });
      }
      if (queryError) throw queryError;
      return (
        queryResult ??
        textResult(queryPayload(), {
          _meta: { [MCP_BENCHMARK_META_KEY]: measurement() },
        })
      );
    },
    async close() {
      state.closeCalls += 1;
    },
  };
  return { client, state };
}

function input(values = {}) {
  return {
    binding: BINDING,
    serverPath: "C:\\repo\\build\\index.js",
    scenarioId: READ_ONLY_LIVE_SCENARIO_ID,
    variantId: "phase-2a2-readonly",
    iteration: 1,
    temperatureState: "cold",
    expectedDrawingPath: EXPECTED_DRAWING,
    ...values,
  };
}

function dependencies(client, counters = { connects: 0, ids: 0 }) {
  return {
    counters,
    value: {
      async connect() {
        counters.connects += 1;
        return client;
      },
      createRunId() {
        counters.ids += 1;
        return RUN_ID;
      },
    },
  };
}

test("2A.2 runner records one fixed query and never calls execute", async () => {
  const { client, state } = makeClient();
  const deps = dependencies(client);
  const record = await runReadOnlyLiveBenchmark(input(), deps.value);

  assert.equal(record.schema_version, RUN_SCHEMA_VERSION);
  assert.equal(record.measurement_mode, "plugin_internal");
  assert.equal(record.metrics.tool_calls.value, 1);
  assert.equal(record.metrics.model_tool_rounds.value, 0);
  assert.equal(record.metrics.runtime_errors.value, 0);
  assert.equal(record.metrics.outcome_success.value, true);
  assert.equal(record.metrics.first_pass_success.value, true);
  assert.equal(record.metrics.queue_ms.availability, "measured");
  assert.equal(record.metrics.execution_ms.availability, "measured");
  assert.equal(record.internal_requests.length, 1);
  assert.equal(record.generated_csharp.length, 1);
  assert.equal(record.generated_csharp[0].sha256, sha256Utf8(READ_ONLY_LIVE_QUERY_CODE));

  assert.equal(state.listCalls, 1);
  assert.deepEqual(
    state.toolCalls.map((call) => call.name),
    ["civil3d_skills", "civil3d_query"]
  );
  assert.equal(state.toolCalls.filter((call) => call.name === "civil3d_query").length, 1);
  assert.equal(state.toolCalls.some((call) => call.name === "civil3d_execute"), false);
  assert.equal(state.toolCalls[1].arguments.code, READ_ONLY_LIVE_QUERY_CODE);
  assert.deepEqual(state.toolCalls[1]._meta[MCP_BENCHMARK_META_KEY], {
    enabled: true,
    run_id: RUN_ID,
  });
  assert.equal(state.closeCalls, 1);

  const serialized = JSON.stringify(record);
  assert.equal(serialized.includes(EXPECTED_DRAWING), false);
  assert.equal(serialized.includes("databaseFilename"), false);
  assert.equal(serialized.includes("CivilDoc.GetSurfaceIds"), false);
});

test("drawing path comparison is Windows-aware without persisting the path", () => {
  assert.equal(
    normalizeDrawingPath("\\\\?\\UNC\\Server\\Share\\Árvíz\\fixture.DWG"),
    normalizeDrawingPath("\\\\server\\share\\árvíz\\FIXTURE.dwg")
  );
  assert.equal(
    normalizeDrawingPath("\\\\?\\C:\\Teszt\\Árvíztűrő\\fixture.dwg"),
    normalizeDrawingPath("c:/teszt/árvíztűrő/FIXTURE.DWG")
  );
});

test("live connection stays on loopback and outlasts inner timeouts", () => {
  assert.deepEqual(parseReadOnlyConnectionSettings({}), {
    host: "localhost",
    port: 8080,
    connectTimeoutMs: 5_000,
    commandTimeoutMs: 120_000,
    sdkRequestTimeoutMs: 135_000,
  });
  assert.equal(
    parseReadOnlyConnectionSettings({
      CIVIL3D_HOST: "[::1]",
      CIVIL3D_PORT: "18080",
      CIVIL3D_CONNECT_TIMEOUT: "60000",
      CIVIL3D_COMMAND_TIMEOUT: "600000",
    }).sdkRequestTimeoutMs,
    670_000
  );
  for (const environment of [
    { CIVIL3D_HOST: "192.0.2.10" },
    { CIVIL3D_PORT: "0" },
    { CIVIL3D_PORT: "65536" },
    { CIVIL3D_CONNECT_TIMEOUT: "0" },
    { CIVIL3D_COMMAND_TIMEOUT: "600001" },
  ]) {
    assert.throws(
      () => parseReadOnlyConnectionSettings(environment),
      (error) =>
        error instanceof ReadOnlyLiveProbeError &&
        error.code === "PROBE.INVALID_CONNECTION_CONFIG"
    );
  }
});

test("local MCP SDK preserves request and response benchmark metadata", async () => {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "metadata-test-server", version: "1.0.0" });
  let receivedMeta;
  server.tool("metadata_echo", "metadata test", { value: z.string() }, async (_args, extra) => {
    receivedMeta = extra._meta;
    return {
      content: [{ type: "text", text: "ok" }],
      _meta: { [MCP_BENCHMARK_META_KEY]: { marker: "response" } },
    };
  });
  const client = new Client({ name: "metadata-test-client", version: "1.0.0" });
  try {
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    const result = await client.callTool(
      {
        name: "metadata_echo",
        arguments: { value: "test" },
        _meta: { [MCP_BENCHMARK_META_KEY]: { marker: "request" } },
      },
      undefined,
      { timeout: 1_000 }
    );
    assert.deepEqual(receivedMeta[MCP_BENCHMARK_META_KEY], { marker: "request" });
    assert.deepEqual(result._meta[MCP_BENCHMARK_META_KEY], { marker: "response" });
  } finally {
    await client.close();
    await server.close();
  }
});

test("live output is limited to benchmark-output JSONL", () => {
  const workspace = "C:\\repo";
  assert.equal(resolveReadOnlyOutputPath(undefined, false, workspace), undefined);
  assert.equal(
    resolveReadOnlyOutputPath("benchmark-output\\runs.jsonl", true, workspace),
    "C:\\repo\\benchmark-output\\runs.jsonl"
  );
  for (const [outputPath, append] of [
    ["-", true],
    ["drawing.dwg", false],
    ["..\\outside.jsonl", false],
    ["benchmark-output\\nested\\runs.jsonl", false],
    ["benchmark-output\\not-json.txt", false],
  ]) {
    assert.throws(
      () => resolveReadOnlyOutputPath(outputPath, append, workspace),
      (error) =>
        error instanceof ReadOnlyLiveProbeError && error.code === "PROBE.INVALID_OUTPUT"
    );
  }
});

test("live output rejects linked roots and accidental overwrite", async () => {
  const outputPath = "C:\\repo\\benchmark-output\\runs.jsonl";
  const rootPath = "C:\\repo\\benchmark-output";
  const missing = Object.assign(new Error("missing"), { code: "ENOENT" });
  const info = (kind, linked = false) => ({
    isDirectory: () => kind === "directory",
    isFile: () => kind === "file",
    isSymbolicLink: () => linked,
  });

  await assertReadOnlyOutputTargetIsSafe(outputPath, false, async (target) => {
    if (target === rootPath) return info("directory");
    throw missing;
  });

  await assert.rejects(
    assertReadOnlyOutputTargetIsSafe(outputPath, false, async (target) =>
      target === rootPath ? info("directory", true) : info("file")
    ),
    (error) =>
      error instanceof ReadOnlyLiveProbeError && error.code === "PROBE.INVALID_OUTPUT"
  );

  await assert.rejects(
    assertReadOnlyOutputTargetIsSafe(outputPath, false, async (target) =>
      target === rootPath ? info("directory") : info("file")
    ),
    (error) =>
      error instanceof ReadOnlyLiveProbeError && error.code === "PROBE.INVALID_OUTPUT"
  );

  await assertReadOnlyOutputTargetIsSafe(outputPath, true, async (target) =>
    target === rootPath ? info("directory") : info("file")
  );
});

test("write and unknown scenarios fail before connection", async () => {
  for (const scenarioId of [
    "execute.single-entity.synthetic.v1",
    "unknown.synthetic.v1",
  ]) {
    const { client } = makeClient();
    const counters = { connects: 0, ids: 0 };
    const deps = dependencies(client, counters);
    await assert.rejects(
      runReadOnlyLiveBenchmark(input({ scenarioId }), deps.value),
      (error) =>
        error instanceof ReadOnlyLiveProbeError &&
        (error.code === "PROBE.SCENARIO_NOT_ALLOWED" ||
          error.message.includes("scenario_id"))
    );
    assert.equal(counters.connects, 0);
    assert.equal(counters.ids, 0);
  }
});

test("invalid drawing input fails before connection", async () => {
  const { client } = makeClient();
  const counters = { connects: 0, ids: 0 };
  const deps = dependencies(client, counters);
  await assert.rejects(
    runReadOnlyLiveBenchmark(input({ expectedDrawingPath: "relative.dwg" }), deps.value),
    (error) =>
      error instanceof ReadOnlyLiveProbeError && error.code === "PROBE.INVALID_INPUT"
  );
  assert.equal(counters.connects, 0);
  assert.equal(counters.ids, 0);
});

test("query failure is not retried and closes the client", async () => {
  const secret = "C:\\client\\secret.dwg";
  const { client, state } = makeClient({ queryError: new Error(secret) });
  const deps = dependencies(client);
  await assert.rejects(
    runReadOnlyLiveBenchmark(input(), deps.value),
    (error) =>
      error instanceof ReadOnlyLiveProbeError && error.code === "PROBE.QUERY_FAILED"
  );
  assert.equal(state.toolCalls.filter((call) => call.name === "civil3d_query").length, 1);
  assert.equal(state.closeCalls, 1);
});

test("wrong drawing, dirty drawing, and partial measurement fail closed", async () => {
  const cases = [
    {
      queryResult: textResult(queryPayload({ databaseFilename: "C:\\wrong\\drawing.dwg" }), {
        _meta: { [MCP_BENCHMARK_META_KEY]: measurement() },
      }),
      code: "PROBE.DRAWING_GUARD_FAILED",
    },
    {
      queryResult: textResult(queryPayload({ dbmod: 1 }), {
        _meta: { [MCP_BENCHMARK_META_KEY]: measurement() },
      }),
      code: "PROBE.DRAWING_NOT_CLEAN",
    },
    {
      queryResult: textResult(queryPayload(), {
        _meta: {
          [MCP_BENCHMARK_META_KEY]: measurement({
            measurement_status: "partial",
            partial_reason: "missing_sidecar",
            roslyn_cache_hits: null,
            roslyn_cache_misses: null,
            compilation_attempts: null,
            compilation_errors: null,
            command_context_wait_ms: null,
            execution_ms: null,
          }),
        },
      }),
      code: "PROBE.MEASUREMENT_INCOMPLETE",
    },
    {
      queryResult: textResult(queryPayload(), {
        _meta: {
          [MCP_BENCHMARK_META_KEY]: measurement({
            roslyn_cache_hits: 0,
            roslyn_cache_misses: 0,
            compilation_attempts: 0,
            command_context_wait_ms: null,
            execution_ms: null,
          }),
        },
      }),
      code: "PROBE.MEASUREMENT_INCOMPLETE",
    },
  ];

  for (const testCase of cases) {
    const { client, state } = makeClient({ queryResult: testCase.queryResult });
    const deps = dependencies(client);
    await assert.rejects(
      runReadOnlyLiveBenchmark(input(), deps.value),
      (error) => error instanceof ReadOnlyLiveProbeError && error.code === testCase.code
    );
    assert.equal(state.toolCalls.filter((call) => call.name === "civil3d_query").length, 1);
    assert.equal(state.closeCalls, 1);
  }
});

test("tool-surface mismatch stops before any tool call", async () => {
  const { client, state } = makeClient({
    tools: ["civil3d_query", "civil3d_skills"].map((name) => ({ name })),
  });
  const deps = dependencies(client);
  await assert.rejects(
    runReadOnlyLiveBenchmark(input(), deps.value),
    (error) =>
      error instanceof ReadOnlyLiveProbeError &&
      error.code === "PROBE.TOOL_SURFACE_MISMATCH"
  );
  assert.equal(state.toolCalls.length, 0);
  assert.equal(state.closeCalls, 1);
});
