import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { performance } from "node:perf_hooks";
import { z } from "zod";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
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
import {
  createOperationAuditContext,
  logOperationFailure,
  logOperationSuccess,
} from "../utils/operationAudit.js";

const log = createLogger("ExecuteTool");
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
 * The code runs inside a transaction that gets committed on success.
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
export function registerExecuteTool(server: McpServer) {
  server.tool(
    "civil3d_execute",
    "Execute C# code in Civil 3D with write access. The code runs inside a committed transaction. " +
      "Available globals: Document, CivilDoc, Database, Transaction, Editor. " +
      "All Civil 3D namespaces are auto-imported. Return a value to get results back as JSON. " +
      "Use this for operations that MODIFY the drawing (create, edit, delete objects). " +
      "expectedDrawing must come from a prior read-only identity query.",
    {
      code: z.string().describe(
        "C# code to execute. Has access to Document, CivilDoc, Database, Transaction, Editor. " +
          "Example: var id = TinSurface.Create(Database, \"MySurface\"); return new { success = true };"
      ),
      description: z.string().optional().describe(
        "Optional human-readable summary; excluded from operation audit logs."
      ),
      expectedDrawing: expectedDrawingSchema,
      idempotencyKey: idempotencyKeySchema,
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
      const audit = createOperationAuditContext("civil3d_execute", args.code, performance.now());
      try {
        const commandResult = await withApplicationConnection(async (client) =>
          await client.sendCommand(
            "executeCode",
            {
              code: args.code,
              readOnly: false,
              description: args.description,
              expectedDrawing: args.expectedDrawing,
              idempotencyKey: args.idempotencyKey,
            },
            benchmarkTrace
          )
        );
        const result = benchmarkTrace
          ? (commandResult as MeasuredCommandResult).result
          : commandResult;
        const benchmarkEvent = benchmarkTrace
          ? (commandResult as MeasuredCommandResult).benchmarkEvent
          : undefined;

        const normalResult = {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify(result, null, 2),
            },
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
        const normalResult = createStructuredToolErrorResult(error, "Execution failed: ");
        return withBenchmarkEventMeta(
          normalResult,
          finalizeReturnedPayloadMeasurement(
            benchmarkTrace && benchmarkFallbackStartedAt !== undefined
              ? getOrCreateTransportFailureEvent(
                  error,
                  benchmarkTrace,
                  args.code,
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
