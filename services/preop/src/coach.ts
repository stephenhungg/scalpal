import { STEP_COACHING, STRUCTURE_FACTS } from "./catalog/coach-knowledge.js";
import { INSTRUMENTS_BY_ID } from "./catalog/instruments.js";
import { StepEngine, perfectEvents, type EngineEvent } from "./engine.js";
import type { ProcedureStep, Severity, StepAction, SurgicalCase } from "./types.js";

// Live coaching state for one surgery attempt. Wraps the reference StepEngine (same semantics as
// CaseRunner.cs) and adds what a coach needs: time on step, off-target attempts, wrong instruments,
// what the learner is looking at, tracking validity, and escalating hints. Everything Jarvis says
// about progress comes from this state, never from the model's own guess.

export type CoachEvent =
  | EngineEvent
  | { type: "focus"; structureId: string } // learner gaze or instrument hover, from Unity
  | { type: "tracking"; valid: boolean }; // registration validity; invalid pauses scoring

export type AlertKind =
  | "mistake"
  | "danger_focus"
  | "wrong_instrument"
  | "step_complete"
  | "case_complete"
  | "stuck"
  | "tracking_lost"
  | "tracking_restored";

export type AlertPriority = "urgent" | "normal" | "low";

// Alerting tiers after FAA AC 25.1322-1: warnings preempt everything and play a pre-rendered reflex clip
// (no LLM in the loop), cautions become a queued spoken turn, advisories only update silent context.
export type AlertTier = "warning" | "caution" | "advisory";

export interface CoachAlert {
  id: string;
  kind: AlertKind;
  priority: AlertPriority;
  tier: AlertTier;
  stepId: string;
  version: number; // state version this alert belongs to; the client drops it once the state moves on
  say: string;
  reflexKey: string; // "" unless a pre-rendered clip exists for this warning
  reflexText: string;
  highlight: string[];
  at: string;
}

export interface LoggedAlert extends CoachAlert {
  seq: number;
  simEvent: string; // exact text to send as a user message when this alert becomes an LLM turn
}

export interface StructureRef {
  id: string;
  name: string;
}

export interface CoachStepView {
  id: string;
  title: string;
  instruction: string;
  action: string;
  instrumentId: string;
  instrumentName: string;
  ports: string[];
  targets: StructureRef[];
  remaining: string[];
  progressText: string;
  dangers: StructureRef[];
  patientNotes: string[];
  why: string;
  lookHere: string;
  nextTitle: string;
}

export interface CoachMistakeView {
  stepId: string;
  mistakeId: string;
  severity: Severity;
  structure: string;
  feedback: string;
  at: string;
}

export interface CoachCommand {
  commandId: string;
  action: "highlight" | "clear_highlight";
  targetId: string;
  status: "pending" | "applied" | "rejected";
  reason: string;
  requestedAt: string;
}

export interface CoachSnapshot {
  sessionId: string;
  version: number;
  mode: PresentationMode;
  eventCount: number;
  headsetStepId: string;
  desynced: boolean;
  resyncCount: number;
  status: "active" | "paused" | "completed";
  caseId: string;
  patientId: string;
  patientLabel: string;
  procedureId: string;
  procedureTitle: string;
  urgency: string;
  stepNumber: number;
  stepCount: number;
  completedCount: number;
  step: CoachStepView;
  stuckLevel: number;
  stuckLabel: string;
  secondsOnStep: number;
  secondsSinceProgress: number;
  offTargetAttempts: number;
  hintTier: number;
  focusStructure: StructureRef;
  trackingValid: boolean;
  lastEvent: string;
  recentMistakes: CoachMistakeView[];
  completedSteps: { stepId: string; title: string; seconds: number; mistakes: number }[];
  mistakeCount: number;
  highSeverityMistakeCount: number;
  hintsUsed: number;
  elapsedSeconds: number;
  guidance: { say: string; highlight: string[] };
  commands: CoachCommand[];
}

export interface StuckPolicy {
  // Seconds without progress before each hint tier, and off-target attempts that trigger the same tier.
  seconds: [number, number, number];
  attempts: [number, number, number];
}

