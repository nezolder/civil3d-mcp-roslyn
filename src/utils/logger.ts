/**
 * Structured logger for the Civil 3D MCP Server.
 * Outputs to stderr so it doesn't interfere with stdio MCP transport.
 */

type LogLevel = "debug" | "info" | "warn" | "error";

const LOG_LEVELS: Record<LogLevel, number> = {
  debug: 0,
  info: 1,
  warn: 2,
  error: 3,
};

const CURRENT_LEVEL: LogLevel = (process.env.LOG_LEVEL as LogLevel) ?? "info";

function shouldLog(level: LogLevel): boolean {
  return LOG_LEVELS[level] >= LOG_LEVELS[CURRENT_LEVEL];
}

function formatMessage(
  level: LogLevel,
  scope: string,
  message: string,
  data?: Record<string, unknown>
): string {
  const timestamp = new Date().toISOString();
  const parts = [`[${timestamp}]`, `[${level.toUpperCase()}]`, `[${scope}]`, message];

  if (data && Object.keys(data).length > 0) {
    parts.push(JSON.stringify(data));
  }

  return parts.join(" ");
}

export interface Logger {
  debug(message: string, data?: Record<string, unknown>): void;
  info(message: string, data?: Record<string, unknown>): void;
  warn(message: string, data?: Record<string, unknown>): void;
  error(message: string, data?: Record<string, unknown>): void;
}

export function createLogger(scope: string): Logger {
  return {
    debug(message: string, data?: Record<string, unknown>) {
      if (shouldLog("debug")) {
        console.error(formatMessage("debug", scope, message, data));
      }
    },
    info(message: string, data?: Record<string, unknown>) {
      if (shouldLog("info")) {
        console.error(formatMessage("info", scope, message, data));
      }
    },
    warn(message: string, data?: Record<string, unknown>) {
      if (shouldLog("warn")) {
        console.error(formatMessage("warn", scope, message, data));
      }
    },
    error(message: string, data?: Record<string, unknown>) {
      if (shouldLog("error")) {
        console.error(formatMessage("error", scope, message, data));
      }
    },
  };
}
