import * as fs from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { ApplicationClientConnection, cancelledBeforeSend } from "./SocketClient.js";
import { createLogger } from "./logger.js";
import { Civil3dMcpError } from "../errors/structuredError.js";
import { BuildInfo, SERVER_BUILD, buildsDiffer } from "../buildInfo.js";

const log = createLogger("ConnectionManager");

// The plugin listens on IPv4 loopback only. "localhost" can resolve to ::1
// first on Windows and delay every connection before falling back.
const DEFAULT_HOST = "127.0.0.1";
const DEFAULT_PORT = 8080;
const DEFAULT_CONNECT_TIMEOUT_MS = 5_000;
const DEFAULT_DISCOVERY_TIMEOUT_MS = 5_000;
const ENDPOINT_SCHEMA_VERSION = 1;
const MAX_ENDPOINT_RECORDS = 64;
const MAX_ENDPOINT_RECORD_BYTES = 4_096;
const INSTANCE_ID_PATTERN = /^[0-9a-f]{32}$/i;
// A refused loopback connection means nothing listens on that port. Windows
// takes about a second to report each refusal, so remember it for a while.
const REFUSED_ENDPOINT_TTL_MS = 30_000;
const MAX_REFUSED_ENDPOINTS = 256;
const refusedEndpoints = new Map<string, number>();

export interface ExpectedDrawingTarget {
  databaseFilename: string;
  fingerprintGuid: string;
}

export interface ApplicationConnectionTarget {
  expectedDrawing?: ExpectedDrawingTarget;
  instanceId?: string;
  /** Aborted when the MCP client cancels the tool call. */
  signal?: AbortSignal;
  /**
   * Plugin abilities the call depends on (see the plugin's health reply). An
   * older plugin would silently ignore an input it does not know, so the call
   * is refused before it is sent when one is missing.
   */
  requiredFeatures?: string[];
}

interface ConnectionSettings {
  host: string;
  port: number;
  explicitPort: boolean;
  /**
   * Whether the default port is also tried when no endpoint record covers it.
   * On for plugins from before the endpoint records; tests switch it off so
   * that they can never reach a Civil 3D that happens to run on the machine.
   */
  defaultPortFallback: boolean;
  connectTimeoutMs: number;
  discoveryTimeoutMs: number;
  endpointDirectory: string;
}

interface EndpointRecord {
  schemaVersion: number;
  instanceId: string;
  processId: number;
  port: number;
  startedAtUtc: string;
}

/** What the plugin's health reply says beyond "alive". Older plugins leave most of it empty. */
interface EndpointHealth {
  /** True when the plugin has looked at its active drawing; false for plugins that cannot say. */
  activeDrawingKnown: boolean;
  /** The remembered active drawing, or null when none is open (or unknown). */
  activeDrawing: { databaseFilename: string; fingerprintGuid: string; observedAtUtc: string | null } | null;
  build: BuildInfo | null;
  features: string[];
  operationInProgress: boolean;
  queueDepth: number | null;
  executionBackend: string | null;
}

interface LiveEndpoint {
  host: string;
  port: number;
  instanceId: string;
  processId: number | null;
  startedAtUtc: string | null;
  legacy: boolean;
  /** Present once the endpoint answered a health probe. */
  health?: EndpointHealth;
}

interface DrawingIdentity {
  instanceId: string;
  databaseFilename: string;
  fingerprintGuid: string;
}

interface EndpointWithIdentity {
  endpoint: LiveEndpoint;
  identity: DrawingIdentity | null;
  /** The plugin reported that no drawing is open, as opposed to not answering. */
  noDrawing?: boolean;
}

/** One running Civil 3D with the plugin, as shown by the civil3d_skills status action. */
export interface InstanceStatus {
  instanceId: string;
  processId: number | null;
  port: number;
  startedAtUtc: string | null;
  pluginBuild: BuildInfo | null;
  /** Null when either side does not know its commit. */
  buildMatchesServer: boolean | null;
  activeDrawingKnown: boolean;
  activeDrawing: EndpointHealth["activeDrawing"];
  operationInProgress: boolean;
  queueDepth: number | null;
  executionBackend: string | null;
  features: string[];
}