export const DEFAULT_STUCK_POLICY: StuckPolicy = { seconds: [20, 45, 75], attempts: [2, 3, 5] };

export const STUCK_LABELS = ["on track", "nudge", "look here", "walk through"] as const;

const VERBS: Record<StepAction, string> = {
  place_port: "place",
  retract: "grasp and retract",
  dissect: "dissect",
  identify: "identify",
  clip: "clip",
  divide: "cut",
  seal: "seal",
  staple: "staple across",
  extract: "bag and extract",
  inspect: "inspect",
  anastomose: "join",
  close: "close",
};

export interface EventOutcome {
  accepted: boolean;
  reason: string;
  alerts: CoachAlert[];
}

export class CoachSession {
  readonly engine: StepEngine;
  version = 0;
  private startedAt: number;
  private stepStartedAt: number;
  private lastProgressAt: number;
  private offTarget = 0;
  private tier = 0; // hint tier already delivered on this step
  private hintsUsed = 0;
  private inputCount = 0; // exercise inputs that reached the engine; 0 means an untouched attempt
  private seenEventIds = new Map<string, string>();
  private alertLog: LoggedAlert[] = [];
  private alertLogSeq = 0;
  private headsetStepId = "";
  private desynced = false;
  private resyncCount = 0;
  private focus = "";
  private trackingValid = true;
  private lastEvent = "Session started.";
  private warnedFocus = new Set<string>();
  private mistakes: CoachMistakeView[] = [];
  private completed: { stepId: string; title: string; seconds: number; mistakes: number }[] = [];
  private commands: CoachCommand[] = [];
  private alertSeq = 0;
  private commandSeq = 0;
  private listeners = new Set<(update: { snapshot: CoachSnapshot; alerts: CoachAlert[] }) => void>();

  constructor(
    readonly id: string,
    readonly kase: SurgicalCase,
    private readonly clock: () => Date = () => new Date(),
    private readonly policy: StuckPolicy = DEFAULT_STUCK_POLICY,
    readonly mode: PresentationMode = "mixed_reality",
  ) {
    this.engine = new StepEngine(kase.procedure);
    this.startedAt = this.stepStartedAt = this.lastProgressAt = this.ms();
  }

  private ms() {
    return this.clock().getTime();
  }

  get done() {
    return this.engine.completed;
  }

  subscribe(fn: (update: { snapshot: CoachSnapshot; alerts: CoachAlert[] }) => void): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  get listenerCount() {
    return this.listeners.size;
  }

  private changed(alerts: CoachAlert[]) {
    this.version += 1;
    for (const a of alerts) a.version = this.version;
    const snapshot = this.snapshot();
    // Clients that cannot hold an SSE stream (the native headset voice client) poll this log instead.
    const tag = snapshot.status === "completed" ? `v${snapshot.version} complete` : `v${snapshot.version} step ${snapshot.stepNumber}/${snapshot.stepCount} "${snapshot.step.title}"`;
    for (const a of alerts) {
      this.alertLog.push({ ...a, seq: ++this.alertLogSeq, simEvent: `[SIM EVENT ${tag}] kind=${a.kind} tier=${a.tier}: ${a.say}` });
    }
    if (this.alertLog.length > 200) this.alertLog.splice(0, this.alertLog.length - 200);
    for (const fn of this.listeners) fn({ snapshot, alerts });
  }

  private alert(kind: AlertKind, priority: AlertPriority, say: string, highlight: string[] = [], stepId = this.engine.current?.id ?? "", reflexKey = "", reflexText = say): CoachAlert {
    this.alertSeq += 1;
    const tier: AlertTier = priority === "urgent" ? "warning" : priority === "low" ? "advisory" : "caution";
    return {
      id: `${this.id}-a${this.alertSeq}`,
      kind,
      priority,
      tier,
      stepId,
      version: this.version,
      say,
      reflexKey,
      reflexText: reflexKey ? reflexText : "",
      highlight,
      at: this.clock().toISOString(),
    };
  }

  private name(id: string): string {
    // Atlas parts outside the catalog look like "skeletal__rib_7_l"; speak the part, not the system prefix.
    return this.kase.anatomy.find((a) => a.id === id)?.displayName ?? (id.split("__").pop() ?? id).replaceAll("_", " ");
  }

