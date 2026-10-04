import { ANATOMY_BY_ID } from "../src/catalog/anatomy.js";
import { STEP_ROLES } from "../src/catalog/cases.js";
import { INSTRUMENTS_BY_ID } from "../src/catalog/instruments.js";
import { PROCEDURES_BY_ID } from "../src/catalog/procedures.js";
import type { SurgicalCase } from "../src/types.js";
import { routes } from "../src/case-builder.js";

export const ADVANCED_LAP_CASE_ID = "case_patient-demo-multi-source_lap_appendectomy";

// Kept only in the packaged Resources bundle for the explicit advanced port scene.
// Patient cards and live encounter routing continue to select open appendectomy.
export function withOfflineAdvancedCase<T extends { cases: SurgicalCase[] }>(bundle: T): T {
  if (bundle.cases.some(kase => kase.caseId === ADVANCED_LAP_CASE_ID)) return bundle;
  const source = bundle.cases.find(kase => kase.patientId === "patient-demo-multi-source"
    && kase.procedureId === "open_appendectomy");
  if (!source) throw new Error("offline advanced case requires the multi-source open appendectomy case");
  const procedure = PROCEDURES_BY_ID.get("lap_appendectomy")!;
  const openRoles = STEP_ROLES.open_appendectomy!, lapRoles = STEP_ROLES.lap_appendectomy!;
  const roles = Object.keys(openRoles) as (keyof typeof openRoles)[];
  const stepIds = new Map(roles.map(role => [openRoles[role], lapRoles[role]]));
  const structureIds = [...new Set([...procedure.structures, ...source.brief.highlightStructures])];
  const instrumentIds = [...new Set([
    ...procedure.ports.flatMap(port => port.instrumentIds),
    ...procedure.steps.map(step => step.instrumentId),
  ])];
  const advanced: SurgicalCase = structuredClone({
    ...source,
    caseId: ADVANCED_LAP_CASE_ID,
    procedureId: procedure.id,
    procedure,
    presentation: `Advanced offline laparoscopic variant. ${source.presentation}`,
    actions: [routes.procedure(procedure.id), routes.patients()],
    considerations: source.considerations.map(item => ({
      ...item, stepId: stepIds.get(item.stepId) ?? item.stepId,
    })),
    anatomy: structureIds.map(id => {
      const structure = ANATOMY_BY_ID.get(id);
      if (!structure) throw new Error(`advanced offline case references missing anatomy ${id}`);
      return structure;
    }),
    instruments: instrumentIds.map(id => {
      const instrument = INSTRUMENTS_BY_ID.get(id);
      if (!instrument) throw new Error(`advanced offline case references missing instrument ${id}`);
      return instrument;
    }),
  });
  return { ...bundle, cases: [...bundle.cases, advanced] };
}