/**
 * Opens a short-lived connection to the selected Civil 3D plugin instance,
 * runs one operation, and tears the connection down afterwards.
 */
export async function withApplicationConnection<T>(
  operation: (client: ApplicationClientConnection) => Promise<T>,
  target: ApplicationConnectionTarget = {}
): Promise<T> {
  const settings = getConnectionSettings();
  if (target.signal?.aborted) throw cancelledBeforeSend();
  const endpoint = await selectEndpoint(settings, target);
  if (target.requiredFeatures?.length) {
    await requireFeatures(endpoint, target.requiredFeatures, settings);
  }
  if (target.signal?.aborted) throw cancelledBeforeSend();
  return await withDirectConnection(endpoint, operation, settings.connectTimeoutMs);
}

/**
 * The running Civil 3D instances and what each says about itself. Uses the
 * health reply only, so it runs no command in any instance.
 */
export async function listInstances(): Promise<InstanceStatus[]> {
  const settings = getConnectionSettings();
  let endpoints: LiveEndpoint[];
  if (settings.explicitPort || !isLocalHost(settings.host)) {
    const live = await probeEndpoint(fixedEndpoint(settings.host, settings.port), settings);
    endpoints = live ? [live] : [];
  } else {
    endpoints = await discoverLiveEndpoints(settings);
  }
  return endpoints.map((endpoint) => {
    const health = endpoint.health;
    const pluginBuild = health?.build ?? null;
    return {
      instanceId: endpoint.instanceId,
      processId: endpoint.processId,
      port: endpoint.port,
      startedAtUtc: endpoint.startedAtUtc,
      pluginBuild,
      buildMatchesServer: pluginBuild ? matchesServerBuild(pluginBuild) : null,
      activeDrawingKnown: health?.activeDrawingKnown ?? false,
      activeDrawing: health?.activeDrawing ?? null,
      operationInProgress: health?.operationInProgress ?? false,
      queueDepth: health?.queueDepth ?? null,
      executionBackend: health?.executionBackend ?? null,
      features: health?.features ?? [],
    };
  });
}

function matchesServerBuild(pluginBuild: BuildInfo): boolean | null {
  const differ = buildsDiffer(SERVER_BUILD, pluginBuild);
  return differ === null ? null : !differ;
}

async function requireFeatures(
  endpoint: LiveEndpoint,
  features: string[],
  settings: ConnectionSettings
): Promise<void> {
  // A fixed port is used without a probe, so ask now.
  const health = endpoint.health ?? (await probeEndpoint(endpoint, settings))?.health;
  const missing = features.filter((feature) => !health?.features.includes(feature));
  if (missing.length === 0) return;
  throw new Civil3dMcpError(
    "CIVIL3D.PLUGIN_UPDATE_REQUIRED",
    `The Civil 3D plugin in the selected instance does not support: ${missing.join(", ")}. ` +
      "It would ignore the matching inputs, so nothing was sent. Update the plugin in Civil 3D " +
      "(see the status action of civil3d_skills for the versions), or repeat the call without those inputs.",
    "validation",
    "node",
    "not_started"
  );
}

function getConnectionSettings(): ConnectionSettings {
  const explicitPortValue = process.env.CIVIL3D_PORT;
  const localApplicationData =
    process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
  return {
    host: process.env.CIVIL3D_HOST ?? DEFAULT_HOST,
    port: parseIntegerEnvironment("CIVIL3D_PORT", explicitPortValue, DEFAULT_PORT, 1, 65_535),
    explicitPort: explicitPortValue !== undefined,
    defaultPortFallback: parseSwitchEnvironment(
      "CIVIL3D_MCP_DEFAULT_PORT_FALLBACK",
      process.env.CIVIL3D_MCP_DEFAULT_PORT_FALLBACK,
      true
    ),
    connectTimeoutMs: parseIntegerEnvironment(
      "CIVIL3D_CONNECT_TIMEOUT",
      process.env.CIVIL3D_CONNECT_TIMEOUT,
      DEFAULT_CONNECT_TIMEOUT_MS,
      1,
      600_000
    ),
    discoveryTimeoutMs: parseIntegerEnvironment(
      "CIVIL3D_DISCOVERY_TIMEOUT",
      process.env.CIVIL3D_DISCOVERY_TIMEOUT,
      DEFAULT_DISCOVERY_TIMEOUT_MS,
      1,
      600_000
    ),
    endpointDirectory:
      process.env.CIVIL3D_MCP_ENDPOINT_DIR ??
      path.join(localApplicationData, "Civil3dMcpRoslyn", "endpoints"),
  };
}