  private ref(id: string): StructureRef {
    return { id, name: id ? this.name(id) : "" };
  }

  private instrumentName(id: string) {
    return INSTRUMENTS_BY_ID.get(id)?.displayName ?? id;
  }

  private portLabel(id: string) {
    return this.kase.procedure.ports.find((p) => p.id === id)?.label ?? id;
  }

  private dangersOf(step: ProcedureStep): string[] {
    return [...new Set(step.mistakes.map((m) => m.structure))];
  }

  private resetStep() {
    this.stepStartedAt = this.lastProgressAt = this.ms();
    this.offTarget = 0;
    this.tier = 0;
    this.warnedFocus.clear();
  }

  // Entry point for headset input. eventId makes retries safe (a repeated clip touch must not count twice);
  // stepId is the step the headset's own CaseRunner was on, which is the authority for progression.
  receive(event: CoachEvent, meta: { eventId?: string; stepId?: string } = {}): EventOutcome {
    if (meta.eventId) {
      if (this.seenEventIds.has(meta.eventId)) return { accepted: false, reason: this.seenEventIds.get(meta.eventId) || "duplicate", alerts: [] };
      this.seenEventIds.set(meta.eventId, "");
      if (this.seenEventIds.size > 10000) this.seenEventIds.delete(this.seenEventIds.keys().next().value!);
    }
    if (meta.stepId && event.type !== "focus" && event.type !== "tracking") {
      this.reconcile(meta.stepId);
      if (this.desynced) {
        // Preserve rejection across a lost HTTP response; retrying this event must
        // not turn a desynchronized action into an accepted duplicate receipt.
        if (meta.eventId) this.seenEventIds.set(meta.eventId, "step_desynchronized");
        this.changed([]);
        return { accepted: false, reason: "step_desynchronized", alerts: [] };
      }
    }
    return this.handle(event);
  }

  // The headset's CaseRunner owns progression. If it is ahead, catch up silently; if it is behind or on a
  // step we do not know, flag the desync so Jarvis trusts the headset instead of coaching the wrong step.
  private reconcile(headsetStepId: string) {
    this.headsetStepId = headsetStepId;
    const steps = this.kase.procedure.steps;
    const target = steps.findIndex((s) => s.id === headsetStepId);
    let current = this.engine.current ? steps.findIndex((s) => s.id === this.engine.current!.id) : steps.length;
    if (target === current) {
      this.desynced = false;
      return;
    }
    if (target > current) {
      for (let guard = 0; guard < 500 && this.engine.current && this.engine.current.id !== headsetStepId; guard++) {
        const before = this.engine.current;
        const e = this.nextCorrectEvent();
        if (!e) break;
        const r = this.engine.handle(e);
        if (r.advanced) this.completed.push({ stepId: before.id, title: before.title, seconds: 0, mistakes: this.mistakes.filter((m) => m.stepId === before.id).length });
      }
      current = this.engine.current ? steps.findIndex((s) => s.id === this.engine.current!.id) : steps.length;
      this.resetStep();
      this.resyncCount += 1;
      this.desynced = current !== target;
      this.lastEvent = `Resynced to the headset at ${steps[target]?.title.toLowerCase() ?? headsetStepId}.`;
      return;
    }
    this.desynced = true;
  }

