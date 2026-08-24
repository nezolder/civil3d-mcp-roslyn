import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import {
  StdioClientTransport,
  getDefaultEnvironment,
} from "@modelcontextprotocol/sdk/client/stdio.js";
import { randomBytes } from "node:crypto";
import { appendFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { aggregateRunJsonLines } from "./aggregate.js";
import { bindBenchmarkManifest, summarizeBenchmarkManifest } from "./manifest.js";
import {
  EXPECTED_DRAWING_ENV,
  ReadOnlyLiveProbeError,
  assertReadOnlyOutputTargetIsSafe,
  type ReadOnlyConnectionSettings,
  type ReadOnlyProbeClient,
  type ReadOnlyProbeToolResult,
  parseReadOnlyConnectionSettings,
  resolveReadOnlyOutputPath,
  runReadOnlyLiveBenchmark,
} from "./readOnlyLiveRunner.js";
import { recordEventJsonLines } from "./recorder.js";
import type { TemperatureState } from "./schema.js";
import { captureToolsListFromBuiltServer } from "./toolsList.js";

interface CliOptions {
  input?: string;
  output?: string;
  server?: string;
  manifest?: string;
  scenarioId?: string;
  variantId?: string;
  iteration?: number;
  temperatureState?: TemperatureState;
  append: boolean;
}

const USAGE = `Civil 3D MCP benchmark harness

Commands:
  tools-list [--server build/index.js] [--output benchmark-output/tools-list.json]
  record --manifest benchmark/scenarios.v1.json [--input events.jsonl|-] [--output benchmark-output/runs.jsonl] [--append]
  aggregate --manifest benchmark/scenarios.v1.json --input benchmark-output/runs.jsonl [--output benchmark-output/aggregate.json]
  validate-manifest [--input benchmark/scenarios.v1.json] [--output FILE]
  live-readonly --manifest benchmark/scenarios.v1.json --scenario-id query.bounded-summary.synthetic.v1 --variant-id ID --iteration N --temperature-state cold|warm [--server build/index.js] [--output FILE] [--append]
`;

function parseOptions(args: readonly string[]): CliOptions {
  const options: CliOptions = { append: false };
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === "--append") {
      options.append = true;
      continue;
    }
    if (
      arg === "--input" ||
      arg === "--output" ||
      arg === "--server" ||
      arg === "--manifest" ||
      arg === "--scenario-id" ||
      arg === "--variant-id" ||
      arg === "--iteration" ||
      arg === "--temperature-state"
    ) {
      const value = args[index + 1];
      if (!value || value.startsWith("--")) throw new Error(`${arg} requires a value`);
      index += 1;
      if (arg === "--input") options.input = value;
      if (arg === "--output") options.output = value;
      if (arg === "--server") options.server = value;
      if (arg === "--manifest") options.manifest = value;
      if (arg === "--scenario-id") options.scenarioId = value;
      if (arg === "--variant-id") options.variantId = value;
      if (arg === "--iteration") {
        const iteration = Number(value);
        if (!Number.isSafeInteger(iteration)) throw new Error("--iteration must be an integer");
        options.iteration = iteration;
      }
      if (arg === "--temperature-state") {
        if (value !== "cold" && value !== "warm") {
          throw new Error("--temperature-state must be cold or warm");
        }
        options.temperatureState = value;
      }
      continue;
    }
    throw new Error(`Unknown option: ${arg}`);
  }
  return options;
}

function hasLiveOptions(options: CliOptions): boolean {
  return (
    options.scenarioId !== undefined ||
    options.variantId !== undefined ||
    options.iteration !== undefined ||
    options.temperatureState !== undefined
  );
}

async function connectReadOnlyProbeClient(
  serverPath: string,
  settings: ReadOnlyConnectionSettings
): Promise<ReadOnlyProbeClient> {
  const childEnvironment = getDefaultEnvironment();
  childEnvironment.CIVIL3D_HOST = settings.host;
  childEnvironment.CIVIL3D_PORT = String(settings.port);
  childEnvironment.CIVIL3D_CONNECT_TIMEOUT = String(settings.connectTimeoutMs);
  childEnvironment.CIVIL3D_COMMAND_TIMEOUT = String(settings.commandTimeoutMs);
  childEnvironment.LOG_LEVEL = "error";

  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [serverPath],
    cwd: process.cwd(),
    env: childEnvironment,
    stderr: "ignore",
  });
  const client = new Client({ name: "civil3d-mcp-readonly-live-runner", version: "1.0.0" });
  try {
    await client.connect(transport);
  } catch (error) {
    await client.close().catch(() => undefined);
    throw error;
  }
  return {
    listTools: () => client.listTools(),
    callTool: async (params) => {
      const result = await client.callTool(
        params,
        undefined,
        params.name === "civil3d_query"
          ? { timeout: settings.sdkRequestTimeoutMs }
          : undefined
      );
      if (!("content" in result) || !Array.isArray(result.content)) {
        throw new ReadOnlyLiveProbeError("PROBE.QUERY_FAILED");
      }
      return result as ReadOnlyProbeToolResult;
    },
    close: () => client.close(),
  };
}

async function readStdin(): Promise<string> {
  process.stdin.setEncoding("utf8");
  let input = "";
  for await (const chunk of process.stdin) input += chunk;
  return input;
}

async function readInput(inputPath: string): Promise<string> {
  return inputPath === "-" ? readStdin() : readFile(inputPath, "utf8");
}

