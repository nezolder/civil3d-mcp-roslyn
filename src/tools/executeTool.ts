import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { performance } from "node:perf_hooks";
import { z } from "zod";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
import { COMMAND_TIMEOUT_MS, readTimeoutEnvironment } from "../utils/SocketClient.js";
import { createLogger } from "../utils/logger.js";
import {
  Civil3dMcpError,
  createStructuredToolErrorResult,
  normalizeCivil3dError,
} from "../errors/structuredError.js";
import {
  BenchmarkTraceRequest,
  INVALID_BENCHMARK_METADATA_MESSAGE,
  MeasuredCommandResult,
  finalizeReturnedPayloadMeasurement,
  getBenchmarkTraceRequest,
  getOrCreateTransportFailureEvent,
  withBenchmarkEventMeta,
} from "../benchmark/liveTrace.js";
import { expectedDrawingSchema } from "./expectedDrawing.js";
import { instanceIdSchema } from "./instanceSelection.js";
import { commandWaitMs, timeoutSecondsSchema } from "./scriptTimeout.js";
import {
  ScriptSourceOptions,
  resolveScriptCode,
  skillNameSchema,
  skillParamsSchema,
} from "./scriptSource.js";
import {
  createOperationAuditContext,
  logOperationFailure,
  logOperationSuccess,
} from "../utils/operationAudit.js";

const log = createLogger("ExecuteTool");
/** Plugin ability behind requireClean and backup; an older plugin would ignore them. */
const WRITE_SAFETY_FEATURE = "writeSafety";

/** The plugin's account of what happened to the drawing, sent next to the result. */
function readReceipt(response: Record<string, unknown>): Record<string, unknown> | undefined {
  const receipt = response.receipt;
  return typeof receipt === "object" && receipt !== null && !Array.isArray(receipt)
    ? (receipt as Record<string, unknown>)
    : undefined;
}
const SAVE_COMMAND_TIMEOUT_MS = readTimeoutEnvironment("CIVIL3D_SAVE_TIMEOUT", 600_000);
const idempotencyKeySchema = z
  .string()
  .min(1)
  .max(128)
  .regex(/^[A-Za-z0-9._:-]+$/)
  .optional()
  .describe(
    "Optional opaque session key. Reuse it only to manually reconcile an uncertain outcome; use a new key for an intentional new write."
  );

/**
 * civil3d_execute — Executes C# code in Civil 3D with WRITE access.
 * The code runs inside a transaction that gets committed on success. Optional
 * disk saving runs only after that transaction and its document lock are closed.
 *
 * Available globals in the script:
 *   - Document (active AutoCAD document)
 *   - CivilDoc (active Civil 3D document)
 *   - Database (document database)
 *   - Transaction (active transaction — auto-committed)
 *   - Editor (document editor)
 *
 * The script can use all Civil 3D and AutoCAD namespaces (auto-imported).
 * Return a value to send it back as JSON to the AI.
 */
