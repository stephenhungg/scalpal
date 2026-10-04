import { TOOL_VERBS } from "./open-body.js";
import { ANATOMY_BY_ID } from "./catalog/anatomy.js";
import { CASE_PLANS, CHECKLIST_LABELS, CONSIDERATION_NOTES, STEP_ROLES, fallbackPlan, presentationFor } from "./catalog/cases.js";
import { INSTRUMENTS_BY_ID } from "./catalog/instruments.js";
import { PROCEDURES_BY_ID } from "./catalog/procedures.js";
import { DISCLAIMER, buildBrief } from "./brief.js";
import type { FinchNodeError } from "./finchnode.js";
import type {
  Action,
  CaseConsideration,
  ChecklistOption,
  DataGap,
  FlagType,
  HealthRecord,
  PreopBrief,
  Procedure,
  SurgicalCase,
} from "./types.js";

export const routes = {
  index: (): Action => ({ id: "home", label: "Home", method: "GET", route: "/" }),
  patients: (): Action => ({ id: "choose_patient", label: "Choose another patient", method: "GET", route: "/patients" }),
  caseFor: (id: string, label = "Open case"): Action => ({ id: "open_case", label, method: "GET", route: `/patients/${id}/case` }),
  brief: (id: string): Action => ({ id: "view_brief", label: "View pre-op brief", method: "GET", route: `/patients/${id}/brief` }),
  preopCheck: (id: string): Action => ({ id: "submit_preop_check", label: "Submit pre-op safety check", method: "POST", route: `/patients/${id}/preop-check` }),
  procedure: (pid: string): Action => ({ id: "view_procedure", label: "View procedure steps", method: "GET", route: `/procedures/${pid}` }),
  connect: (scenarioId: string, label: string): Action => ({ id: "start_connect", label, method: "POST", route: `/connect/${scenarioId}` }),
  admit: (scenarioId: string): Action => ({ id: "admit_patient", label: "Admit through FinchNode Connect", method: "POST", route: `/admit/${scenarioId}` }),
  admission: (sessionId: string): Action => ({ id: "check_admission", label: "Check admission", method: "GET", route: `/admissions/${sessionId}` }),
};

const ALL_FLAG_TYPES = Object.keys(CHECKLIST_LABELS) as FlagType[];
const CHECKLIST_MIN = 6;
const MIN_DISTRACTORS = 2;

const emptyProcedure = (): Procedure => ({
  id: "",
  title: "",
  shortTitle: "",
  approach: "",
  summary: "",
  typicalMinutes: 0,
  structures: [],
  focusStructures: [],
  ports: [],
  steps: [],
  firstStep: "",
});

const emptyBrief = (patientId: string, now: Date): PreopBrief => ({
  patientId,
  synthetic: true,
  dataSource: "",
  consentStatus: "",
  consentReceipts: [],
  generatedAt: now.toISOString(),
  dataAsOf: "",
  patient: { name: "", age: -1, sex: "", displayLabel: "Unavailable patient" },
  flags: [],
  highlightStructures: [],
  activeMedicationCount: 0,
  chart: [],
  dataGaps: [],
  sources: [],
  say: "",
  disclaimer: DISCLAIMER,
});

// Stable pseudo-random order so distractors differ per patient but never between requests.
function seededOrder<T>(items: T[], seed: string): T[] {
  let h = 2166136261;
  for (const ch of seed) h = Math.imul(h ^ ch.charCodeAt(0), 16777619);
  return items
    .map((item, i) => ({ item, key: Math.imul(h ^ (i + 1), 2654435761) >>> 0 }))
    .sort((a, b) => a.key - b.key)
    .map((x) => x.item);
}

export function checklistFor(patientId: string, present: FlagType[]): ChecklistOption[] {
  const distractors = seededOrder(ALL_FLAG_TYPES.filter((t) => !present.includes(t)), patientId);
  const size = Math.max(CHECKLIST_MIN, present.length + MIN_DISTRACTORS);
  const chosen = [...present, ...distractors.slice(0, size - present.length)];
  return seededOrder(chosen, `${patientId}:order`).map((type) => ({ type, label: CHECKLIST_LABELS[type] }));
}

// planSubject lets a sandbox subject (u_...) use the authored plan of the demo patient its scenario mirrors.
export function buildCase(
  record: HealthRecord,
  scenarioId: string,
  now: Date = new Date(),
  planSubject = record.id,
  extraGaps: DataGap[] = [],
): SurgicalCase {
  const brief = buildBrief(record, now, extraGaps);
  const plan = CASE_PLANS[planSubject] ?? CASE_PLANS[record.id] ?? fallbackPlan(brief.patient.age);
  const procedure = PROCEDURES_BY_ID.get(plan.procedureId);
  if (!procedure) throw new Error(`Case plan for ${record.id} references missing procedure ${plan.procedureId}`);

  const roles = STEP_ROLES[procedure.id];
  const considerations: CaseConsideration[] = [];
  for (const flag of brief.flags) {
    for (const { role, note } of CONSIDERATION_NOTES[flag.type]) {
      const stepId = roles?.[role];
      if (stepId) considerations.push({ flagId: flag.id, stepId, note });
    }
  }

  const structureIds = [...new Set([...procedure.structures, ...brief.highlightStructures])];
  const instrumentIds = procedure.openBody ? Object.keys(TOOL_VERBS).filter(id => INSTRUMENTS_BY_ID.has(id)) : [...new Set(procedure.ports.flatMap((p) => p.instrumentIds).concat(procedure.steps.map((s) => s.instrumentId)))];
  const age = brief.patient.age;

  return {
    caseId: `case_${record.id}_${procedure.id}`,
    patientId: record.id,
    scenarioId,
    status: brief.dataGaps.length ? "needs_review" : "ready",
    statusReason: brief.dataGaps.length
      ? `The chart has ${brief.dataGaps.length} gap${brief.dataGaps.length === 1 ? "" : "s"} to resolve. Practice can proceed; the pre-op check covers them.`
      : "Chart reviewed. Ready for the pre-op safety check.",
    retryAfterSeconds: 0,
    patient: brief.patient,
    // Port positions are authored for an adult torso; children scale down. Registration still owns the final fit.
    bodyScale: age >= 0 && age < 18 ? Math.round((0.55 + age * 0.025) * 100) / 100 : 1,
    urgency: plan.urgency,
    indication: plan.indication,
    presentation: presentationFor(plan, brief),
    procedureId: procedure.id,
    procedure,
    brief,
    considerations,
    checklistOptions: checklistFor(record.id, brief.flags.map((f) => f.type)),
    instruments: instrumentIds.map((id) => INSTRUMENTS_BY_ID.get(id)).filter((i) => i != null),
    anatomy: structureIds.map((id) => ANATOMY_BY_ID.get(id)).filter((s) => s != null),
    actions: [routes.preopCheck(record.id), routes.brief(record.id), routes.procedure(procedure.id), routes.patients()],
    disclaimer: DISCLAIMER,
  };
}