function parseIntegerEnvironment(
  name: string,
  value: string | undefined,
  fallback: number,
  minimum: number,
  maximum: number
): number {
  if (value === undefined) return fallback;
  if (!/^\d+$/.test(value)) throw invalidConfiguration(name, minimum, maximum);
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < minimum || parsed > maximum) {
    throw invalidConfiguration(name, minimum, maximum);
  }
  return parsed;
}

function parseSwitchEnvironment(name: string, value: string | undefined, fallback: boolean): boolean {
  if (value === undefined) return fallback;
  if (value === "1") return true;
  if (value === "0") return false;
  throw new Civil3dMcpError(
    "CIVIL3D.INVALID_INPUT",
    `${name} must be 0 or 1.`,
    "validation",
    "node",
    "not_started"
  );
}

function invalidConfiguration(name: string, minimum: number, maximum: number) {
  return new Civil3dMcpError(
    "CIVIL3D.INVALID_INPUT",
    `${name} must be an integer between ${minimum} and ${maximum}.`,
    "validation",
    "node",
    "not_started"
  );
}

async function selectEndpoint(
  settings: ConnectionSettings,
  target: ApplicationConnectionTarget
): Promise<LiveEndpoint> {
  if (settings.explicitPort || !isLocalHost(settings.host)) {
    const directEndpoint = fixedEndpoint(settings.host, settings.port);
    if (target.instanceId) {
      const live = await probeEndpoint(directEndpoint, settings);
      if (!live || live.instanceId !== target.instanceId) {
        throw instanceNotFound(target.instanceId, live ? [live] : []);
      }
      return live;
    }
    return directEndpoint;
  }

  const endpoints = await discoverLiveEndpoints(settings, target.instanceId);
  if (endpoints.length === 0) {
    if (target.instanceId) throw instanceNotFound(target.instanceId, []);
    if (!settings.defaultPortFallback) throw noInstanceFound(settings);
    // Backward-compatible final attempt for a pre-registry plugin.
    return fixedEndpoint(settings.host, settings.port);
  }

  if (target.instanceId) {
    const endpoint = endpoints.find((candidate) => candidate.instanceId === target.instanceId);
    if (!endpoint) throw instanceNotFound(target.instanceId, endpoints);
    // The plugin's DrawingGuard still validates expectedDrawing immediately
    // before Civil API access. No extra identity round trip is needed once the
    // caller selected an exact live plugin session.
    return endpoint;
  }

  // Preserve the ordinary one-Civil workflow: the final plugin guard remains
  // authoritative and no routing-only drawing query is added.
  if (endpoints.length === 1) return endpoints[0];

  // The health reply of each instance already says which drawing is active
  // there, so choosing between instances normally runs no command in any of
  // them. Only a plugin too old to say is still asked with a command.
  if (target.expectedDrawing) {
    const candidates = await readDrawingIdentities(endpoints, settings);
    const matches = candidates.filter(
      ({ identity }) => identity && drawingIdentityMatches(identity, target.expectedDrawing!)
    );
    if (matches.length === 1) return matches[0].endpoint;
    if (matches.length === 0) throw drawingMismatch(target.expectedDrawing, candidates);
    throw instanceSelectionRequired(matches);
  }

  throw instanceSelectionRequired(await readDrawingIdentities(endpoints, settings));
}

