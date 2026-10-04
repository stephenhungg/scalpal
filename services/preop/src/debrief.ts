import type { CoachSnapshot } from "./coach.js";

// Scalpal's spoken recap: a short, deterministic summary of the finished run (no LLM). Two to four sentences:
// the outcome and what went well, the main mistake, one thing to improve, and the robot result when ready.

export interface DebriefRobot {
  status: string;
  stepTitle: string;
  success: boolean | null;
}

const sentence = (text: string) => {
  const t = text.trim().replace(/[.;,\s]+$/, "");
  return t ? `${t.charAt(0).toUpperCase()}${t.slice(1)}.` : "";
};

const minutes = (seconds: number) => {
  const m = Math.max(1, Math.round(seconds / 60));
  return `${m} minute${m === 1 ? "" : "s"}`;
};

export function debriefText(s: CoachSnapshot, robot: DebriefRobot | null = null): string {
  const total = s.checklist.length || s.stepCount;
  const met = s.checklist.length ? s.checklist.filter((c) => c.done).length : s.completedCount;
  const procedure = s.procedureTitle.toLowerCase();
  const outcome = s.condition?.outcome;
  const lines: string[] = [];

  if (outcome?.result === "died") lines.push(sentence(`We lost the patient${outcome.cause ? `: ${outcome.cause}` : ""}`));
  else if (outcome?.result === "ended") lines.push(`You ended the ${procedure} early with ${met} of ${total} milestones met.`);
  else lines.push(`You finished the ${procedure} in ${minutes(s.elapsedSeconds)}, meeting ${met} of ${total} milestones${s.mistakeCount === 0 ? " with no mistakes" : ""}.`);

  const main = s.recentMistakes.find((m) => m.severity === "high") ?? s.recentMistakes[0];
  if (main) {
    const others = s.mistakeCount - 1;
    lines.push(sentence(`Main mistake: ${main.feedback}`) + (others > 0 ? ` You made ${others} other mistake${others === 1 ? "" : "s"}.` : ""));
  }

  if (s.bloodLossMl >= 100) lines.push(`Next time, control bleeding sooner: you lost about ${Math.round(s.bloodLossMl / 10) * 10} millilitres.`);
  else if (main) lines.push(`Next time, slow down and confirm the structure before you act.`);
  else if (met < total) lines.push(`Next time, aim to reach every milestone.`);
  else if (s.hintsUsed > 0) lines.push(`Next time, try it with fewer hints.`);

  if (robot?.status === "ready" && robot.success !== null && robot.stepTitle)
    lines.push(`The robot ${robot.success ? "completed" : "missed"} ${robot.stepTitle.toLowerCase()} from your demonstration.`);

  return lines.slice(0, 4).join(" ");
}