export function registerExecuteTool(server: McpServer, options: ScriptSourceOptions = {}) {
  server.tool(
    "civil3d_execute",
    "Execute C# code in Civil 3D with write access. The code runs inside a committed transaction. " +
      "Available globals: Document, CivilDoc, Database, Transaction, Editor. " +
      "Common Civil 3D and AutoCAD namespaces are auto-imported. Return a value to get results back as JSON. " +
      "The script rules of civil3d_query apply. " +
      "Use this for operations that MODIFY the drawing (create, edit, delete objects). " +
      "expectedDrawing must come from a prior read-only identity query. " +
      "Instead of code you can pass skill and params to run a skill whose run_by_name is true. " +
      "To persist the drawing file, set saveDrawing=true; do not call Database.SaveAs or queue QSAVE from the C# code. " +
      "Safety switches: requireClean=true refuses a drawing with unsaved changes, backup=true first copies the saved DWG. " +
      "The reply has a second text block {\"receipt\": {...}} with DBMOD before/after, committed, saved, the file time and the backup path, " +
      "so no separate checks are needed around a write.",
    {
      code: z.string().optional().describe(
        "C# code to execute. Has access to Document, CivilDoc, Database, Transaction, Editor. " +
          "Example: var id = TinSurface.Create(Database, \"MySurface\"); return new { success = true };"
      ),
      skill: skillNameSchema,
      params: skillParamsSchema,
      description: z.string().optional().describe(
        "Optional one-line summary of the change. It is shown on the Civil 3D command line and kept " +
          "in the local write journal, so write it for the person at the computer."
      ),
      expectedDrawing: expectedDrawingSchema,
      instanceId: instanceIdSchema,
      idempotencyKey: idempotencyKeySchema,
      saveDrawing: z
        .boolean()
        .optional()
        .describe(
          "When true, save the currently named DWG after the write transaction commits and wait for completion. " +
            "Use this instead of Database.SaveAs or Document.SendStringToExecute(\"QSAVE\") in code. " +
            "An unsaved drawing must first be named in Civil 3D."
        ),
      requireClean: z
        .boolean()
        .optional()
        .describe(
          "When true, run only if the drawing has no unsaved changes (DBMOD=0); otherwise nothing runs " +
            "(CIVIL3D.DRAWING_NOT_CLEAN). Defaults to the value of backup."
        ),
      backup: z
        .boolean()
        .optional()
        .describe(
          "When true, copy the saved DWG file to a time-stamped backup before the script runs; the receipt " +
            "returns its path. Needs a named drawing, and a clean one unless requireClean is explicitly false."
        ),
      backupDirectory: z
        .string()
        .optional()
        .describe(
          "Absolute folder for the backup copy (only with backup=true). Default: the CIVIL3D_MCP_BACKUP_DIR " +
            "setting of Civil 3D, else %LOCALAPPDATA%\\Civil3dMcpRoslyn\\backups."
        ),
      timeoutSeconds: timeoutSecondsSchema,
    },
    {
      title: "Civil 3D execute (writes the drawing)",
      readOnlyHint: false,
      destructiveHint: true,
      idempotentHint: false,
      openWorldHint: false,
    },
    async (args, extra) => {
      let benchmarkTrace: BenchmarkTraceRequest | undefined;
      try {
        benchmarkTrace = getBenchmarkTraceRequest(extra);
      } catch {
        return createStructuredToolErrorResult(
          new Civil3dMcpError(
            "CIVIL3D.INVALID_BENCHMARK_METADATA",
            INVALID_BENCHMARK_METADATA_MESSAGE,
            "validation",
            "node",
            "not_started"
          )
        );
      }
      const benchmarkFallbackStartedAt = benchmarkTrace ? performance.now() : undefined;
      let code: string;
      try {
        code = resolveScriptCode(args, "civil3d_execute", options);
      } catch (error) {
        return createStructuredToolErrorResult(error, "Execution failed: ");
      }
      const audit = createOperationAuditContext("civil3d_execute", code, performance.now());
      const usesWriteSafety =
        args.requireClean === true || args.backup === true || args.backupDirectory !== undefined;
      let receipt: Record<string, unknown> | undefined;
      try {
        const commandResult = await withApplicationConnection(
          async (client) =>
            await client.sendCommand(
              "executeCode",
              {
                code,
                readOnly: false,
                description: args.description,
                expectedDrawing: args.expectedDrawing,
                idempotencyKey: args.idempotencyKey,
                saveDrawing: args.saveDrawing,
                timeoutSeconds: args.timeoutSeconds,
                requireClean: args.requireClean,
                backup: args.backup,
                backupDirectory: args.backupDirectory,
              },
              benchmarkTrace,
              commandWaitMs(
                args.saveDrawing ? SAVE_COMMAND_TIMEOUT_MS : COMMAND_TIMEOUT_MS,
                args.timeoutSeconds
              ),
              extra.signal,
              (response) => {
                receipt = readReceipt(response);
              }
            ),
          {
            expectedDrawing: args.expectedDrawing,
            instanceId: args.instanceId,
            signal: extra.signal,
            requiredFeatures: usesWriteSafety ? [WRITE_SAFETY_FEATURE] : undefined,
          }
        );
        const result = benchmarkTrace
          ? (commandResult as MeasuredCommandResult).result
          : commandResult;
        const benchmarkEvent = benchmarkTrace
          ? (commandResult as MeasuredCommandResult).benchmarkEvent
          : undefined;

        // The result keeps the first block to itself; the receipt follows in its own.
        const normalResult = {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify(result) ?? "null",
            },
            ...(receipt
              ? [{ type: "text" as const, text: JSON.stringify({ receipt }) }]
              : []),
          ],
        };
        logOperationSuccess(log, audit, performance.now());
        return withBenchmarkEventMeta(
          normalResult,
          finalizeReturnedPayloadMeasurement(benchmarkEvent, normalResult)
        );
      } catch (error) {
        const normalizedError = normalizeCivil3dError(error);
        logOperationFailure(log, audit, performance.now(), normalizedError);
        const errorResult = createStructuredToolErrorResult(error, "Execution failed: ");
        // A refusal or failure that reached the drawing still reports its state,
        // for example that a write was committed although the save failed.
        const normalResult = receipt
          ? {
              ...errorResult,
              content: [
                ...errorResult.content,
                { type: "text" as const, text: JSON.stringify({ receipt }) },
              ],
              structuredContent: { ...errorResult.structuredContent, receipt },
            }
          : errorResult;
        return withBenchmarkEventMeta(
          normalResult,
          finalizeReturnedPayloadMeasurement(
            benchmarkTrace && benchmarkFallbackStartedAt !== undefined
              ? getOrCreateTransportFailureEvent(
                  error,
                  benchmarkTrace,
                  code,
                  performance.now() - benchmarkFallbackStartedAt
                )
              : undefined,
            normalResult
          )
        );
      }
    }
  );
}