async function discoverLiveEndpoints(
  settings: ConnectionSettings,
  wantedInstanceId?: string
): Promise<LiveEndpoint[]> {
  const records = await readEndpointRecords(settings.endpointDirectory);
  if (records.length === 0) return [];

  // Records left behind by a crashed or killed Civil 3D are never removed by
  // that process. Skip the ones that cannot be live instead of paying for a
  // refused connection on every call. The instance the caller asked for is
  // always probed.
  const likely: EndpointRecord[] = [];
  const skipped: EndpointRecord[] = [];
  for (const record of records) {
    const skip =
      record.instanceId !== wantedInstanceId &&
      (!isProcessAlive(record.processId) || wasRecentlyRefused(settings.host, record.port, record.instanceId));
    (skip ? skipped : likely).push(record);
  }

  const registered = await probeRecords(likely, settings);

  if (
    settings.defaultPortFallback &&
    !registered.some((endpoint) => endpoint.port === settings.port) &&
    !wasRecentlyRefused(settings.host, settings.port, null)
  ) {
    const fixed = await probeEndpoint(fixedEndpoint(settings.host, settings.port), settings, null);
    if (fixed && !registered.some((endpoint) => endpoint.instanceId === fixed.instanceId)) {
      registered.push(fixed);
    }
  }

  // The shortcut must never hide a session: with nothing live, probe the rest.
  if (registered.length === 0 && skipped.length > 0) {
    registered.push(...(await probeRecords(skipped, settings)));
  }

  return registered.sort((left, right) => left.instanceId.localeCompare(right.instanceId));
}

async function probeRecords(
  records: EndpointRecord[],
  settings: ConnectionSettings
): Promise<LiveEndpoint[]> {
  return (
    await Promise.all(
      records.map((record) =>
        probeEndpoint(
          {
            host: settings.host,
            port: record.port,
            instanceId: record.instanceId,
            processId: record.processId,
            startedAtUtc: record.startedAtUtc,
            legacy: false,
          },
          settings,
          record.instanceId
        )
      )
    )
  ).filter((endpoint): endpoint is LiveEndpoint => endpoint !== null);
}

/** Signal 0 only tests for existence; a process we may not signal still exists. */
function isProcessAlive(processId: number): boolean {
  try {
    process.kill(processId, 0);
    return true;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code !== "ESRCH";
  }
}

function refusedKey(host: string, port: number, instanceId: string | null): string {
  return `${host}|${port}|${instanceId ?? ""}`;
}

function wasRecentlyRefused(host: string, port: number, instanceId: string | null): boolean {
  const key = refusedKey(host, port, instanceId);
  const expiresAt = refusedEndpoints.get(key);
  if (expiresAt === undefined) return false;
  if (expiresAt > Date.now()) return true;
  refusedEndpoints.delete(key);
  return false;
}

function rememberRefused(host: string, port: number, instanceId: string | null): void {
  if (refusedEndpoints.size >= MAX_REFUSED_ENDPOINTS) refusedEndpoints.clear();
  refusedEndpoints.set(refusedKey(host, port, instanceId), Date.now() + REFUSED_ENDPOINT_TTL_MS);
}

async function readEndpointRecords(directory: string): Promise<EndpointRecord[]> {
  let names: string[];
  try {
    names = (await fs.readdir(directory))
      .filter((name) => name.endsWith(".json"))
      .sort()
      .reverse()
      .slice(0, MAX_ENDPOINT_RECORDS);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return [];
    log.warn("Could not read Civil 3D endpoint registry", { directory });
    return [];
  }

  const records: EndpointRecord[] = [];
  for (const name of names) {
    try {
      const contents = await fs.readFile(path.join(directory, name));
      if (contents.length > MAX_ENDPOINT_RECORD_BYTES) continue;
      const candidate = JSON.parse(contents.toString("utf8"));
      if (isEndpointRecord(candidate)) records.push(candidate);
    } catch {
      // A partial, invalid, or stale local record is ignored and never trusted.
    }
  }
  return records;
}

function isEndpointRecord(value: unknown): value is EndpointRecord {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const record = value as Record<string, unknown>;
  return (
    record.schemaVersion === ENDPOINT_SCHEMA_VERSION &&
    typeof record.instanceId === "string" &&
    INSTANCE_ID_PATTERN.test(record.instanceId) &&
    Number.isInteger(record.processId) &&
    (record.processId as number) > 0 &&
    Number.isInteger(record.port) &&
    (record.port as number) >= 1 &&
    (record.port as number) <= 65_535 &&
    typeof record.startedAtUtc === "string"
  );
}

