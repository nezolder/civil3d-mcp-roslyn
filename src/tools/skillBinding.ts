/**
 * Runs a skill by name: binds caller values into the reviewed code template
 * so a client does not have to fetch and echo the whole template back.
 *
 * A template is bindable when every declared parameter is initialised once,
 * at the start of a top-level line, in one of these forms:
 *
 *   var name = "default";            string tolerance = ...;  (string)
 *   var name = 100;                  int / long name = 100;   (integer)
 *   var name = 0.0;  / double.NaN    double name = 0.0;       (double)
 *   var name = true;                 bool name = true;        (boolean)
 *   var name = new (double x, double y)[] { };                (rows: tuple array)
 *   var name = new double[] { };     string[] name = new string[] { "a" };
 *                                                             (plain list)
 *
 * Only the initialiser is replaced, and every value becomes a C# literal, so
 * caller input can never add code. Templates in any other form stay
 * available through get and adapted code.
 */

export interface SkillParameter {
  name: string;
  type: string;
  required: boolean;
  description?: string;
}

type ScalarKind = "string" | "int" | "long" | "double" | "bool";

interface TupleElement {
  kind: ScalarKind;
  name: string;
}

interface Declaration {
  kind: ScalarKind | "tuple-array" | "scalar-array";
  /** Fields of one row of a tuple array. */
  elements?: TupleElement[];
  /** Item type of a plain list. */
  itemKind?: ScalarKind;
  /** Start and end of the initialiser expression within the template code. */
  start: number;
  end: number;
  /** The initialiser as written in the template: the default value. */
  initializer: string;
}

/**
 * What a caller needs to know to fill one parameter: the compact form of a
 * skill's documentation, also used to explain a missing or wrong value.
 */
export interface ParameterCard {
  name: string;
  /** string, int, long, double, bool, a plain list such as "double[]", or "rows". */
  type: string;
  required: boolean;
  /** Value used when an optional parameter is omitted; absent when there is none to show. */
  default?: string | number | boolean | Array<string | number | boolean>;
  /** For "rows": the fields of one row, in positional order. */
  fields?: Array<{ name: string; type: string }>;
  description?: string;
}

export class SkillBindingError extends Error {}

const CODE_BLOCK = /```csharp\r?\n([\s\S]*?)```/;
const SCALAR_TYPES = new Set<ScalarKind>(["string", "int", "long", "double", "bool"]);
const TUPLE_ARRAY = /^new \(([^()]+)\)\[\] \{\s*\}$/;
const SCALAR_ARRAY = /^new (string|int|long|double|bool)\[\] \{(.*)\}$/;
const STRING_LITERAL = /^"(?:[^"\\\r\n]|\\.)*"/;

/** The first C# block of a skill body: its code template. */
export function extractCodeTemplate(content: string): string | null {
  return content.match(CODE_BLOCK)?.[1] ?? null;
}

/** The skill body without its code template, for metadata-only reads. */
export function omitCodeTemplate(content: string): string {
  return content.replace(
    CODE_BLOCK,
    "(Code template omitted. Run this skill by name with civil3d_query or civil3d_execute.)"
  );
}

/** Whether every declared parameter of the template can be bound by name. */
export function isRunnableByName(content: string, parameters: SkillParameter[]): boolean {
  const code = extractCodeTemplate(content);
  return code !== null && parameters.every((parameter) => findDeclaration(code, parameter.name) !== null);
}

/**
 * Returns the template code with caller values bound. Omitted optional
 * parameters keep the template default.
 */
