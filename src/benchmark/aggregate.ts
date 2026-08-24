import {
  BenchmarkManifestBinding,
  assertBenchmarkRunMatchesManifest,
} from "./manifest.js";
import {
  AGGREGATE_SCHEMA_VERSION,
  AGGREGATE_SCHEMA_VERSION_V1,
  AnyBenchmarkRunRecord,
  BOOLEAN_METRIC_KEYS,
  BenchmarkRunRecord,
  MeasurementMode,
  NUMERIC_METRIC_KEYS,
  NumericMetricKey,
  RUNTIME_ERROR_CATEGORIES,
  RUN_SCHEMA_VERSION,
  RuntimeErrorCategory,
  V1_NUMERIC_METRIC_KEYS,
  V1NumericMetricKey,
  assertBenchmarkRunRecord,
} from "./schema.js";

export const P95_DEFINITION = "nearest_rank: sorted ascending, rank=max(1,ceil(0.95*n))";

export interface NumericSummary {
  available_count: number;
  not_available_count: number;
  median: number | null;
  p95: number | null;
}

export interface BooleanSummary {
  available_count: number;
  not_available_count: number;
  true_count: number;
  false_count: number;
  true_rate: number | null;
}

export interface RuntimeErrorCategorySummary {
  available_run_count: number;
  not_available_run_count: number;
  total: number | null;
}

interface AggregateGroupCommon {
  suite_id: string;
  variant_id: string;
  scenario_id: string;
  temperature_state: "cold" | "warm";
  expected_repetitions: number;
  observed_repetitions: number;
  missing_iterations: number[];
  complete: boolean;
  iterations: number[];
  boolean_metrics: Record<(typeof BOOLEAN_METRIC_KEYS)[number], BooleanSummary>;
  runtime_error_categories: Record<RuntimeErrorCategory, RuntimeErrorCategorySummary>;
  generated_csharp_sha256: {
    available_run_count: number;
    not_available_run_count: number;
    unique_hashes: string[];
  };
}

export interface AggregateGroupV1 extends AggregateGroupCommon {
  numeric_metrics: Record<V1NumericMetricKey, NumericSummary>;
}

export interface AggregateGroup extends AggregateGroupCommon {
  measurement_mode: MeasurementMode;
  numeric_metrics: Record<NumericMetricKey, NumericSummary>;
}

interface BenchmarkAggregateCommon {
  manifest_sha256: string;
  suite_id: string;
  model_config_id: string;
  runner_config_id: string;
  drawing_fixture_id: string;
  expected_repetitions_per_state: number;
  p95_definition: typeof P95_DEFINITION;
}

export interface BenchmarkAggregateV1 extends BenchmarkAggregateCommon {
  schema_version: typeof AGGREGATE_SCHEMA_VERSION_V1;
  groups: AggregateGroupV1[];
}

export interface BenchmarkAggregate extends BenchmarkAggregateCommon {
  schema_version: typeof AGGREGATE_SCHEMA_VERSION;
  measurement_mode: MeasurementMode;
  groups: AggregateGroup[];
}

export type AnyBenchmarkAggregate = BenchmarkAggregateV1 | BenchmarkAggregate;

export function median(values: readonly number[]): number | null {
  if (values.length === 0) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 1
    ? sorted[middle]
    : (sorted[middle - 1] + sorted[middle]) / 2;
}

export function nearestRankP95(values: readonly number[]): number | null {
  if (values.length === 0) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const rank = Math.max(1, Math.ceil(0.95 * sorted.length));
  return sorted[rank - 1];
}

function numericSummary(values: readonly (number | null)[]): NumericSummary {
  const available = values.filter((value): value is number => value !== null);
  return {
    available_count: available.length,
    not_available_count: values.length - available.length,
    median: median(available),
    p95: nearestRankP95(available),
  };
}

function booleanSummary(values: readonly (boolean | null)[]): BooleanSummary {
  const available = values.filter((value): value is boolean => value !== null);
  const trueCount = available.filter(Boolean).length;
  return {
    available_count: available.length,
    not_available_count: values.length - available.length,
    true_count: trueCount,
    false_count: available.length - trueCount,
    true_rate: available.length === 0 ? null : trueCount / available.length,
  };
}

