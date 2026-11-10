import { z } from "zod";

/** Script limit the plugin applies when the caller sends no timeoutSeconds. */
export const DEFAULT_SCRIPT_TIMEOUT_SECONDS = 120;
export const MAX_SCRIPT_TIMEOUT_SECONDS = 1_800;

/**
 * Time the Node host keeps waiting after the plugin's own script limit. The
 * plugin's clock starts later (queueing, command start, compilation) and it
 * still has to serialize the reply, so the host must outlast it: the caller
 * then gets the plugin's definite "rolled back" answer, not an unknown outcome.
 */
const REPLY_MARGIN_MS = 30_000;

export const timeoutSecondsSchema = z
  .number()
  .int()
  .min(1)
  .max(MAX_SCRIPT_TIMEOUT_SECONDS)
  .optional()
  .describe(
    `Optional script time limit in seconds (default ${DEFAULT_SCRIPT_TIMEOUT_SECONDS}, ` +
      `max ${MAX_SCRIPT_TIMEOUT_SECONDS}). A script still running at the limit is stopped and rolled back, ` +
      "so raise this for a known long operation instead of repeating the call."
  );

/**
 * How long the host waits for one executeCode reply. baseWaitMs is the
 * configured wait for a default-length script (CIVIL3D_COMMAND_TIMEOUT, or
 * CIVIL3D_SAVE_TIMEOUT when the drawing is saved); a longer script limit
 * extends it by the same amount.
 */
export function commandWaitMs(baseWaitMs: number, timeoutSeconds: number | undefined): number {
  const scriptMs = (timeoutSeconds ?? DEFAULT_SCRIPT_TIMEOUT_SECONDS) * 1_000;
  const extensionMs = Math.max(0, scriptMs - DEFAULT_SCRIPT_TIMEOUT_SECONDS * 1_000);
  return Math.max(baseWaitMs + extensionMs, scriptMs + REPLY_MARGIN_MS);
}
