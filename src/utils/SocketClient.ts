import * as net from "net";
import { performance } from "node:perf_hooks";
import { TextDecoder } from "node:util";
import {
  BenchmarkTraceRequest,
  MeasuredCommandResult,
  addInternalMeasurementRequest,
  attachBenchmarkEventToError,
  createMeasurementEventFromResponse,
  createPartialInternalMeasurementEvent,
} from "../benchmark/liveTrace.js";
import { createLogger } from "./logger.js";
import {
  Civil3dMcpError,
  civil3dErrorFromPlugin,
} from "../errors/structuredError.js";

const log = createLogger("SocketClient");
const COMMAND_TIMEOUT_MS = parseInt(process.env.CIVIL3D_COMMAND_TIMEOUT ?? "120000", 10);
const JSON_FRAME_DELIMITER = 0x0a;
const UTF8_DECODER = new TextDecoder("utf-8", { fatal: true });

export const MAX_JSON_BODY_BYTES = 8 * 1024 * 1024;

interface PendingResponse {
  handle: (response: Record<string, unknown>) => void;
  fail: (error: Civil3dMcpError) => void;
}

export class ApplicationClientConnection {
  host: string;
  port: number;
  socket: net.Socket;
  isConnected: boolean = false;
  responseCallbacks: Map<string, PendingResponse> = new Map();
  private responseChunks: Buffer[] = [];
  private responseByteLength = 0;
  private commandSent = false;

  constructor(host: string, port: number) {
    this.host = host;
    this.port = port;
    this.socket = new net.Socket();
    this.setupSocketListeners();
  }

  private setupSocketListeners(): void {
    this.socket.on("connect", () => {
      this.isConnected = true;
      log.info("Connected", { host: this.host, port: this.port });
    });

    this.socket.on("data", (data) => this.processData(data));

    this.socket.on("end", () => this.processLegacyResponseAtEof());

    this.socket.on("close", () => {
      this.isConnected = false;
      if (this.responseCallbacks.size > 0) {
        this.failPendingResponses(
          this.createTransportError(
            "Connection to Civil 3D plugin closed before a complete response was received."
          )
        );
      }
      log.debug("Connection closed");
    });

    this.socket.on("error", (error) => {
      log.error("Connection error", { error: String(error) });
      this.isConnected = false;
      if (this.responseCallbacks.size > 0) {
        this.failPendingResponses(
          this.createTransportError(
            "Connection to Civil 3D plugin failed before a complete response was received."
          )
        );
      }
    });
  }

  /**
   * Accumulate a single LF-delimited UTF-8 JSON response without decoding
   * individual TCP chunks. This keeps byte limits and split multibyte
   * characters exact.
   */
  private processData(data: Buffer): void {
    if (this.responseCallbacks.size === 0) return;

    const delimiterIndex = data.indexOf(JSON_FRAME_DELIMITER);
    const bodyBytesInChunk = delimiterIndex === -1 ? data.length : delimiterIndex;

    if (this.responseByteLength + bodyBytesInChunk > MAX_JSON_BODY_BYTES) {
      this.failPendingResponses(
        this.createTransportError(
          `Civil 3D plugin response exceeded the ${MAX_JSON_BODY_BYTES}-byte UTF-8 JSON body limit.`,
          "CIVIL3D.RESPONSE_TOO_LARGE"
        )
      );
      this.socket.destroy();
      return;
    }

    if (bodyBytesInChunk > 0) {
      this.responseChunks.push(data.subarray(0, bodyBytesInChunk));
      this.responseByteLength += bodyBytesInChunk;
    }

    if (delimiterIndex === -1) return;

    if (delimiterIndex !== data.length - 1) {
      this.failPendingResponses(
        this.createTransportError("Civil 3D plugin returned data after the response frame delimiter.")
      );
      this.socket.destroy();
      return;
    }

    this.completeResponse();
  }

