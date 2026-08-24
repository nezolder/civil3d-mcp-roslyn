# Benchmark harness (phases 2A, 2A.1, and 2A.2)

This directory defines a host-independent measurement contract for comparing the preserved Civil 3D 2025 baseline with later, single-function phases. Phase 2A.1 adds opt-in request-scoped internal measurements. Phase 2A.2 adds a deliberately small, separately invoked read-only runner for an authorized live acceptance probe. It does not register an MCP tool, add a queue or retry, alter transaction behavior, or change Roslyn cache semantics.

## Contract and data sources

Every run value is wrapped as `{ value, availability, source }`. A value that was not observed is always `{ "value": null, "availability": "not_available", ... }`; the recorder never turns a missing event into zero. Zero external counts are valid only when `run_start.observed_counts` declares that the external runner covered that count. A v2 `plugin_internal` run requires one or more `internal_measurement` events with unique request IDs; events may be complete or explicitly partial.

Every sanitized run and aggregate is bound to the supplied manifest by `manifest_sha256`, `model_config_id`, `runner_config_id`, and `drawing_fixture_id`. The manifest SHA-256 is calculated from the validated manifest serialized as canonical JSON with recursively sorted object keys and no insignificant whitespace. File indentation, object-key order, and LF versus CRLF therefore do not change the hash.

| Output field | Source |
| --- | --- |
| `tools[*].top_level_description` | Measured from the actual MCP `tools/list` top-level description. |
| `tools[*].all_description_fields` | Measured from every string-valued `description` field in that tool's `tools/list` object, including nested input-schema descriptions. |
| Description `characters` | Unicode code points. |
| Description `utf8_bytes` | Exact UTF-8 byte count. |
| Description `estimated_tokens` | Documented estimator `utf8_bytes_div_4_ceil_v1`: `ceil(utf8_bytes / 4)` separately for each description field, then summed. This is an estimate, not a tokenizer result. |
| `generated_csharp[*]` and generated-code totals | Measured from a transient `generated_csharp` JSONL event. Only character/line/byte counts and SHA-256 are retained. Empty text has zero lines; otherwise CRLF, LF, and CR delimit lines. |
| `tool_calls`, `model_tool_rounds` | Counted from external `tool_call` and `model_tool_round` trace events. A declared, observed zero is sourced from explicit runner coverage. The baseline cannot infer model rounds internally. |
| `internal_requests[*]` | Per-request opaque ID, exact forwarded-C# SHA-256, completeness status, and fixed partial reason. Only the hash is retained, so multiple attempts remain individually checkable without storing code. |
| `roslyn_cache_hits`, `roslyn_cache_misses` | Counted from the actual existing `_scriptCache.TryGetValue` lookup and summed across complete request events. One measured request performs at most one lookup. If any request lacks the plugin observation, the run totals are `not_available`, not lower-bound zeroes. |
| `compilation_attempts`, `compilation_errors` | External mode counts `compile_attempt` events. Internal mode sums actual `script.Compile()` calls and failed compile attempts across complete request events; a returned diagnostic with `Severity.Error` marks that attempt as failed even when `Compile()` does not throw. No diagnostic text is retained. A partial request makes these run totals `not_available`. |
| `compilation_retries` | Derived as `max(0, compilation_attempts - 1)` when compilation attempts are available. |
| `runtime_errors` | Counted only from external `runtime_error` benchmark-category events. Allowed categories are `runtime_exception`, `timeout`, `transport_error`, and `unknown`; no message, type, stack, or path is accepted. |
| `returned_payload_utf8_bytes` | External mode uses a transient `serialized_payload` or runner byte count. Internal mode sums UTF-8 bytes of `JSON.stringify()` over every normal MCP tool result before trace `_meta` is attached, so trace metadata is excluded; this is not the full JSON-RPC wire envelope. The value remains known for partial events. |
| `queue_ms` | In internal mode each request measures command-context wait with monotonic `Stopwatch`, from immediately before `ExecuteInCommandContextAsync` until its callback begins. The run value is the cumulative sum across requests, not wall-clock makespan. Any unknown request value makes the run metric `not_available`. The field name is retained for aggregate compatibility; no queue is introduced. |
| `execution_ms` | Each request uses monotonic `Stopwatch` from command-context callback entry through document lookup, lock, transaction, Roslyn execution, optional commit, disposal, and exception capture. The run value is the cumulative sum, and becomes `not_available` if any request value is unknown. |
| `end_to_end_ms` | Complete responses use monotonic Node `performance.now()` from immediately before the private TCP write until response parsing. A failure before `sendCommand` uses the enclosing connection/send attempt boundary instead. The run value is the sum of per-request durations, including partial events; it is not elapsed run makespan and overlapping requests therefore contribute separately. |
| `temperature_state` | Explicit `cold` or `warm` runner input. |
| `outcome_success` | External overall outcome event. |
| `first_pass_success` | Derived only when outcome plus complete compilation/runtime coverage are available. It requires success, zero compile retries, zero compile errors, and zero runtime errors. A query/write run is compile-covered by either at least one actual compile attempt or at least one proven Roslyn cache hit; therefore a warm cache hit with zero `Compile()` calls can be first-pass success. |

