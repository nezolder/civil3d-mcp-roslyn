import { z } from "zod";

export const instanceIdSchema = z
  .string()
  .regex(/^[0-9a-fA-F]{32}$/)
  .optional()
  .describe(
    "Optional opaque Civil 3D instance ID. Use a value returned by a CIVIL3D.INSTANCE_SELECTION_REQUIRED error when more than one Civil 3D is running."
  );