async function emit(text: string, outputPath: string | undefined, append: boolean): Promise<void> {
  const withNewline = text.endsWith("\n") ? text : `${text}\n`;
  if (!outputPath || outputPath === "-") {
    process.stdout.write(withNewline);
    return;
  }
  await mkdir(path.dirname(path.resolve(outputPath)), { recursive: true });
  if (append) await appendFile(outputPath, withNewline, "utf8");
  else await writeFile(outputPath, withNewline, "utf8");
}

async function emitLiveRecord(
  text: string,
  outputPath: string | undefined,
  append: boolean
): Promise<void> {
  const withNewline = text.endsWith("\n") ? text : `${text}\n`;
  if (!outputPath) {
    process.stdout.write(withNewline);
    return;
  }
  await assertReadOnlyOutputTargetIsSafe(outputPath, append);
  await mkdir(path.dirname(outputPath), { recursive: true });
  if (append) await appendFile(outputPath, withNewline, "utf8");
  else await writeFile(outputPath, withNewline, { encoding: "utf8", flag: "wx" });
}

async function main(): Promise<void> {
  const [command, ...optionArgs] = process.argv.slice(2);
  if (!command || command === "help" || command === "--help") {
    process.stdout.write(USAGE);
    return;
  }
  const options = parseOptions(optionArgs);

  switch (command) {
    case "tools-list": {
      if (options.input || options.manifest || options.append || hasLiveOptions(options)) {
        throw new Error("tools-list does not accept input/manifest/append");
      }
      const snapshot = await captureToolsListFromBuiltServer(
        options.server ? path.resolve(options.server) : undefined
      );
      await emit(JSON.stringify(snapshot, null, 2), options.output, false);
      return;
    }

    case "record": {
      if (options.server || hasLiveOptions(options)) {
        throw new Error("record does not accept server/live-runner options");
      }
      if (!options.manifest || options.manifest === "-") {
        throw new Error("record requires --manifest with a JSON file path");
      }
      const binding = bindBenchmarkManifest(await readInput(options.manifest));
      const input = await readInput(options.input ?? "-");
      const record = recordEventJsonLines(input, binding);
      await emit(JSON.stringify(record), options.output, options.append);
      return;
    }

    case "aggregate": {
      if (!options.input || options.input === "-") {
        throw new Error("aggregate requires --input with a JSONL file path");
      }
      if (!options.manifest || options.manifest === "-") {
        throw new Error("aggregate requires --manifest with a JSON file path");
      }
      if (options.server || options.append || hasLiveOptions(options)) {
        throw new Error("aggregate does not accept server/append/live-runner options");
      }
      const binding = bindBenchmarkManifest(await readInput(options.manifest));
      const aggregate = aggregateRunJsonLines(await readInput(options.input), binding);
      await emit(JSON.stringify(aggregate, null, 2), options.output, false);
      return;
    }

    case "validate-manifest": {
      if (options.server || options.manifest || options.append || hasLiveOptions(options)) {
        throw new Error("validate-manifest does not accept server/manifest/append");
      }
      const inputPath = options.input ?? path.resolve("benchmark", "scenarios.v1.json");
      const summary = summarizeBenchmarkManifest(await readInput(inputPath));
      await emit(JSON.stringify(summary, null, 2), options.output, false);
      return;
    }

    case "live-readonly": {
      if (options.input) throw new Error("live-readonly does not accept --input");
      if (!options.manifest || options.manifest === "-") {
        throw new Error("live-readonly requires --manifest with a JSON file path");
      }
      if (
        !options.scenarioId ||
        !options.variantId ||
        options.iteration === undefined ||
        options.temperatureState === undefined
      ) {
        throw new Error(
          "live-readonly requires scenario-id, variant-id, iteration, and temperature-state"
        );
      }
      if (options.append && !options.output) {
        throw new Error("live-readonly --append requires --output");
      }
      const expectedDrawingPath = process.env[EXPECTED_DRAWING_ENV];
      if (!expectedDrawingPath) {
        throw new ReadOnlyLiveProbeError("PROBE.INVALID_INPUT");
      }
      const connectionSettings = parseReadOnlyConnectionSettings(process.env);
      const liveOutputPath = resolveReadOnlyOutputPath(
        options.output,
        options.append,
        process.cwd()
      );
      await assertReadOnlyOutputTargetIsSafe(liveOutputPath, options.append);
      const binding = bindBenchmarkManifest(await readInput(options.manifest));
      const record = await runReadOnlyLiveBenchmark(
        {
          binding,
          serverPath: options.server ? path.resolve(options.server) : path.resolve("build", "index.js"),
          scenarioId: options.scenarioId,
          variantId: options.variantId,
          iteration: options.iteration,
          temperatureState: options.temperatureState,
          expectedDrawingPath,
        },
        {
          connect: (serverPath) =>
            connectReadOnlyProbeClient(serverPath, connectionSettings),
          createRunId: () => `run-${randomBytes(16).toString("hex")}`,
        }
      );
      await emitLiveRecord(JSON.stringify(record), liveOutputPath, options.append);
      return;
    }

    default:
      throw new Error(`Unknown command: ${command}\n${USAGE}`);
  }
}

main().catch((error) => {
  const message =
    error instanceof ReadOnlyLiveProbeError
      ? error.code
      : error instanceof Error
        ? error.message
        : String(error);
  process.stderr.write(`Benchmark CLI error: ${message}\n`);
  process.exitCode = 1;
});