Description hashes are SHA-256 over the deterministically ordered JSON path/text vector; raw description text is not retained in the snapshot. Generated C# hashes are SHA-256 directly over the transient UTF-8 code text.

The recorder accepts one run per JSONL stream. Its first event is `run_start`; supported v2 measurement events are `tool_call`, `model_tool_round`, `generated_csharp`, `internal_measurement`, `compile_attempt`, `runtime_error`, `returned_payload`, `timing`, and `outcome`. `run_start.measurement_mode` is either `external` or `plugin_internal`. See [event-v2.schema.json](schemas/event-v2.schema.json) and [run-v2.schema.json](schemas/run-v2.schema.json). The published v1 event/run/aggregate schema files remain unchanged, and the recorder/aggregator continue to accept homogeneous legacy v1 external streams.

Emit one `tool_call` event per MCP tool request. Emit one `model_tool_round` event per model response that produces a tool-call batch and receives the corresponding tool results before the next model response. Emit one `compile_attempt` for each actual Roslyn compilation invocation, including retries, and one categorized `runtime_error` for each observed non-compilation runtime, timeout, transport, or unknown failure. These definitions are runner obligations; the 2A harness does not infer them from elapsed time or result text.

## Opt-in live trace (phase 2A.1)

The live runner opts in per `civil3d_query` or `civil3d_execute` call through MCP request metadata, not through the public tool arguments:

```json
{
  "_meta": {
    "civil3d-mcp/benchmark": {
      "enabled": true,
      "run_id": "run-0123456789abcdef0123456789abcdef"
    }
  }
}
```

The runner must generate `run_id` as `run-` plus 32 lowercase random hexadecimal characters and use that exact opaque ID in `run_start`. Arbitrary labels, paths, drawing names, or client text are rejected rather than sanitized. Node derives a separate opaque request ID by SHA-256 hashing the run ID together with the MCP SDK request ID; the SDK request ID itself is not forwarded. Missing benchmark metadata or `enabled=false` preserves normal behavior. Once `enabled=true` is present, a missing, malformed, non-opaque, or extra-field run request fails closed with a fixed input error before query/write execution.

Only the schema version and opaque run ID cross the private Node-to-plugin TCP hop. The plugin creates a fresh measurement object per request and returns a sidecar on both result and plugin-error responses. Node validates the sidecar with exact fields and places the recorder-ready `internal_measurement` event under response `_meta["civil3d-mcp/benchmark"]`. Normal `content` and `isError` remain unchanged. With no benchmark metadata, no private request field, TCP sidecar, or MCP result `_meta` is produced.

