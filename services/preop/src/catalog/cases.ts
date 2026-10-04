import type { FlagType, PreopBrief, SurgicalCase } from "../types.js";

// Authored surgical scenarios layered on top of FinchNode's synthetic patients. The chart (meds, labs,
// allergies, problems) comes from FinchNode; the acute presentation below is fictional and labeled as such.
// Each pairing is chosen to be clinically plausible for that patient's age and chart.

// A sentence about the chart is appended to the presentation only when the actual record supports
// it: sandbox patients can connect fewer sources or different data than the demo record the plan was
// written against, and the presentation feeds Jarvis's prompt.
export interface ChartNote {
  text: string;
  whenFlags?: FlagType[];
  whenGaps?: string[];
  minSources?: number;
}

export interface CasePlan {
  procedureId: string;
  urgency: SurgicalCase["urgency"];
  indication: string;
  // Authored clinical story only. No claims about what the chart contains.
  presentation: string;
  chartNotes?: ChartNote[];
}

export const CASE_PLANS: Record<string, CasePlan> = {
  "patient-demo-polypharmacy": {
    procedureId: "lap_cholecystectomy",
    urgency: "urgent",
    indication: "Acute calculous cholecystitis, moderate (Tokyo grade II)",
    presentation:
      "Two days of constant right upper quadrant pain, temperature 38.3 C, positive Murphy sign. Ultrasound shows gallstones, a 6 mm gallbladder wall, and pericholecystic fluid.",
    chartNotes: [{ text: "Her anticoagulation and kidney function shape the timing.", whenFlags: ["bleeding", "renal"] }],
  },
  "patient-demo-001": {
    procedureId: "lap_cholecystectomy",
    urgency: "elective",
    indication: "Symptomatic cholelithiasis (recurrent biliary colic)",
    presentation:
      "Four episodes of post-meal right upper quadrant pain over three months, each resolving within hours. Ultrasound shows multiple gallstones with a normal wall and duct. Scheduled from clinic.",
  },
  "patient-demo-source-unavailable": {
    procedureId: "lap_cholecystectomy",
    urgency: "elective",
    indication: "Interval cholecystectomy after mild gallstone pancreatitis",
    presentation:
      "Admitted last month with mild gallstone pancreatitis that resolved with supportive care. Lipase has normalized. Returns for gallbladder removal to prevent recurrence.",
    chartNotes: [{ text: "One of her record sources failed to sync.", whenGaps: ["source_unavailable"] }],
  },
  "patient-demo-consent-partial": {
    procedureId: "lap_cholecystectomy",
    urgency: "elective",
    indication: "Symptomatic cholelithiasis",
    presentation:
      "Referred for gallbladder removal after recurrent biliary colic.",
    chartNotes: [
      { text: "The patient shared only medications and allergies, so identity, problems, and labs must be confirmed directly.", whenGaps: ["not_shared_conditions", "not_shared_labs"] },
    ],
  },
  "patient-demo-pediatric-asthma": {
    procedureId: "lap_appendectomy",
    urgency: "urgent",
    indication: "Acute uncomplicated appendicitis",
    presentation:
      "Eighteen hours of pain that started around the umbilicus and moved to the right lower quadrant, with anorexia and one episode of vomiting. Temperature 38.0 C, white count 14.2. Ultrasound shows a noncompressible 9 mm appendix.",
  },
  "patient-demo-multi-source": {
    procedureId: "lap_appendectomy",
    urgency: "urgent",
    indication: "Acute appendicitis",
    presentation:
      "One day of right lower quadrant pain with rebound tenderness and a temperature of 37.9 C. CT shows an 11 mm appendix with periappendiceal fat stranding and no abscess.",
    chartNotes: [{ text: "Her records come from two health systems that disagree in places.", minSources: 2 }],
  },
  "patient-demo-sparse": {
    procedureId: "lap_appendectomy",
    urgency: "emergency",
    indication: "Suspected perforated appendicitis",
    presentation:
      "Brought in with two days of worsening right lower quadrant pain, now diffuse, with guarding, temperature 38.9 C, and heart rate 118.",
    chartNotes: [
      { text: "His record holds only demographics and one visit: medications, allergies, and history are unknown.", whenGaps: ["missing_medications", "missing_allergies"] },
    ],
  },
  "patient-demo-messy-coding": {
    procedureId: "lap_sigmoid_colectomy",
    urgency: "elective",
    indication: "Recurrent sigmoid diverticulitis",
    presentation:
      "Three CT-confirmed episodes of sigmoid diverticulitis in eighteen months, the last with a small contained abscess treated with antibiotics. Colonoscopy after recovery excluded cancer.",
    chartNotes: [{ text: "Her chart has an uncoded blood pressure pill and a creatinine with no result.", whenGaps: ["uncoded_medication", "lab_no_value"] }],
  },
};

