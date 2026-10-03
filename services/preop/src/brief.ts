import type {
  Allergy,
  ChartLine,
  DataGap,
  Evidence,
  Flag,
  FlagType,
  HealthRecord,
  Lab,
  Medication,
  PatientSummary,
  PreopBrief,
  RecordEntry,
  Severity,
} from "./types.js";

// Deterministic chart rules. No model decides what counts as a risk; every flag cites the record entries behind it.

export const DISCLAIMER =
  "Synthetic FinchNode demo data. Flags and cases are illustrative teaching material, not clinical guidance.";

const SEVERITY_RANK: Record<Severity, number> = { high: 0, moderate: 1, info: 2 };

const ANTICOAGULANTS = ["apixaban", "rivaroxaban", "edoxaban", "dabigatran", "warfarin", "enoxaparin", "heparin", "fondaparinux"];
const ANTIPLATELETS = ["aspirin", "clopidogrel", "prasugrel", "ticagrelor", "dipyridamole", "cilostazol"];
const GLUCOSE_LOWERING = ["metformin", "insulin", "glipizide", "glyburide", "glimepiride", "empagliflozin", "dapagliflozin", "canagliflozin", "ertugliflozin", "semaglutide", "liraglutide", "dulaglutide", "tirzepatide"];
const SGLT2 = ["empagliflozin", "dapagliflozin", "canagliflozin", "ertugliflozin"];

const LAB_KINDS = {
  egfr: { loinc: ["98979-8", "33914-3", "62238-1", "48642-3", "48643-1", "69405-9", "50044-7", "88293-6", "88294-4"], name: /glomerular filtration|\begfr\b/i },
  creatinine: { loinc: ["2160-0", "38483-4"], name: /^creatinine\b/i },
  hemoglobin: { loinc: ["718-7", "20509-6"], name: /^hemoglobin(?!\s*a1c)/i },
  a1c: { loinc: ["4548-4", "17856-6"], name: /a1c/i },
  platelets: { loinc: ["777-3", "26515-7"], name: /platelet/i },
} as const;
type LabKind = keyof typeof LAB_KINDS;

const CONDITIONS = {
  ckd: /chronic kidney disease|\bckd\b/i,
  afib: /atrial fibrillation/i,
  heartFailure: /heart failure/i,
  coronary: /coronary|myocardial infarction/i,
  diabetes: /diabetes/i,
  asthma: /asthma/i,
  copd: /chronic obstructive|\bcopd\b/i,
  sleepApnea: /sleep apnea/i,
  anemia: /anemia/i,
} as const;

const RELEVANT_CATEGORIES = ["medications", "allergies", "conditions", "labs"] as const;

export interface Range {
  low: number | null;
  high: number | null;
}

// FinchNode ranges come as "0.6 - 1.2 mg/dL", ">= 60 mL/min", "Synthetic reference: 70<en dash>99 mg/dL", "below 5.7%".
export function parseRange(text: string | null | undefined): Range {
  const out: Range = { low: null, high: null };
  if (!text) return out;
  const t = text.replace(/^synthetic reference:\s*/i, "");
  const between = t.match(/(\d+(?:\.\d+)?)\s*[-\u2013\u2014]\s*(\d+(?:\.\d+)?)/);
  if (between) return { low: Number(between[1]), high: Number(between[2]) };
  const lower = t.match(/(?:>=?|≥|above|at least)\s*(\d+(?:\.\d+)?)/i);
  if (lower) out.low = Number(lower[1]);
  const upper = t.match(/(?:<=?|≤|below|under)\s*(\d+(?:\.\d+)?)/i);
  if (upper) out.high = Number(upper[1]);
  return out;
}

export function numericValue(lab: Lab): number | null {
  if (lab.value == null) return null;
  const n = Number.parseFloat(String(lab.value));
  return Number.isFinite(n) ? n : null;
}

export function labDirection(lab: Lab): "high" | "low" | "normal" | "unknown" {
  const flag = (lab.interpretation ?? "").toUpperCase();
  if (flag === "H" || flag === "HH" || flag === "HU") return "high";
  if (flag === "L" || flag === "LL" || flag === "LU") return "low";
  if (flag === "N") return "normal";
  const value = numericValue(lab);
  if (value == null) return "unknown";
  const range = parseRange(lab.referenceRange);
  if (range.low == null && range.high == null) return "unknown";
  if (range.low != null && value < range.low) return "low";
  if (range.high != null && value > range.high) return "high";
  return "normal";
}