The runner copies every call's `_meta` value into its JSONL event stream. A `plugin_internal` run rejects zero internal events, duplicate request IDs, correlation mismatch, non-opaque IDs, or mixing with external `compile_attempt`, `timing`, or `returned_payload` events. `generated_csharp`, `tool_call`, model-round, sanitized runtime-category, and outcome events remain runner-provided; when generated C# is supplied, every per-request executed hash must match one of its attempts.

If TCP times out, transport fails, or the plugin sidecar is missing/invalid, Node emits a machine-readable partial event. It retains only the opaque IDs, forwarded-code SHA-256, fixed partial reason, elapsed end-to-end time, and the byte size later measured from the normal MCP error result. Cache, compilation, command-wait, and execution fields are `null`; the recorder converts their run totals to `not_available`. No raw transport or plugin error is copied into the event. The existing Node and Roslyn defaults are both 120 seconds, so Node can win a boundary race; the partial timeout path intentionally preserves that request instead of inventing plugin values. A live runner may configure `CIVIL3D_COMMAND_TIMEOUT` above the known Roslyn timeout, but instrumentation itself does not change timeout or retry behavior.

`record` validates `suite_id`, `scenario_id`, `temperature_state`, and `iteration` against the supplied manifest. `aggregate` validates every record's canonical manifest hash and model/runner/drawing binding before grouping it.

## Minimal read-only live runner (phase 2A.2)

`live-readonly` is intentionally narrower than the full manifest. Before connecting it validates every CLI value, then checks that the server still exposes exactly the three public tools and that `civil3d_skills` can list at least one skill. Those two checks are preflight only. The measured run contains exactly one call to `civil3d_query` with fixed, reviewed C# that returns only the active `Database.Filename`, `DBMOD`, and bounded alignment/surface/COGO-point counts.

The expected full DWG path is supplied only through `CIVIL3D_BENCHMARK_EXPECTED_DRAWING`. The runner compares it in memory and never copies the path or normal tool response into the saved record. It also requires `DBMOD=0`. Only the existing recorder's sanitized v2 run record is written; there is no intermediate raw event file. File output is restricted to a direct `.jsonl` child of the workspace `benchmark-output` directory (no linked directory or subdirectory), and an existing file requires explicit `--append`.

The runner rejects every scenario except `query.bounded-summary.synthetic.v1`. It has no arbitrary-code or prompt input, cannot call `civil3d_execute`, makes one query attempt, and never retries automatically. The manifest's write scenario is therefore outside phase 2A.2. Its TCP target is restricted to loopback (`localhost`, `127.0.0.1`, or `::1`), and port/timeouts are validated before the MCP child starts. The outer MCP request timeout is longer than the connection plus Civil 3D command timeouts.

An authorized live invocation uses an already open, disposable synthetic DWG:

```powershell
$env:CIVIL3D_BENCHMARK_EXPECTED_DRAWING = "C:\path\to\synthetic-benchmark.dwg"

npm run benchmark -- live-readonly `
  --manifest benchmark/scenarios.v1.json `
  --scenario-id query.bounded-summary.synthetic.v1 `
  --variant-id phase-2a2-readonly `
  --iteration 1 `
  --temperature-state cold `
  --output benchmark-output/runs.phase-2a2-readonly.jsonl `
  --append
