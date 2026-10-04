import { BodyState, type BodyAction } from "./open-body.js";
import { idealBodyActions } from "./open-body-fixtures.js";
import type { Procedure, ProcedureStep, StepMistake } from "./types.js";

// Reference step engine. Apps/quest CaseRunner.cs mirrors these semantics exactly; tests here prove
// every procedure can be completed, so no step in the data is a dead end.

export type EngineEvent =
  | { type: "place_port"; portId: string }
  | { type: "touch"; structureId: string; instrumentId: string }
  | { type: "identify"; structureId: string }
  | { type: "confirm" }
  | { type: "surgery"; evidence: BodyAction };

export interface EngineResult {
  advanced: boolean;
  completed: boolean;
  mistake: StepMistake | null;
  stepId: string;
}

export class StepEngine {
  private index = new Map<string, ProcedureStep>();
  private progress = new Set<string>();
  private applied = 0;
  readonly body: BodyState | null;
  readonly completedMilestones = new Set<string>();
  readonly orderDeviations: string[] = [];
  private seenActions = new Set<string>();
  current: ProcedureStep | null;
  mistakes: StepMistake[] = [];

  constructor(readonly procedure: Procedure) {
    this.body = procedure.openBody ? new BodyState(procedure.openBody.tissues) : null;
    for (const s of procedure.steps) this.index.set(s.id, s);
    this.current = this.index.get(procedure.firstStep) ?? null;
  }

  get completed() {
    return this.current == null;
  }

  // Read-only view of progress inside the current step (targets or ports done, apply count so far).
  get stepProgress(): { done: string[]; applied: number } {
    return { done: [...this.progress], applied: this.applied };
  }

  handle(event: EngineEvent): EngineResult {
    if (this.body) return this.handleBody(event);
    const step = this.current;
    if (!step) return { advanced: false, completed: true, mistake: null, stepId: "" };

    const mistake = this.matchMistake(step, event);
    if (mistake) {
      this.mistakes.push(mistake);
      return { advanced: false, completed: false, mistake, stepId: step.id };
    }

    const { check } = step;
    switch (check.type) {
      case "place_ports":
        if (event.type === "place_port" && check.targets.includes(event.portId)) this.progress.add(event.portId);
        break;
      case "touch_target":
        if (event.type === "touch" && event.instrumentId === step.instrumentId && check.targets.includes(event.structureId)) {
          this.progress.add(event.structureId);
        }
        break;
      case "identify_targets":
        if (event.type === "identify" && check.targets.includes(event.structureId)) this.progress.add(event.structureId);
        break;
      case "apply_count":
        if (event.type === "touch" && event.instrumentId === step.instrumentId && check.targets.includes(event.structureId)) {
          this.applied += 1;
        }
        break;
      case "confirm":
        if (event.type === "confirm") this.progress.add("confirm");
        break;
    }

    if (!this.isSatisfied(step)) return { advanced: false, completed: false, mistake: null, stepId: step.id };
    this.progress.clear();
    this.applied = 0;
    this.current = step.next ? (this.index.get(step.next) ?? null) : null;
    return { advanced: true, completed: this.current == null, mistake: null, stepId: step.id };
  }

  private handleBody(event: EngineEvent): EngineResult {
    const previous = this.current;
    const empty = { advanced: false, completed: this.completed, mistake: null, stepId: previous?.id ?? "" };
    if (event.type !== "surgery") return empty;
    const record = this.body!.apply(event.evidence);
    if (!record) return empty;
    const plan = this.procedure.openBody!;
    let mistake: StepMistake | null = null;
    for (const rule of plan.guardrails) if ((!rule.tissueId || rule.tissueId === record.action.tissueId) && record.outcomes.includes(rule.outcome)) {
      const detected: StepMistake = { id: rule.id, trigger: "wrong_order", structure: record.action.tissueId, severity: rule.severity, feedback: rule.feedback };
      this.mistakes.push(detected); mistake ??= detected;
    }
    const satisfied = new Set(plan.milestones.filter(m => m.predicates.every(p => this.body!.test(p))).map(m => m.id));
    let advanced = false;
    for (const m of plan.milestones) if (satisfied.has(m.id) && !this.completedMilestones.has(m.id)) {
      if (previous && previous.id !== m.id) this.orderDeviations.push(m.id);
      this.completedMilestones.add(m.id); advanced = true;
    }
    // Live state can invalidate a formerly achieved milestone (e.g. a new bleed).
    this.current = this.procedure.steps.find(s => !satisfied.has(s.id)) ?? null;
    return { advanced, completed: this.completed, mistake, stepId: previous?.id ?? "" };
  }

  private isSatisfied(step: ProcedureStep): boolean {
    const { check } = step;
    if (check.type === "confirm") return this.progress.has("confirm");
    if (check.type === "apply_count") return this.applied >= check.count;
    return check.targets.every((t) => this.progress.has(t)) && this.progress.size >= check.count;
  }

  private matchMistake(step: ProcedureStep, event: EngineEvent): StepMistake | null {
    for (const m of step.mistakes) {
      if (m.trigger === "touch_structure" && event.type === "touch" && event.structureId === m.structure) return m;
      if (m.trigger === "wrong_identification" && event.type === "identify" && event.structureId === m.structure) return m;
      if (m.trigger === "wrong_order" && event.type === "touch" && event.structureId === m.structure) return m;
      if (m.trigger === "excess_energy" && event.type === "touch" && event.structureId === m.structure) return m;
    }
    return null;
  }
}

// The ideal event sequence for one step: used by tests and by a "demo autoplay" mode.
export function perfectEvents(step: ProcedureStep): EngineEvent[] {
  const { check } = step;
  if (check.type === "body_predicate") return idealBodyActions(step.id).map(evidence => ({ type: "surgery", evidence }));
  switch (check.type) {
    case "place_ports":
      return check.targets.map((portId) => ({ type: "place_port", portId }));
    case "touch_target":
      return check.targets.map((structureId) => ({ type: "touch", structureId, instrumentId: step.instrumentId }));
    case "identify_targets":
      return check.targets.map((structureId) => ({ type: "identify", structureId }));
    case "apply_count":
      return Array.from({ length: check.count }, () => ({ type: "touch", structureId: check.targets[0] ?? "", instrumentId: step.instrumentId }));
    case "confirm":
      return [{ type: "confirm" }];
    default: return [];
  }
}