  handle(event: CoachEvent): EventOutcome {
    if (event.type === "tracking") return this.handleTracking(event.valid);
    if (event.type === "focus") return this.handleFocus(event.structureId);

    const step = this.engine.current;
    if (!step) return { accepted: false, reason: "case_completed", alerts: [] };
    if (!this.trackingValid) {
      return { accepted: false, reason: "tracking_invalid", alerts: [] };
    }

    this.inputCount += 1;
    const before = this.engine.stepProgress;
    const result = this.engine.handle(event);
    const alerts: CoachAlert[] = [];

    if (result.mistake) {
      const m = result.mistake;
      this.mistakes.push({ stepId: step.id, mistakeId: m.id, severity: m.severity, structure: m.structure, feedback: m.feedback, at: this.clock().toISOString() });
      this.offTarget += 1;
      this.lastEvent = `Mistake on ${this.name(m.structure)}: ${m.feedback}`;
      const urgent = m.severity === "high";
      const say = urgent ? reflexLine(m.feedback) : m.feedback;
      alerts.push(this.alert("mistake", urgent ? "urgent" : "normal", say, [m.structure, ...step.targets.slice(0, 1)], step.id, `mistake.${m.id}`));
    } else if (result.advanced) {
      const seconds = Math.round((this.ms() - this.stepStartedAt) / 1000);
      const stepMistakes = this.mistakes.filter((m) => m.stepId === step.id).length;
      this.completed.push({ stepId: step.id, title: step.title, seconds, mistakes: stepMistakes });
      this.resetStep();
      this.lastEvent = `Completed step: ${step.title}.`;
      const next = this.engine.current;
      if (next) {
        alerts.push(this.alert("step_complete", "normal", `${step.title} done. ${nextStepLine(next.title)}`, next.targets, next.id, `step.${next.id}`, nextStepLine(next.title)));
      } else {
        const total = this.mistakes.length;
        alerts.push(this.alert("case_complete", "normal", `That completes the ${this.kase.procedure.title.toLowerCase()} with ${total === 0 ? "no mistakes" : `${total} mistake${total === 1 ? "" : "s"}`}.`, [], step.id));
      }
    } else {
      const after = this.engine.stepProgress;
      const progressed = after.done.length > before.done.length || after.applied > before.applied;
      if (progressed) {
        this.lastProgressAt = this.ms();
        this.lastEvent = `Progress on ${step.title.toLowerCase()}: ${this.progressText(step)}.`;
      } else {
        this.offTarget += 1;
        const wrongTool = event.type === "touch" && step.targets.includes(event.structureId) && event.instrumentId !== step.instrumentId;
        if (wrongTool) {
          const need = this.instrumentName(step.instrumentId);
          this.lastEvent = `Right structure (${this.name(event.structureId)}) but wrong instrument (${this.instrumentName(event.instrumentId)}); this step needs the ${need}.`;
          alerts.push(this.alert("wrong_instrument", "normal", `Right spot, wrong tool. This step needs the ${need}.`, [event.structureId]));
        } else {
          this.lastEvent = describeOffTarget(event, (id) => this.name(id), (id) => this.instrumentName(id), (id) => this.portLabel(id));
        }
      }
    }

    alerts.push(...this.checkStuck());
    this.changed(alerts);
    return { accepted: true, reason: "", alerts };
  }

  private handleTracking(valid: boolean): EventOutcome {
    if (valid === this.trackingValid) return { accepted: true, reason: "", alerts: [] };
    this.trackingValid = valid;
    const alerts: CoachAlert[] = [];
    if (valid) {
      // The pause should not count as being stuck.
      this.lastProgressAt = this.ms();
      this.lastEvent = "Tracking restored; scoring resumed.";
      alerts.push(this.alert("tracking_restored", "low", "Tracking is back. Pick up where you left off."));
    } else {
      this.lastEvent = "Tracking lost; anatomy hidden and scoring paused.";
      alerts.push(this.alert("tracking_lost", "urgent", trackingLostLine(this.mode), [], this.engine.current?.id ?? "", "tracking_lost"));
    }
    this.changed(alerts);
    return { accepted: true, reason: "", alerts };
  }

  private handleFocus(structureId: string): EventOutcome {
    if (structureId === this.focus) return { accepted: true, reason: "", alerts: [] };
    this.focus = structureId;
    const alerts: CoachAlert[] = [];
    const step = this.engine.current;
    // Looking at a step's own target is the point of the step, even if mishandling it is a listed mistake.
    const danger = step && structureId && this.dangersOf(step).includes(structureId) && !step.targets.includes(structureId);
    if (step && danger && !this.warnedFocus.has(structureId)) {
      this.warnedFocus.add(structureId);
      // The step's own mistake feedback is the relevant warning; the general fact is the fallback.
      const reason = step.mistakes.find((m) => m.structure === structureId)?.feedback ?? STRUCTURE_FACTS[structureId]?.why ?? "";
      alerts.push(this.alert("danger_focus", "normal", `Careful, that's the ${this.name(structureId).toLowerCase()}. ${reason}`.trim(), [structureId]));
    }
    this.changed(alerts);
    return { accepted: true, reason: "", alerts };
  }