export function bindSkillCode(
  skillName: string,
  content: string,
  parameters: SkillParameter[],
  values: Record<string, unknown> = {}
): string {
  const code = extractCodeTemplate(content);
  if (code === null) {
    throw new SkillBindingError(`Skill '${skillName}' has no code template.`);
  }

  const declared = new Map(parameters.map((parameter) => [parameter.name, parameter]));
  const unknown = Object.keys(values).filter((name) => !declared.has(name));
  if (unknown.length > 0) {
    const allowed = parameters.map((parameter) => parameter.name).join(", ") || "none";
    throw new SkillBindingError(
      `Unknown parameter(s) for skill '${skillName}': ${unknown.join(", ")}. Allowed: ${allowed}.`
    );
  }

  const declarations = parameters.map((parameter) => {
    const declaration = findDeclaration(code, parameter.name);
    if (declaration === null) {
      throw new SkillBindingError(
        `Skill '${skillName}' cannot run by name: parameter '${parameter.name}' has no bindable declaration. ` +
          "Read it with civil3d_skills get and adapt the code instead."
      );
    }
    return declaration;
  });

  const missing = parameters.filter(
    (parameter) => parameter.required && !Object.prototype.hasOwnProperty.call(values, parameter.name)
  );
  if (missing.length > 0) {
    // Name every parameter at once, so one more call is enough to get it right.
    const names = missing.map((parameter) => `'${parameter.name}'`).join(", ");
    throw new SkillBindingError(
      `Skill '${skillName}' requires parameter${missing.length > 1 ? "s" : ""} ${names}. ` +
        `Parameters: ${summarizeParameters(content, parameters)}.`
    );
  }

  const replacements: Array<{ start: number; end: number; literal: string }> = [];
  for (const [index, parameter] of parameters.entries()) {
    const declaration = declarations[index];
    if (!Object.prototype.hasOwnProperty.call(values, parameter.name)) continue;
    replacements.push({
      start: declaration.start,
      end: declaration.end,
      literal: toLiteral(parameter.name, declaration, values[parameter.name]),
    });
  }

  let bound = code;
  for (const { start, end, literal } of replacements.sort((left, right) => right.start - left.start)) {
    bound = bound.slice(0, start) + literal + bound.slice(end);
  }
  return bound;
}

/**
 * The parameter cards of a skill: type, default and row fields are read from
 * the template itself where the parameter is bindable, so the card cannot
 * disagree with what a run by name accepts.
 */
export function describeParameters(content: string, parameters: SkillParameter[]): ParameterCard[] {
  const code = extractCodeTemplate(content);
  return parameters.map((parameter) => {
    const declaration = code === null ? null : findDeclaration(code, parameter.name);
    const card: ParameterCard = {
      name: parameter.name,
      type: declaration === null ? parameter.type : declarationType(declaration),
      required: parameter.required,
    };
    if (declaration?.kind === "tuple-array") {
      card.fields = declaration.elements!.map((element) => ({ name: element.name, type: element.kind }));
    }
    // The default of a required parameter is only a placeholder in the template.
    const defaultValue = declaration === null || parameter.required ? undefined : defaultOf(declaration);
    if (defaultValue !== undefined) card.default = defaultValue;
    if (parameter.description) card.description = parameter.description;
    return card;
  });
}

/** One line naming every parameter with its type, for error messages. */
function summarizeParameters(content: string, parameters: SkillParameter[]): string {
  return describeParameters(content, parameters)
    .map((card) => {
      const type = card.fields
        ? `rows of {${card.fields.map((field) => `${field.name}: ${field.type}`).join(", ")}}`
        : card.type;
      const state = card.required
        ? "required"
        : card.default === undefined ? "optional" : `optional, default ${JSON.stringify(card.default)}`;
      return `${card.name} (${type}, ${state})`;
    })
    .join("; ");
}

function declarationType(declaration: Declaration): string {
  if (declaration.kind === "tuple-array") return "rows";
  if (declaration.kind === "scalar-array") return `${declaration.itemKind}[]`;
  return declaration.kind;
}

