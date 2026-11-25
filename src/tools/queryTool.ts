import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { performance } from "node:perf_hooks";
import { z } from "zod";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
import { COMMAND_TIMEOUT_MS } from "../utils/SocketClient.js";
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
import { imageContentBlocks, readResultImages, type ResultImage } from "./resultImages.js";
import { apiNotesContentBlocks, readApiNotes, type ApiNote } from "./apiNotes.js";

const log = createLogger("QueryTool");

/**
 * Guidance for the synchronous Civil command context and bounded result
 * serializer. Stated once here; civil3d_execute refers back to it.
 */
export const SCRIPT_RULES =
  "Script rules: keep code synchronous; a script that uses await is rejected because it could deadlock Civil 3D. " +
  "Return primitives, strings, Guid, dates/times (ISO 8601 text), anonymous objects or your own classes/records " +
  "(public properties and fields), arrays, lists, LINQ or other sequences, tuples (as arrays), Dictionary<string,...>, " +
  "ObjectId, Handle, Point2d/3d, Vector2d/3d; enums return as their names. " +
  "Not returnable: DBObject instances. Doubles are rounded to 10 significant digits (whole part untouched); " +
  "return a number as text when every digit matters. A result is kept below 40000 characters: a collection " +
  "keeps at most 1000 items, a longer result loses the end of its largest lists, and the reply becomes " +
  "{result, truncated: [{path, returned, total}]} (reason 'size' on a cut made for size), so narrow or page " +
  "the query for the rest; NaN/Infinity become null and the reply becomes " +
  "{result, nonFinite: {count, paths}}; depth 8. " +
  "Other namespaces (e.g. Autodesk.Civil.DatabaseServices.Styles, Autodesk.AutoCAD.Colors, System.IO) need a using line or a full name. " +
  "Names that exist twice have a default: DBObject, Entity and ErrorStatus are the AutoCAD types, Surface and Section " +
  "the Civil 3D ones, Exception is System.Exception; write the full name for the other one. " +
  "A script still running at its time limit (120 s unless timeoutSeconds is set) is stopped and rolled back. " +
  "A block {apiNotes: [...]} after the result warns about API members the script uses that crashed or misled earlier work; " +
  "read it before trusting the result or sending the call again.";

/**
 * The working order is docs/USAGE.md. A tool description is the one text every
 * client passes to the assistant, so each script tool names the first step and
 * where the rest is read.
 */
const QUERY_GUIDE_POINTER =
  "Before the first drawing call, call civil3d_skills with action 'status' to see the running instances, " +
  "their active drawing and whether an operation is running; " +
  "civil3d_skills with action 'guide' gives the working order for this server. ";

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
      QUERY_GUIDE_POINTER +
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
      timeoutSeconds: timeoutSecondsSchema,
    },
    // Not marked read-only: the transaction is rolled back, but the script is
    // arbitrary code and can still act outside it (files, sysvars, commands).
    { title: "Civil 3D query (rolled back)", destructiveHint: false, openWorldHint: false },
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
      let images: ResultImage[] = [];
      let apiNotes: ApiNote[] = [];
      try {
        const commandResult = await withApplicationConnection(
          async (client) =>
            await client.sendCommand(
              "executeCode",
              {
                code,
                readOnly: true,
                expectedDrawing: args.expectedDrawing,
                timeoutSeconds: args.timeoutSeconds,
              },
              benchmarkTrace,
              commandWaitMs(COMMAND_TIMEOUT_MS, args.timeoutSeconds),
              extra.signal,
              (response) => {
                images = readResultImages(response);
                // A catalog recipe already handles what a note would say.
                apiNotes = args.skill ? [] : readApiNotes(response);
              }
            ),
          {
            expectedDrawing: args.expectedDrawing,
            instanceId: args.instanceId,
            signal: extra.signal,
          }
        );
        const result = benchmarkTrace
          ? (commandResult as MeasuredCommandResult).result
          : commandResult;
        const benchmarkEvent = benchmarkTrace
          ? (commandResult as MeasuredCommandResult).benchmarkEvent
          : undefined;

        // Pictures follow the text, in the order of their references in it.
        const normalResult = {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify(result) ?? "null",
            },
            ...apiNotesContentBlocks(apiNotes),
            ...imageContentBlocks(images),
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
        const errorResult = createStructuredToolErrorResult(error, "Query failed: ");
        const normalResult =
          apiNotes.length > 0
            ? {
                ...errorResult,
                content: [...errorResult.content, ...apiNotesContentBlocks(apiNotes)],
                structuredContent: { ...errorResult.structuredContent, apiNotes },
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
