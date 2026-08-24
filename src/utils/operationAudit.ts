import { createHash, randomBytes } from "node:crypto";

import type { Civil3dErrorCategory, Civil3dErrorOutcome, Civil3dErrorSource } from "../errors/structuredError.js";
import type { Logger } from "./logger.js";

type OperationAuditTool = "civil3d_query" | "civil3d_execute";

interface OperationAuditContext {
  operationId: string;
  tool: OperationAuditTool;
  codeSha256: string;
  codeUtf8Bytes: number;
  startedAtMs: number;
}

interface OperationAuditFailure {
  code: string;
  category: Civil3dErrorCategory;
  source: Civil3dErrorSource;
  outcome: Civil3dErrorOutcome;
}

/**
 * Creates the deliberately small, stderr-only audit context for one tool call.
 * The C# source is reduced immediately to a digest and byte count; it is never
 * retained in the emitted record.
 */
export function createOperationAuditContext(
  tool: OperationAuditTool,
  code: string,
  startedAtMs: number
): OperationAuditContext {
  return {
    operationId: `op-${randomBytes(8).toString("hex")}`,
    tool,
    codeSha256: createHash("sha256").update(code, "utf8").digest("hex"),
    codeUtf8Bytes: Buffer.byteLength(code, "utf8"),
    startedAtMs,
  };
}

function baseRecord(context: OperationAuditContext, completedAtMs: number) {
  return {
    operationId: context.operationId,
    tool: context.tool,
    codeSha256: context.codeSha256,
    codeUtf8Bytes: context.codeUtf8Bytes,
    durationMs: Math.max(0, Math.round(completedAtMs - context.startedAtMs)),
  };
}

export function logOperationSuccess(
  logger: Logger,
  context: OperationAuditContext,
  completedAtMs: number
): void {
  logger.info("Operation audit", {
    ...baseRecord(context, completedAtMs),
    status: "success",
  });
}

export function logOperationFailure(
  logger: Logger,
  context: OperationAuditContext,
  completedAtMs: number,
  failure: OperationAuditFailure
): void {
  logger.info("Operation audit", {
    ...baseRecord(context, completedAtMs),
    status: "error",
    code: failure.code,
    category: failure.category,
    source: failure.source,
    outcome: failure.outcome,
  });
}
