import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { createHash } from "node:crypto";
import { describe, expect, it } from "vitest";
import { ANATOMY } from "../src/catalog/anatomy.js";
import { PROCEDURES } from "../src/catalog/procedures.js";
import { INSTRUMENTS } from "../src/catalog/instruments.js";

const quest = resolve(import.meta.dirname, "../../../apps/quest");
const atlas = JSON.parse(readFileSync(resolve(quest, "Assets/Scalpal/Anatomy/Resources/anatomy-atlas.json"), "utf8"));
const bundle = JSON.parse(readFileSync(resolve(quest, "Assets/Scalpal/Exercises/Resources/scalpal_bundle.json"), "utf8"));
type Part = { stableId: string; catalogId: string; objectName: string; system: string; triangles: number; provenance?: string };
const parts = atlas.parts as Part[];

describe("anatomy and authored exercise integration", () => {
  it("ships the same catalogs the live service uses", () => {
    expect(bundle.procedures).toEqual(PROCEDURES);
    expect(bundle.anatomy).toEqual(ANATOMY);
    expect(bundle.instruments).toEqual(INSTRUMENTS);
  });

  it.each(PROCEDURES)("provides all interactive and mistake targets for $id", procedure => {
    const required = new Set<string>();
    for (const step of procedure.steps) {
      if (["touch_target", "identify_targets", "apply_count"].includes(step.check.type)) {
        step.targets.forEach(id => required.add(id));
        step.check.targets.forEach(id => required.add(id));
      }
      step.mistakes.forEach(mistake => { if (mistake.structure) required.add(mistake.structure); });
    }
    for (const id of required) {
      const matches = parts.filter(part => part.stableId === id);
      expect(matches, `${procedure.id}: ${id}`).toHaveLength(1);
      expect(matches[0]!.catalogId).toBe(id); // Prefab builder gives catalog parts colliders.
      expect(matches[0]!.objectName).toBe(`anat_${id}`);
      expect(matches[0]!.triangles).toBeGreaterThan(0);
    }
    const coverage = atlas.exerciseCoverage.find((entry: { procedureId: string }) => entry.procedureId === procedure.id);
    expect(coverage.interactionTargets).toEqual([...required].sort());
    expect(coverage.missingTargets).toEqual([]);
  });

  it("ships the supplemental mesh matching its manifest checksum", () => {
    const system = atlas.systems.find((s: { id: string }) => s.id === "exercise-targets");
    expect(system).toBeDefined();
    const bytes = readFileSync(resolve(quest, system.assetPath));
    expect(createHash("sha256").update(bytes).digest("hex")).toBe(system.sha256);
    const authored = parts.filter(part => part.system === system.id);
    expect(authored).toHaveLength(11);
    expect(authored.every(part => part.provenance)).toBe(true);
  });
});
