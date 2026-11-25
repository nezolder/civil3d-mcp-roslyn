/**
 * Notes the plugin attaches when a script uses an API member that has
 * surprised earlier work (a crash in field use, a raw value, a documented
 * limit). They refuse nothing; the plugin sends them next to the result.
 */
export interface ApiNote {
  id: string;
  member: string;
  line: number;
  source: string;
  note: string;
  guideTopic?: string;
}

const MAX_NOTES = 8;
const MAX_TEXT = 600;

/** Reads the plugin's note list; anything malformed is left out. */
export function readApiNotes(response: Record<string, unknown>): ApiNote[] {
  const notes = response.apiNotes;
  if (!Array.isArray(notes)) return [];
  const result: ApiNote[] = [];
  for (const item of notes) {
    if (result.length >= MAX_NOTES) break;
    if (typeof item !== "object" || item === null) continue;
    const { id, member, line, source, note, guideTopic } = item as Record<string, unknown>;
    if (
      typeof id !== "string" ||
      typeof member !== "string" ||
      typeof line !== "number" ||
      !Number.isInteger(line) ||
      typeof source !== "string" ||
      typeof note !== "string"
    ) {
      continue;
    }
    result.push({
      id,
      member,
      line,
      source,
      note: note.slice(0, MAX_TEXT),
      ...(typeof guideTopic === "string" && guideTopic.length > 0 ? { guideTopic } : {}),
    });
  }
  return result;
}

/** A text block {"apiNotes": [...]}, or nothing when there are no notes. */
export function apiNotesContentBlocks(
  notes: readonly ApiNote[]
): Array<{ type: "text"; text: string }> {
  return notes.length > 0 ? [{ type: "text" as const, text: JSON.stringify({ apiNotes: notes }) }] : [];
}
