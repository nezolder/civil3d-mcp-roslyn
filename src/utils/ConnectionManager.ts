import * as fs from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { ApplicationClientConnection } from "./SocketClient.js";
import { createLogger } from "./logger.js";
import { Civil3dMcpError } from "../errors/structuredError.js";

const log = createLogger("ConnectionManager");

const DEFAULT_HOST = "localhost";
const DEFAULT_PORT = 8080;
const DEFAULT_CONNECT_TIMEOUT_MS = 5_000;
const DEFAULT_DISCOVERY_TIMEOUT_MS = 5_000;
const ENDPOINT_SCHEMA_VERSION = 1;
const MAX_ENDPOINT_RECORDS = 64;
const MAX_ENDPOINT_RECORD_BYTES = 4_096;
const INSTANCE_ID_PATTERN = /^[0-9a-f]{32}$/i;

export interface ExpectedDrawingTarget {
  databaseFilename: string;
  fingerprintGuid: string;
}

export interface ApplicationConnectionTarget {
  expectedDrawing?: ExpectedDrawingTarget;
  instanceId?: string;
}

interface ConnectionSettings {
  host: string;
  port: number;
  explicitPort: boolean;
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

interface LiveEndpoint {
  host: string;
  port: number;
  instanceId: string;
  processId: number | null;
  startedAtUtc: string | null;
  legacy: boolean;
}

interface DrawingIdentity {
  instanceId: string;
  databaseFilename: string;
  fingerprintGuid: string;
}

interface EndpointWithIdentity {
  endpoint: LiveEndpoint;
  identity: DrawingIdentity | null;
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
  const endpoint = await selectEndpoint(settings, target);
  return await withDirectConnection(endpoint, operation, settings.connectTimeoutMs);
}

function getConnectionSettings(): ConnectionSettings {
  const explicitPortValue = process.env.CIVIL3D_PORT;
  const localApplicationData =
    process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
  return {
    host: process.env.CIVIL3D_HOST ?? DEFAULT_HOST,
    port: parseIntegerEnvironment("CIVIL3D_PORT", explicitPortValue, DEFAULT_PORT, 1, 65_535),
    explicitPort: explicitPortValue !== undefined,
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

  const endpoints = await discoverLiveEndpoints(settings);
  if (endpoints.length === 0) {
    if (target.instanceId) throw instanceNotFound(target.instanceId, []);
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

async function discoverLiveEndpoints(settings: ConnectionSettings): Promise<LiveEndpoint[]> {
  const records = await readEndpointRecords(settings.endpointDirectory);
  if (records.length === 0) return [];
  const registered = (
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
          settings
        )
      )
    )
  ).filter((endpoint): endpoint is LiveEndpoint => endpoint !== null);

  if (!registered.some((endpoint) => endpoint.port === settings.port)) {
    const fixed = await probeEndpoint(fixedEndpoint(settings.host, settings.port), settings);
    if (fixed && !registered.some((endpoint) => endpoint.instanceId === fixed.instanceId)) {
      registered.push(fixed);
    }
  }

  return registered.sort((left, right) => left.instanceId.localeCompare(right.instanceId));
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

async function probeEndpoint(
  endpoint: LiveEndpoint,
  settings: ConnectionSettings
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

    if (!endpoint.legacy) {
      if (
        healthInstanceId !== endpoint.instanceId ||
        healthProcessId !== endpoint.processId ||
        healthPort !== endpoint.port
      ) {
        return null;
      }
      return endpoint;
    }

    return {
      ...endpoint,
      instanceId: healthInstanceId ?? endpoint.instanceId,
      processId: healthProcessId,
      port: healthPort,
      startedAtUtc:
        typeof payload.startedAtUtc === "string" ? payload.startedAtUtc : null,
      legacy: healthInstanceId === undefined,
    };
  } catch {
    return null;
  }
}

async function readDrawingIdentities(
  endpoints: LiveEndpoint[],
  settings: ConnectionSettings
): Promise<EndpointWithIdentity[]> {
  return await Promise.all(
    endpoints.map(async (endpoint) => ({
      endpoint,
      identity: await readDrawingIdentity(endpoint, settings),
    }))
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
    .map(({ endpoint, identity }) => {
      const process = endpoint.processId === null ? "unknown" : String(endpoint.processId);
      if (!identity) {
        return `- instanceId=${endpoint.instanceId}; processId=${process}; drawing identity unavailable`;
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