  // Called on a timer: escalates the hint tier once per level as time passes without progress.
  tick(): CoachAlert[] {
    const alerts = this.checkStuck();
    if (alerts.length) this.changed(alerts);
    return alerts;
  }

  private stuckLevel(): number {
    if (this.done || !this.trackingValid) return 0;
    const idle = (this.ms() - this.lastProgressAt) / 1000;
    let level = 0;
    for (let i = 0; i < 3; i++) {
      if (idle >= this.policy.seconds[i]! || this.offTarget >= this.policy.attempts[i]!) level = i + 1;
    }
    return level;
  }

  private checkStuck(): CoachAlert[] {
    const step = this.engine.current;
    const level = this.stuckLevel();
    if (!step || level <= this.tier) return [];
    this.tier = level;
    const hint = this.hintAt(step, level);
    return [this.alert("stuck", "normal", hint.say, hint.highlight)];
  }

  // Learner asked for help: deliver the next tier immediately.
  requestHint(): { tier: number; say: string; highlight: string[] } {
    const step = this.engine.current;
    if (!step) return { tier: 0, say: "The procedure is complete. Nothing left to do.", highlight: [] };
    this.tier = Math.min(3, this.tier + 1);
    this.hintsUsed += 1;
    const hint = this.hintAt(step, this.tier);
    this.lastEvent = `Learner asked for a hint (tier ${this.tier}).`;
    this.changed([]);
    return { tier: this.tier, ...hint };
  }

  hintAt(step: ProcedureStep, tier: number): { say: string; highlight: string[] } {
    const coaching = STEP_COACHING[this.kase.procedure.id]?.[step.id];
    const remaining = this.remainingTargets(step);
    if (tier <= 1) {
      const why = coaching?.why ?? step.instruction;
      const authored = step.hints[0] && !restates(why, step.hints[0]) ? ` ${step.hints[0]}` : "";
      return { say: `${why}${authored}`, highlight: [] };
    }
    if (tier === 2) {
      return { say: coaching?.lookHere ?? step.instruction, highlight: remaining.structures.length ? remaining.structures : step.targets };
    }
    const ports = step.portIds.map((p) => this.portLabel(p).toLowerCase());
    const via = ports.length ? ` through the ${ports.join(" or ")}` : "";
    return {
      say: `Take the ${this.instrumentName(step.instrumentId).toLowerCase()}${via} and ${remaining.text}. ${step.instruction}`,
      highlight: remaining.structures.length ? remaining.structures : step.targets,
    };
  }

  private remainingTargets(step: ProcedureStep): { text: string; structures: string[]; labels: string[] } {
    const { done, applied } = this.engine.stepProgress;
    const { check } = step;
    const verb = VERBS[step.action];
    switch (check.type) {
      case "place_ports": {
        const left = check.targets.filter((t) => !done.includes(t));
        const labels = left.map((p) => this.portLabel(p));
        return { text: `place the ${labels.map((l) => l.toLowerCase()).join(" and ")}`, structures: [], labels };
      }
      case "apply_count": {
        const left = Math.max(0, check.count - applied);
        const target = check.targets[0] ?? "";
        const labels = [`${left} more on the ${this.name(target).toLowerCase()}`];
        return { text: `${verb} the ${this.name(target).toLowerCase()} ${left} more time${left === 1 ? "" : "s"}`, structures: [target], labels };
      }
      case "confirm":
        return { text: "confirm when the step is finished", structures: [], labels: ["confirmation"] };
      default: {
        const left = check.targets.filter((t) => !done.includes(t));
        const labels = left.map((t) => this.name(t));
        return { text: `${verb} the ${labels.map((l) => l.toLowerCase()).join(" and ")}`, structures: left, labels };
      }
    }
  }

  private progressText(step: ProcedureStep): string {
    const { done, applied } = this.engine.stepProgress;
    const { check } = step;
    switch (check.type) {
      case "apply_count":
        return `${applied} of ${check.count} applied`;
      case "confirm":
        return "waiting for confirmation";
      case "place_ports":
        return `${done.length} of ${check.targets.length} ports placed`;
      default:
        return `${done.length} of ${check.targets.length} targets done`;
    }
  }

