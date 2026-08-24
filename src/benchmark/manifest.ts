import { sha256Utf8 } from "./measure.js";
import {
  MANIFEST_SCHEMA_VERSION,
  SAFE_IDENTIFIER_PATTERN,
  TemperatureState,
} from "./schema.js";
import type { AnyBenchmarkRunRecord } from "./schema.js";

const PUBLIC_TOOLS = ["civil3d_execute", "civil3d_query", "civil3d_skills"] as const;
const OPERATION_KINDS = ["metadata", "read_only", "write"] as const;

export interface BenchmarkScenario {
  scenario_id: string;
  public_tool: (typeof PUBLIC_TOOLS)[number];
  operation_kind: (typeof OPERATION_KINDS)[number];
  live_required: boolean;
  fixture_reset_required: boolean;
  prompt: string;
}

export interface BenchmarkManifest {
  schema_version: typeof MANIFEST_SCHEMA_VERSION;
  suite_id: string;
  model_config_id: string;
  runner_config_id: string;
  drawing_fixture_id: string;
  repetitions_per_state: number;
  temperature_states: TemperatureState[];
  scenarios: BenchmarkScenario[];
}

export interface BenchmarkManifestBinding {
  manifest: BenchmarkManifest;
  manifest_sha256: string;
}

export interface ManifestRunPosition {
  suite_id: string;
  scenario_id: string;
  iteration: number;
  temperature_state: TemperatureState;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function assertExactKeys(
  value: Record<string, unknown>,
  keys: readonly string[],
  label: string
): void {
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${label} has unexpected or missing fields`);
  }
}

function assertIdentifier(value: unknown, label: string): asserts value is string {
  if (typeof value !== "string" || !SAFE_IDENTIFIER_PATTERN.test(value)) {
    throw new Error(`${label} must be a sanitized identifier`);
  }
}

/** JSON with recursively sorted object keys and no insignificant whitespace. */
export function canonicalJsonStringify(value: unknown): string {
  if (value === null || typeof value === "string" || typeof value === "boolean") {
    return JSON.stringify(value);
  }
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new Error("Canonical JSON cannot contain non-finite numbers");
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) {
    return `[${value.map((item) => canonicalJsonStringify(item)).join(",")}]`;
  }
  if (isPlainObject(value)) {
    const members = Object.keys(value)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${canonicalJsonStringify(value[key])}`);
    return `{${members.join(",")}}`;
  }
  throw new Error("Canonical JSON supports only JSON values");
}

export function parseBenchmarkManifest(value: unknown): BenchmarkManifest {
  if (!isPlainObject(value)) throw new Error("Benchmark manifest must be an object");
  assertExactKeys(
    value,
    [
      "schema_version",
      "suite_id",
      "model_config_id",
      "runner_config_id",
      "drawing_fixture_id",
      "repetitions_per_state",
      "temperature_states",
      "scenarios",
    ],
    "Benchmark manifest"
  );
  if (value.schema_version !== MANIFEST_SCHEMA_VERSION) {
    throw new Error(`Unsupported benchmark manifest schema: ${String(value.schema_version)}`);
  }
  assertIdentifier(value.suite_id, "suite_id");
  assertIdentifier(value.model_config_id, "model_config_id");
  assertIdentifier(value.runner_config_id, "runner_config_id");
  assertIdentifier(value.drawing_fixture_id, "drawing_fixture_id");
  if (
    !Number.isSafeInteger(value.repetitions_per_state) ||
    (value.repetitions_per_state as number) < 3 ||
    (value.repetitions_per_state as number) > 5
  ) {
    throw new Error("repetitions_per_state must be an integer from 3 through 5");
  }
  if (
    !Array.isArray(value.temperature_states) ||
    JSON.stringify(value.temperature_states) !== JSON.stringify(["cold", "warm"])
  ) {
    throw new Error("temperature_states must be exactly [cold, warm]");
  }
  if (!Array.isArray(value.scenarios) || value.scenarios.length === 0) {
    throw new Error("scenarios must be a non-empty array");
  }

  const scenarios: BenchmarkScenario[] = value.scenarios.map((rawScenario, index) => {
    if (!isPlainObject(rawScenario)) throw new Error(`scenarios[${index}] must be an object`);
    assertExactKeys(
      rawScenario,
      [
        "scenario_id",
        "public_tool",
        "operation_kind",
        "live_required",
        "fixture_reset_required",
        "prompt",
      ],
      `scenarios[${index}]`
    );
    assertIdentifier(rawScenario.scenario_id, `scenarios[${index}].scenario_id`);
    if (!PUBLIC_TOOLS.includes(rawScenario.public_tool as BenchmarkScenario["public_tool"])) {
      throw new Error(`scenarios[${index}].public_tool is unsupported`);
    }
    if (!OPERATION_KINDS.includes(rawScenario.operation_kind as BenchmarkScenario["operation_kind"])) {
      throw new Error(`scenarios[${index}].operation_kind is unsupported`);
    }
    if (
      typeof rawScenario.live_required !== "boolean" ||
      typeof rawScenario.fixture_reset_required !== "boolean"
    ) {
      throw new Error(`scenarios[${index}] live/reset flags must be booleans`);
    }
    if (
      typeof rawScenario.prompt !== "string" ||
      rawScenario.prompt.length === 0 ||
      rawScenario.prompt.length > 500
    ) {
      throw new Error(`scenarios[${index}].prompt must contain 1 through 500 characters`);
    }

    const tool = rawScenario.public_tool as BenchmarkScenario["public_tool"];
    const kind = rawScenario.operation_kind as BenchmarkScenario["operation_kind"];
    if (
      (tool === "civil3d_skills" && kind !== "metadata") ||
      (tool === "civil3d_query" && kind !== "read_only") ||
      (tool === "civil3d_execute" && kind !== "write")
    ) {
      throw new Error(`scenarios[${index}] tool and operation_kind do not match`);
    }
    if (kind !== "metadata" && rawScenario.live_required !== true) {
      throw new Error(`scenarios[${index}] Civil 3D operations must be marked live_required`);
    }
    if (kind === "write" && rawScenario.fixture_reset_required !== true) {
      throw new Error(`scenarios[${index}] write operations require fixture reset`);
    }

    return {
      scenario_id: rawScenario.scenario_id,
      public_tool: tool,
      operation_kind: kind,
      live_required: rawScenario.live_required,
      fixture_reset_required: rawScenario.fixture_reset_required,
      prompt: rawScenario.prompt,
    };
  });

  if (new Set(scenarios.map((scenario) => scenario.scenario_id)).size !== scenarios.length) {
    throw new Error("scenario_id values must be unique");
  }

  return {
    schema_version: MANIFEST_SCHEMA_VERSION,
    suite_id: value.suite_id,
    model_config_id: value.model_config_id,
    runner_config_id: value.runner_config_id,
    drawing_fixture_id: value.drawing_fixture_id,
    repetitions_per_state: value.repetitions_per_state as number,
    temperature_states: ["cold", "warm"],
    scenarios,
  };
}

