import { z } from "zod";

const GUID_BODY =
  "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
const GUID_PATTERN = new RegExp(
  `^(?:[0-9a-fA-F]{32}|${GUID_BODY}|\\{${GUID_BODY}\\}|\\(${GUID_BODY}\\))$`
);

export const expectedDrawingSchema = z
  .object({
    databaseFilename: z.string().describe(
      "Full Database.Filename returned by a prior civil3d_query. Use an empty string only for an unsaved drawing."
    ),
    fingerprintGuid: z.string().regex(GUID_PATTERN).describe(
      "Database.FingerprintGuid returned by the same prior civil3d_query."
    ),
  })
  .strict()
  .describe("Expected active drawing identity checked immediately before Civil API access.");