  // Unity (or the SpacetimeDB bridge) acks scene commands; Jarvis only claims what was applied.
  requestCommand(action: CoachCommand["action"], targetId: string): CoachCommand | { error: string } {
    if (action === "highlight" && !this.kase.anatomy.some((a) => a.id === targetId)) {
      return { error: `${targetId} is not part of this case's anatomy.` };
    }
    this.commandSeq += 1;
    const command: CoachCommand = {
      commandId: `${this.id}-c${this.commandSeq}`,
      action,
      targetId,
      status: "pending",
      reason: "",
      requestedAt: this.clock().toISOString(),
    };
    this.commands.push(command);
    if (this.commands.length > 20) this.commands.shift();
    this.changed([]);
    return command;
  }

  ackCommand(commandId: string, status: "applied" | "rejected", reason = ""): CoachCommand | null {
    const command = this.commands.find((c) => c.commandId === commandId);
    if (!command) return null;
    // The first scene outcome is final. A lost response can cause an identical retry,
    // and a delayed contradictory acknowledgement must not rewrite that outcome.
    if (command.status !== "pending") return command;
    command.status = status;
    command.reason = reason;
    this.changed([]);
    return command;
  }

  command(commandId: string): CoachCommand | undefined {
    return this.commands.find((c) => c.commandId === commandId);
  }

  alertsAfter(seq: number): { alerts: LoggedAlert[]; latestSeq: number } {
    return { alerts: this.alertLog.filter((a) => a.seq > seq), latestSeq: this.alertLogSeq };
  }

  pendingCommands(): CoachCommand[] {
    return this.commands.filter((c) => c.status === "pending");
  }

  // The next correct event for the current step, for demo autoplay and tests.
  nextCorrectEvent(): EngineEvent | null {
    const step = this.engine.current;
    if (!step) return null;
    const { done } = this.engine.stepProgress;
    const events = perfectEvents(step);
    if (step.check.type === "apply_count" || step.check.type === "confirm") return events[0] ?? null;
    return (
      events.find((e) => {
        if (e.type === "place_port") return !done.includes(e.portId);
        if (e.type === "touch" || e.type === "identify") return !done.includes(e.structureId);
        return true;
      }) ?? null
    );
  }

  // A plausible wrong event for the current step, for demo and tests.
  sampleMistakeEvent(): EngineEvent | null {
    const step = this.engine.current;
    const m = step?.mistakes[0];
    if (!step || !m) return null;
    return m.trigger === "wrong_identification" ? { type: "identify", structureId: m.structure } : { type: "touch", structureId: m.structure, instrumentId: step.instrumentId };
  }

  snapshot(): CoachSnapshot {
    const step = this.engine.current;
    const procedure = this.kase.procedure;
    const now = this.ms();
    const index = step ? procedure.steps.findIndex((s) => s.id === step.id) : procedure.steps.length;
    const level = this.stuckLevel();
    const highSeverity = this.mistakes.filter((m) => m.severity === "high").length;
    const view = step ? this.stepView(step) : emptyStepView();
    const guidance = step
      ? this.hintAt(step, Math.max(1, this.tier, level))
      : { say: `The ${procedure.title.toLowerCase()} is complete.`, highlight: [] };

    return {
      sessionId: this.id,
      version: this.version,
      mode: this.mode,
      eventCount: this.inputCount,
      headsetStepId: this.headsetStepId,
      desynced: this.desynced,
      resyncCount: this.resyncCount,
      status: !step ? "completed" : this.trackingValid ? "active" : "paused",
      caseId: this.kase.caseId,
      patientId: this.kase.patientId,
      patientLabel: this.kase.patient.displayLabel,
      procedureId: procedure.id,
      procedureTitle: procedure.title,
      urgency: this.kase.urgency,
      stepNumber: index + 1,
      stepCount: procedure.steps.length,
      completedCount: this.completed.length,
      step: view,
      stuckLevel: level,
      stuckLabel: STUCK_LABELS[level] ?? "on track",
      secondsOnStep: step ? Math.round((now - this.stepStartedAt) / 1000) : 0,
      secondsSinceProgress: step ? Math.round((now - this.lastProgressAt) / 1000) : 0,
      offTargetAttempts: this.offTarget,
      hintTier: this.tier,
      focusStructure: this.ref(this.focus),
      trackingValid: this.trackingValid,
      lastEvent: this.lastEvent,
      recentMistakes: this.mistakes.slice(-5),
      completedSteps: [...this.completed],
      mistakeCount: this.mistakes.length,
      highSeverityMistakeCount: highSeverity,
      hintsUsed: this.hintsUsed,
      elapsedSeconds: Math.round((now - this.startedAt) / 1000),
      guidance,
      commands: [...this.commands],
    };
  }