// Fallback for any subject without an authored plan, so new FinchNode patients still route to a case.
export function fallbackPlan(age: number): CasePlan {
  if (age >= 0 && age < 30) {
    return {
      procedureId: "lap_appendectomy",
      urgency: "urgent",
      indication: "Acute appendicitis",
      presentation: "Right lower quadrant pain with fever and leukocytosis. Imaging is consistent with appendicitis. (Generated scenario.)",
    };
  }
  return {
    procedureId: "lap_cholecystectomy",
    urgency: "elective",
    indication: "Symptomatic cholelithiasis",
    presentation: "Recurrent biliary colic with gallstones on ultrasound. (Generated scenario.)",
  };
}

// Which step of each procedure a chart risk attaches to, by role.
type StepRole = "entry" | "ports" | "critical" | "bleeding" | "hemostasis";

export const STEP_ROLES: Record<string, Record<StepRole, string>> = {
  lap_cholecystectomy: { entry: "access_umbilical", ports: "working_ports", critical: "critical_view", bleeding: "liver_bed", hemostasis: "hemostasis" },
  lap_appendectomy: { entry: "access_umbilical", ports: "working_ports", critical: "find_appendix", bleeding: "divide_mesoappendix", hemostasis: "irrigate" },
  lap_sigmoid_colectomy: { entry: "access_umbilical", ports: "working_ports", critical: "identify_ureter", bleeding: "divide_ima", hemostasis: "leak_test" },
};

export const CONSIDERATION_NOTES: Record<FlagType, { role: StepRole; note: string }[]> = {
  bleeding: [
    { role: "bleeding", note: "Anticoagulant or antiplatelet on board: this is the step most likely to bleed. Confirm the documented hold before incision and seal precisely." },
    { role: "hemostasis", note: "Bleeding risk patient: inspect every raw surface and clip line twice before leaving." },
  ],
  allergy: [{ role: "entry", note: "Allergy on file: confirm the prophylactic antibiotic was chosen around it before incision." }],
  latex: [{ role: "entry", note: "Latex allergy: the room, gloves, catheters, and every instrument must be latex free before entry." }],
  contrast: [{ role: "critical", note: "Contrast allergy: an intraoperative contrast study needs an allergy plan. Rely on clear anatomic identification." }],
  renal: [{ role: "entry", note: "Reduced kidney function: high-pressure pneumoperitoneum lowers renal blood flow. Use the lowest pressure that gives a working view." }],
  metformin_renal: [{ role: "entry", note: "Metformin with low eGFR: confirm it was held. Watch for acidosis if the case runs long." }],
  diabetes: [{ role: "entry", note: "Diabetes: glucose checked before incision and monitored through the case." }],
  anemia: [{ role: "hemostasis", note: "Preoperative anemia: little reserve for blood loss. Know the latest hemoglobin and whether blood is available." }],
  cardiac: [{ role: "entry", note: "Cardiac disease: insufflate slowly and warn anesthesia. Pneumoperitoneum and tilt change venous return." }],
  airway: [{ role: "entry", note: "Airway disease: bronchospasm risk at induction. Pneumoperitoneum raises airway pressures; watch them as you insufflate." }],
  polypharmacy: [{ role: "entry", note: "Many active medications: the reconciliation list should be on the board before the time out." }],
  pediatric: [{ role: "ports", note: "Pediatric abdomen: less working space. Lower insufflation pressure and watch every trocar enter." }],
  elderly: [{ role: "entry", note: "Older adult: lower reserve. Keep the case efficient and watch temperature and fluids." }],
  incomplete_chart: [{ role: "entry", note: "Incomplete chart: confirm identity, allergies, and medications directly with the patient or family at the time out." }],
};

export const CHECKLIST_LABELS: Record<FlagType, string> = {
  bleeding: "Bleeding risk from blood thinners",
  allergy: "Drug allergy affecting antibiotic choice",
  latex: "Latex allergy",
  contrast: "Contrast media allergy",
  renal: "Reduced kidney function",
  metformin_renal: "Metformin with low kidney function",
  diabetes: "Diabetes needing glucose control",
  anemia: "Anemia",
  cardiac: "Heart disease",
  airway: "Airway or lung disease",
  polypharmacy: "Polypharmacy needing reconciliation",
  pediatric: "Pediatric patient",
  elderly: "Older adult with reduced reserve",
  incomplete_chart: "Incomplete chart needing confirmation",
};

// Every listed condition must hold: all flag types present, all gap codes present, enough sources.
export function presentationFor(plan: CasePlan, brief: PreopBrief): string {
  const flags = new Set(brief.flags.map((f) => f.type));
  const gaps = new Set(brief.dataGaps.map((g) => g.code));
  const notes = (plan.chartNotes ?? []).filter(
    (n) =>
      (n.whenFlags ?? []).every((t) => flags.has(t)) &&
      (n.whenGaps ?? []).every((c) => gaps.has(c)) &&
      brief.sources.length >= (n.minSources ?? 0),
  );
  return [plan.presentation, ...notes.map((n) => n.text)].join(" ");
}
