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
import { instanceIdSchema } from "./instanceSelection.js";
import {
  createOperationAuditContext,
  logOperationFailure,
  logOperationSuccess,
} from "../utils/operationAudit.js";

const log = createLogger("QueryTool");

/**
 * civil3d_query — Executes C# code in Civil 3D in READ-ONLY mode.
 * The transaction is NOT committed — no changes are persisted.
 *
 * Same globals as civil3d_execute but safe for data retrieval.
 * Use this for listing objects, getting properties, analyzing data, etc.
 */
export function registerQueryTool(server: McpServer) {
  server.tool(
    "civil3d_query",
    "Execute C# code in Civil 3D in READ-ONLY mode (no changes saved). " +
      "Available globals: Document, CivilDoc, Database, Transaction, Editor. " +
      "All Civil 3D namespaces are auto-imported. Return a value to get results as JSON. " +
      "Use this for querying data: listing objects, getting properties, analyzing surfaces, etc. " +
      "Omit expectedDrawing only to bootstrap Database.Filename and Database.FingerprintGuid; " +
      "otherwise supply it to guard the active drawing.",
    {
      code: z.string().describe(
        "C# code to query data. Has access to Document, CivilDoc, Database, Transaction, Editor. " +
          "Example: var surfaces = new List<object>(); " +
          "foreach (ObjectId id in CivilDoc.GetSurfaceIds()) { " +
          "var s = Transaction.GetObject(id, OpenMode.ForRead) as TinSurface; " +
          'surfaces.Add(new { s.Name, s.Layer }); } return surfaces;'
      ),
      expectedDrawing: expectedDrawingSchema.optional(),
      instanceId: instanceIdSchema,
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
      const audit = createOperationAuditContext("civil3d_query", args.code, performance.now());
      try {
        const commandResult = await withApplicationConnection(
          async (client) =>
            await client.sendCommand(
              "executeCode",
              {
                code: args.code,
                readOnly: true,
                expectedDrawing: args.expectedDrawing,
              },
              benchmarkTrace
            ),
          { expectedDrawing: args.expectedDrawing, instanceId: args.instanceId }
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
        const normalResult = createStructuredToolErrorResult(error, "Query failed: ");
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
