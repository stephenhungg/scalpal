import { ANATOMY, ANATOMY_BY_ID } from "./catalog/anatomy.js";
import { CASE_PLANS, CONSIDERATION_NOTES, STEP_ROLES } from "./catalog/cases.js";
import { STEP_COACHING, STRUCTURE_FACTS } from "./catalog/coach-knowledge.js";
import { INSTRUMENTS, INSTRUMENTS_BY_ID } from "./catalog/instruments.js";
import { PROCEDURES, PROCEDURES_BY_ID } from "./catalog/procedures.js";

// Referential integrity for every authored catalog. Any error here means Unity would hit a missing
// mesh, prefab, port, or step at runtime, so tests and the validate script fail on it.
export function validateCatalog(): string[] {
  const errors: string[] = [];
  const err = (msg: string) => errors.push(msg);
  const unique = (ids: string[], what: string) => {
    const seen = new Set<string>();
    for (const id of ids) {
      if (seen.has(id)) err(`duplicate ${what} id "${id}"`);
      seen.add(id);
    }
  };
  const identifier = /^[a-z][a-z0-9_]*$/;

  unique(ANATOMY.map((a) => a.id), "anatomy");
  unique(INSTRUMENTS.map((i) => i.id), "instrument");
  unique(PROCEDURES.map((p) => p.id), "procedure");
  for (const a of ANATOMY) if (!identifier.test(a.id)) err(`anatomy id "${a.id}" is not a snake_case identifier`);
  for (const i of INSTRUMENTS) if (!identifier.test(i.id)) err(`instrument id "${i.id}" is not a snake_case identifier`);

  for (const p of PROCEDURES) {
    const at = (s: string) => `${p.id}: ${s}`;
    const structures = new Set(p.structures);
    const ports = new Map(p.ports.map((port) => [port.id, port]));
    const steps = new Map(p.steps.map((s) => [s.id, s]));
    unique(p.steps.map((s) => s.id), `${p.id} step`);
    unique(p.ports.map((s) => s.id), `${p.id} port`);

    for (const s of p.structures) if (!ANATOMY_BY_ID.has(s)) err(at(`structure "${s}" is not in the anatomy catalog`));
    for (const s of p.focusStructures) if (!structures.has(s)) err(at(`focus structure "${s}" is not in the procedure's structures`));
    for (const port of p.ports) {
      for (const i of port.instrumentIds) if (!INSTRUMENTS_BY_ID.has(i)) err(at(`port "${port.id}" lists unknown instrument "${i}"`));
      const { x, y, z } = port.position;
      if (![x, y, z].every(Number.isFinite) || Math.hypot(x, y, z) > 0.4) err(at(`port "${port.id}" position is outside a plausible torso`));
    }
    if (!steps.has(p.firstStep)) err(at(`firstStep "${p.firstStep}" does not exist`));

    const terminal = p.steps.filter((s) => s.next === "");
    if (terminal.length !== 1) err(at(`expected exactly one final step, found ${terminal.length}`));

    for (const step of p.steps) {
      const st = (s: string) => at(`step "${step.id}": ${s}`);
      if (step.next && !steps.has(step.next)) err(st(`next "${step.next}" does not exist`));
      if (!INSTRUMENTS_BY_ID.has(step.instrumentId)) err(st(`unknown instrument "${step.instrumentId}"`));
      for (const t of step.targets) if (!structures.has(t)) err(st(`target "${t}" is not in the procedure's structures`));
      for (const id of step.portIds) if (!ports.has(id)) err(st(`port "${id}" does not exist`));
      for (const m of step.mistakes) if (!structures.has(m.structure)) err(st(`mistake "${m.id}" structure "${m.structure}" is not in the procedure`));
      if (step.portIds.length) {
        const reachable = new Set(step.portIds.flatMap((id) => ports.get(id)?.instrumentIds ?? []));
        if (!reachable.has(step.instrumentId)) err(st(`instrument "${step.instrumentId}" does not fit through ports ${step.portIds.join(", ")}`));
      }
      const { check } = step;
      if (check.type === "place_ports") {
        for (const t of check.targets) if (!step.portIds.includes(t)) err(st(`check port "${t}" is not one of the step's ports`));
        if (check.count !== check.targets.length) err(st("place_ports count must equal its targets"));
      } else if (check.type === "confirm") {
        if (check.targets.length) err(st("confirm checks take no targets"));
      } else {
        if (!check.targets.length) err(st(`${check.type} check needs targets`));
        for (const t of check.targets) if (!step.targets.includes(t)) err(st(`check target "${t}" is not a step target`));
        if (check.type !== "apply_count" && check.count > check.targets.length) err(st("check count exceeds its targets"));
        if (check.type === "apply_count" && check.count < 1) err(st("apply_count needs a positive count"));
        // A required target that the same event type also flags as a mistake can never be completed.
        const checkEvent = check.type === "identify_targets" ? "identify" : "touch";
        for (const m of step.mistakes) {
          const mistakeEvent = m.trigger === "wrong_identification" ? "identify" : "touch";
          if (mistakeEvent === checkEvent && check.targets.includes(m.structure)) err(st(`target "${m.structure}" is also mistake "${m.id}"`));
        }
      }
    }

    // Every step must be reachable from firstStep, and the chain must terminate.
    const visited = new Set<string>();
    let cursor = steps.get(p.firstStep);
    while (cursor && !visited.has(cursor.id)) {
      visited.add(cursor.id);
      cursor = cursor.next ? steps.get(cursor.next) : undefined;
    }
    if (cursor) err(at(`step chain loops at "${cursor.id}"`));
    for (const s of p.steps) if (!visited.has(s.id)) err(at(`step "${s.id}" is unreachable`));
  }

  for (const [subject, plan] of Object.entries(CASE_PLANS)) {
    if (!PROCEDURES_BY_ID.has(plan.procedureId)) err(`case plan ${subject} uses unknown procedure "${plan.procedureId}"`);
  }
  for (const p of PROCEDURES) {
    const roles = STEP_ROLES[p.id];
    if (!roles) {
      err(`${p.id}: no STEP_ROLES entry, so chart considerations cannot attach`);
      continue;
    }
    for (const [role, stepId] of Object.entries(roles)) {
      if (!p.steps.some((s) => s.id === stepId)) err(`${p.id}: role "${role}" points at missing step "${stepId}"`);
    }
  }
  for (const [type, notes] of Object.entries(CONSIDERATION_NOTES)) {
    if (!notes.length) err(`flag type "${type}" has no consideration notes`);
  }

  // Jarvis coaching: every structure has facts and every step has coaching, with no orphans.
  for (const a of ANATOMY) if (!STRUCTURE_FACTS[a.id]) err(`anatomy "${a.id}" has no coach facts`);
  for (const id of Object.keys(STRUCTURE_FACTS)) if (!ANATOMY_BY_ID.has(id)) err(`coach facts for unknown anatomy "${id}"`);
  for (const p of PROCEDURES) {
    const coaching = STEP_COACHING[p.id] ?? {};
    for (const s of p.steps) {
      const sc = coaching[s.id];
      if (!sc?.why || !sc.lookHere) err(`${p.id}: step "${s.id}" has no coaching (why and lookHere)`);
    }
    for (const id of Object.keys(coaching)) if (!p.steps.some((s) => s.id === id)) err(`${p.id}: coaching for unknown step "${id}"`);
  }
  for (const id of Object.keys(STEP_COACHING)) if (!PROCEDURES_BY_ID.has(id)) err(`coaching for unknown procedure "${id}"`);
  return errors;
}