  private stepView(step: ProcedureStep): CoachStepView {
    const coaching = STEP_COACHING[this.kase.procedure.id]?.[step.id];
    const next = this.kase.procedure.steps.find((s) => s.id === step.next);
    return {
      id: step.id,
      title: step.title,
      instruction: step.instruction,
      action: step.action,
      instrumentId: step.instrumentId,
      instrumentName: this.instrumentName(step.instrumentId),
      ports: step.portIds.map((p) => this.portLabel(p)),
      targets: step.targets.map((t) => this.ref(t)),
      remaining: this.remainingTargets(step).labels,
      progressText: this.progressText(step),
      dangers: this.dangersOf(step).map((d) => this.ref(d)),
      patientNotes: this.kase.considerations.filter((c) => c.stepId === step.id).map((c) => c.note),
      why: coaching?.why ?? "",
      lookHere: coaching?.lookHere ?? "",
      nextTitle: next?.title ?? "",
    };
  }
}

// Mixed reality overlays generic anatomy on a real reclining person; full VR uses a virtual patient and room.
// Same coach, steps, and tools; only what "tracking" means and how the scene is described differ.
export type PresentationMode = "mixed_reality" | "virtual";
export const PRESENTATION_MODES: PresentationMode[] = ["mixed_reality", "virtual"];

export function trackingLostLine(mode: PresentationMode): string {
  return mode === "virtual"
    ? "I've lost headset tracking, so I've paused. Hold still for a moment."
    : "I've lost tracking on the patient, so I've paused. Hold still and look back at the torso.";
}

// Urgent mistake lines open with "Stop." so the reflex clip and any LLM follow-up sound the same.
export function reflexLine(feedback: string): string {
  return /^(stop|careful)\b/i.test(feedback) ? feedback : `Stop. ${feedback}`;
}

// Milestone callouts are deterministic too: no LLM turn just to announce the next step.
export function nextStepLine(title: string): string {
  return `Next step: ${title.charAt(0).toLowerCase()}${title.slice(1)}.`;
}

// Every pre-renderable line for a case: high-severity mistake warnings, tracking loss, and next-step callouts.
export function reflexLines(kase: SurgicalCase, mode: PresentationMode = "mixed_reality"): { key: string; text: string }[] {
  const lines = kase.procedure.steps.flatMap((s) => s.mistakes.filter((m) => m.severity === "high").map((m) => ({ key: `mistake.${m.id}`, text: reflexLine(m.feedback) })));
  const unique = [...new Map(lines.map((l) => [l.key, l])).values()];
  const callouts = kase.procedure.steps.slice(1).map((s) => ({ key: `step.${s.id}`, text: nextStepLine(s.title) }));
  return [...unique, { key: "tracking_lost", text: trackingLostLine(mode) }, ...callouts];
}

// True when the authored hint mostly repeats the coaching sentence, so the nudge says it once.
function restates(a: string, b: string): boolean {
  const words = (s: string) => new Set(s.toLowerCase().match(/[a-z]{4,}/g) ?? []);
  const wa = words(a);
  const wb = [...words(b)];
  return wb.length > 0 && wb.filter((w) => wa.has(w)).length / wb.length >= 0.5;
}