  /** Accept the previous plugin's unframed response only after an orderly EOF. */
  private processLegacyResponseAtEof(): void {
    if (this.responseCallbacks.size === 0) return;

    if (this.responseByteLength === 0) {
      this.failPendingResponses(
        this.createTransportError(
          "Connection to Civil 3D plugin closed before a response was received."
        )
      );
      return;
    }

    this.completeResponse();
  }

  private completeResponse(): void {
    let responseData: string;
    let response: unknown;

    try {
      responseData = UTF8_DECODER.decode(
        Buffer.concat(this.responseChunks, this.responseByteLength)
      );
      response = JSON.parse(responseData);
    } catch {
      this.failPendingResponses(
        this.createTransportError("Civil 3D plugin returned malformed UTF-8 JSON.")
      );
      this.socket.destroy();
      return;
    }

    if (typeof response !== "object" || response === null || Array.isArray(response)) {
      this.failPendingResponses(
        this.createTransportError("Civil 3D plugin response must be a JSON object.")
      );
      this.socket.destroy();
      return;
    }

    this.handleResponse(response as Record<string, unknown>);
  }

  private createTransportError(
    message: string,
    code = "CIVIL3D.TRANSPORT_ERROR"
  ): Civil3dMcpError {
    return new Civil3dMcpError(
      code,
      message,
      "transport",
      "transport",
      "unknown"
    );
  }

  private resetResponseBuffer(): void {
    this.responseChunks = [];
    this.responseByteLength = 0;
  }

  private failPendingResponses(error: Civil3dMcpError): void {
    const pendingResponses = [...this.responseCallbacks.values()];
    this.responseCallbacks.clear();
    this.resetResponseBuffer();
    for (const pending of pendingResponses) pending.fail(error);
  }

  public connect(): boolean {
    if (this.isConnected) {
      return true;
    }

    try {
      this.socket.connect(this.port, this.host);
      return true;
    } catch (error) {
      log.error("Failed to connect", { host: this.host, port: this.port, error: String(error) });
      return false;
    }
  }

  public disconnect(): void {
    this.socket.end();
    this.isConnected = false;
  }

  private generateRequestId(): string {
    return Date.now().toString() + Math.random().toString().substring(2, 8);
  }

  private handleResponse(response: Record<string, unknown>): void {
    const requestId = typeof response.id === "string" ? response.id : undefined;
    const callback = requestId ? this.responseCallbacks.get(requestId) : undefined;

    if (requestId === undefined || !callback) {
      this.failPendingResponses(
        this.createTransportError("Civil 3D plugin response has a missing or unexpected request id.")
      );
      this.socket.destroy();
      return;
    }

    this.responseCallbacks.delete(requestId);
    this.resetResponseBuffer();
    callback.handle(response);
  }

