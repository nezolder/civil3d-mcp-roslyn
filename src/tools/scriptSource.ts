import { z } from "zod";
import { Civil3dMcpError } from "../errors/structuredError.js";
import { SkillBindingError, bindSkillCode } from "./skillBinding.js";
import { SKILLS_DIR, findSkill } from "./skillsTool.js";

export interface ScriptSourceOptions {
  // Internal fixture directory only; this is not a public MCP input.
  skillsDirectory?: string;
}

export const skillNameSchema = z
  .string()
  .optional()
  .describe(
    "Instead of code: the name of a civil3d_skills skill whose run_by_name is true. " +
      "Its reviewed template runs with params bound; all other inputs apply as with code."
  );

export const skillParamsSchema = z
  .record(z.string(), z.unknown())
  .optional()
  .describe(
    "Values for the skill's parameters by name, as listed by civil3d_skills get. " +
      "Omitted optional parameters keep the template default. Tuple arrays take objects or positional arrays."
  );

interface ScriptSourceArgs {
  code?: string;
  skill?: string;
  params?: Record<string, unknown>;
}

/**
 * Returns the C# code to send: the caller's code, or a skill template with
 * the caller's parameter values bound. Throws a not-started validation error.
 */
export function resolveScriptCode(
  args: ScriptSourceArgs,
  tool: "civil3d_query" | "civil3d_execute",
  options: ScriptSourceOptions = {}
): string {
  if (args.skill === undefined) {
    if (args.params !== undefined) throw invalidInput("Parameter 'params' is only valid together with 'skill'.");
    if (args.code === undefined) throw invalidInput("Provide either 'code' or 'skill'.");
    return args.code;
  }
  if (args.code !== undefined) throw invalidInput("Provide either 'code' or 'skill', not both.");

  const skill = findSkill(args.skill, options.skillsDirectory ?? SKILLS_DIR);
  if (!skill) throw invalidInput(`Skill '${args.skill}' not found. Use civil3d_skills search to find one.`);
  if (tool === "civil3d_query" && skill.metadata.requires_write) {
    throw invalidInput(`Skill '${skill.metadata.name}' writes to the drawing; run it with civil3d_execute.`);
  }

  try {
    return bindSkillCode(skill.metadata.name, skill.content, skill.metadata.parameters, args.params);
  } catch (error) {
    if (error instanceof SkillBindingError) throw invalidInput(error.message);
    throw error;
  }
}

function invalidInput(message: string): Civil3dMcpError {
  return new Civil3dMcpError("CIVIL3D.INVALID_INPUT", message, "validation", "node", "not_started");
}
