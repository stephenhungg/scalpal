import type { HistoryTopic, TestId } from "./catalog/encounters.js";
import type { FlagType, Severity, SurgicalCase } from "./types.js";

// These chips report whether a chart risk's relevant evidence was covered in the office. They do
// not assert that the risk was resolved, or award additional points to the encounter score.
export interface CarryoverItem {
  flagId: string;
  type: FlagType;
  severity: Severity;
  label: string;
  detail: string;
  status: "found" | "missed" | "chart_only";
  historyTopics: string[];
  testIds: string[];
  stepIds: string[];
}

type EvidenceGroup = { history?: HistoryTopic[]; tests?: TestId[] };
// Each group must have at least one covered source. A compound medication/renal risk needs both
// medication reconciliation and renal review. Age has no office tool and must not become a miss.
const COVERAGE: Record<FlagType, EvidenceGroup[]> = {
  bleeding: [{ history: ["medications"] }],
  allergy: [{ history: ["allergies"] }],
  latex: [{ history: ["allergies"] }],
  contrast: [{ history: ["allergies"] }],
  renal: [{ history: ["past_medical"], tests: ["bmp"] }],
  metformin_renal: [{ history: ["medications"] }, { history: ["past_medical"], tests: ["bmp"] }],
  diabetes: [{ history: ["past_medical", "medications"], tests: ["bmp"] }],
  anemia: [{ history: ["past_medical"], tests: ["cbc"] }],
  cardiac: [{ history: ["past_medical"] }],
  airway: [{ history: ["past_medical", "recent_illness"] }],
  polypharmacy: [{ history: ["medications"] }],
  pediatric: [],
  elderly: [],
  incomplete_chart: [{ history: ["past_medical"] }, { history: ["medications"] }, { history: ["allergies"] }],
};

export function buildCarryoverItems(kase: SurgicalCase, history: HistoryTopic[], tests: TestId[]): CarryoverItem[] {
  return kase.brief.flags.flatMap((flag) => {
    const stepIds = [...new Set(kase.considerations.filter((item) => item.flagId === flag.id).map((item) => item.stepId))];
    if (!stepIds.length) return [];
    const groups = COVERAGE[flag.type];
    const found = groups.length > 0 && groups.every((group) =>
      group.history?.some((topic) => history.includes(topic)) || group.tests?.some((test) => tests.includes(test)));
    return [{
      flagId: flag.id,
      type: flag.type,
      severity: flag.severity,
      label: flag.title,
      detail: flag.detail,
      status: groups.length === 0 ? "chart_only" as const : found ? "found" as const : "missed" as const,
      historyTopics: [...new Set(groups.flatMap((group) => group.history ?? []))].filter((topic) => history.includes(topic)),
      testIds: [...new Set(groups.flatMap((group) => group.tests ?? []))].filter((test) => tests.includes(test)),
      stepIds,
    }];
  });
}

// Authored teaching site, not a patient-specific incision plan. Unknown procedures fail visibly.
export function procedureSite(procedureId: string): string {
  return ({
    lap_appendectomy: "Abdomen — appendix / right lower quadrant",
    lap_cholecystectomy: "Abdomen — gallbladder / right upper quadrant",
    lap_sigmoid_colectomy: "Abdomen — sigmoid colon / left lower quadrant",
  } as Record<string, string>)[procedureId] ?? "Site unavailable — confirm before proceeding";
}
