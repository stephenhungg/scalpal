import type { SurgicalCase } from "./types.js";

// Scalpal's spoken breakdown over the pre-surgery flythrough (docs/operation-flow.md, "Operating room
// entry"). One line per beat, keyed by the same step ids the briefing atlas uses
// (apps/quest/Assets/Scalpal/Briefing/Resources/briefing_parts.json), pre-rendered as reflex clips so
// the headset plays each beat's voice the moment the camera arrives, with no model in the loop.

const OPEN_APPENDECTOMY: Record<string, string> = {
  mark_incision: "First, find the hip bone and the belly button. A third of the way along that line is McBurney's point. Mark five to eight centimeters along the skin lines.",
  incise_skin: "One smooth stroke along your mark, through skin and fat only.",
  open_fascia: "Under the fat is the external oblique aponeurosis. Open it along its fibers, never across them.",
  split_muscle: "The internal oblique and transversus come next. Split them with two retractors. Pull, don't cut, so the muscle keeps its nerves.",
  open_peritoneum: "Lift the peritoneum with forceps so the bowel falls away, then nick it.",
  deliver_appendix: "Find the cecum, follow its taenia to the appendix, and lift it gently out into the wound.",
  divide_mesoappendix: "The appendicular artery runs in the mesoappendix. Clamp twice, cut between, and tie both sides. No bleeding before you move on.",
  ligate_base: "Find the true base where the taeniae meet. Crush it, tie within five millimeters of the cecum, and cut above your tie.",
  inspect_clean: "Suction the field dry and check the stump and the tied vessels.",
  close: "Then close in layers.",
};

export interface BriefingLine {
  key: string; // reflex clip key, "brief.<stepId>" or "brief.intro" / "brief.outro"
  stepId: string; // the briefing beat ("" for intro and outro)
  title: string;
  text: string;
}

export function briefingLines(kase: SurgicalCase): BriefingLine[] {
  const p = kase.procedure;
  const authored = p.id === "open_appendectomy" ? OPEN_APPENDECTOMY : {};
  const risks = kase.brief.flags.filter((f) => f.severity === "high").map((f) => f.title.toLowerCase());
  const intro = `Here's the plan: ${p.title.toLowerCase()} for ${kase.patient.name || "this patient"}, ${kase.urgency || "scheduled"}.${risks.length ? ` Keep in mind: ${risks.slice(0, 2).join(" and ")}.` : ""}`;
  return [
    { key: "brief.intro", stepId: "", title: p.title, text: intro },
    ...p.steps.map((s) => ({ key: `brief.${s.id}`, stepId: s.id, title: s.title, text: authored[s.id] ?? `${s.title}. ${s.instruction}` })),
    { key: "brief.outro", stepId: "", title: "Begin", text: "That's the plan. Time-out first, then the case is yours. The checklist stays top left, but your hands decide." },
  ];
}