const hasCode = (entry: RecordEntry, codes: readonly string[]) => (entry.codes ?? []).some((c) => c.code != null && codes.includes(c.code));

const entryText = (entry: RecordEntry) =>
  [entry.name ?? "", ...(entry.codes ?? []).map((c) => c.display ?? "")].join(" ").toLowerCase();

const isLive = (status: string | null | undefined) => status == null || ["active", "unknown", "on-hold", "intended"].includes(status);

function labKind(lab: Lab): LabKind | null {
  for (const [kind, def] of Object.entries(LAB_KINDS) as [LabKind, (typeof LAB_KINDS)[LabKind]][]) {
    if (hasCode(lab, def.loinc)) return kind;
  }
  for (const [kind, def] of Object.entries(LAB_KINDS) as [LabKind, (typeof LAB_KINDS)[LabKind]][]) {
    if (def.name.test(lab.name ?? "")) return kind;
  }
  return null;
}

function latestLabs(labs: Lab[]): Map<LabKind, Lab> {
  const out = new Map<LabKind, Lab>();
  for (const lab of labs) {
    const kind = labKind(lab);
    if (!kind) continue;
    const prev = out.get(kind);
    if (!prev || (lab.date ?? "") > (prev.date ?? "")) out.set(kind, lab);
  }
  return out;
}