export function bindBenchmarkManifest(rawText: string): BenchmarkManifestBinding {
  let parsedJson: unknown;
  try {
    parsedJson = JSON.parse(rawText);
  } catch {
    throw new Error("Benchmark manifest is not valid JSON");
  }
  const manifest = parseBenchmarkManifest(parsedJson);
  return {
    manifest,
    manifest_sha256: sha256Utf8(canonicalJsonStringify(manifest)),
  };
}

export function assertManifestRunPosition(
  binding: BenchmarkManifestBinding,
  position: ManifestRunPosition
): BenchmarkScenario {
  const { manifest } = binding;
  if (position.suite_id !== manifest.suite_id) {
    throw new Error("Run suite_id does not match the benchmark manifest");
  }
  const scenario = manifest.scenarios.find(
    (candidate) => candidate.scenario_id === position.scenario_id
  );
  if (!scenario) throw new Error("Run scenario_id is not defined by the benchmark manifest");
  if (!manifest.temperature_states.includes(position.temperature_state)) {
    throw new Error("Run temperature_state is not defined by the benchmark manifest");
  }
  if (position.iteration < 1 || position.iteration > manifest.repetitions_per_state) {
    throw new Error("Run iteration is outside the benchmark manifest repetition range");
  }
  return scenario;
}

export function assertBenchmarkRunMatchesManifest(
  run: AnyBenchmarkRunRecord,
  binding: BenchmarkManifestBinding
): BenchmarkScenario {
  const { manifest, manifest_sha256 } = binding;
  if (run.manifest_sha256 !== manifest_sha256) {
    throw new Error("Run manifest_sha256 does not match the supplied benchmark manifest");
  }
  if (run.model_config_id !== manifest.model_config_id) {
    throw new Error("Run model_config_id does not match the supplied benchmark manifest");
  }
  if (run.runner_config_id !== manifest.runner_config_id) {
    throw new Error("Run runner_config_id does not match the supplied benchmark manifest");
  }
  if (run.drawing_fixture_id !== manifest.drawing_fixture_id) {
    throw new Error("Run drawing_fixture_id does not match the supplied benchmark manifest");
  }
  return assertManifestRunPosition(binding, run);
}

export function summarizeBenchmarkManifest(rawText: string): {
  schema_version: "civil3d-mcp-benchmark-manifest-validation/v1";
  valid: true;
  suite_id: string;
  model_config_id: string;
  runner_config_id: string;
  drawing_fixture_id: string;
  scenario_count: number;
  planned_run_count: number;
  manifest_sha256: string;
} {
  const binding = bindBenchmarkManifest(rawText);
  const { manifest } = binding;
  return {
    schema_version: "civil3d-mcp-benchmark-manifest-validation/v1",
    valid: true,
    suite_id: manifest.suite_id,
    model_config_id: manifest.model_config_id,
    runner_config_id: manifest.runner_config_id,
    drawing_fixture_id: manifest.drawing_fixture_id,
    scenario_count: manifest.scenarios.length,
    planned_run_count:
      manifest.scenarios.length *
      manifest.temperature_states.length *
      manifest.repetitions_per_state,
    manifest_sha256: binding.manifest_sha256,
  };
}