```

The operator remains responsible for truthfully labelling cold/warm state and resetting the disposable fixture when required. Phase 2A.2 does not restart Civil 3D, load the plugin, reset caches, create a drawing, or automate repetitions. If preflight, query, drawing guard, or internal measurement validation fails, the command exits non-zero and writes no run record; aggregation therefore exposes a missing iteration instead of accepting an unguarded or invented failure record. Building or unit-testing the runner is not a live Civil 3D result.

## Commands

Install the lockfile dependencies and build once:

```powershell
npm ci --cache .npm-cache
npm run build
```

Capture the current `tools/list` descriptions without contacting Civil 3D:

```powershell
npm run benchmark -- tools-list --output benchmark-output/tools-list.baseline.json
```

Validate the fixed scenario manifest:

```powershell
npm run benchmark -- validate-manifest --input benchmark/scenarios.v1.json
```

The live runner should stream event JSONL to stdin so raw generated C# and returned payloads do not need an intermediate file:

```powershell
& .\path\to\external-runner.ps1 | node .\build\benchmark\cli.js record --manifest .\benchmark\scenarios.v1.json --input - --output .\benchmark-output\runs.baseline.jsonl --append
```

Replace the example runner path with the actual runner command. PowerShell invokes the Node CLI directly here so npm argument forwarding cannot consume the recorder option names.

Aggregate completed sanitized run records:

```powershell
npm run benchmark -- aggregate --manifest benchmark/scenarios.v1.json --input benchmark-output/runs.baseline.jsonl --output benchmark-output/aggregate.baseline.json
```

Generated files belong under `benchmark-output/`, which is ignored by Git. The tracked manifest and tests use only artificial, non-client data.

## Repetitions and deterministic aggregation

[scenarios.v1.json](scenarios.v1.json) fixes separate model and runner configuration IDs, one disposable synthetic drawing fixture ID, three artificial scenarios, both temperature states, and five repetitions per state. Aggregation groups by variant, scenario, and cold/warm state under that manifest binding. It rejects duplicate `run_id` values and duplicate variant/scenario/temperature/iteration records. A v2 aggregate also requires one uniform `measurement_mode` across its input and records that mode at aggregate and group level, so external and plugin-internal timing meanings cannot share a median or p95.

For each variant the aggregate materializes every manifest scenario and temperature group, including completely missing groups. Each group reports `expected_repetitions`, `observed_repetitions`, `missing_iterations`, and `complete`. Missing metric observations within present records remain visible through `not_available_count`.

Median is the middle sorted value, or the arithmetic mean of the two middle values for an even sample. P95 uses nearest rank: sort ascending, set `rank = max(1, ceil(0.95 * n))`, and select the one-based rank. Therefore p95 of five repetitions is their maximum. The definition is embedded in every aggregate.

## Live prerequisites and comparison procedure

No live run is part of phase 2A. A later authorized runner must use a disposable, synthetic benchmark drawing; pin the model/configuration and manifest; start from the same fixture state; and prevent names, paths, coordinates, object dumps, or client data from entering identifiers or result records.

For a cold repetition, restart the components and reset only the explicitly defined benchmark caches/fixture. For a warm repetition, keep the same component processes and eligible caches after a documented warm-up. Use the fixed internal timing boundaries above for 2A.1 comparisons. Supplying timing metadata does not change scheduling or execution semantics.

Use a distinct sanitized `variant_id` for baseline and each later phase. Compare aggregates only when the suite ID, canonical manifest SHA-256, model configuration, runner configuration, synthetic drawing fixture, repetition count, timing boundaries, and cold/warm procedure match. The aggregator refuses records with a different binding. Baseline fields that the external runner cannot observe remain `not_available`; do not replace them with estimates.

## Deliberate limitations

- `tools/list` capture and manifest validation are offline; they do not prove Civil 3D runtime behavior.
- Model-round and overall outcome remain external runner observations. Internal compile/cache and timing fields exist only for explicitly opted-in query/execute calls.
- `runtime_error` stores only one of four benchmark category labels and deterministic counts. Phase 2B.1 structured tool errors remain separate and are not copied into benchmark records.
- No retry behavior is added. In particular, an uncertain write outcome must never be retried automatically.
- Raw C#, payload text, error messages, drawing paths, coordinates, and client fields are absent from the sanitized run and aggregate schemas.
- Offline unit tests and .NET compilation do not prove live Civil 3D timing, cache, main-thread, transaction, or response behavior; those require an authorized run against the disposable synthetic fixture.

Additional schemas: [aggregate-v2.schema.json](schemas/aggregate-v2.schema.json), the unchanged [aggregate-v1.schema.json](schemas/aggregate-v1.schema.json), [tools-list-v1.schema.json](schemas/tools-list-v1.schema.json), and [scenario-manifest-v1.schema.json](schemas/scenario-manifest-v1.schema.json).