/**
 * Returns the endpoint when its health reply matches the record. A string or
 * null refusalInstanceId (null for the fixed port) also remembers a refused
 * connection so that the next calls skip this endpoint for a while.
 */
async function probeEndpoint(
  endpoint: LiveEndpoint,
  settings: ConnectionSettings,
  refusalInstanceId?: string | null
): Promise<LiveEndpoint | null> {
  try {
    const health = await withDirectConnection(
      endpoint,
      (client) => client.sendCommand(
        "getCivil3DHealth",
        {},
        undefined,
        settings.discoveryTimeoutMs
      ),
      settings.connectTimeoutMs
    );
    if (typeof health !== "object" || health === null || Array.isArray(health)) return null;
    const payload = health as Record<string, unknown>;
    if (payload.connected !== true) return null;

    const healthInstanceId =
      typeof payload.instanceId === "string" && INSTANCE_ID_PATTERN.test(payload.instanceId)
        ? payload.instanceId
        : undefined;
    const healthProcessId =
      Number.isInteger(payload.processId) && (payload.processId as number) > 0
        ? (payload.processId as number)
        : null;
    const healthPort =
      Number.isInteger(payload.port) && (payload.port as number) >= 1
        ? (payload.port as number)
        : endpoint.port;

    const healthDetails = readEndpointHealth(payload);
    if (!endpoint.legacy) {
      if (
        healthInstanceId !== endpoint.instanceId ||
        healthProcessId !== endpoint.processId ||
        healthPort !== endpoint.port
      ) {
        return null;
      }
      warnOnBuildMismatch(endpoint.instanceId, healthDetails.build);
      return { ...endpoint, health: healthDetails };
    }

    warnOnBuildMismatch(healthInstanceId ?? endpoint.instanceId, healthDetails.build);
    return {
      ...endpoint,
      health: healthDetails,
      instanceId: healthInstanceId ?? endpoint.instanceId,
      processId: healthProcessId,
      port: healthPort,
      startedAtUtc:
        typeof payload.startedAtUtc === "string" ? payload.startedAtUtc : null,
      legacy: healthInstanceId === undefined,
    };
  } catch (error) {
    if (
      refusalInstanceId !== undefined &&
      error instanceof Civil3dMcpError &&
      error.code === "CIVIL3D.CONNECTION_FAILED"
    ) {
      rememberRefused(endpoint.host, endpoint.port, refusalInstanceId);
    }
    return null;
  }
}

function readEndpointHealth(payload: Record<string, unknown>): EndpointHealth {
  const drawing = payload.activeDrawing;
  const activeDrawing =
    typeof drawing === "object" &&
    drawing !== null &&
    !Array.isArray(drawing) &&
    typeof (drawing as Record<string, unknown>).databaseFilename === "string" &&
    typeof (drawing as Record<string, unknown>).fingerprintGuid === "string"
      ? {
          databaseFilename: (drawing as Record<string, unknown>).databaseFilename as string,
          fingerprintGuid: (drawing as Record<string, unknown>).fingerprintGuid as string,
          observedAtUtc:
            typeof (drawing as Record<string, unknown>).observedAtUtc === "string"
              ? ((drawing as Record<string, unknown>).observedAtUtc as string)
              : null,
        }
      : null;
  const build = payload.build;
  const buildRecord =
    typeof build === "object" && build !== null && !Array.isArray(build)
      ? (build as Record<string, unknown>)
      : null;
  return {
    activeDrawingKnown: payload.activeDrawingKnown === true,
    activeDrawing: payload.activeDrawingKnown === true ? activeDrawing : null,
    build:
      buildRecord && typeof buildRecord.commit === "string"
        ? {
            commit: buildRecord.commit,
            dirty: typeof buildRecord.dirty === "boolean" ? buildRecord.dirty : null,
            builtAtUtc: typeof buildRecord.builtAtUtc === "string" ? buildRecord.builtAtUtc : null,
          }
        : null,
    features: Array.isArray(payload.features)
      ? payload.features.filter((feature): feature is string => typeof feature === "string")
      : [],
    operationInProgress: payload.operationInProgress === true,
    queueDepth: Number.isInteger(payload.queueDepth) ? (payload.queueDepth as number) : null,
    executionBackend: typeof payload.executionBackend === "string" ? payload.executionBackend : null,
  };
}