  /**
   * Send a JSON-RPC command to the Civil 3D plugin and wait for a response.
   */
  public sendCommand(
    command: string,
    params: Record<string, unknown> = {},
    benchmarkTrace?: BenchmarkTraceRequest
  ): Promise<any | MeasuredCommandResult> {
    return new Promise((resolve, reject) => {
      const fallbackStartedAt = benchmarkTrace ? performance.now() : undefined;
      const forwardedCode =
        benchmarkTrace && typeof params.code === "string" ? params.code : undefined;
      let benchmarkStartedAt: number | undefined;
      let timeoutHandle: NodeJS.Timeout | undefined;
      let pendingRequestId: string | undefined;

      const elapsedBenchmarkMs = () =>
        performance.now() - (benchmarkStartedAt ?? fallbackStartedAt ?? performance.now());
      const attachPartial = (
        error: Error,
        reason: "transport_timeout" | "transport_error"
      ) => {
        if (benchmarkTrace && forwardedCode !== undefined) {
          attachBenchmarkEventToError(
            error,
            createPartialInternalMeasurementEvent(
              benchmarkTrace,
              forwardedCode,
              elapsedBenchmarkMs(),
              reason
            )
          );
        }
        return error;
      };

      try {
        const requestId = this.generateRequestId();
        pendingRequestId = requestId;
        const commandParams = benchmarkTrace
          ? addInternalMeasurementRequest(params, benchmarkTrace)
          : params;
        const commandObj = {
          jsonrpc: "2.0",
          method: command,
          params: commandParams,
          id: requestId,
        };

        const commandString = JSON.stringify(commandObj);
        const commandByteLength = Buffer.byteLength(commandString, "utf8");
        if (commandByteLength > MAX_JSON_BODY_BYTES) {
          throw new Civil3dMcpError(
            "CIVIL3D.REQUEST_TOO_LARGE",
            `Civil 3D request exceeded the ${MAX_JSON_BODY_BYTES}-byte UTF-8 JSON body limit.`,
            "transport",
            "node",
            "not_started"
          );
        }

        if (this.commandSent || this.responseCallbacks.size > 0) {
          throw new Civil3dMcpError(
            "CIVIL3D.TRANSPORT_ERROR",
            "Only one Civil 3D command is allowed per TCP connection.",
            "transport",
            "node",
            "not_started"
          );
        }

        if (!this.isConnected) {
          this.connect();
        }

        this.responseCallbacks.set(requestId, {
          fail: (error) => {
            if (timeoutHandle) clearTimeout(timeoutHandle);
            reject(attachPartial(error, "transport_error"));
          },
          handle: (response) => {
            if (timeoutHandle) clearTimeout(timeoutHandle);
            try {
              const benchmarkEvent =
                benchmarkTrace && forwardedCode !== undefined
                  ? createMeasurementEventFromResponse(
                      benchmarkTrace,
                      forwardedCode,
                      response as Record<string, unknown>,
                      elapsedBenchmarkMs()
                    )
                  : undefined;
              if (response.error) {
                const commandError = civil3dErrorFromPlugin(response.error);
                if (benchmarkEvent) attachBenchmarkEventToError(commandError, benchmarkEvent);
                reject(commandError);
              } else {
                resolve(
                  benchmarkTrace && benchmarkEvent
                    ? { result: response.result, benchmarkEvent }
                    : response.result
                );
              }
            } catch (error) {
              const responseError = new Civil3dMcpError(
                "CIVIL3D.TRANSPORT_ERROR",
                `Failed to process response: ${
                  error instanceof Error ? error.message : String(error)
                }`,
                "transport",
                "transport",
                "unknown"
              );
              reject(attachPartial(responseError, "transport_error"));
            }
          },
        });

        log.debug("Sending command", { method: command, requestId });
        if (benchmarkTrace) benchmarkStartedAt = performance.now();
        timeoutHandle = setTimeout(() => {
          if (this.responseCallbacks.has(requestId)) {
            this.responseCallbacks.delete(requestId);
            this.resetResponseBuffer();
            log.warn("Command timed out", {
              method: command,
              requestId,
              timeoutMs: COMMAND_TIMEOUT_MS,
            });
            const timeoutError = new Civil3dMcpError(
              "CIVIL3D.COMMAND_TIMEOUT",
              `Command timed out after ${COMMAND_TIMEOUT_MS}ms: ${command}`,
              "timeout",
              "transport",
              "unknown"
            );
            reject(
              attachPartial(timeoutError, "transport_timeout")
            );
            this.socket.destroy();
          }
        }, COMMAND_TIMEOUT_MS);
        this.commandSent = true;
        this.socket.write(`${commandString}\n`);
      } catch (error) {
        if (pendingRequestId) this.responseCallbacks.delete(pendingRequestId);
        this.resetResponseBuffer();
        if (timeoutHandle) clearTimeout(timeoutHandle);
        const commandError =
          error instanceof Civil3dMcpError
            ? error
            : new Civil3dMcpError(
                "CIVIL3D.TRANSPORT_ERROR",
                error instanceof Error ? error.message : String(error),
                "transport",
                "transport",
                "not_started"
              );
        reject(attachPartial(commandError, "transport_error"));
      }
    });
  }
}
