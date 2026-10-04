import { STEP_COACHING, STRUCTURE_FACTS } from "./catalog/coach-knowledge.js";
import { INSTRUMENTS_BY_ID } from "./catalog/instruments.js";
import type { BodyGrade } from "./open-body-grade.js";
import { StepEngine, perfectEvents, type EngineEvent } from "./engine.js";
import { bodyAction, isBodyTelemetry, type BodyAction, type BodyPredicate, type BodyState } from "./open-body.js";
import { CLASS_LINES, DEATH_LINE, PatientCondition, REGIONS, type ConditionView, type RegionId } from "./patient-condition.js";
import type { Baseline } from "./physiology.js";
import type { ProcedureStep, Severity, StepAction, SurgicalCase } from "./types.js";

// Live coaching state for one surgery attempt. Wraps the reference StepEngine (same semantics as
// CaseRunner.cs) and adds what a coach needs: time on step, off-target attempts, wrong instruments,
// what the learner is looking at, tracking validity, and escalating hints. Everything Jarvis says
// about progress comes from this state, never from the model's own guess.

export type CoachEvent =
  | EngineEvent
  | { type: "focus"; structureId: string } // learner gaze or instrument hover, from Unity
  // State tracker facts that do not score: a tool picked up or put down, and a tool tip touching tissue.
  | { type: "instrument"; instrumentId: string; hand: "left" | "right"; held: boolean }
  | { type: "contact"; instrumentId: string; structureId: string }
  // A cutting tool hit a body region outside the surgical field (coarse regions, docs/operation-flow.md);
  // controlled: true when the learner gets control of that region's bleeding.
  | { type: "injury"; region: RegionId; instrumentId: string; controlled?: boolean }
  | { type: "tracking"; valid: boolean } // registration validity; invalid pauses scoring
  // Simulated vessel injury from the headset's tissue model; totalMl is cumulative for the attempt.
  | { type: "bleeding"; structureId: string; active: boolean; rateMlPerMin: number; totalMl: number };

export type AlertKind =
  | "mistake"
  | "danger_focus"
  | "wrong_instrument"
  | "step_complete"
  | "case_complete"
  | "stuck"
  | "tracking_lost"
  | "tracking_restored"
  | "bleeding"
  | "bleeding_controlled"
  | "region_injury"
  | "vitals"
  | "patient_died";

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

// A completed step, kept so Jarvis can refer back ("you nicked the ileum two steps ago").
export interface StepCheckpoint {
  stepId: string;
  title: string;
  seconds: number;
  mistakes: number;
  hints: number;
  bloodLossMl: number;
  at: string;
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
  timeline: { atSeconds: number; text: string }[]; // oldest first, session clock
  held: { hand: "left" | "right"; instrumentId: string; name: string; touching: StructureRef }[];
  recentMistakes: CoachMistakeView[];
  completedSteps: StepCheckpoint[];
  bloodLossMl: number;
  scene: { summary: string; at: string; source: string };
  activeBleeds: { structure: StructureRef; rateMlPerMin: number }[];
  mistakeCount: number;
  highSeverityMistakeCount: number;
  hintsUsed: number;
  elapsedSeconds: number;
  guidance: { say: string; highlight: string[] };
  commands: CoachCommand[];
  openBody: boolean;
  bodyFacts: { key: string; value: number }[];
  bodyGrade?: BodyGrade;
  achievedMilestones: string[];
  orderDeviations: string[];
  decisionPrompts: { id: string; prompt: string; choices: string[] }[];
  // The procedure's steps for the on-screen checklist: guidance only, never an action gate.
  checklist: { id: string; title: string; done: boolean; current: boolean }[];
  // Simulated vitals, injuries outside the field, and the case outcome (patient-condition.ts).
  condition: ConditionView;
}

export interface StuckPolicy {
  // Seconds without progress before each hint tier, and off-target attempts that trigger the same tier.
  seconds: [number, number, number, number?];
  attempts: [number, number, number, number?];
}

export const DEFAULT_STUCK_POLICY: StuckPolicy = { seconds: [20, 45, 75], attempts: [2, 3, 5] };