const warnedBuildMismatches = new Set<string>();

/** Said once per Civil 3D session; the status action shows the details at any time. */
function warnOnBuildMismatch(instanceId: string, pluginBuild: BuildInfo | null): void {
  if (!pluginBuild || buildsDiffer(SERVER_BUILD, pluginBuild) !== true) return;
  if (warnedBuildMismatches.has(instanceId)) return;
  if (warnedBuildMismatches.size >= 256) warnedBuildMismatches.clear();
  warnedBuildMismatches.add(instanceId);
  log.warn("The Civil 3D plugin and the MCP server were built from different commits", {
    instanceId,
    pluginCommit: pluginBuild.commit,
    serverCommit: SERVER_BUILD.commit,
  });
}

async function readDrawingIdentities(
  endpoints: LiveEndpoint[],
  settings: ConnectionSettings
): Promise<EndpointWithIdentity[]> {
  return await Promise.all(
    endpoints.map(async (endpoint) => {
      const health = endpoint.health;
      if (health?.activeDrawingKnown) {
        return health.activeDrawing
          ? {
              endpoint,
              identity: {
                instanceId: endpoint.instanceId,
                databaseFilename: health.activeDrawing.databaseFilename,
                fingerprintGuid: health.activeDrawing.fingerprintGuid,
              },
            }
          : { endpoint, identity: null, noDrawing: true };
      }
      // An older plugin, or one that has not been idle yet since it started.
      return { endpoint, identity: await readDrawingIdentity(endpoint, settings) };
    })
  );
}

async function readDrawingIdentity(
  endpoint: LiveEndpoint,
  settings: ConnectionSettings
): Promise<DrawingIdentity | null> {
  try {
    const result = await withDirectConnection(
      endpoint,
      (client) => client.sendCommand(
        "getActiveDrawingIdentity",
        {},
        undefined,
        settings.discoveryTimeoutMs
      ),
      settings.connectTimeoutMs
    );
    if (typeof result !== "object" || result === null || Array.isArray(result)) return null;
    const identity = result as Record<string, unknown>;
    if (
      typeof identity.instanceId !== "string" ||
      identity.instanceId !== endpoint.instanceId ||
      typeof identity.databaseFilename !== "string" ||
      typeof identity.fingerprintGuid !== "string"
    ) {
      return null;
    }
    return {
      instanceId: identity.instanceId,
      databaseFilename: identity.databaseFilename,
      fingerprintGuid: identity.fingerprintGuid,
    };
  } catch {
    return null;
  }
}

function drawingIdentityMatches(
  actual: DrawingIdentity,
  expected: ExpectedDrawingTarget
): boolean {
  return (
    normalizeDrawingPath(actual.databaseFilename) ===
      normalizeDrawingPath(expected.databaseFilename) &&
    normalizeFingerprint(actual.fingerprintGuid) ===
      normalizeFingerprint(expected.fingerprintGuid)
  );
}

function normalizeDrawingPath(value: string): string {
  if (value.length === 0) return "";
  return path.win32.normalize(value).toLowerCase();
}

function normalizeFingerprint(value: string): string {
  return value.replace(/[^0-9a-f]/gi, "").toLowerCase();
}

function instanceSelectionRequired(candidates: EndpointWithIdentity[]): Civil3dMcpError {
  return new Civil3dMcpError(
    "CIVIL3D.INSTANCE_SELECTION_REQUIRED",
    "More than one Civil 3D instance is available. Retry with the instanceId " +
      "for the intended active drawing:\n" + formatCandidates(candidates),
    "drawing",
    "node",
    "not_started"
  );
}

function instanceNotFound(instanceId: string, endpoints: LiveEndpoint[]): Civil3dMcpError {
  const available = endpoints.length === 0
    ? "none"
    : endpoints.map((endpoint) => endpoint.instanceId).join(", ");
  return new Civil3dMcpError(
    "CIVIL3D.INSTANCE_NOT_FOUND",
    `Civil 3D instance '${instanceId}' is not available. Available instanceIds: ${available}.`,
    "connection",
    "node",
    "not_started"
  );
}