/** The template default as a JSON value, or undefined when it says "not set". */
function defaultOf(declaration: Declaration): ParameterCard["default"] | undefined {
  if (declaration.kind === "tuple-array") return undefined;
  if (declaration.kind === "scalar-array") {
    const items = splitLiterals(declaration.initializer.match(SCALAR_ARRAY)![2], declaration.itemKind!);
    const values = items!.map((item) => literalValue(item, declaration.itemKind!));
    return values.length === 0 || values.some((value) => value === undefined)
      ? undefined
      : (values as Array<string | number | boolean>);
  }
  const value = literalValue(declaration.initializer, declaration.kind);
  // An empty text or NaN is the template's way of saying that nothing is set.
  return value === "" ? undefined : value;
}

function literalValue(literal: string, kind: ScalarKind): string | number | boolean | undefined {
  switch (kind) {
    case "string":
      try {
        // A C# regular string literal without unusual escapes is also valid JSON.
        return JSON.parse(literal) as string;
      } catch {
        return literal.slice(1, -1);
      }
    case "bool":
      return literal === "true";
    default: {
      const value = Number(literal);
      return Number.isFinite(value) ? value : undefined;
    }
  }
}

/** The literals of a plain list initialiser, or null when anything else is in it. */
function splitLiterals(text: string, kind: ScalarKind): string[] | null {
  const items: string[] = [];
  let rest = text.trim();
  while (rest.length > 0) {
    let literal: string;
    if (kind === "string") {
      const match = rest.match(STRING_LITERAL);
      if (!match) return null;
      literal = match[0];
    } else {
      literal = rest.split(",")[0].trim();
      const literalKind = inferScalarKind(literal);
      const compatible = literalKind === kind
        || ((kind === "long" || kind === "double") && literalKind === "int");
      if (!compatible) return null;
    }
    items.push(literal);
    rest = rest.slice(rest.indexOf(literal) + literal.length).trim();
    if (rest.length === 0) break;
    if (!rest.startsWith(",")) return null;
    rest = rest.slice(1).trim();
    if (rest.length === 0) return null;
  }
  return items;
}

function findDeclaration(code: string, name: string): Declaration | null {
  const pattern = new RegExp(
    `^(var|(?:string|int|long|double|bool)(?:\\[\\])?)[ \\t]+${escapeRegExp(name)}[ \\t]*=[ \\t]*(.+?)[ \\t]*;[ \\t]*(?://.*)?$`,
    "gm"
  );
  const matches = [...code.matchAll(pattern)];
  if (matches.length !== 1) return null;

  const [line, declaredType, initializer] = matches[0];
  const start = matches[0].index! + line.indexOf(initializer, line.indexOf("=") + 1);
  const end = start + initializer.length;

  const list = initializer.match(SCALAR_ARRAY);
  if (list) {
    // "double[] name = new double[] { ... }" must name the same item type twice.
    const itemKind = list[1] as ScalarKind;
    if (declaredType !== "var" && declaredType !== `${itemKind}[]`) return null;
    if (splitLiterals(list[2], itemKind) === null) return null;
    return { kind: "scalar-array", itemKind, start, end, initializer };
  }
  if (declaredType.endsWith("[]")) return null;

  if (declaredType !== "var") {
    // The default must be a single literal of that type, so replacing it
    // cannot drop another declarator such as "double x = 0, y = 0".
    const kind = declaredType as ScalarKind;
    const literalKind = inferScalarKind(initializer);
    const compatible = literalKind === kind
      || (kind === "long" && literalKind === "int")
      || (kind === "double" && literalKind === "int");
    return compatible ? { kind, start, end, initializer } : null;
  }

  const tuple = initializer.match(TUPLE_ARRAY);
  if (tuple) {
    const elements = tuple[1].split(",").map((element) => {
      const [type, elementName, ...rest] = element.trim().split(/\s+/);
      return SCALAR_TYPES.has(type as ScalarKind) && elementName && rest.length === 0
        ? { kind: type as ScalarKind, name: elementName }
        : null;
    });
    if (elements.length < 2 || elements.some((element) => element === null)) return null;
    return { kind: "tuple-array", elements: elements as TupleElement[], start, end, initializer };
  }

  const kind = inferScalarKind(initializer);
  return kind === null ? null : { kind, start, end, initializer };
}

