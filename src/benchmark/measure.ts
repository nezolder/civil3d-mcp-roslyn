import { createHash } from "node:crypto";

export const TOKEN_ESTIMATOR_ID = "utf8_bytes_div_4_ceil_v1";
export const TOKEN_ESTIMATOR_FORMULA = "ceil(utf8_bytes / 4) per text field";

export interface TextMeasurement {
  characters: number;
  lines: number;
  utf8_bytes: number;
  sha256: string;
}

/** Counts Unicode code points, not JavaScript UTF-16 code units. */
export function countCharacters(text: string): number {
  return Array.from(text).length;
}

/** Empty text has zero lines; otherwise CRLF, LF, and CR are line separators. */
export function countLines(text: string): number {
  return text.length === 0 ? 0 : text.split(/\r\n|\n|\r/).length;
}

export function countUtf8Bytes(text: string): number {
  return Buffer.byteLength(text, "utf8");
}

export function sha256Utf8(text: string): string {
  return createHash("sha256").update(text, "utf8").digest("hex");
}

export function estimateTokensFromUtf8Bytes(utf8Bytes: number): number {
  if (!Number.isSafeInteger(utf8Bytes) || utf8Bytes < 0) {
    throw new Error("utf8Bytes must be a non-negative safe integer");
  }
  return Math.ceil(utf8Bytes / 4);
}

export function measureText(text: string): TextMeasurement {
  return {
    characters: countCharacters(text),
    lines: countLines(text),
    utf8_bytes: countUtf8Bytes(text),
    sha256: sha256Utf8(text),
  };
}