// A case that cannot load still renders and still routes somewhere useful.
export function unavailableCase(patientId: string, scenarioId: string, error: FinchNodeError, now: Date = new Date()): SurgicalCase {
  const blocked = error.code === "consent_inactive" || error.status === 410 || error.status === 403;
  const retry = error.status === 429 || error.status >= 500;
  const status: SurgicalCase["status"] = blocked ? "blocked" : retry ? "retry" : "blocked";
  const reason = blocked
    ? "The patient revoked sharing with this app. Their chart cannot be read until they grant consent again."
    : retry
      ? `The health record is temporarily unavailable (${error.code}). Try again${error.retryAfterSeconds ? ` in ${error.retryAfterSeconds} seconds` : ""}.`
      : error.message;
  const brief = emptyBrief(patientId, now);
  brief.say = blocked
    ? "This patient revoked consent, so I can't open their chart. Pick another patient."
    : `I couldn't reach this patient's record right now. ${retry ? "Try again in a moment." : ""}`.trim();
  return {
    caseId: `case_${patientId}_unavailable`,
    patientId,
    scenarioId,
    status,
    statusReason: reason,
    retryAfterSeconds: error.retryAfterSeconds,
    patient: brief.patient,
    bodyScale: 1,
    urgency: "",
    indication: "",
    presentation: "",
    procedureId: "",
    procedure: emptyProcedure(),
    brief,
    considerations: [],
    checklistOptions: [],
    instruments: [],
    anatomy: [],
    actions: retry
      ? [routes.caseFor(patientId, "Try again"), routes.patients()]
      : [routes.patients(), routes.caseFor(patientId, "Check consent again")],
    disclaimer: DISCLAIMER,
  };
}

export interface PreopCheckResult {
  patientId: string;
  caseId: string;
  caught: ChecklistOption[];
  missed: ChecklistOption[];
  falseAlarms: ChecklistOption[];
  score: number;
  total: number;
  passed: boolean;
  feedback: string[];
  say: string;
  actions: Action[];
}

// scope "surgical" scores only risks pinned to procedure steps: exactly the risks the office hands to the
// OR Time-Out (encounter-carryover.ts). The default "chart" scope scores every flagged chart risk.
export function scorePreopCheck(c: SurgicalCase, selected: string[], scope: "chart" | "surgical" = "chart"): PreopCheckResult {
  const pinned = new Set(c.considerations.map((item) => item.flagId));
  const present = new Set(c.brief.flags.filter((f) => scope === "chart" || pinned.has(f.id)).map((f) => f.type));
  const offered = new Map(c.checklistOptions.map((o) => [o.type, o]));
  const picked = new Set(selected.filter((s) => offered.has(s as FlagType)) as FlagType[]);
  const option = (t: FlagType) => offered.get(t) ?? { type: t, label: CHECKLIST_LABELS[t] };

  const caught = [...present].filter((t) => picked.has(t)).map(option);
  const missed = [...present].filter((t) => !picked.has(t)).map(option);
  const falseAlarms = [...picked].filter((t) => !present.has(t)).map(option);
  const feedback = [
    ...missed.map((m) => {
      const flag = c.brief.flags.find((f) => f.type === m.type);
      return `Missed: ${m.label}. ${flag?.detail ?? ""}`.trim();
    }),
    ...falseAlarms.map((f) => `Not in this chart: ${f.label}.`),
  ];
  const total = present.size;
  const passed = missed.length === 0 && falseAlarms.length === 0;
  const say = total === 0
    ? "This chart has no flagged risks. Good to proceed."
    : passed
      ? `You caught all ${total} risks. Let's scrub in.`
      : `You caught ${caught.length} of ${total}.${missed.length ? ` You missed ${missed.map((m) => m.label.toLowerCase()).join(", ")}.` : ""}${falseAlarms.length ? ` ${falseAlarms.length === 1 ? "One pick isn't" : "Some picks aren't"} in this chart.` : ""}`;

  return {
    patientId: c.patientId,
    caseId: c.caseId,
    caught,
    missed,
    falseAlarms,
    score: caught.length,
    total,
    passed,
    feedback,
    say,
    actions: [routes.procedure(c.procedureId), routes.caseFor(c.patientId, "Back to case"), routes.patients()],
  };
}