function dedupe<T extends RecordEntry>(entries: T[]): T[] {
  const seen = new Set<string>();
  return entries.filter((e) => {
    const code = (e.codes ?? []).find((c) => c.code && c.system && !c.system.includes("category"))?.code;
    const key = code ?? (e.name ?? "").trim().toLowerCase();
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

const shortLabName = (lab: Lab, kind: LabKind) =>
  ({ egfr: "eGFR", creatinine: "Creatinine", hemoglobin: "Hemoglobin", a1c: "Hemoglobin A1c", platelets: "Platelets" })[kind] ??
  lab.name ??
  "Lab";

const labEvidence = (lab: Lab, kind: LabKind): Evidence => ({
  kind: "lab",
  label: shortLabName(lab, kind),
  value: lab.value == null ? "no value" : `${lab.value}${lab.unit ? ` ${lab.unit}` : ""}`,
  date: (lab.date ?? "").slice(0, 10),
  source: lab.sourceName ?? lab.source ?? "",
});

const medEvidence = (med: Medication): Evidence => ({
  kind: "medication",
  label: med.name ?? "Unnamed medication",
  value: med.status ?? "unknown status",
  date: "",
  source: med.sourceName ?? med.source ?? "",
});

const conditionEvidence = (c: RecordEntry): Evidence => ({
  kind: "condition",
  label: c.name ?? "Unnamed condition",
  value: c.status ?? "unknown status",
  date: "",
  source: c.sourceName ?? c.source ?? "",
});

const allergyName = (a: Allergy) => a.substance ?? a.name ?? "Unnamed allergy";

// "Allergy to penicillin" and "Amoxicillin allergy" both become the bare substance.
const allergen = (a: Allergy) => allergyName(a).replace(/^allergy to\s+/i, "").replace(/\s+allergy$/i, "");

const allergyEvidence = (a: Allergy): Evidence => ({
  kind: "allergy",
  label: allergyName(a),
  value: a.severity ? `${a.severity} severity` : "severity not recorded",
  date: "",
  source: a.sourceName ?? a.source ?? "",
});

const medClass = (med: Medication, names: readonly string[]) => {
  const text = entryText(med);
  return names.find((n) => text.includes(n)) ?? null;
};

const listPhrase = (items: string[]) =>
  items.length <= 2 ? items.join(" and ") : `${items.slice(0, -1).join(", ")}, and ${items[items.length - 1]}`;

export function ageAt(birthDate: string | undefined, asOf: string): number {
  if (!birthDate) return -1;
  const b = new Date(birthDate);
  const now = new Date(asOf);
  if (Number.isNaN(b.getTime()) || Number.isNaN(now.getTime())) return -1;
  let age = now.getUTCFullYear() - b.getUTCFullYear();
  const beforeBirthday =
    now.getUTCMonth() < b.getUTCMonth() || (now.getUTCMonth() === b.getUTCMonth() && now.getUTCDate() < b.getUTCDate());
  if (beforeBirthday) age -= 1;
  return age;
}

export function summarizePatient(record: HealthRecord, asOf: string): PatientSummary {
  const d = record.data.demographics;
  const name = d?.name ?? "";
  const age = ageAt(d?.birthDate, asOf);
  const sex = d?.gender ?? "";
  const parts = [name || "Unnamed patient", age >= 0 ? String(age) : "", sex ? sex.charAt(0).toUpperCase() : ""].filter(Boolean);
  return { name, age, sex, displayLabel: name ? parts.join(", ").replace(/, ([MFOU])$/, " $1") : "Unnamed patient (limited chart)" };
}

export function buildBrief(record: HealthRecord, now: Date = new Date()): PreopBrief {
  const asOf = record.meta?.dataAsOf ?? now.toISOString();
  const patient = summarizePatient(record, asOf);
  const meds = dedupe((record.data.medications ?? []).filter((m) => isLive(m.status)));
  const conditions = dedupe((record.data.conditions ?? []).filter((c) => isLive(c.status)));
  const allergies = (record.data.allergies ?? []).filter((a) => isLive(a.status) && !/no known/i.test(allergyName(a)));
  const labs = latestLabs(record.data.labs ?? []);
  const flags: Flag[] = [];
  const gaps: DataGap[] = [];

  const condition = (key: keyof typeof CONDITIONS) => conditions.filter((c) => CONDITIONS[key].test(c.name ?? ""));
  const add = (flag: Omit<Flag, "id">) => flags.push({ id: `flag_${flag.type}`, ...flag });

  // Bleeding
  const anticoag = meds.filter((m) => medClass(m, ANTICOAGULANTS));
  const antiplatelet = meds.filter((m) => medClass(m, ANTIPLATELETS));
  const platelets = labs.get("platelets");
  if (anticoag.length || antiplatelet.length) {
    const names = [...anticoag, ...antiplatelet].map((m) => medClass(m, [...ANTICOAGULANTS, ...ANTIPLATELETS]) ?? "");
    const both = anticoag.length > 0 && antiplatelet.length > 0;
    const evidence = [...anticoag, ...antiplatelet].map(medEvidence);
    if (platelets && labDirection(platelets) === "low") evidence.push(labEvidence(platelets, "platelets"));
    add({
      type: "bleeding",
      severity: anticoag.length ? "high" : "moderate",
      title: both ? "Bleeding risk: anticoagulant plus antiplatelet" : anticoag.length ? "Bleeding risk: anticoagulant" : "Bleeding risk: antiplatelet",
      spoken: `bleeding risk from ${listPhrase(names)}`,
      detail: `Active ${listPhrase(names)}.${both ? " Combined anticoagulant and antiplatelet therapy compounds bleeding risk." : ""} The hold and restart plan must be confirmed with the care team before incision.`,
      structures: [],
      evidence,
    });
  }

  // Allergies: latex and contrast change the room and imaging plan, so they get their own flags.
  const latex = allergies.filter((a) => /latex/i.test(allergyName(a)));
  const contrast = allergies.filter((a) => /contrast|iodin/i.test(allergyName(a)));
  const other = allergies.filter((a) => !latex.includes(a) && !contrast.includes(a));
  if (latex.length) {
    add({
      type: "latex",
      severity: "high",
      title: "Latex allergy",
      spoken: "a latex allergy, so the room must be latex free",
      detail: "Latex allergy on file. The operating room, gloves, catheters, and equipment must be latex free before the patient enters.",
      structures: [],
      evidence: latex.map(allergyEvidence),
    });
  }
  if (contrast.length) {
    add({
      type: "contrast",
      severity: "moderate",
      title: "Contrast media allergy",
      spoken: "a contrast allergy",
      detail: "Contrast allergy on file. Any cholangiogram, CT, or contrast study needs an allergy plan first.",
      structures: [],
      evidence: contrast.map(allergyEvidence),
    });
  }
  if (other.length) {
    const isSevere = (a: Allergy) => /high|severe/i.test(a.severity ?? "");
    const severe = other.filter(isSevere).map(allergen);
    const mild = other.filter((a) => !isSevere(a)).map(allergen);
    const names = other.map(allergen);
    add({
      type: "allergy",
      severity: severe.length ? "high" : "moderate",
      title: severe.length ? `Allergy: ${severe.join(", ")} (high severity)${mild.length ? `, ${mild.join(", ")}` : ""}` : `Allergies: ${names.join(", ")}`,
      spoken: [severe.length ? `a high severity allergy to ${listPhrase(severe)}` : "", mild.length ? `${severe.length ? "plus an " : "an "}allergy to ${listPhrase(mild)}` : ""].filter(Boolean).join(" "),
      detail: `Allergies on file: ${names.join(", ")}. Prophylactic antibiotics and perioperative drugs must be chosen around them.`,
      structures: [],
      evidence: other.map(allergyEvidence),
    });
  }

  // Kidney function
  const egfr = labs.get("egfr");
  const creatinine = labs.get("creatinine");
  const ckd = condition("ckd");
  const egfrValue = egfr ? numericValue(egfr) : null;
  const creatinineHigh = creatinine ? labDirection(creatinine) === "high" : false;
  if ((egfrValue != null && egfrValue < 60) || creatinineHigh || ckd.length) {
    const evidence = [
      ...(egfr ? [labEvidence(egfr, "egfr")] : []),
      ...(creatinine && creatinineHigh ? [labEvidence(creatinine, "creatinine")] : []),
      ...ckd.map(conditionEvidence),
    ];
    add({
      type: "renal",
      severity: egfrValue != null && egfrValue < 30 ? "high" : "moderate",
      title: egfrValue != null ? `Reduced kidney function (eGFR ${egfrValue})` : "Reduced kidney function",
      spoken: egfrValue != null ? `reduced kidney function with an e G F R of ${egfrValue}` : "reduced kidney function",
      detail: "Renally cleared drugs, contrast, and prolonged high-pressure pneumoperitoneum all need adjustment.",
      structures: ["right_kidney", "left_kidney"],
      evidence,
    });
  }

  // Metformin with reduced eGFR
  const metformin = meds.filter((m) => medClass(m, ["metformin"]));
  if (metformin.length && egfrValue != null && egfrValue < 45) {
    add({
      type: "metformin_renal",
      severity: "high",
      title: "Metformin with eGFR below 45",
      spoken: "metformin with low kidney function",
      detail: `Metformin is active while eGFR is ${egfrValue}. Lactic acidosis risk rises around surgery and contrast; the hold plan needs confirming.`,
      structures: ["right_kidney", "left_kidney"],
      evidence: [...metformin.map(medEvidence), ...(egfr ? [labEvidence(egfr, "egfr")] : [])],
    });
  }

  // Diabetes
  const diabetes = condition("diabetes");
  const a1c = labs.get("a1c");
  const a1cValue = a1c ? numericValue(a1c) : null;
  const glucoseMeds = meds.filter((m) => medClass(m, GLUCOSE_LOWERING));
  if (diabetes.length || (a1cValue != null && a1cValue >= 6.5)) {
    const sglt2 = glucoseMeds.filter((m) => medClass(m, SGLT2));
    add({
      type: "diabetes",
      severity: a1cValue != null && a1cValue >= 8.5 ? "high" : "moderate",
      title: a1cValue != null ? `Diabetes (A1c ${a1cValue}%)` : "Diabetes",
      spoken: a1cValue != null ? `diabetes with an A one C of ${a1cValue}` : "diabetes",
      detail: `Perioperative glucose monitoring needed.${glucoseMeds.length ? ` Glucose-lowering drugs: ${glucoseMeds.map((m) => m.name).join(", ")}.` : ""}${sglt2.length ? " SGLT2 inhibitors carry euglycemic ketoacidosis risk and are usually held before surgery." : ""}`,
      structures: ["pancreas"],
      evidence: [...diabetes.map(conditionEvidence), ...(a1c ? [labEvidence(a1c, "a1c")] : []), ...glucoseMeds.map(medEvidence)],
    });
  }

  // Anemia
  const hemoglobin = labs.get("hemoglobin");
  const hgbValue = hemoglobin ? numericValue(hemoglobin) : null;
  const hgbLow = hemoglobin ? labDirection(hemoglobin) === "low" : false;
  const anemia = condition("anemia");
  if (hgbLow || anemia.length) {
    add({
      type: "anemia",
      severity: hgbValue != null && hgbValue < 8 ? "high" : "moderate",
      title: hgbValue != null && hgbLow ? `Anemia (hemoglobin ${hgbValue})` : "Anemia",
      spoken: hgbValue != null && hgbLow ? `anemia with a hemoglobin of ${hgbValue}` : "anemia",
      detail: "Lower tolerance for blood loss. Type and screen, and know the latest hemoglobin before incision.",
      structures: [],
      evidence: [...(hemoglobin && hgbLow ? [labEvidence(hemoglobin, "hemoglobin")] : []), ...anemia.map(conditionEvidence)],
    });
  }

  // Cardiac
  const cardiac = [...condition("afib"), ...condition("heartFailure"), ...condition("coronary")];
  if (cardiac.length) {
    const names = cardiac.map((c) => (c.name ?? "").toLowerCase());
    add({
      type: "cardiac",
      severity: condition("heartFailure").length ? "high" : "moderate",
      title: `Cardiac: ${cardiac.map((c) => c.name).join(", ")}`,
      spoken: names.join(" with "),
      detail: "Pneumoperitoneum and positioning change venous return and afterload. Anesthesia needs to know before insufflation.",
      structures: ["heart"],
      evidence: cardiac.map(conditionEvidence),
    });
  }

  // Airway
  const airway = [...condition("asthma"), ...condition("copd"), ...condition("sleepApnea")];
  if (airway.length) {
    add({
      type: "airway",
      severity: "moderate",
      title: `Airway: ${airway.map((c) => c.name).join(", ")}`,
      spoken: airway.map((c) => (c.name ?? "").toLowerCase()).join(" with "),
      detail: "Bronchospasm or difficult ventilation risk at induction, and pneumoperitoneum raises airway pressures.",
      structures: ["lungs"],
      evidence: airway.map(conditionEvidence),
    });
  }

  // Polypharmacy
  if (meds.length >= 10) {
    add({
      type: "polypharmacy",
      severity: "moderate",
      title: `Polypharmacy (${meds.length} active medications)`,
      spoken: `${meds.length} active medications`,
      detail: "Medication reconciliation is required: which to continue, hold, or restart after surgery.",
      structures: [],
      evidence: meds.map(medEvidence),
    });
  }

  // Age extremes
  if (patient.age >= 0 && patient.age < 18) {
    add({
      type: "pediatric",
      severity: "moderate",
      title: `Pediatric patient (age ${patient.age})`,
      spoken: `a ${patient.age} year old child`,
      detail: "Smaller working space, weight-based dosing, and lower insufflation pressures.",
      structures: [],
      evidence: [{ kind: "demographic", label: "Age", value: String(patient.age), date: "", source: "" }],
    });
  } else if (patient.age >= 75) {
    add({
      type: "elderly",
      severity: "moderate",
      title: `Older adult (age ${patient.age})`,
      spoken: `age ${patient.age}`,
      detail: "Reduced physiological reserve. Screen for frailty and delirium risk.",
      structures: [],
      evidence: [{ kind: "demographic", label: "Age", value: String(patient.age), date: "", source: "" }],
    });
  }

  // Data gaps: absence of data is reported as unknown, never as "none".
  const shared = record.categories;
  for (const category of RELEVANT_CATEGORIES) {
    if (shared && !shared.includes(category)) {
      gaps.push({ code: `not_shared_${category}`, message: `Patient consent does not share ${category}. Treat ${category} as unknown, not as none.` });
    } else if ((record.meta?.missingCategories ?? []).includes(category)) {
      gaps.push({ code: `missing_${category}`, message: `No ${category} available from the health record. Treat ${category} as unknown, not as none.` });
    }
  }
  if (!record.data.demographics?.name) {
    gaps.push({ code: "no_demographics", message: "No name or birth date available. Confirm identity before any procedure." });
  }
  for (const warning of record.meta?.warnings ?? []) {
    if (warning.code === "source_unavailable") {
      gaps.push({ code: "source_unavailable", message: warning.message });
    }
  }
  for (const med of meds) {
    if (!(med.codes ?? []).some((c) => c.code)) {
      gaps.push({ code: "uncoded_medication", message: `"${med.name}" has no drug code, so its class could not be checked. Reconcile it with the patient.` });
    }
    if (med.status === "unknown" || med.status == null) {
      gaps.push({ code: "medication_status_unknown", message: `"${med.name}" has an unknown status. Confirm whether the patient still takes it.` });
    }
  }
  for (const lab of record.data.labs ?? []) {
    const kind = labKind(lab);
    if (kind && lab.value == null) {
      gaps.push({ code: "lab_no_value", message: `${shortLabName(lab, kind)} was ordered but has no result. Repeat it before surgery.` });
    }
  }
  const labsShared = !shared || shared.includes("labs");
  if (labsShared && !(record.meta?.missingCategories ?? []).includes("labs") && egfrValue == null && !(creatinine && numericValue(creatinine) != null)) {
    gaps.push({ code: "no_kidney_function", message: "No usable creatinine or eGFR on file. Kidney function is unknown." });
  }

  // An empty or partial chart must never read as a safe chart.
  const finalGaps = dedupeGaps(gaps);
  if (finalGaps.length) {
    const unknownCritical = finalGaps.some((g) => /(missing|not_shared)_(medications|allergies)|no_demographics/.test(g.code));
    add({
      type: "incomplete_chart",
      severity: unknownCritical ? "high" : "moderate",
      title: `Incomplete chart (${finalGaps.length} gap${finalGaps.length === 1 ? "" : "s"})`,
      spoken: unknownCritical ? "an incomplete chart, so allergies or medications are unknown" : "an incomplete chart",
      detail: finalGaps.map((g) => g.message).join(" "),
      structures: [],
      evidence: [],
    });
  }

  flags.sort((a, b) => SEVERITY_RANK[a.severity] - SEVERITY_RANK[b.severity]);
  const highlightStructures = [...new Set(flags.flatMap((f) => f.structures))];

  const dataSource = record.environment === "sandbox" ? "sandbox" : "demo";
  const consentReceipts = record.consent?.receiptIds ?? [];
  return {
    patientId: record.id,
    synthetic: record.synthetic !== false,
    dataSource,
    consentStatus: record.consent?.status ?? "unknown",
    consentReceipts,
    generatedAt: now.toISOString(),
    dataAsOf: record.meta?.dataAsOf ?? "",
    patient,
    flags,
    highlightStructures,
    activeMedicationCount: meds.length,
    chart: [
      ...buildChart(patient, conditions, meds, allergies, labs, flags),
      {
        section: "Consent",
        text: consentReceipts.length
          ? `Shared by patient consent, receipt ${consentReceipts.join(", ")}`
          : dataSource === "sandbox"
            ? "Shared by patient consent (no receipt returned)"
            : "FinchNode demo record (consent simulated)",
        flagged: false,
      },
    ],
    dataGaps: finalGaps,
    sources: (record.sources ?? []).map((s) => s.organization),
    say: buildSay(patient, flags),
    disclaimer: DISCLAIMER,
  };
}

function dedupeGaps(gaps: DataGap[]): DataGap[] {
  const seen = new Set<string>();
  return gaps.filter((g) => (seen.has(g.message) ? false : (seen.add(g.message), true)));
}

function buildChart(
  patient: PatientSummary,
  conditions: RecordEntry[],
  meds: Medication[],
  allergies: Allergy[],
  labs: Map<LabKind, Lab>,
  flags: Flag[],
): ChartLine[] {
  const flaggedLabels = new Set(flags.flatMap((f) => f.evidence.map((e) => e.label)));
  const lines: ChartLine[] = [{ section: "Patient", text: patient.displayLabel, flagged: flags.some((f) => f.type === "pediatric" || f.type === "elderly") }];
  for (const c of conditions) lines.push({ section: "Problems", text: c.name ?? "", flagged: flaggedLabels.has(c.name ?? "") });
  for (const m of meds) lines.push({ section: "Medications", text: m.name ?? "", flagged: flaggedLabels.has(m.name ?? "") });
  for (const a of allergies) {
    const name = allergyName(a);
    lines.push({ section: "Allergies", text: a.severity ? `${name} (${a.severity})` : name, flagged: true });
  }
  for (const [kind, lab] of labs) {
    const e = labEvidence(lab, kind);
    const direction = labDirection(lab);
    lines.push({ section: "Key labs", text: `${e.label}: ${e.value}${e.date ? ` (${e.date})` : ""}`, flagged: direction === "high" || direction === "low" });
  }
  return lines;
}

function buildSay(patient: PatientSummary, flags: Flag[]): string {
  const who = patient.name ? `${patient.name}${patient.age >= 0 ? `, ${patient.age}` : ""}` : "Unnamed patient";
  const high = flags.filter((f) => f.severity === "high");
  const rest = flags.filter((f) => f.severity !== "high");
  const parts = [`${who}.`];
  if (!flags.length) parts.push("Complete chart with no risk flags.");
  if (high.length) parts.push(`${countWord(high.length)} high priority: ${listPhrase(high.map((f) => f.spoken))}.`);
  if (rest.length) parts.push(`${high.length ? "Also" : "Watch for"} ${listPhrase(rest.map((f) => f.spoken))}.`);
  parts.push("Synthetic demo patient.");
  return parts.join(" ");
}

const COUNT_WORDS = ["Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten"];
const countWord = (n: number) => COUNT_WORDS[n] ?? String(n);
