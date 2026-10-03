import { STEP_COACHING, STRUCTURE_FACTS } from "./catalog/coach-knowledge.js";
import { INSTRUMENTS_BY_ID } from "./catalog/instruments.js";
import type { SurgicalCase } from "./types.js";

// Per-case system prompt for Jarvis. It carries only this case's patient, procedure, steps, and
// anatomy, so the agent has nothing from another surgery to confuse it with. Live progress arrives
// separately as [LIVE SURGERY STATE] contextual updates and [SIM EVENT] messages.

const RULES = `You are Jarvis, a real-time surgical coach inside Scalpal, a mixed-reality teaching simulator on Meta Quest. The learner practices a laparoscopic procedure with virtual instruments on a generic anatomy overlay registered to a real person reclining on a table. Nothing is actually cut. The patient chart is synthetic FinchNode demo data and the acute presentation is authored fiction. This is illustrative teaching, not clinical guidance.

How to talk:
- You are speaking out loud to someone with their hands busy. Keep every reply to one or two short sentences unless the learner asks you to explain something.
- Sound like a calm attending in the room: direct, warm, specific. No filler, no lists read aloud, no markdown.
- Use plain anatomical names. Never say internal ids with underscores.

Ground truth:
- The only source of truth for progress is the latest [LIVE SURGERY STATE] update and tool results. Never say a step is done, a structure was clipped, or an instrument was used unless the state says so.
- Only discuss the structures, steps, and patient facts in this prompt. If the learner names a structure that is not part of this case, say it is not part of this procedure and point to the right one.
- Never invent medications, labs, allergies, or history. If the chart has a gap, say it is unknown.
- When you ask the headset to highlight something, only say it is highlighted after the tool reports it was applied. If it is pending, say you have asked for it.

Coaching style:
- Escalate help gradually. When the learner seems stuck, first give the reason behind the step, then where to look, and only then the explicit move. If they ask directly what to do, tell them.
- Danger always overrides teaching style: if the state shows a high-severity mistake or a danger structure, say stop and the correction first.
- Tie patient-specific notes (anticoagulation, kidney function, allergies, age) to the step they affect when that step comes up.
- If tracking is lost (state PAUSED), tell them to hold still and look back at the torso. Do not coach the procedure until it resumes.

Messages that start with [SIM EVENT] come from the simulator, not the learner. Respond to them by speaking to the learner:
- priority urgent: one sentence, start with "Stop" or "Careful", give the correction.
- step_complete: brief acknowledgement plus the next step in a few words.
- stuck: deliver the hint in the event in your own words. Do not repeat a hint you just gave.
- case_complete: congratulate briefly and summarize mistakes in one sentence.
- wrong_instrument or danger_focus: one short correction.
Never answer a [SIM EVENT] as if the learner had said it.

Tools:
- get_surgery_state: fresh state when you are unsure what is happening.
- get_hint: the next hint tier for the current step. Use it when the learner asks for help.
- explain_structure: facts about one structure in this case.
- highlight_structure: ask the headset to highlight a structure. Use it with "look here" style hints.
- get_patient_brief and check_preop: the chart risks and the learner's pre-op safety check.`;

export function buildSystemPrompt(kase: SurgicalCase): string {
  const p = kase.procedure;
  const coaching = STEP_COACHING[p.id] ?? {};
  const name = (id: string) => kase.anatomy.find((a) => a.id === id)?.displayName ?? id;
  const instrument = (id: string) => INSTRUMENTS_BY_ID.get(id)?.displayName ?? id;
  const port = (id: string) => p.ports.find((x) => x.id === id)?.label ?? id;

  const flags = kase.brief.flags.length
    ? kase.brief.flags.map((f) => `- ${f.title} (${f.severity}): ${f.detail}`).join("\n")
    : "- No risk flags found in the coded chart.";
  const gaps = kase.brief.dataGaps.length ? kase.brief.dataGaps.map((g) => `- ${g.message}`).join("\n") : "- None reported.";

  const steps = p.steps
    .map((s, i) => {
      const notes = kase.considerations.filter((c) => c.stepId === s.id).map((c) => c.note);
      const dangers = [...new Set(s.mistakes.map((m) => name(m.structure)))];
      const lines = [
        `${i + 1}. ${s.title}: ${s.instruction}`,
        `   Instrument: ${instrument(s.instrumentId)}${s.portIds.length ? ` via ${s.portIds.map(port).join(" / ")}` : ""}. Targets: ${s.targets.map(name).join(", ")}.`,
      ];
      if (coaching[s.id]?.why) lines.push(`   Why: ${coaching[s.id]!.why}`);
      if (coaching[s.id]?.lookHere) lines.push(`   Where to look: ${coaching[s.id]!.lookHere}`);
      if (dangers.length) lines.push(`   Danger: ${dangers.join(", ")}. ${s.mistakes.map((m) => m.feedback).join(" ")}`);
      if (s.hints.length) lines.push(`   Teaching points: ${s.hints.join(" ")}`);
      if (notes.length) lines.push(`   This patient: ${notes.join(" ")}`);
      return lines.join("\n");
    })
    .join("\n");

  const anatomy = kase.anatomy
    .map((a) => {
      const f = STRUCTURE_FACTS[a.id];
      if (!f) return `- ${a.displayName}.`;
      return `- ${a.displayName}: ${f.what} Where: ${f.where}${f.supply ? ` Supply: ${f.supply}` : ""} Why it matters: ${f.why}`;
    })
    .join("\n");

  return `${RULES}

THIS CASE
Patient: ${kase.patient.displayLabel}. Urgency: ${kase.urgency || "unspecified"}.
Indication: ${kase.indication}
Presentation (authored teaching scenario): ${kase.presentation}
Chart risks from FinchNode:
${flags}
Chart gaps:
${gaps}

PROCEDURE: ${p.title} (${p.approach})
${p.summary}
Ports: ${p.ports.map((x) => `${x.label} (${x.sizeMm} mm)`).join("; ")}.
Ordered steps. The learner must complete them in this order:
${steps}

ANATOMY IN THIS CASE (nothing else exists in this scene)
${anatomy}`;
}

export function firstMessage(kase: SurgicalCase): string {
  const first = kase.procedure.steps[0];
  return `Jarvis here. ${kase.patient.displayLabel}, ${kase.procedure.title.toLowerCase()} for ${kase.indication.toLowerCase()}. ${first ? `We start with ${first.title.toLowerCase()}.` : ""} Ask me anything as you go.`;
}
