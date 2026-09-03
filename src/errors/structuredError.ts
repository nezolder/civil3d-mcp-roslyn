export type Civil3dErrorCategory =
  | "validation"
  | "connection"
  | "transport"
  | "timeout"
  | "compilation"
  | "policy"
  | "drawing"
  | "execution"
  | "unknown";

export type Civil3dErrorSource = "node" | "transport" | "plugin";
export type Civil3dErrorOutcome = "not_started" | "reported_error" | "unknown";

export interface StructuredCivil3dError {
  code: string;
  category: Civil3dErrorCategory;
  message: string;
  source: Civil3dErrorSource;
  outcome: Civil3dErrorOutcome;
  retryable: false;
}

export class Civil3dMcpError extends Error {
  readonly code: string;
  readonly category: Civil3dErrorCategory;
  readonly source: Civil3dErrorSource;
  readonly outcome: Civil3dErrorOutcome;

  constructor(
    code: string,
    message: string,
    category: Civil3dErrorCategory,
    source: Civil3dErrorSource,
    outcome: Civil3dErrorOutcome
  ) {
    super(message);
    this.name = "Civil3dMcpError";
    this.code = code;
    this.category = category;
    this.source = source;
    this.outcome = outcome;
  }

  toStructuredError(): StructuredCivil3dError {
    return {
      code: this.code,
      category: this.category,
      message: this.message,
      source: this.source,
      outcome: this.outcome,
      retryable: false,
    };
  }
}

const CIVIL3D_ERROR_CODE = /^CIVIL3D\.[A-Z0-9_]+$/;

function classifyPluginCode(code: string): {
  category: Civil3dErrorCategory;
  outcome: Civil3dErrorOutcome;
} {
  switch (code) {
    case "CIVIL3D.INVALID_INPUT":
      return { category: "validation", outcome: "reported_error" };
    case "CIVIL3D.SANDBOX_VIOLATION":
      return { category: "policy", outcome: "reported_error" };
    case "CIVIL3D.COMPILATION_ERROR":
      return { category: "compilation", outcome: "reported_error" };
    case "CIVIL3D.NO_DRAWING":
      return { category: "drawing", outcome: "reported_error" };
    case "CIVIL3D.API_LOOKUP_UNAVAILABLE":
      return { category: "execution", outcome: "reported_error" };
    case "CIVIL3D.DRAWING_GUARD_REQUIRED":
    case "CIVIL3D.DRAWING_MISMATCH":
      return { category: "drawing", outcome: "not_started" };
    case "CIVIL3D.SAVE_PATH_REQUIRED":
      return { category: "drawing", outcome: "not_started" };
    case "CIVIL3D.DRAWING_GUARD_INVALID":
      return { category: "validation", outcome: "not_started" };
    case "CIVIL3D.IDEMPOTENCY_CONFLICT":
    case "CIVIL3D.IDEMPOTENCY_IN_PROGRESS":
    case "CIVIL3D.IDEMPOTENCY_COMPLETED":
      return { category: "execution", outcome: "not_started" };
    case "CIVIL3D.COMMAND_CONTEXT_TIMEOUT":
      return { category: "timeout", outcome: "not_started" };
    case "CIVIL3D.TIMEOUT":
      return { category: "timeout", outcome: "unknown" };
    case "CIVIL3D.REQUEST_TOO_LARGE":
      return { category: "transport", outcome: "not_started" };
    case "CIVIL3D.RESPONSE_TOO_LARGE":
    case "CIVIL3D.TRANSPORT_ERROR":
      return { category: "transport", outcome: "unknown" };
    case "CIVIL3D.TRANSACTION_FAILED":
    case "CIVIL3D.SAVE_FAILED":
    case "CIVIL3D.RESULT_SERIALIZATION_FAILED":
      return { category: "execution", outcome: "reported_error" };
    default:
      return { category: "execution", outcome: "reported_error" };
  }
}

export function civil3dErrorFromPlugin(error: unknown): Civil3dMcpError {
  const candidate =
    typeof error === "object" && error !== null
      ? (error as Record<string, unknown>)
      : undefined;
  const code =
    typeof candidate?.code === "string" && CIVIL3D_ERROR_CODE.test(candidate.code)
      ? candidate.code
      : "CIVIL3D.PLUGIN_ERROR";
  const message =
    typeof candidate?.message === "string" && candidate.message.length > 0
      ? candidate.message
      : "Unknown error from Civil 3D plugin";
  const classification = classifyPluginCode(code);

  return new Civil3dMcpError(
    code,
    message,
    classification.category,
    "plugin",
    classification.outcome
  );
}

export function normalizeCivil3dError(error: unknown): Civil3dMcpError {
  if (error instanceof Civil3dMcpError) return error;

  return new Civil3dMcpError(
    "CIVIL3D.UNKNOWN_ERROR",
    error instanceof Error ? error.message : String(error),
    "unknown",
    "node",
    "unknown"
  );
}

export function createStructuredToolErrorResult(
  error: unknown,
  legacyPrefix = ""
) {
  const normalized = normalizeCivil3dError(error);
  return {
    content: [
      {
        type: "text" as const,
        text: `${legacyPrefix}${normalized.message}`,
      },
    ],
    structuredContent: {
      schema_version: "civil3d-mcp-error/v1",
      ok: false,
      error: normalized.toStructuredError(),
    },
    isError: true as const,
  };
}