function runtimeCategorySummary(
  runs: readonly AnyBenchmarkRunRecord[],
  category: RuntimeErrorCategory
): RuntimeErrorCategorySummary {
  const values = runs
    .map((run) => run.runtime_error_categories[category].value)
    .filter((value): value is number => value !== null);
  return {
    available_run_count: values.length,
    not_available_run_count: runs.length - values.length,
    total: values.length === 0 ? null : values.reduce((sum, value) => sum + value, 0),
  };
}

function compareText(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

function compareRuns(a: AnyBenchmarkRunRecord, b: AnyBenchmarkRunRecord): number {
  return a.iteration - b.iteration || compareText(a.run_id, b.run_id);
}

function observedGroupKey(run: AnyBenchmarkRunRecord): string {
  return [run.variant_id, run.scenario_id, run.temperature_state].join("\u001f");
}

function iterationIdentity(run: AnyBenchmarkRunRecord): string {
  return [run.variant_id, run.scenario_id, run.temperature_state, run.iteration].join(
    "\u001f"
  );
}

export function aggregateBenchmarkRuns(
  rawRuns: readonly AnyBenchmarkRunRecord[],
  binding: BenchmarkManifestBinding
): AnyBenchmarkAggregate {
  if (rawRuns.length === 0) throw new Error("Benchmark run stream is empty");

  const schemaVersion = rawRuns[0].schema_version;
  const isV2 = schemaVersion === RUN_SCHEMA_VERSION;
  if (rawRuns.some((run) => run.schema_version !== schemaVersion)) {
    throw new Error("Benchmark aggregate cannot mix v1 and v2 run records");
  }

  let measurementMode: MeasurementMode = "external";
  if (isV2) {
    const modes = new Set((rawRuns as readonly BenchmarkRunRecord[]).map((run) => run.measurement_mode));
    if (modes.size !== 1) {
      throw new Error("Benchmark aggregate cannot mix measurement modes");
    }
    measurementMode = [...modes][0];
  }

  const runIds = new Set<string>();
  const iterationIdentities = new Set<string>();
  const grouped = new Map<string, AnyBenchmarkRunRecord[]>();
  for (const run of rawRuns) {
    assertBenchmarkRunRecord(run);
    assertBenchmarkRunMatchesManifest(run, binding);
    if (runIds.has(run.run_id)) throw new Error(`Duplicate run_id: ${run.run_id}`);
    runIds.add(run.run_id);

    const identity = iterationIdentity(run);
    if (iterationIdentities.has(identity)) {
      throw new Error(
        `Duplicate group temperature iteration: ${run.variant_id}/${run.scenario_id}/${run.temperature_state}/${run.iteration}`
      );
    }
    iterationIdentities.add(identity);

    const key = observedGroupKey(run);
    const existing = grouped.get(key);
    if (existing) existing.push(run);
    else grouped.set(key, [run]);
  }

  const variants = [...new Set(rawRuns.map((run) => run.variant_id))].sort(compareText);
  const scenarios = [...binding.manifest.scenarios].sort((a, b) =>
    compareText(a.scenario_id, b.scenario_id)
  );
  const expectedIterations = Array.from(
    { length: binding.manifest.repetitions_per_state },
    (_, index) => index + 1
  );
  const groups: (AggregateGroupV1 | AggregateGroup)[] = [];
  const numericMetricKeys = isV2 ? NUMERIC_METRIC_KEYS : V1_NUMERIC_METRIC_KEYS;

  for (const variantId of variants) {
    for (const scenario of scenarios) {
      for (const temperatureState of binding.manifest.temperature_states) {
        const key = [variantId, scenario.scenario_id, temperatureState].join("\u001f");
        const runs = [...(grouped.get(key) ?? [])].sort(compareRuns);
        const iterations = runs.map((run) => run.iteration);
        const observedIterations = new Set(iterations);
        const missingIterations = expectedIterations.filter(
          (iteration) => !observedIterations.has(iteration)
        );

        const numericMetrics: Record<string, NumericSummary> = {};
        for (const metric of numericMetricKeys) {
          numericMetrics[metric] = numericSummary(
            runs.map(
              (run) =>
                (run.metrics as unknown as Record<string, { value: number | null }>)[metric]
                  .value
            )
          );
        }
        const booleanMetrics = {} as Record<
          (typeof BOOLEAN_METRIC_KEYS)[number],
          BooleanSummary
        >;
        for (const metric of BOOLEAN_METRIC_KEYS) {
          booleanMetrics[metric] = booleanSummary(
            runs.map((run) => run.metrics[metric].value)
          );
        }
        const runtimeErrorCategories = {} as Record<
          RuntimeErrorCategory,
          RuntimeErrorCategorySummary
        >;
        for (const category of RUNTIME_ERROR_CATEGORIES) {
          runtimeErrorCategories[category] = runtimeCategorySummary(runs, category);
        }

        const hashes = new Set<string>();
        let hashAvailableRuns = 0;
        for (const run of runs) {
          if (run.generated_csharp.length > 0) {
            hashAvailableRuns += 1;
            for (const generated of run.generated_csharp) hashes.add(generated.sha256);
          }
        }

        const commonGroup = {
          suite_id: binding.manifest.suite_id,
          variant_id: variantId,
          scenario_id: scenario.scenario_id,
          temperature_state: temperatureState,
          expected_repetitions: binding.manifest.repetitions_per_state,
          observed_repetitions: runs.length,
          missing_iterations: missingIterations,
          complete:
            runs.length === binding.manifest.repetitions_per_state &&
            missingIterations.length === 0,
          iterations,
          boolean_metrics: booleanMetrics,
          runtime_error_categories: runtimeErrorCategories,
          generated_csharp_sha256: {
            available_run_count: hashAvailableRuns,
            not_available_run_count: runs.length - hashAvailableRuns,
            unique_hashes: [...hashes].sort(),
          },
        };

        groups.push(
          isV2
            ? {
                ...commonGroup,
                measurement_mode: measurementMode,
                numeric_metrics: numericMetrics as Record<NumericMetricKey, NumericSummary>,
              }
            : {
                ...commonGroup,
                numeric_metrics: numericMetrics as Record<V1NumericMetricKey, NumericSummary>,
              }
        );
      }
    }
  }

  const commonAggregate = {
    manifest_sha256: binding.manifest_sha256,
    suite_id: binding.manifest.suite_id,
    model_config_id: binding.manifest.model_config_id,
    runner_config_id: binding.manifest.runner_config_id,
    drawing_fixture_id: binding.manifest.drawing_fixture_id,
    expected_repetitions_per_state: binding.manifest.repetitions_per_state,
    p95_definition: P95_DEFINITION as typeof P95_DEFINITION,
  };

  return isV2
    ? {
        schema_version: AGGREGATE_SCHEMA_VERSION,
        ...commonAggregate,
        measurement_mode: measurementMode,
        groups: groups as AggregateGroup[],
      }
    : {
        schema_version: AGGREGATE_SCHEMA_VERSION_V1,
        ...commonAggregate,
        groups: groups as AggregateGroupV1[],
      };
}

export function parseRunJsonLines(input: string): AnyBenchmarkRunRecord[] {
  const records: AnyBenchmarkRunRecord[] = [];
  for (const [index, line] of input.split(/\r?\n/).entries()) {
    if (line.trim().length === 0) continue;
    let parsed: unknown;
    try {
      parsed = JSON.parse(line);
    } catch {
      throw new Error(`Invalid JSON on benchmark run line ${index + 1}`);
    }
    assertBenchmarkRunRecord(parsed);
    records.push(parsed);
  }
  if (records.length === 0) throw new Error("Benchmark run stream is empty");
  return records;
}

export function aggregateRunJsonLines(
  input: string,
  binding: BenchmarkManifestBinding
): AnyBenchmarkAggregate {
  return aggregateBenchmarkRuns(parseRunJsonLines(input), binding);
}
