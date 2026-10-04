// Unity's JsonUtility silently drops or zeroes anything it cannot map. These rules keep every
// payload loss-free for [Serializable] public-field DTOs:
// root is an object, no null/undefined, no arrays of arrays, no mixed-type arrays,
// keys are C# identifiers, numbers are finite.
export function unitySafetyErrors(value: unknown, path = "$"): string[] {
  const errors: string[] = [];
  if (path === "$" && (typeof value !== "object" || value === null || Array.isArray(value))) {
    return ["$: root must be a JSON object (JsonUtility cannot parse a top-level array or primitive)"];
  }
  walk(value, path, errors);
  return errors;
}

const IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*$/;

function kind(v: unknown): string {
  if (Array.isArray(v)) return "array";
  if (v === null) return "null";
  return typeof v;
}

function walk(value: unknown, path: string, errors: string[]) {
  if (value === null || value === undefined) {
    errors.push(`${path}: ${value === null ? "null" : "undefined"} is not allowed; use "" or -1`);
    return;
  }
  if (typeof value === "number" && !Number.isFinite(value)) {
    errors.push(`${path}: non-finite number`);
    return;
  }
  if (Array.isArray(value)) {
    const kinds = new Set(value.map(kind));
    if (kinds.has("array")) errors.push(`${path}: nested arrays are not supported by JsonUtility`);
    if (kinds.size > 1) errors.push(`${path}: mixed element types ${[...kinds].join(", ")}`);
    value.forEach((item, i) => walk(item, `${path}[${i}]`, errors));
    return;
  }
  if (typeof value === "object") {
    for (const [key, child] of Object.entries(value)) {
      if (!IDENTIFIER.test(key)) errors.push(`${path}.${key}: key is not a valid C# field name`);
      walk(child, `${path}.${key}`, errors);
    }
  }
}
