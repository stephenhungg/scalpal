import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { MANIFEST_PATH, UNITY_EXERCISES_DIR, generateIds, generateManifest } from "../scripts/gen-unity.js";
import { createApp } from "../src/app.js";
import { unitySafetyErrors } from "../src/unity-safe.js";
import { NOW, fixtureClient } from "./helpers.js";

// Parses the C# DTO file into { className: { field: type } } so payloads can be checked against it.
type Schema = Map<string, Map<string, string>>;

function parseDtos(source: string): Schema {
  const schema: Schema = new Map();
  const classRe = /public (?:class|struct) (\w+)\s*\{([^}]*)\}/g;
  for (const [, name, body] of source.matchAll(classRe)) {
    const fields = new Map<string, string>();
    for (const [, type, field] of (body ?? "").matchAll(/public ([\w[\]]+) (\w+);/g)) fields.set(field!, type!);
    schema.set(name!, fields);
  }
  return schema;
}

const PRIMITIVES: Record<string, (v: unknown) => boolean> = {
  string: (v) => typeof v === "string",
  int: (v) => Number.isInteger(v),
  float: (v) => typeof v === "number",
  bool: (v) => typeof v === "boolean",
};

// Walks a payload against a DTO class: every JSON key needs a field of a compatible type,
// otherwise JsonUtility would silently drop it. Records which fields were seen.
function check(value: unknown, type: string, schema: Schema, path: string, errors: string[], seen: Map<string, Set<string>>) {
  if (type.endsWith("[]")) {
    if (!Array.isArray(value)) return void errors.push(`${path}: expected array for ${type}`);
    value.forEach((v, i) => check(v, type.slice(0, -2), schema, `${path}[${i}]`, errors, seen));
    return;
  }
  const primitive = PRIMITIVES[type];
  if (primitive) {
    if (!primitive(value)) errors.push(`${path}: ${JSON.stringify(value)} is not a C# ${type}`);
    return;
  }
  const fields = schema.get(type);
  if (!fields) return void errors.push(`${path}: no C# class ${type}`);
  if (typeof value !== "object" || value === null || Array.isArray(value)) return void errors.push(`${path}: expected object for ${type}`);
  const used = seen.get(type) ?? new Set<string>();
  seen.set(type, used);
  for (const [key, child] of Object.entries(value)) {
    const fieldType = fields.get(key);
    if (!fieldType) {
      errors.push(`${path}.${key}: ${type} has no field "${key}", JsonUtility would drop it`);
      continue;
    }
    used.add(key);
    check(child, fieldType, schema, `${path}.${key}`, errors, seen);
  }
}

const dtoSource = readFileSync(join(UNITY_EXERCISES_DIR, "Data/ScalpalCaseData.cs"), "utf8");
const schema = parseDtos(dtoSource);
const app = createApp({ client: fixtureClient(), now: () => NOW });

const ENDPOINTS: { method: string; route: string; type: string; body?: unknown }[] = [
  { method: "GET", route: "/", type: "ServiceIndex" },
  { method: "GET", route: "/health", type: "HealthStatus" },
  { method: "GET", route: "/patients", type: "PatientList" },
  ...[
    "patient-demo-polypharmacy", "patient-demo-001", "patient-demo-pediatric-asthma", "patient-demo-multi-source",
    "patient-demo-messy-coding", "patient-demo-sparse", "patient-demo-consent-partial", "patient-demo-source-unavailable",
    "patient-demo-consent-revoked", "patient-demo-rate-limited",
  ].flatMap((id) => [
    { method: "GET", route: `/patients/${id}/case`, type: "SurgicalCase" },
    { method: "POST", route: `/patients/${id}/preop-check`, type: "PreopCheckResult", body: { selected: ["bleeding", "latex"] } },
  ]),
  { method: "GET", route: "/patients/patient-demo-polypharmacy/brief", type: "PreopBrief" },
  { method: "GET", route: "/procedures", type: "ProcedureList" },
  { method: "GET", route: "/procedures/lap_sigmoid_colectomy", type: "Procedure" },
  { method: "GET", route: "/anatomy", type: "AnatomyList" },
  { method: "GET", route: "/instruments", type: "InstrumentList" },
  { method: "POST", route: "/connect/connect-failed", type: "ConnectResult" },
  { method: "GET", route: "/unity/bundle", type: "ScalpalBundle" },
  { method: "GET", route: "/patients/patient-demo-nobody/case", type: "ErrorResponse" },
  { method: "POST", route: "/patients/patient-demo-consent-revoked/preop-check", type: "ErrorResponse", body: { selected: [] } },
];

describe("Unity DTO contract", () => {
  const seen = new Map<string, Set<string>>();

  for (const ep of ENDPOINTS) {
    it(`${ep.method} ${ep.route} deserializes into ${ep.type} without loss`, async () => {
      const res = await app.request(ep.route, {
        method: ep.method,
        headers: ep.body ? { "Content-Type": "application/json" } : {},
        body: ep.body ? JSON.stringify(ep.body) : undefined,
      });
      const json = await res.json();
      // The revoked-consent preop check is an error, the rest succeed; pick the DTO by shape.
      const type = (json as { error?: unknown }).error ? "ErrorResponse" : ep.type;
      expect(unitySafetyErrors(json)).toEqual([]);
      const errors: string[] = [];
      check(json, type, schema, "$", errors, seen);
      expect(errors).toEqual([]);
    });
  }

  it("has no C# fields that no payload ever fills", () => {
    const unused: string[] = [];
    for (const [cls, fields] of schema) {
      if (["PreopCheckRequest"].includes(cls)) continue; // request body, sent by Unity
      for (const field of fields.keys()) if (!seen.get(cls)?.has(field)) unused.push(`${cls}.${field}`);
    }
    expect(unused).toEqual([]);
  });

  it("generated ids are current with the catalogs", () => {
    const file = readFileSync(join(UNITY_EXERCISES_DIR, "Generated/ScalpalIds.cs"), "utf8");
    expect(file, "run `npm run gen:unity`").toBe(generateIds());
  });

  it("the asset manifest is current with the catalogs", () => {
    expect(readFileSync(MANIFEST_PATH, "utf8"), "run `npm run gen:unity`").toBe(generateManifest());
  });

  it("the packaged offline bundle is renderable", () => {
    const path = join(UNITY_EXERCISES_DIR, "Resources/scalpal_bundle.json");
    expect(existsSync(path), "run `npm run export:unity`").toBe(true);
    const bundle = JSON.parse(readFileSync(path, "utf8"));
    expect(unitySafetyErrors(bundle)).toEqual([]);
    const errors: string[] = [];
    check(bundle, "ScalpalBundle", schema, "$", errors, new Map());
    expect(errors).toEqual([]);
  });
});
