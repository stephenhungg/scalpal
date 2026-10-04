import type { CoachEvent, CoachSession } from "./coach.js";
import { bodyAction, type BodyAction } from "./open-body.js";

// Laptop demo driver for open-body cases: the same sim buttons as the laparoscopic flow, expressed as
// measured body actions and tracker events, so Scalpal can be exercised without the headset. Each button
// does something real in the body reducer; none of it bypasses the coach's normal event path.

// A wrong way to do each expected milestone, chosen so the matching guardrail fires.
const MISTAKES: Record<string, (t: number) => Partial<BodyAction> & { verb: string; tissueId: string }> = {
  open_fascia: () => ({ verb: "cut", tissueId: "fascia", instrumentId: "scalpel", lengthMm: 30, angleDegrees: 60 }),
  split_muscle: () => ({ verb: "cut", tissueId: "muscle", instrumentId: "scalpel", lengthMm: 20 }),
  open_peritoneum: () => ({ verb: "cut", tissueId: "peritoneum", instrumentId: "scalpel", lengthMm: 10 }),
  deliver_appendix: () => ({ verb: "grasp", tissueId: "appendix", instrumentId: "babcock", depthMm: 5, speedMps: 0.3 }),
  divide_mesoappendix: () => ({ verb: "cut", tissueId: "mesoappendix", instrumentId: "metzenbaum_scissors", lengthMm: 10 }),
  ligate_base: () => ({ verb: "cut", tissueId: "appendix", instrumentId: "scalpel", lengthMm: 10, distanceMm: 3 }),
};
const ROUGH_SKIN = { verb: "grasp", tissueId: "skin", instrumentId: "toothed_forceps", depthMm: 2, speedMps: 0.3 };

// Tools the learner would plausibly grab by mistake, per expected tool.
const WRONG_TOOL: Record<string, string> = { retractor: "scalpel", skin_marker: "scalpel", hemostat: "metzenbaum_scissors", babcock: "toothed_forceps" };

// Correct actions come from authored fixtures with fixed timestamps. Only when another simulated action
// already moved the body clock past one is it moved just after the last action; ids stay, so the fixture
// still tracks what is done.
export function restampForBody(s: CoachSession, e: CoachEvent | null): CoachEvent | null {
  const body = s.engine.body;
  if (!e || !body || e.type !== "surgery") return e;
  const lastT = body.log.at(-1)?.action.timeMs ?? 0;
  return { ...e, evidence: { ...e.evidence, timeMs: !body.log.length || e.evidence.timeMs >= lastT ? e.evidence.timeMs : lastT + 1 } };
}

export function openBodySimulation(s: CoachSession, kind: string): CoachEvent[] | null {
  const body = s.engine.body;
  if (!body) return null;
  const step = s.engine.current;
  const opened = (id: string) => body.get(id, "opened") > 0;
  let n = 0;
  const lastT = body.log.at(-1)?.action.timeMs ?? 0;
  const act = (values: Partial<BodyAction> & { verb: string; tissueId: string }): CoachEvent => {
    n += 1;
    const { verb, tissueId, ...rest } = values;
    return { type: "surgery", evidence: bodyAction(verb, tissueId, { actionId: `sim-${kind}-${lastT + 1000 * n}`, timeMs: lastT + 1000 * n, ...rest }) };
  };
  const target = step?.targets[0] && step.targets[0] !== "abdominal_wall" ? step.targets[0] : "skin";
  // The perfused tissue a bleed can come from right now, deepest exposed first.
  const bleedSite = opened("peritoneum") ? "mesoappendix" : opened("fascia") ? "muscle" : "";

  switch (kind) {
    case "mistake":
      return [act(step && MISTAKES[step.id] ? MISTAKES[step.id]!(0) : ROUGH_SKIN)];
    case "wrong_instrument": {
      const need = step?.instrumentId ?? "";
      const wrong = WRONG_TOOL[need] ?? (need === "scalpel" ? "babcock" : "scalpel");
      return [{ type: "instrument", instrumentId: wrong, hand: "right", held: true }, { type: "contact", instrumentId: wrong, structureId: target }];
    }
    case "off_target":
      return [{ type: "contact", instrumentId: step?.instrumentId ?? "scalpel", structureId: target === "skin" ? "umbilicus" : "skin" }];
    case "look_at_danger":
      return [{ type: "contact", instrumentId: step?.instrumentId ?? "scalpel", structureId: "cecum" }];
    case "bleed":
      return bleedSite ? [act({ verb: "cut", tissueId: bleedSite, instrumentId: "scalpel", lengthMm: 10 })] : [];
    case "stop_bleed": {
      const bleeding = body.tissues.filter((t) => body.get(t.id, "bleeding") > 0).map((t) => t.id);
      return bleeding.map((id) => act({ verb: "seal", tissueId: id, instrumentId: "hook_cautery" }));
    }
    default:
      return null; // correct_action, complete_step and tracking work unchanged
  }
}
