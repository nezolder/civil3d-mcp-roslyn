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

const log = createLogger("QueryTool");

/**
 * Guidance for the synchronous Civil command context and bounded result
 * serializer. Stated once here; civil3d_execute refers back to it.
 */
export const SCRIPT_RULES =
  "Script rules: keep code synchronous; a script that uses await is rejected because it could deadlock Civil 3D. " +
  "Return primitives, strings, Guid, dates/times (ISO 8601 text), anonymous objects or your own classes/records " +
  "(public properties and fields), arrays, lists, LINQ or other sequences, tuples (as arrays), Dictionary<string,...>, " +
  "ObjectId, Handle, Point2d/3d, Vector2d/3d; enums return as numbers, so use .ToString() for names. " +
  "Not returnable: DBObject instances, NaN/Infinity. A collection over 1000 items keeps its first 1000 and the reply " +
  "becomes {result, truncated: [{path, returned, total}]}; depth 8 and 10000 total result nodes. " +
  "Other namespaces (e.g. Autodesk.Civil.DatabaseServices.Styles, Autodesk.AutoCAD.Colors, System.IO) need a using line or a full name. " +
  "Surface, Entity and Exception are ambiguous between AutoCAD and Civil/System: write the full name.";

/**
 * civil3d_query — Executes C# code in Civil 3D in READ-ONLY mode.
 * The transaction is NOT committed — no changes are persisted.
 *
 * Same globals as civil3d_execute but safe for data retrieval.
 * Use this for listing objects, getting properties, analyzing data, etc.
 */
export function registerQueryTool(server: McpServer, options: ScriptSourceOptions = {}) {
  server.tool(
    "civil3d_query",
    "Execute C# code in Civil 3D in READ-ONLY mode (no changes saved). " +
      "Available globals: Document, CivilDoc, Database, Transaction, Editor. " +
      "Common Civil 3D and AutoCAD namespaces are auto-imported. Return a value to get results as JSON. " +
      "Use this for querying data: listing objects, getting properties, analyzing surfaces, etc. " +
      "Omit expectedDrawing only to bootstrap Database.Filename and Database.FingerprintGuid; " +
      "otherwise supply it to guard the active drawing. " +
      "Instead of code you can pass skill and params to run a read skill whose run_by_name is true. " +
      SCRIPT_RULES,
    {
      code: z.string().optional().describe(
        "C# code to query data. Has access to Document, CivilDoc, Database, Transaction, Editor. " +
          "Example: var surfaces = new List<object>(); " +
          "foreach (ObjectId id in CivilDoc.GetSurfaceIds()) { " +
          "var s = Transaction.GetObject(id, OpenMode.ForRead) as TinSurface; " +
          'surfaces.Add(new { s.Name, s.Layer }); } return surfaces;'
      ),
      skill: skillNameSchema,
      params: skillParamsSchema,
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
      let code: string;
      try {
        code = resolveScriptCode(args, "civil3d_query", options);
      } catch (error) {
        return createStructuredToolErrorResult(error, "Query failed: ");
      }
      const audit = createOperationAuditContext("civil3d_query", code, performance.now());
      try {
        const commandResult = await withApplicationConnection(
          async (client) =>
            await client.sendCommand(
              "executeCode",
              {
                code,
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
              text: JSON.stringify(result) ?? "null",
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