function inferScalarKind(initializer: string): ScalarKind | null {
  if (/^"(?:[^"\\\r\n]|\\.)*"$/.test(initializer)) return "string";
  if (/^-?\d+$/.test(initializer)) return "int";
  if (/^-?\d+\.\d+(?:[eE][-+]?\d+)?$/.test(initializer) || initializer === "double.NaN") return "double";
  if (initializer === "true" || initializer === "false") return "bool";
  return null;
}

function toLiteral(name: string, declaration: Declaration, value: unknown): string {
  if (declaration.kind === "scalar-array") {
    if (!Array.isArray(value)) {
      throw new SkillBindingError(`Parameter '${name}' must be an array of ${declaration.itemKind} values.`);
    }
    const items = value.map((item, index) => scalarLiteral(`${name}[${index}]`, declaration.itemKind!, item));
    return `new ${declaration.itemKind}[] { ${items.join(", ")} }`;
  }
  if (declaration.kind !== "tuple-array") return scalarLiteral(name, declaration.kind, value);

  const elements = declaration.elements!;
  if (!Array.isArray(value)) {
    throw new SkillBindingError(`Parameter '${name}' must be an array.`);
  }
  const rows = value.map((row, index) => {
    const label = `${name}[${index}]`;
    let fields: unknown[];
    if (Array.isArray(row)) {
      if (row.length !== elements.length) {
        throw new SkillBindingError(`Parameter '${label}' must have ${elements.length} values.`);
      }
      fields = row;
    } else if (row !== null && typeof row === "object") {
      const keys = Object.keys(row);
      const expected = elements.map((element) => element.name);
      if (keys.length !== expected.length || !expected.every((key) => keys.includes(key))) {
        throw new SkillBindingError(`Parameter '${label}' must have exactly the fields ${expected.join(", ")}.`);
      }
      fields = expected.map((key) => (row as Record<string, unknown>)[key]);
    } else {
      throw new SkillBindingError(`Parameter '${label}' must be an object or an array.`);
    }
    return `(${fields.map((field, position) =>
      scalarLiteral(`${label}.${elements[position].name}`, elements[position].kind, field)).join(", ")})`;
  });
  const elementList = elements.map((element) => `${element.kind} ${element.name}`).join(", ");
  return `new (${elementList})[] { ${rows.join(", ")} }`;
}

function scalarLiteral(name: string, kind: ScalarKind, value: unknown): string {
  switch (kind) {
    case "string":
      if (typeof value !== "string") throw new SkillBindingError(`Parameter '${name}' must be a string.`);
      return stringLiteral(value);
    case "bool":
      if (typeof value !== "boolean") throw new SkillBindingError(`Parameter '${name}' must be a boolean.`);
      return value ? "true" : "false";
    case "int":
      if (!Number.isInteger(value) || (value as number) < -2147483648 || (value as number) > 2147483647) {
        throw new SkillBindingError(`Parameter '${name}' must be a 32-bit integer.`);
      }
      return String(value);
    case "long":
      if (!Number.isSafeInteger(value)) {
        throw new SkillBindingError(`Parameter '${name}' must be a safe integer.`);
      }
      return `${value}L`;
    case "double":
      if (typeof value !== "number" || !Number.isFinite(value)) {
        throw new SkillBindingError(`Parameter '${name}' must be a finite number.`);
      }
      // The suffix keeps the variable a double even for whole numbers.
      return `${Object.is(value, -0) ? 0 : value}d`;
  }
}

/** A C# regular string literal that escapes everything outside printable ASCII. */
function stringLiteral(value: string): string {
  let literal = '"';
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    const character = value[index];
    if (character === '"' || character === "\\") literal += `\\${character}`;
    else if (code >= 0x20 && code <= 0x7e) literal += character;
    else literal += `\\u${code.toString(16).padStart(4, "0")}`;
  }
  return `${literal}"`;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