function emptyStepView(): CoachStepView {
  return {
    id: "",
    title: "",
    instruction: "",
    action: "",
    instrumentId: "",
    instrumentName: "",
    ports: [],
    targets: [],
    remaining: [],
    progressText: "",
    dangers: [],
    patientNotes: [],
    why: "",
    lookHere: "",
    nextTitle: "",
  };
}

function describeOffTarget(event: EngineEvent, name: (id: string) => string, tool: (id: string) => string, port: (id: string) => string): string {
  switch (event.type) {
    case "touch":
      return `Touched the ${name(event.structureId).toLowerCase()} with the ${tool(event.instrumentId).toLowerCase()}; that does not advance this step.`;
    case "identify":
      return `Identified the ${name(event.structureId).toLowerCase()}; that is not what this step asks for.`;
    case "place_port":
      return `Placed the ${port(event.portId).toLowerCase()}; that port is not part of this step.`;
    case "confirm":
      return "Tried to confirm, but this step finishes on its targets, not a confirmation.";
  }
}

// Changes only when something Jarvis should know changes; ticking timers do not. Clients send the
// context to the agent only when this key differs from the last one they sent.
export function contextKey(s: CoachSnapshot): string {
  const parts = [
    s.status, s.step.id, s.step.progressText, s.completedCount, s.mistakeCount, s.focusStructure.id, s.stuckLevel,
    s.trackingValid, s.hintTier, s.desynced, s.commands.map((c) => `${c.commandId}:${c.status}`).join(","),
  ];
  let h = 2166136261;
  for (const ch of JSON.stringify(parts)) h = Math.imul(h ^ ch.charCodeAt(0), 16777619);
  return (h >>> 0).toString(16);
}

// Compact text for the voice agent's contextual updates. Short lines, facts only.
export function renderContext(s: CoachSnapshot): string {
  if (s.status === "completed") {
    return [
      `[LIVE SURGERY STATE v${s.version}] ${s.procedureTitle} for ${s.patientLabel}: COMPLETE.`,
      `Mistakes: ${s.mistakeCount} (${s.highSeverityMistakeCount} high severity). Hints used: ${s.hintsUsed}. Time: ${s.elapsedSeconds}s.`,
      s.completedSteps.map((c) => `${c.title} ${c.seconds}s${c.mistakes ? `, ${c.mistakes} mistake(s)` : ""}`).join("; "),
    ].join("\n");
  }
  const st = s.step;
  const desync = s.desynced
    ? `HEADSET DISAGREES: the headset reports step "${s.headsetStepId}" while this state shows "${st.id}". Trust the headset; describe progress only in general terms until they agree.`
    : "";
  const lines = [
    ...(desync ? [desync] : []),
    `[LIVE SURGERY STATE v${s.version}] ${s.procedureTitle} for ${s.patientLabel}. ${s.status === "paused" ? "PAUSED: tracking lost, anatomy hidden, scoring paused." : ""}`.trim(),
    `Step ${s.stepNumber} of ${s.stepCount}: ${st.title}. ${st.instruction}`,
    `Instrument: ${st.instrumentName}${st.ports.length ? ` via ${st.ports.join(" / ")}` : ""}. Progress: ${st.progressText}. Still needed: ${st.remaining.join(", ") || "nothing"}.`,
  ];
  if (st.dangers.length) lines.push(`Danger structures this step: ${st.dangers.map((d) => d.name).join(", ")}.`);
  if (st.patientNotes.length) lines.push(`Patient-specific: ${st.patientNotes.join(" ")}`);
  if (s.focusStructure.id) lines.push(`Learner is looking at: ${s.focusStructure.name}.`);
  lines.push(`Last event: ${s.lastEvent}`);
  lines.push(`Time on step ${s.secondsOnStep}s, ${s.secondsSinceProgress}s since progress, ${s.offTargetAttempts} off-target attempts. Coaching level: ${s.stuckLabel} (hint tier ${s.hintTier}).`);
  const recent = s.recentMistakes.filter((m) => m.stepId === st.id);
  if (recent.length) lines.push(`Mistakes this step: ${recent.map((m) => m.feedback).join(" ")}`);
  lines.push(`If asked what to do: ${s.guidance.say}`);
  if (st.nextTitle) lines.push(`After this: ${st.nextTitle}.`);
  return lines.join("\n");
}