function drawingMismatch(
  expected: ExpectedDrawingTarget,
  candidates: EndpointWithIdentity[]
): Civil3dMcpError {
  return new Civil3dMcpError(
    "CIVIL3D.DRAWING_MISMATCH",
    `No available Civil 3D instance has the expected drawing '${expected.databaseFilename}' ` +
      `and fingerprint '${expected.fingerprintGuid}'. Active candidates:\n${formatCandidates(candidates)}`,
    "drawing",
    "node",
    "not_started"
  );
}

function formatCandidates(candidates: EndpointWithIdentity[]): string {
  return candidates
    .slice(0, 16)
    .map(({ endpoint, identity, noDrawing }) => {
      const process = endpoint.processId === null ? "unknown" : String(endpoint.processId);
      if (!identity) {
        return `- instanceId=${endpoint.instanceId}; processId=${process}; ` +
          (noDrawing ? "no drawing open" : "drawing identity unavailable");
      }
      return (
        `- instanceId=${endpoint.instanceId}; processId=${process}; ` +
        `drawing=${JSON.stringify(identity.databaseFilename)}; ` +
        `fingerprint=${JSON.stringify(identity.fingerprintGuid)}`
      );
    })
    .join("\n");
}

function fixedEndpoint(host: string, port: number): LiveEndpoint {
  return {
    host,
    port,
    instanceId: `legacy-${port}`,
    processId: null,
    startedAtUtc: null,
    legacy: true,
  };
}

function isLocalHost(host: string): boolean {
  const normalized = host.trim().toLowerCase();
  return normalized === "localhost" || normalized === "127.0.0.1" || normalized === "::1";
}

async function withDirectConnection<T>(
  endpoint: Pick<LiveEndpoint, "host" | "port">,
  operation: (client: ApplicationClientConnection) => Promise<T>,
  connectTimeoutMs: number
): Promise<T> {
  const appClient = new ApplicationClientConnection(endpoint.host, endpoint.port);

  try {
    await new Promise<void>((resolve, reject) => {
      let settled = false;
      let timeoutHandle: NodeJS.Timeout | undefined;

      const cleanup = () => {
        if (timeoutHandle) clearTimeout(timeoutHandle);
        appClient.socket.removeListener("connect", onConnect);
        appClient.socket.removeListener("error", onError);
      };
      const finish = (handler: () => void) => {
        if (settled) return;
        settled = true;
        cleanup();
        handler();
      };
      const onConnect = () => finish(resolve);
      const onError = () => finish(() => reject(connectionFailed(endpoint)));

      appClient.socket.on("connect", onConnect);
      appClient.socket.on("error", onError);
      timeoutHandle = setTimeout(() => {
        finish(() => {
          appClient.socket.destroy();
          reject(
            new Civil3dMcpError(
              "CIVIL3D.CONNECTION_TIMEOUT",
              `Connection to Civil 3D plugin timed out after ${connectTimeoutMs}ms at ` +
                `${endpoint.host}:${endpoint.port}.`,
              "connection",
              "transport",
              "not_started"
            )
          );
        });
      }, connectTimeoutMs);

      if (!appClient.connect()) onError();
    });

    return await operation(appClient);
  } finally {
    appClient.disconnect();
  }
}

function noInstanceFound(settings: ConnectionSettings): Civil3dMcpError {
  return new Civil3dMcpError(
    "CIVIL3D.CONNECTION_FAILED",
    `No running Civil 3D with the MCP plugin is registered in ${settings.endpointDirectory}, ` +
      "and the default port is not tried because CIVIL3D_MCP_DEFAULT_PORT_FALLBACK is 0.",
    "connection",
    "transport",
    "not_started"
  );
}

function connectionFailed(endpoint: Pick<LiveEndpoint, "host" | "port">): Civil3dMcpError {
  log.error("Connection failed", { host: endpoint.host, port: endpoint.port });
  return new Civil3dMcpError(
    "CIVIL3D.CONNECTION_FAILED",
    `Failed to connect to Civil 3D plugin at ${endpoint.host}:${endpoint.port}. ` +
      "Make sure Civil 3D is running and the MCP plugin is loaded.",
    "connection",
    "transport",
    "not_started"
  );
}