export const OPEN_BODY_STUCK_POLICY: StuckPolicy = { seconds: [15, 25, 40, 60], attempts: [2, 3, 5, 8] };
export const STUCK_LABELS = ["on track", "nudge", "look here", "walk through", "assistance offer"] as const;

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
  // Rolling plain-language log of what physically happened, so Jarvis knows the recent sequence.
  private timeline: { atMs: number; text: string }[] = [];
  private held = new Map<"left" | "right", string>(); // hand -> instrument id
  private touching = new Map<string, string>(); // instrument id -> structure its tip last touched
  private warnedFocus = new Set<string>();
  private mistakes: CoachMistakeView[] = [];
  private completed: StepCheckpoint[] = [];
  private stepHints = 0;
  private bleeds = new Map<string, number>(); // structureId -> ml/min
  private bleedStartMs = new Map<string, number>(); // open body: headset time each active bleed began
  private bleedEscalated = new Set<string>();
  private bloodLossMl = 0;
  // Simulated vitals and outcome; the baseline is set from the chart (VR) or Presage (AR) by the route.
  readonly condition = new PatientCondition(() => this.ms());
  simulated = false; // laptop demo driver in use: the coach advances bleeding time itself
  private scene = { summary: "", at: "", source: "" }; // latest vision summary of the learner's view
  private commands: CoachCommand[] = [];
  private alertSeq = 0;
  private commandSeq = 0;
  private listeners = new Set<(update: { snapshot: CoachSnapshot; alerts: CoachAlert[] }) => void>();

  constructor(
    readonly id: string,
    readonly kase: SurgicalCase,
    private readonly clock: () => Date = () => new Date(),
    private readonly policy: StuckPolicy = kase.procedure.id === "open_appendectomy" ? OPEN_BODY_STUCK_POLICY : DEFAULT_STUCK_POLICY,
    readonly mode: PresentationMode = "mixed_reality",
  ) {
    this.engine = new StepEngine(kase.procedure);
    this.startedAt = this.stepStartedAt = this.lastProgressAt = this.ms();
  }

  private note(text: string) {
    this.lastEvent = text;
    this.timeline.push({ atMs: this.ms(), text });
    if (this.timeline.length > TIMELINE_MAX) this.timeline.shift();
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
    alerts.push(...this.conditionAlerts());
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

  private checkpoint(step: ProcedureStep, seconds: number): StepCheckpoint {
    return {
      stepId: step.id,
      title: step.title,
      seconds,
      mistakes: this.mistakes.filter((m) => m.stepId === step.id).length,
      hints: this.stepHints,
      bloodLossMl: Math.round(this.bloodLossMl),
      at: this.clock().toISOString(),
    };
  }

  private handleBleeding(e: { structureId: string; active: boolean; rateMlPerMin: number; totalMl: number }): EventOutcome {
    this.bloodLossMl = Math.max(this.bloodLossMl, Number.isFinite(e.totalMl) ? e.totalMl : 0);
    const alerts: CoachAlert[] = [];
    const known = this.bleeds.has(e.structureId);
    if (e.active) {
      this.bleeds.set(e.structureId, Math.max(0, e.rateMlPerMin));
      if (!known) {
        this.note(`Bleeding started from the ${this.name(e.structureId).toLowerCase()}.`);
        alerts.push(this.alert("bleeding", "urgent", bleedingLine(this.name(e.structureId)), [e.structureId], this.engine.current?.id ?? "", `bleeding.${e.structureId}`));
      }
    } else if (known) {
      this.bleeds.delete(e.structureId);
      this.note(`Bleeding from the ${this.name(e.structureId).toLowerCase()} controlled. Total blood loss ${Math.round(this.bloodLossMl)} ml.`);
      alerts.push(this.alert("bleeding_controlled", "low", `Bleeding controlled. Total loss about ${Math.round(this.bloodLossMl)} milliliters.`, [e.structureId]));
    }
    this.changed(alerts);
    return { accepted: true, reason: "", alerts };
  }

  private resetStep() {
    this.stepHints = 0;
    this.stepStartedAt = this.lastProgressAt = this.ms();
    this.offTarget = 0;
    this.tier = 0;
    this.warnedFocus.clear();
  }

  // Entry point for headset input. eventId makes retries safe (a repeated clip touch must not count twice);
  // stepId is the step the headset's own CaseRunner was on, which is the authority for progression.
  receive(event: CoachEvent, meta: { eventId?: string; stepId?: string } = {}): EventOutcome {
    if (event.type === "finish" && !this.engine.body) return { accepted: false, reason: "invalid: finish requires an open body case", alerts: [] };
    if (meta.eventId) {
      if (this.seenEventIds.has(meta.eventId)) return { accepted: false, reason: this.seenEventIds.get(meta.eventId) || "duplicate", alerts: [] };
      this.seenEventIds.set(meta.eventId, "");
      if (this.seenEventIds.size > 10000) this.seenEventIds.delete(this.seenEventIds.keys().next().value!);
    }
    // No catch-up while tracking is lost: handle() rejects the event, and nothing may be synthesized either.
    if (!this.kase.procedure.openBody && meta.stepId && event.type !== "focus" && event.type !== "tracking" && event.type !== "bleeding" && this.trackingValid) {
      this.reconcile(meta.stepId);
      if (this.desynced) {
        // Preserve rejection across a lost HTTP response; retrying this event must
        // not turn a desynchronized action into an accepted duplicate receipt.
        if (meta.eventId) this.seenEventIds.set(meta.eventId, "step_desynchronized");
        this.changed([]);
        return { accepted: false, reason: "step_desynchronized", alerts: [] };
      }
    }
    if (this.engine.body && meta.stepId) this.headsetStepId = meta.stepId;
    const outcome = this.handle(event);
    if (meta.eventId && outcome.reason.startsWith("invalid:")) this.seenEventIds.set(meta.eventId, outcome.reason);
    return outcome;
  }

  // The headset's CaseRunner owns progression. If it is exactly one step ahead (the event that completed our
  // current step was lost), catch up that one step. A larger jump, a step behind, or a step we do not know is
  // flagged as a desync so Jarvis trusts the headset; skipped steps are never synthesized as completed,
  // otherwise one event with a late stepId would award the whole procedure with a perfect record.
  private reconcile(headsetStepId: string) {
    this.headsetStepId = headsetStepId;
    const steps = this.kase.procedure.steps;
    const target = steps.findIndex((s) => s.id === headsetStepId);
    let current = this.engine.current ? steps.findIndex((s) => s.id === this.engine.current!.id) : steps.length;
    if (target === current) {
      this.desynced = false;
      return;
    }
    const before = this.engine.current;
    if (before && target !== -1 && steps[target]?.id === before.next) {
      for (let guard = 0; guard < 500 && this.engine.current === before; guard++) {
        const e = this.nextCorrectEvent();
        if (!e) break;
        const r = this.engine.handle(e);
        if (r.advanced) this.completed.push(this.checkpoint(before, 0));
      }
      current = this.engine.current ? steps.findIndex((s) => s.id === this.engine.current!.id) : steps.length;
      this.resetStep();
      this.resyncCount += 1;
      this.desynced = current !== target;
      this.note(`Resynced to the headset at ${steps[target]?.title.toLowerCase() ?? headsetStepId}.`);
      return;
    }
    this.desynced = true;
  }

  handle(event: CoachEvent): EventOutcome {
    if (this.condition.died) return { accepted: false, reason: "patient_died", alerts: [] };
    if (event.type === "tracking") return this.handleTracking(event.valid);
    if (event.type === "injury") return this.handleInjury(event);
    if (event.type === "bleeding") return this.kase.procedure.openBody
      ? { accepted: false, reason: "body_state_authoritative", alerts: [] } : this.handleBleeding(event);
    if (event.type === "focus") return this.handleFocus(event.structureId);
    if (event.type === "instrument") return this.handleInstrument(event);
    if (event.type === "contact") return this.handleContact(event.instrumentId, event.structureId);

    if (this.engine.body) return this.handleBodyEvent(event);
    if (event.type === "finish") return { accepted: false, reason: "invalid: finish requires an open body case", alerts: [] };
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
      this.note(`Mistake on ${this.name(m.structure)}: ${m.feedback}`);
      const urgent = m.severity === "high";
      const say = urgent ? reflexLine(m.feedback) : m.feedback;
      alerts.push(this.alert("mistake", urgent ? "urgent" : "normal", say, [m.structure, ...step.targets.slice(0, 1)], step.id, `mistake.${m.id}`));
    } else if (result.advanced) {
      const seconds = Math.round((this.ms() - this.stepStartedAt) / 1000);
      this.completed.push(this.checkpoint(step, seconds));
      this.resetStep();
      this.note(`Completed step: ${step.title}.`);
      const next = this.engine.current;
      if (next) {
        const line = this.kase.procedure.id === "open_appendectomy" ? STEP_COACHING.open_appendectomy?.[next.id]?.why ?? next.instruction : `${step.title} done. ${nextStepLine(next.title)}`;
        alerts.push(this.alert("step_complete", "normal", line, next.targets, next.id, `step.${next.id}`, line));
      } else {
        const total = this.mistakes.length;
        alerts.push(this.alert("case_complete", "normal", `That completes the ${this.kase.procedure.title.toLowerCase()} with ${total === 0 ? "no mistakes" : `${total} mistake${total === 1 ? "" : "s"}`}.`, [], step.id));
      }
    } else {
      const after = this.engine.stepProgress;
      const progressed = after.done.length > before.done.length || after.applied > before.applied;
      if (progressed) {
        this.lastProgressAt = this.ms();
        this.note(`Progress on ${step.title.toLowerCase()}: ${this.progressText(step)}.`);
      } else {
        this.offTarget += 1;
        const wrongTool = event.type === "touch" && step.targets.includes(event.structureId) && event.instrumentId !== step.instrumentId;
        if (wrongTool) {
          const need = this.instrumentName(step.instrumentId);
          this.note(`Right structure (${this.name(event.structureId)}) but wrong instrument (${this.instrumentName(event.instrumentId)}); this step needs the ${need}.`);
          alerts.push(this.alert("wrong_instrument", "normal", `Right spot, wrong tool. This step needs the ${need}.`, [event.structureId]));
        } else {
          this.note(describeOffTarget(event, (id) => this.name(id), (id) => this.instrumentName(id), (id) => this.portLabel(id)));
        }
      }
    }

    alerts.push(...this.checkStuck());
    this.changed(alerts);
    return { accepted: true, reason: "", alerts };
  }

  // The body reducer is the only authority for consequences; the current milestone is advice.
  private handleBodyEvent(event: EngineEvent): EventOutcome {
    if (this.engine.grade) return { accepted: event.type === "finish", reason: event.type === "finish" ? "" : "case_completed", alerts: [] };
    if (event.type === "finish") {
      this.engine.handle(event);
      const missing = this.engine.grade!.missingMilestones.length;
      this.lastEvent = `Attempt ended with ${missing} unmet milestones; recorded work preserved.`;
      const alerts = [this.alert("case_complete", "normal", "Attempt ended. Review completed goals and remaining safety findings.", [], "")];
      this.changed(alerts);
      return { accepted: true, reason: "", alerts };
    }
    if (!this.trackingValid) return { accepted: false, reason: "tracking_invalid", alerts: [] };
    if (event.type !== "surgery") return { accepted: false, reason: "invalid: body action required", alerts: [] };
    const body = this.engine.body!;
    if (body.log.some(r => r.action.actionId === event.evidence.actionId)) return { accepted: false, reason: "duplicate", alerts: [] };
    const count = body.log.length;
    const mistakeCount = this.engine.mistakes.length;
    const previous = this.engine.current;
    const wasComplete = this.engine.completed;
    const achieved = new Set(this.engine.completedMilestones);
    this.engine.handle(event);
    if (body.log.length === count) return { accepted: false, reason: "invalid: body action rejected", alerts: [] };
    if (!isBodyTelemetry(event.evidence)) this.inputCount++;
    const record = body.log.at(-1)!;
    const alerts: CoachAlert[] = [];
    // Assistant ticks and fluid snapshots are telemetry, not learner actions: keep them out of the history Jarvis reads.
    if (!isBodyTelemetry(event.evidence)) this.note(describeBodyAction(event.evidence, record.outcomes, (id) => this.name(id), (id) => this.instrumentName(id)));
    for (const m of this.engine.mistakes.slice(mistakeCount)) {
      this.mistakes.push({ stepId: previous?.id ?? "", mistakeId: m.id, severity: m.severity, structure: m.structure, feedback: m.feedback, at: this.clock().toISOString() });
      const urgent = m.severity === "high";
      alerts.push(this.alert("mistake", urgent ? "urgent" : "normal", urgent ? reflexLine(m.feedback) : m.feedback,
        [m.structure], "", urgent ? `mistake.${m.id}` : ""));
    }
    const nowBleeding = new Map<string, number>();
    for (const tissue of body.tissues) if (body.get(tissue.id, "bleeding") > 0) nowBleeding.set(tissue.id, (body.get(tissue.id,"fluidDriven") > 0 ? body.get(tissue.id,"measuredFlowMlPerSecond") : tissue.flowMlPerSecond) * 60);
    this.bloodLossMl = body.get("", "bloodLostMl");
    for (const [id] of nowBleeding) if (!this.bleeds.has(id))
      alerts.push(this.alert("bleeding", "urgent", bleedingLine(this.name(id)), [id], "", `bleeding.${id}`));
    for (const [id] of this.bleeds) if (!nowBleeding.has(id))
      alerts.push(this.alert("bleeding_controlled", "low", "Bleeding controlled. Check the field.", [id], ""));
    this.bleeds = nowBleeding;
    // Uses the headset clock from the event (1 Hz ticks while bleeding), so it is replayable.
    const at = event.evidence.timeMs;
    for (const id of [...this.bleedStartMs.keys()]) if (!nowBleeding.has(id)) { this.bleedStartMs.delete(id); this.bleedEscalated.delete(id); }
    for (const [id] of nowBleeding) {
      const start = this.bleedStartMs.get(id);
      if (start === undefined) this.bleedStartMs.set(id, at);
      else if (at - start >= UNCONTROLLED_BLEED_MS && !this.bleedEscalated.has(id)) {
        this.bleedEscalated.add(id);
        this.note(`Bleeding from the ${this.name(id).toLowerCase()} uncontrolled for ${Math.round((at - start) / 1000)} s.`);
        alerts.push(this.alert("bleeding", "urgent", uncontrolledBleedLine(this.name(id)), [id], "", `bleeding_uncontrolled.${id}`));
      }
    }
    const newly = this.kase.procedure.steps.filter(s => this.engine.completedMilestones.has(s.id) && !achieved.has(s.id));
    for (const milestone of newly) {
      this.completed.push(this.checkpoint(milestone, Math.round((this.ms() - this.stepStartedAt) / 1000)));
      this.note(`Milestone reached: ${milestone.title.toLowerCase()}.`);
    }
    if (newly.length) {
      this.resetStep();
      const next = this.engine.current;
      if (next && (!previous || next.id !== previous.id)) {
        const line = STEP_COACHING[this.kase.procedure.id]?.[next.id]?.why ?? next.instruction;
        alerts.push(this.alert("step_complete", "normal", line, next.targets, next.id, `step.${next.id}`, line));
      }
    }
    if (!wasComplete && this.engine.completed)
      alerts.push(this.alert("case_complete", "normal", "Case goals reached. Review the recorded safety findings.", [], ""));
    // A physical obstruction or injury warrants help; harmless exploration is not a wrong-tool error.
    if (record.outcomes.includes("not_exposed") || this.engine.mistakes.length > mistakeCount) this.offTarget++;
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
      this.note("Tracking restored; scoring resumed.");
      alerts.push(this.alert("tracking_restored", "low", "Tracking is back. Pick up where you left off."));
    } else {
      this.note("Tracking lost; anatomy hidden and scoring paused.");
      alerts.push(this.alert("tracking_lost", "urgent", trackingLostLine(this.mode), [], this.engine.current?.id ?? "", "tracking_lost"));
    }
    this.changed(alerts);
    return { accepted: true, reason: "", alerts };
  }

  private handleInstrument(e: { instrumentId: string; hand: "left" | "right"; held: boolean }): EventOutcome {
    const name = this.instrumentName(e.instrumentId).toLowerCase();
    if (e.held) {
      this.held.set(e.hand, e.instrumentId);
      this.note(`Picked up the ${name} (${e.hand} hand).`);
    } else {
      if (this.held.get(e.hand) === e.instrumentId) this.held.delete(e.hand);
      this.touching.delete(e.instrumentId);
      this.note(`Put down the ${name}.`);
    }
    this.changed([]);
    return { accepted: true, reason: "", alerts: [] };
  }

  // A tool tip touching tissue. It never scores; it tells Jarvis where the tool is, and warns once per
  // critical structure in open surgery before anything is cut.
  private handleContact(instrumentId: string, structureId: string): EventOutcome {
    this.touching.set(instrumentId, structureId);
    this.note(`${this.instrumentName(instrumentId)} touched the ${this.name(structureId).toLowerCase()}.`);
    const critical = this.kase.procedure.openBody?.tissues.some((t) => t.id === structureId && t.critical);
    if (critical && !this.warnedFocus.has(structureId)) {
      this.warnedFocus.add(structureId);
      this.focus = structureId;
      const alerts = [this.alert("danger_focus", "normal", `Careful, that's the ${this.name(structureId).toLowerCase()}. ${STRUCTURE_FACTS[structureId]?.why ?? ""}`.trim(), [structureId])];
      this.changed(alerts);
      return { accepted: true, reason: "", alerts };
    }
    if (structureId !== this.focus) return this.handleFocus(structureId);
    this.changed([]);
    return { accepted: true, reason: "", alerts: [] };
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
    if (this.simulated) this.advanceSimulatedBleeding();
    const alerts = this.checkStuck();
    const bleeding = this.bleeds.size > 0 || this.condition.view().regions.some((r) => r.bleeding);
    // While anything bleeds the vitals move every tick, so the state is republished.
    if (alerts.length || (bleeding && !this.condition.died)) this.changed(alerts);
    return alerts;
  }

  // quiet: at session creation, before anyone listens, so the state version does not move.
  setBaseline(baseline: Baseline, opts: { weightKg?: number; spo2?: number | null; mlPerKg?: number; quiet?: boolean } = {}) {
    this.condition.setBaseline(baseline, opts);
    this.note(`Vitals baseline set from ${baseline.source ?? "authored"} values: HR ${baseline.hr}, BP ${baseline.sys}/${baseline.dia}, RR ${baseline.rr}.`);
    if (!opts.quiet) this.changed([]);
  }

  // Laptop demo only: with no headset sending 1 Hz ticks, the coach sends the body reducer's tick itself
  // while something bleeds, so blood loss grows in real time.
  private lastSimTickMs = 0;

  private advanceSimulatedBleeding() {
    const body = this.engine.body;
    const now = this.ms();
    if (!body || this.condition.died || !this.bleeds.size) {
      this.lastSimTickMs = now;
      return;
    }
    // One headset second per elapsed clock second, however often tick() is called.
    if (!this.lastSimTickMs) this.lastSimTickMs = now;
    if (now - this.lastSimTickMs < 1000) return;
    this.lastSimTickMs += 1000 * Math.floor((now - this.lastSimTickMs) / 1000);
    const lastT = body.log.at(-1)?.action.timeMs ?? 0;
    const tissue = body.tissues[0]?.id ?? "skin";
    const evidence = bodyAction("tick", tissue, { actionId: `coach-tick-${lastT + 1000}`, instrumentId: "assistant", instrumentInstanceId: "", layer: body.tissues[0]?.layer ?? tissue, timeMs: lastT + 1000 });
    this.handle({ type: "surgery", evidence });
  }

  private handleInjury(e: { region: RegionId; instrumentId: string; controlled?: boolean }): EventOutcome {
    const def = REGIONS[e.region];
    if (e.controlled) {
      const stopped = this.condition.control(e.region);
      if (stopped) this.note(`Bleeding from the ${def.label} controlled.`);
      const alerts = stopped ? [this.alert("bleeding_controlled", "low", `Bleeding from the ${def.label} controlled.`, [], "")] : [];
      this.changed(alerts);
      return { accepted: true, reason: "", alerts };
    }
    if (!this.trackingValid) return { accepted: false, reason: "tracking_invalid", alerts: [] };
    if (this.condition.view().outcome.result !== "in_progress") return { accepted: false, reason: "case_ended", alerts: [] };
    const { first, rebled } = this.condition.injure(e.region);
    this.note(`${this.instrumentName(e.instrumentId)} cut the ${def.label}, outside the surgical field.`);
    this.mistakes.push({ stepId: this.engine.current?.id ?? "", mistakeId: `region_${e.region}`, severity: def.critical ? "high" : "moderate", structure: e.region, feedback: def.alarm, at: this.clock().toISOString() });
    const alerts = first || rebled ? [this.alert("region_injury", "urgent", def.alarm, [], "", `region.${e.region}`)] : [];
    this.changed(alerts);
    return { accepted: true, reason: "", alerts };
  }

  // Feeds the latest bleeding into the condition model and turns class changes and death into alerts.
  private conditionAlerts(): CoachAlert[] {
    let rate = 0;
    for (const r of this.bleeds.values()) rate += r;
    if (!this.bleeds.size) this.lastSimTickMs = this.ms(); // simulated bleeding time starts when a bleed does
    this.condition.setBodyBleeding(this.bloodLossMl, rate);
    if (this.done) {
      const all = this.kase.procedure.steps.every((st) => this.engine.completedMilestones.has(st.id) || this.completed.some((c) => c.stepId === st.id));
      if (all) this.condition.markCompleted();
      else this.condition.markEnded("ended before the case goals were reached");
    }
    const out: CoachAlert[] = [];
    for (const c of this.condition.update()) {
      if (c.kind === "died") {
        this.note(`The patient died: ${c.cause}.`);
        out.push(this.alert("patient_died", "urgent", `${DEATH_LINE} Cause: ${c.cause}.`, [], "", "outcome.died", DEATH_LINE));
      } else if (c.to >= 2) {
        const v = this.condition.view().vitals;
        this.note(`Vitals worsened to hemorrhage class ${c.to}: HR ${v.hr}, BP ${v.sys}/${v.dia} (simulated).`);
        out.push(this.alert("vitals", c.to >= 3 ? "urgent" : "normal", CLASS_LINES[c.to as 2 | 3 | 4], [], "", c.to >= 3 ? `vitals.class${c.to}` : ""));
      }
    }
    return out;
  }

  private stuckLevel(): number {
    if (this.done || !this.trackingValid || this.condition.died) return 0;
    const idle = (this.ms() - this.lastProgressAt) / 1000;
    let level = 0;
    for (let i = 0; i < this.policy.seconds.length; i++) {
      if (idle >= (this.policy.seconds[i] ?? Infinity) || this.offTarget >= (this.policy.attempts[i] ?? Infinity)) level = i + 1;
    }
    return level;
  }

  private checkStuck(): CoachAlert[] {
    const step = this.engine.current;
    const level = this.stuckLevel();
    if (!step || level <= this.tier) return [];
    this.tier = level;
    this.hintsUsed += 1;
    this.stepHints += 1;
    const hint = this.hintAt(step, level);
    return [this.alert("stuck", "normal", hint.say, hint.highlight, step.id, this.kase.procedure.id === "open_appendectomy" ? `hint.${step.id}.${level}` : "")];
  }

  // Learner asked for help: deliver the next tier immediately.
  requestHint(): { tier: number; say: string; highlight: string[] } {
    if (this.condition.died) return { tier: 0, say: "The patient died, so the case is over. There is no next step.", highlight: [] };
    const step = this.engine.current;
    if (!step) return { tier: 0, say: "The procedure is complete. Nothing left to do.", highlight: [] };
    this.tier = Math.min(this.policy.seconds.length, this.tier + 1);
    this.hintsUsed += 1;
    this.stepHints += 1;
    const hint = this.hintAt(step, this.tier);
    this.note(`Learner asked for a hint (tier ${this.tier}).`);
    this.changed([this.alert("stuck", "normal", hint.say, hint.highlight, step.id, this.kase.procedure.id === "open_appendectomy" ? `hint.${step.id}.${this.tier}` : "")]);
    return { tier: this.tier, ...hint };
  }

  hintAt(step: ProcedureStep, tier: number): { say: string; highlight: string[] } {
    const coaching = STEP_COACHING[this.kase.procedure.id]?.[step.id];
    const remaining = this.remainingTargets(step);
    if (tier >= 4) return { say: "Need assistance? Ask for a demonstration before continuing.", highlight: step.targets };
    if (this.kase.procedure.id === "open_appendectomy") return {
      say: tier <= 1 ? (coaching?.why ?? step.instruction) : (coaching?.lookHere ?? step.instruction),
      highlight: tier <= 1 ? [] : step.targets,
    };
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
      case "body_predicate": {
        const milestone = this.kase.procedure.openBody?.milestones.find((m) => m.id === step.id);
        const unmet = milestone && this.engine.body ? unmetPredicates(this.engine.body, milestone.predicates, (id) => this.name(id)) : [];
        return { text: step.instruction, structures: step.targets, labels: unmet.length ? unmet : [step.instruction] };
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
      case "body_predicate":
        return "waiting for measured body-state predicates";
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

  // A one-line description of what the learner can see, from the scene watcher.
  setScene(summary: string, source: string) {
    if (!summary || summary === this.scene.summary) return;
    this.scene = { summary: summary.slice(0, 300), at: this.clock().toISOString(), source };
    this.changed([]);
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
        if (e.type === "surgery") return !this.engine.body?.log.some(record => record.action.actionId === e.evidence.actionId);
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
    const bleeding = [...this.bleeds.keys()][0];
    const guidance = bleeding && step
      ? { say: `Control the bleeding from the ${this.name(bleeding).toLowerCase()} first: grasp or press to slow it, suction so you can see, then seal or clip the vessel.`, highlight: [bleeding] }
      : step
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
      timeline: this.timeline.map((t) => ({ atSeconds: Math.round((t.atMs - this.startedAt) / 1000), text: t.text })),
      held: [...this.held].map(([hand, instrumentId]) => ({ hand, instrumentId, name: this.instrumentName(instrumentId), touching: this.ref(this.touching.get(instrumentId) ?? "") })),
      recentMistakes: this.mistakes.slice(-5),
      completedSteps: [...this.completed],
      bloodLossMl: Math.round(this.bloodLossMl),
      scene: { ...this.scene },
      activeBleeds: [...this.bleeds].map(([id, rate]) => ({ structure: this.ref(id), rateMlPerMin: Math.round(rate * 10) / 10 })),
      mistakeCount: this.mistakes.length,
      highSeverityMistakeCount: highSeverity,
      hintsUsed: this.hintsUsed,
      elapsedSeconds: Math.round((now - this.startedAt) / 1000),
      guidance,
      commands: [...this.commands],
      openBody: Boolean(this.engine.body),
      ...(this.engine.grade ? { bodyGrade: this.engine.grade } : {}),
      bodyFacts: this.engine.body ? [...this.engine.body.facts].map(([key, value]) => ({ key, value })) : [],
      achievedMilestones: [...this.engine.completedMilestones],
      orderDeviations: [...this.engine.orderDeviations],
      decisionPrompts: (procedure.openBody?.decisions ?? []).map(({ id, prompt, choices }) => ({ id, prompt, choices })),
      // A milestone undone later (a new bleed after securing the mesoappendix) is current again, not done.
      checklist: procedure.steps.map((st) => ({
        id: st.id,
        title: st.title,
        done: st.id !== step?.id && (this.engine.completedMilestones.has(st.id) || this.completed.some((c) => c.stepId === st.id)),
        current: st.id === step?.id,
      })),
      condition: this.condition.view(),
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
// Coaching escalation (not grading): a bleed still active this long on the headset clock gets a second warning.
export const UNCONTROLLED_BLEED_MS = 30000;
export function uncontrolledBleedLine(structureName: string): string {
  return `Still bleeding from the ${structureName.toLowerCase()} after thirty seconds. Control it now: clamp, tie, or seal.`;
}

export function bleedingLine(structureName: string): string {
  return `Stop. Bleeding from the ${structureName.toLowerCase()}. Get control first.`;
}

// Structures that can bleed in a case: its vessels and the solid organs with a raw surface.
const BLEEDERS = new Set(["liver"]);
export function bleedingStructures(kase: SurgicalCase): { id: string; name: string }[] {
  return kase.anatomy.filter((a) => (a.system === "cardiovascular" || BLEEDERS.has(a.id)) && kase.procedure.structures.includes(a.id)).map((a) => ({ id: a.id, name: a.displayName }));
}

export function reflexLines(kase: SurgicalCase, mode: PresentationMode = "mixed_reality"): { key: string; text: string }[] {
  const lines = kase.procedure.steps.flatMap((s) => s.mistakes.filter((m) => m.severity === "high").map((m) => ({ key: `mistake.${m.id}`, text: reflexLine(m.feedback) })));
  for (const rule of kase.procedure.openBody?.guardrails ?? []) if (rule.severity === "high")
    lines.push({ key: `mistake.${rule.id}`, text: reflexLine(rule.feedback) });
  const unique = [...new Map(lines.map((l) => [l.key, l])).values()];
  const callouts = (kase.procedure.openBody ? kase.procedure.steps : kase.procedure.steps.slice(1)).map((s) => ({ key: `step.${s.id}`, text: kase.procedure.id === "open_appendectomy" ? STEP_COACHING.open_appendectomy?.[s.id]?.why ?? s.instruction : nextStepLine(s.title) }));
  const hints = kase.procedure.id === "open_appendectomy" ? kase.procedure.steps.flatMap((s) => [1, 2, 3, 4].map((tier) => ({
    key: `hint.${s.id}.${tier}`, text: tier === 4 ? "Need assistance? Ask for a demonstration before continuing."
      : tier === 1 ? STEP_COACHING.open_appendectomy?.[s.id]?.why ?? s.instruction
      : STEP_COACHING.open_appendectomy?.[s.id]?.lookHere ?? s.instruction,
  }))) : [];
  const bleeds = bleedingStructures(kase).map((b) => ({ key: `bleeding.${b.id}`, text: bleedingLine(b.name) }));
  for (const tissue of kase.procedure.openBody?.tissues ?? []) if (tissue.perfused && !bleeds.some(b => b.key === `bleeding.${tissue.id}`))
    bleeds.push({ key: `bleeding.${tissue.id}`, text: bleedingLine(kase.anatomy.find(a => a.id === tissue.id)?.displayName ?? tissue.id.replaceAll("_", " ")) });
  for (const tissue of kase.procedure.openBody?.tissues ?? []) if (tissue.perfused)
    bleeds.push({ key: `bleeding_uncontrolled.${tissue.id}`, text: uncontrolledBleedLine(kase.anatomy.find(a => a.id === tissue.id)?.displayName ?? tissue.id.replaceAll("_", " ")) });
  const condition = [
    ...(Object.keys(REGIONS) as RegionId[]).map((r) => ({ key: `region.${r}`, text: REGIONS[r].alarm })),
    { key: "vitals.class3", text: CLASS_LINES[3] },
    { key: "vitals.class4", text: CLASS_LINES[4] },
    { key: "outcome.died", text: DEATH_LINE },
  ];
  return [...unique, { key: "tracking_lost", text: trackingLostLine(mode) }, ...bleeds, ...callouts, ...hints, ...condition];
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
    case "finish":
      return "End-attempt is available for open-body cases only.";
    case "surgery":
      return `Applied ${event.evidence.verb} to ${name(event.evidence.tissueId).toLowerCase()}; no new milestone reached.`;
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
    s.status, s.step.id, s.step.progressText, s.condition.outcome.result, s.condition.vitals.hemorrhageClass, Math.round(s.condition.vitals.hr / 10), s.condition.regions.map((r) => `${r.region}:${r.bleeding}`).join(","), s.timeline.length && s.timeline[s.timeline.length - 1], s.held, s.completedCount, s.mistakeCount, s.focusStructure.id, s.stuckLevel,
    s.bodyGrade, s.bodyFacts, s.achievedMilestones, s.orderDeviations, s.trackingValid, s.hintTier, s.desynced, s.bloodLossMl, s.activeBleeds.map((b) => b.structure.id).join(","), s.scene.summary, s.commands.map((c) => `${c.commandId}:${c.status}`).join(","),
  ];
  let h = 2166136261;
  for (const ch of JSON.stringify(parts)) h = Math.imul(h ^ ch.charCodeAt(0), 16777619);
  return (h >>> 0).toString(16);
}

// Compact text for the voice agent's contextual updates. Short lines, facts only.
// Plain names for body facts, so unmet milestone predicates read as coaching facts, including negatives.
const FACT_LABELS: Record<string, [string, string]> = {
  // fact: [label, unit]
  marked: ["incision line marked", ""],
  markErrorMm: ["mark distance from McBurney's point", "mm"],
  markLengthMm: ["mark length", "mm"],
  markAngleDegrees: ["mark angle off the skin lines", "deg"],
  cutCoverage: ["share of the marked line cut", ""],
  cutErrorMm: ["cut distance off the line", "mm"],
  cutDepthMm: ["cut depth", "mm"],
  opened: ["opened", ""],
  cutAngleDegrees: ["cut angle off the fibers", "deg"],
  splitWidthMm: ["split width", "mm"],
  bladeUsed: ["blade used on it", ""],
  tentedBeforeCut: ["tented before cutting", ""],
  delivered: ["delivered into the wound", ""],
  clampCount: ["clamps on", ""],
  cutBetweenClamps: ["cut between the clamps", ""],
  tieCount: ["ties on", ""],
  tiedBothSides: ["tied on both sides of the cut", ""],
  activeBleeds: ["active bleeds", ""],
  decision_true_base: ["true base identified", ""],
  crushed: ["base crushed", ""],
  tieDistanceMm: ["tie distance from the cecum", "mm"],
  stumpLengthMm: ["stump length", "mm"],
  cutAboveTie: ["cut above the tie", ""],
  cutBetweenTieAndClamp: ["cut between tie and clamp", ""],
  removed: ["removed", ""],
  poolMl: ["blood pool", "ml"],
  inspectionMs: ["inspected", "ms"],
  closed: ["closed", ""],
};

// Each predicate of a milestone that the body state does not yet satisfy, in words.
export function unmetPredicates(body: BodyState, predicates: BodyPredicate[], name: (id: string) => string): string[] {
  const unmet = predicates.filter((p) => !body.test(p));
  // A fact bounded on both sides (mark length 50 to 80 mm) reads as one range, said once.
  const range = (p: BodyPredicate) => predicates.find((q) => q !== p && q.tissueId === p.tissueId && q.fact === p.fact && q.op !== p.op && q.op !== "eq");
  return unmet
    .filter((p) => !(p.op === "lte" && range(p) && unmet.includes(range(p)!)))
    .map((p) => {
      const other = range(p);
      if (other && p.op !== "eq") {
        const [label, unit] = FACT_LABELS[p.fact] ?? [p.fact, ""];
        const lo = p.op === "gte" ? p.value : other.value;
        const hi = p.op === "lte" ? p.value : other.value;
        const has = body.facts.has(`${p.tissueId}:${p.fact}`);
        const u = unit ? ` ${unit}` : "";
        return `${p.tissueId ? `${name(p.tissueId).toLowerCase()}: ` : ""}${label} ${has ? `is ${Math.round(body.get(p.tissueId, p.fact) * 100) / 100}${u}` : "not measured yet"}, needs ${lo} to ${hi}${u}`;
      }
      const [label, unit] = FACT_LABELS[p.fact] ?? [p.fact.replace(/([A-Z])/g, " $1").toLowerCase(), ""];
      const who = p.tissueId ? `${name(p.tissueId).toLowerCase()}: ` : "";
      const has = body.facts.has(`${p.tissueId}:${p.fact}`);
      const now = body.get(p.tissueId, p.fact);
      const u = unit ? ` ${unit}` : "";
      if (p.op === "eq" && p.value === 1) return `${who}NOT yet ${label}`;
      if (p.op === "eq") return `${who}${label} must be ${p.value} (now ${now})`;
      const need = `${p.op === "gte" ? "at least" : "at most"} ${p.value}${u}`;
      return `${who}${label} ${has ? `is ${Math.round(now * 100) / 100}${u}` : "not measured yet"}, needs ${need}`;
    });
}

const TIMELINE_MAX = 20;
const TIMELINE_IN_CONTEXT = 8;
const clockText = (sec: number) => `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, "0")}`;

const OUTCOME_WORDS: Record<string, string> = {
  not_exposed: "blocked: that layer is not exposed yet",
  not_cuttable: "that tissue cannot be cut",
  no_cut: "no real cut made",
  across_fibers: "cut across the fibers",
  muscle_cut: "used the blade on muscle",
  untented_cut: "cut without tenting",
  cut_unsecured: "started bleeding",
  hollow_leak: "it is leaking",
  critical_injury: "injured a critical structure",
  rough_handling: "rough handling",
  missing_instance: "clamp not registered",
  rebleed: "bleeding resumed",
  not_clamped: "no clamp to remove",
};

// Plain words for one measured body action, e.g. "Scalpel: cut the skin, 52 mm (cut across the fibers)."
export function describeBodyAction(a: BodyAction, outcomes: string[], name: (id: string) => string, instrument: (id: string) => string): string {
  const t = name(a.tissueId).toLowerCase();
  const did: Record<string, string> = {
    cut: `cut the ${t}${a.lengthMm >= 1 ? `, ${Math.round(a.lengthMm)} mm` : ""}`,
    mark: `marked the incision line${a.lengthMm >= 1 ? `, ${Math.round(a.lengthMm)} mm` : ""}`,
    clamp: `clamped the ${t}`,
    release: `removed the clamp from the ${t}`,
    tie: `tied the ${t}`,
    seal: `sealed the ${t}`,
    grasp: `grasped the ${t}`,
    retract: `retracted the ${t}`,
    suction: "suctioned the field",
    inspect: `inspected the ${t}`,
    decide: `answered "${a.choice.replaceAll("_", " ")}"`,
    close: "closed the wound",
    place: `placed a tie on the ${t}`,
  };
  const extra = outcomes.map((o) => OUTCOME_WORDS[o] ?? o.replaceAll("_", " "));
  return `${instrument(a.instrumentId)}: ${did[a.verb] ?? `${a.verb} on the ${t}`}${extra.length ? ` (${extra.join("; ")})` : ""}.`;
}

export function renderContext(s: CoachSnapshot): string {
  if (s.status === "completed") {
    return [
      `[LIVE SURGERY STATE v${s.version}] ${s.procedureTitle} for ${s.patientLabel}: ${s.bodyGrade && !s.bodyGrade.complete ? "ATTEMPT ENDED WITH UNMET GOALS" : "COMPLETE"}.`,
      ...(s.bodyGrade ? [`Illustrative uncalibrated grade: ${s.bodyGrade.earnedPoints}/${s.bodyGrade.availablePoints} measured-weight points. Economy weight 20 unscored; hand paths unavailable.`, `Met goals: ${s.bodyGrade.metMilestones.join(", ") || "none"}. Unmet goals: ${s.bodyGrade.missingMilestones.join(", ") || "none"}.`] : []),
      `Mistakes: ${s.mistakeCount} (${s.highSeverityMistakeCount} high severity). Hints used: ${s.hintsUsed}. Time: ${s.elapsedSeconds}s.`,
      ...(s.openBody ? [`Body facts: ${s.bodyFacts.map(f => `${f.key}=${f.value}`).join("; ")}.`, `Expected-order deviations: ${s.orderDeviations.join(", ") || "none"}.`] : []),
      s.completedSteps.map((c) => `${c.title} ${c.seconds}s${c.mistakes ? `, ${c.mistakes} mistake(s)` : ""}`).join("; "),
    ].join("\n");
  }
  if (s.condition.outcome.result === "died") {
    return [
      `[LIVE SURGERY STATE v${s.version}] ${s.procedureTitle} for ${s.patientLabel}: THE PATIENT DIED (simulated). Cause: ${s.condition.outcome.cause}.`,
      `The case is over; no further actions count. Tell the learner plainly what happened and what would have prevented it, then stop.`,
      `Last events: ${s.timeline.slice(-5).map((t) => t.text).join(" ")}`,
      ...(s.activeBleeds.length || s.condition.regions.some((r) => r.bleeding) ? [`Still bleeding at death: ${[...s.activeBleeds.map((b) => b.structure.name), ...s.condition.regions.filter((r) => r.bleeding).map((r) => r.label)].join(", ")}.`] : []),
    ].join("\n");
  }
  const st = s.step;
  const desync = s.desynced
    ? `HEADSET DISAGREES: the headset reports step "${s.headsetStepId}" while this state shows "${st.id}". Trust the headset; describe progress only in general terms until they agree.`
    : "";
  const lines = [
    ...(desync ? [desync] : []),
    `[LIVE SURGERY STATE v${s.version}] ${s.procedureTitle} for ${s.patientLabel}. ${s.status === "paused" ? "PAUSED: tracking lost, anatomy hidden, scoring paused." : ""}`.trim(),
    `${s.openBody ? "Suggested milestone" : "Step"} ${s.stepNumber} of ${s.stepCount}: ${st.title}. ${st.instruction}`,
    `Instrument: ${st.instrumentName}${st.ports.length ? ` via ${st.ports.join(" / ")}` : ""}. Progress: ${st.progressText}. Still needed: ${st.remaining.join("; ") || "nothing"}.`,
  ];
  if (s.openBody) {
    lines.push("Open body: expected order is guidance, never an action gate. Only report measured facts and detected guardrails.");
    lines.push(`Achieved milestones: ${s.achievedMilestones.filter((id) => id !== st.id).join(", ") || "none"}. Expected-order deviations: ${s.orderDeviations.join(", ") || "none"}.`);
    // Only what the current milestone still needs (Still needed above); the full fact table is in get_surgery_state.
    for (const d of s.decisionPrompts) lines.push(`Authored decision ${d.id}: ${d.prompt} Choices: ${d.choices.join(", ")}.`);
  }
  if (s.activeBleeds.length) {
    lines.push(`ACTIVE BLEEDING: ${s.activeBleeds.map((b) => `${b.structure.name} at ${b.rateMlPerMin} ml/min`).join("; ")}. Total blood loss ${s.bloodLossMl} ml. Coach bleeding control before anything else.`);
  } else if (s.bloodLossMl > 0) {
    lines.push(`Blood loss so far: ${s.bloodLossMl} ml (no active bleeding).`);
  }
  const rough = s.completedSteps.filter((c) => c.mistakes > 0).slice(-2);
  if (rough.length) lines.push(`Earlier: ${rough.map((c) => `${c.title} (${c.mistakes} mistake${c.mistakes === 1 ? "" : "s"}${c.hints ? `, ${c.hints} hint${c.hints === 1 ? "" : "s"}` : ""})`).join("; ")}.`);
    if (st.dangers.length) lines.push(`Danger structures this step: ${st.dangers.map((d) => d.name).join(", ")}.`);
  if (st.patientNotes.length) lines.push(`Patient-specific: ${st.patientNotes.join(" ")}`);
  if (s.scene.summary) lines.push(`In view (${s.scene.source || "camera"}): ${s.scene.summary}`);
  if (s.focusStructure.id) lines.push(`Learner is looking at: ${s.focusStructure.name}.`);
  const v = s.condition.vitals;
  lines.push(`Vitals (simulated; baseline ${s.condition.baselineSource === "measured" ? "measured from the real volunteer at the Time-Out with Presage, the changes are simulated" : s.condition.baselineSource === "demo" ? "from the Presage demo feed, not a real measurement" : `${s.condition.baselineSource} values`}): HR ${v.hr}, BP ${v.sys}/${v.dia}, RR ${v.rr}${v.spo2 >= 0 ? `, SpO2 ${v.spo2}%` : ""}. Simulated blood loss ${v.bloodLossPct}% of volume (class ${v.hemorrhageClass}).${v.hemorrhageClass >= 3 ? " DETERIORATING: bleeding control comes before anything else." : ""}`);
  const injuries = s.condition.regions;
  if (injuries.length) lines.push(`Injuries outside the surgical field: ${injuries.map((r) => `${r.label}${r.bleeding ? " (bleeding)" : ""}`).join(", ")}.`);
  if (s.held.length) lines.push(`In hand: ${s.held.map((h) => `${h.hand} ${h.name}${h.touching.id ? ` (tip on the ${h.touching.name.toLowerCase()})` : ""}`).join("; ")}.`);
  const trail = s.timeline.slice(-TIMELINE_IN_CONTEXT);
  if (trail.length > 1) lines.push(`Recent, oldest first (session clock, now ${clockText(s.elapsedSeconds)}): ${trail.map((t) => `${clockText(t.atSeconds)} ${t.text}`).join(" ")}`);
  lines.push(`Last event: ${s.lastEvent}`);
  lines.push(`Time on step ${s.secondsOnStep}s, ${s.secondsSinceProgress}s since progress, ${s.offTargetAttempts} off-target attempts. Coaching level: ${s.stuckLabel} (hint tier ${s.hintTier}).`);
  const recent = s.recentMistakes.filter((m) => m.stepId === st.id);
  if (recent.length) lines.push(`Mistakes this step: ${recent.map((m) => m.feedback).join(" ")}`);
  // Hints go through get_hint so the tier escalates; this line only tells Jarvis where coaching stands.
  lines.push(`Coaching: hint tier ${s.hintTier} of ${s.openBody ? 4 : 3} used on this step. If the learner asks what to do, call get_hint.`);
  if (st.nextTitle) lines.push(`After this: ${st.nextTitle}.`);
  return lines.join("\n");
}
