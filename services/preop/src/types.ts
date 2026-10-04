import type { OpenBodyCase } from "./open-body.js";
// FinchNode input: the subset of the normalized health record (schemaVersion 2) that Scalpal reads.
// Fields are optional because synthetic scenarios deliberately omit or blank them.

export interface Coding {
  system?: string;
  code?: string;
  display?: string;
}

export interface RecordEntry {
  id?: string;
  name?: string;
  status?: string | null;
  codes?: Coding[];
  source?: string;
  sourceName?: string;
}

export interface Medication extends RecordEntry {
  dosage?: string | null;
  frequency?: string | null;
  reason?: string | null;
}

export interface Lab extends RecordEntry {
  value?: string | null;
  unit?: string | null;
  date?: string | null;
  referenceRange?: string | null;
  interpretation?: string | null;
}

export interface Allergy extends RecordEntry {
  substance?: string;
  reaction?: string | null;
  severity?: string | null;
}

export interface Demographics {
  name?: string;
  birthDate?: string;
  gender?: string;
}

export interface MetaWarning {
  code: string;
  message: string;
  source?: string;
  category?: string;
  retryable?: boolean;
}

export interface HealthRecord {
  id: string;
  synthetic?: boolean;
  environment?: string;
  categories?: string[];
  consent?: { status?: string; receiptIds?: string[]; receipts?: { id: string; categories?: string[] }[] };
  sources?: { system: string; organization: string; lastSyncedAt?: string | null }[];
  data: {
    demographics?: Demographics;
    medications?: Medication[];
    conditions?: RecordEntry[];
    labs?: Lab[];
    allergies?: Allergy[];
  };
  meta?: {
    dataAsOf?: string;
    syncStatus?: string;
    availableCategories?: string[];
    missingCategories?: string[];
    warnings?: MetaWarning[];
  };
}

export interface Scenario {
  id: string;
  kind: string;
  title: string;
  summary: string;
  subject: string | null;
}

// Unity-facing output. Every type below must stay JsonUtility-safe:
// no nulls (use "" or -1), no dictionaries, no nested arrays, no top-level arrays,
// public-field-friendly names. test/unity-contract.test.ts enforces this against the C# DTOs.

export type Severity = "high" | "moderate" | "info";

export type FlagType =
  | "bleeding"
  | "allergy"
  | "latex"
  | "contrast"
  | "renal"
  | "metformin_renal"
  | "diabetes"
  | "anemia"
  | "cardiac"
  | "airway"
  | "polypharmacy"
  | "pediatric"
  | "elderly"
  | "incomplete_chart";

export interface Action {
  id: string;
  label: string;
  method: "GET" | "POST";
  route: string;
}

export interface Evidence {
  kind: "medication" | "lab" | "condition" | "allergy" | "demographic";
  label: string;
  value: string;
  date: string;
  source: string;
}

export interface Flag {
  id: string;
  type: FlagType;
  severity: Severity;
  title: string;
  spoken: string;
  detail: string;
  structures: string[];
  evidence: Evidence[];
}

export interface DataGap {
  code: string;
  message: string;
}

export interface PatientSummary {
  name: string;
  age: number;
  sex: string;
  displayLabel: string;
}

export interface ChartLine {
  section: string;
  text: string;
  flagged: boolean;
}

export interface PreopBrief {
  patientId: string;
  synthetic: boolean;
  // "demo" (keyless public API) or "sandbox" (real Connect session with a consent receipt).
  dataSource: string;
  consentStatus: string;
  consentReceipts: string[];
  generatedAt: string;
  dataAsOf: string;
  patient: PatientSummary;
  flags: Flag[];
  highlightStructures: string[];
  activeMedicationCount: number;
  chart: ChartLine[];
  dataGaps: DataGap[];
  sources: string[];
  say: string;
  disclaimer: string;
}

export interface Vec3 {
  x: number;
  y: number;
  z: number;
}

export interface AnatomyStructure {
  id: string;
  unityName: string;
  displayName: string;
  system: string;
  region: string;
}

export interface Instrument {
  id: string;
  unityPrefab: string;
  displayName: string;
  kind: string;
}

export interface Port {
  id: string;
  label: string;
  sizeMm: number;
  position: Vec3;
  instrumentIds: string[];
}

export type StepAction =
  | "place_port"
  | "retract"
  | "dissect"
  | "identify"
  | "clip"
  | "divide"
  | "seal"
  | "staple"
  | "extract"
  | "inspect"
  | "anastomose"
  | "close";

export interface SuccessCheck {
  type: "touch_target" | "identify_targets" | "apply_count" | "place_ports" | "confirm" | "body_predicate";
  targets: string[];
  count: number;
}

export interface StepMistake {
  id: string;
  trigger: "touch_structure" | "wrong_identification" | "wrong_order" | "excess_energy" | "guardrail";
  structure: string;
  severity: Severity;
  feedback: string;
}

export interface ProcedureStep {
  id: string;
  title: string;
  instruction: string;
  action: StepAction;
  instrumentId: string;
  targets: string[];
  portIds: string[];
  check: SuccessCheck;
  mistakes: StepMistake[];
  hints: string[];
  next: string;
}

export interface Procedure {
  openBody?: OpenBodyCase;
  id: string;
  title: string;
  shortTitle: string;
  approach: string;
  summary: string;
  typicalMinutes: number;
  structures: string[];
  focusStructures: string[];
  ports: Port[];
  steps: ProcedureStep[];
  firstStep: string;
}

export interface CaseConsideration {
  flagId: string;
  stepId: string;
  note: string;
}

export interface ChecklistOption {
  type: FlagType;
  label: string;
}

export type CaseStatus = "ready" | "needs_review" | "blocked" | "retry";

export type { SandboxSession } from "./finchnode.js";

export interface AdmissionStatus {
  sessionId: string;
  scenarioId: string;
  // FinchNode's hosted consent page. Open it on the laptop, pick the scenario's health system, approve.
  connectUrl: string;
  state: "connecting" | "completed" | "failed" | "unavailable";
  sessionStatus: string;
  syncStatus: string;
  patientId: string;
  organization: string;
  say: string;
  actions: Action[];
}

export interface SurgicalCase {
  caseId: string;
  patientId: string;
  scenarioId: string;
  status: CaseStatus;
  statusReason: string;
  retryAfterSeconds: number;
  patient: PatientSummary;
  bodyScale: number;
  urgency: "elective" | "urgent" | "emergency" | "";
  indication: string;
  presentation: string;
  procedureId: string;
  procedure: Procedure;
  brief: PreopBrief;
  considerations: CaseConsideration[];
  checklistOptions: ChecklistOption[];
  instruments: Instrument[];
  anatomy: AnatomyStructure[];
  actions: Action[];
  disclaimer: string;
}
