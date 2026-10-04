import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { DbConnection, tables } from "./module_bindings/index.js";
import type { EncounterLogEntry, EncounterSession, Scorecard } from "./encounter.js";
import type { InterviewScorecard, InterviewSession } from "./interview.js";
import { conditionFromRow, type ConditionView, type RegionId } from "./patient-condition.js";
import type { Baseline } from "./physiology.js";

// Scalpal's connection to the shared SpacetimeDB session (Nathan's module), as the `coach` role.
// Everything Scalpal does is mirrored there live, so the companion, the headset, and anyone else
// subscribed see it as it happens: transcript lines, voice status, the pre-op encounter (every
// question, exam, test, phase, and the scorecard), and highlight commands to the headset.
// The HTTP coach API keeps working when no session is bound.

export interface RealtimeSink {
  readonly bound: boolean;
  coachMessage(speaker: "learner" | "coach" | "system" | "patient", text: string): void;
  coachStatus(status: "offline" | "connecting" | "listening" | "thinking" | "speaking" | "error", detail?: string): void;
  attachEncounter(e: EncounterSession): void;
  encounterPhase(e: EncounterSession): void;
  encounterResult(e: EncounterSession, card: Scorecard): void;
  // The choice-based office interview, mirrored through the same encounter tables.
  attachInterview?(i: InterviewSession): void;
  interviewResult?(i: InterviewSession, card: InterviewScorecard): void;
  // Operating-room logs for the companion dashboard (docs/operation-flow.md "Dashboard"): what the state
  // tracker saw, alerts, vitals samples, checklist changes and the case outcome. Fire and forget.
  simLog?(entry: SimLogEntry): void;
  // Feeds the shared patient_condition (SpacetimeDB advances it at 1 Hz for the dashboard's Live OR panel).
  patientCondition?(update: PatientConditionUpdate): void;
  // The robot learner's verdict for a step, as a robot_result row every member subscribes to. Fire and forget.
  robotResult?(entry: RobotResultEntry): void;
  // The simulated patient, advanced server-side by the SpacetimeDB module (patient_condition). The coach
  // starts it, forwards body facts and injuries, and reads the authoritative condition back. Fire and forget.
  startCondition?(coachSessionId: string, p: ConditionStart): void;
  setConditionBaseline?(coachSessionId: string, baseline: Baseline): void;
  reportBody?(coachSessionId: string, bloodLostMl: number, bleeds: { name: string; rateMlPerMin: number }[]): void;
  reportInjury?(coachSessionId: string, region: RegionId, controlled: boolean): void;
  endCondition?(coachSessionId: string, result: "completed" | "ended", cause: string): void;
  // The module's condition for this coach session and how long ago the bridge last received it, or null.
  readCondition?(coachSessionId: string): { view: ConditionView; ageMs: number } | null;
  // Resolves to the headset's resolution, or null when the shared session cannot carry the command.
  highlight(targetId: string, timeoutMs?: number): Promise<{ status: string; reason: string } | null>;
}

export interface SimLogEntry {
  coachSessionId: string;
  kind: "event" | "alert" | "vitals" | "checklist" | "outcome";
  text: string; // one human-readable line
  data?: unknown; // structured payload, JSON-serialized when stored
}

export interface ConditionStart {
  baseline: Baseline;
  spo2: number | null;
  weightKg: number;
  mlPerKg: number;
}

// The module accepts a short lowercase source label ("chart", "chart+authored", "measured", "demo", ...).
const conditionSource = (b: Baseline) => (b.source ?? "authored").toLowerCase().replace(/[^a-z0-9+_-]/g, "").slice(0, 24) || "authored";

export interface RobotResultEntry {
  stepId: string;
  success: boolean;
  pathErrorMm: number | null;
  policySuccessRate: number | null; // 0..1
  demosHuman: number;
  demosSynthetic: number;
  videoUrl: string | null;
}

export type PatientConditionUpdate =
  | { kind: "start"; coachSessionId: string; baseline: { hr: number; rr: number; sys: number; dia: number; spo2: number; source: string }; weightKg: number; mlPerKg: number }
  | { kind: "baseline"; baseline: { hr: number; rr: number; sys: number; dia: number; source: string } }
  | { kind: "body"; bloodLostMl: number; bleeds: { name: string; rateMlPerMin: number }[] }
  | { kind: "injury"; region: string; controlled: boolean }
  | { kind: "end"; result: "completed" | "ended"; cause: string };

// patient_condition accepts these baseline sources; "chart+authored" (a partly charted baseline) counts as chart.
const baselineSource = (s: string) => (["chart", "measured", "demo", "authored"].includes(s) ? s : s.startsWith("chart") ? "chart" : "authored");

export const NO_REALTIME: RealtimeSink = {
  bound: false,
  coachMessage() {},
  coachStatus() {},
  attachEncounter() {},
  encounterPhase() {},
  encounterResult() {},
  highlight: async () => null,
};

export interface BridgeConfig {
  uri: string;
  database: string;
  tokenFile: string;
  inviteCode?: string;
  log?: (msg: string, extra?: unknown) => void;
}

const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));

export class RealtimeBridge implements RealtimeSink {
  private conn: DbConnection | null = null;
  private ready = false;
  private identityHex = "";
  private sessionId = "";
  private lastError = "";
  private stopped = false;
  private retryMs = 1000;
  private commandSeq = 0;
  private conditionOwner = ""; // the coach session whose patient the module currently simulates
  private conditionSeenAt = 0; // local receipt time of the latest patient_condition row
  private readonly log: (msg: string, extra?: unknown) => void;

  constructor(private readonly cfg: BridgeConfig) {
    this.log = cfg.log ?? ((msg, extra) => console.log(`[realtime] ${msg}`, extra ?? ""));
  }

  get bound() {
    return this.ready && Boolean(this.sessionId);
  }

  status() {
    return { configured: true, connected: this.ready, identity: this.identityHex, sessionId: this.sessionId, lastError: this.lastError };
  }

  start() {
    this.connect();
  }

  stop() {
    this.stopped = true;
    this.conn?.disconnect();
  }

  private token(): string | undefined {
    return existsSync(this.cfg.tokenFile) ? readFileSync(this.cfg.tokenFile, "utf8").trim() || undefined : undefined;
  }

  private connect() {
    if (this.stopped) return;
    this.conn = DbConnection.builder()
      .withUri(this.cfg.uri)
      .withDatabaseName(this.cfg.database)
      .withToken(this.token())
      .onConnect((c, identity, token) => {
        this.identityHex = identity.toHexString();
        try {
          writeFileSync(this.cfg.tokenFile, token, { mode: 0o600 });
        } catch {
          /* token reuse is a convenience */
        }
        this.retryMs = 1000;
        c.subscriptionBuilder()
          .onApplied(() => {
            this.ready = true;
            this.log("connected as coach identity", this.identityHex.slice(0, 12));
            // After a restart, rebind to the session this coach was last bound to (no invite code needed).
            if (!this.sessionId) {
              const saved = this.savedSession();
              if (saved && this.bind(saved)) this.log("rebound to session", saved);
            }
            if (this.cfg.inviteCode && !this.sessionId) void this.join(this.cfg.inviteCode).catch((e) => (this.lastError = String(e)));
          })
          .subscribe([tables.mySessions, tables.myMemberships, tables.sessionExerciseState, tables.sessionCommands, tables.sessionEncounters]);
        // Separate subscription: a database without the patient_condition module keeps the rest working.
        const seen = () => (this.conditionSeenAt = Date.now());
        c.db.sessionPatientCondition.onInsert(seen);
        c.db.sessionPatientCondition.onUpdate(seen);
        c.subscriptionBuilder()
          .onError((_ctx) => this.log("patient_condition subscription failed; the coach computes the condition locally"))
          .subscribe([tables.sessionPatientCondition]);
      })
      .onDisconnect(() => {
        this.ready = false;
        this.reconnect();
      })
      .onConnectError((_c, err) => {
        this.ready = false;
        this.lastError = String(err);
        this.reconnect();
      })
      .build();
  }

  private reconnect() {
    if (this.stopped) return;
    const delay = this.retryMs;
    this.retryMs = Math.min(this.retryMs * 2, 30_000);
    setTimeout(() => this.connect(), delay);
  }

  // Join with the session's coach invite code and bind to that session.
  async join(code: string): Promise<string> {
    const conn = this.conn;
    if (!conn || !this.ready) throw new Error("realtime database not connected");
    // The same identity can already coach older sessions; bind to the membership this join created.
    const before = new Set([...conn.db.myMemberships.iter()].filter((x) => x.role === "coach").map((x) => x.sessionId));
    await conn.reducers.joinSession({ code: code.trim().toUpperCase(), displayName: "Scalpal" });
    for (let i = 0; i < 100; i++) {
      const m = [...conn.db.myMemberships.iter()].find((x) => x.role === "coach" && !before.has(x.sessionId));
      if (m) {
        this.sessionId = m.sessionId;
        this.saveSession();
        this.log("bound to session", this.sessionId);
        this.coachStatus("listening");
        return this.sessionId;
      }
      await wait(30);
    }
    throw new Error("no new coach membership appeared (already joined this session? bind it by id instead)");
  }

  // Bind to a session this identity is already a coach member of (after a restart).
  bind(sessionId: string): boolean {
    const conn = this.conn;
    if (!conn) return false;
    const ok = [...conn.db.myMemberships.iter()].some((m) => m.sessionId === sessionId && m.role === "coach");
    if (ok) {
      this.sessionId = sessionId;
      this.saveSession();
    }
    return ok;
  }

  private get sessionFile() {
    return `${this.cfg.tokenFile}.session`;
  }

  private savedSession(): string {
    try {
      return existsSync(this.sessionFile) ? readFileSync(this.sessionFile, "utf8").trim() : "";
    } catch {
      return "";
    }
  }

  private saveSession() {
    try {
      writeFileSync(this.sessionFile, this.sessionId, { mode: 0o600 });
    } catch {
      /* rebinding after a restart is a convenience */
    }
  }

  private call(name: string, fn: (c: DbConnection) => Promise<unknown>) {
    const conn = this.conn;
    if (!conn || !this.bound) return;
    fn(conn).catch((err) => {
      this.lastError = `${name}: ${String(err)}`;
      this.log(`${name} failed`, String(err));
    });
  }

  patientCondition(u: PatientConditionUpdate) {
    const sessionId = this.sessionId;
    if (u.kind === "start") {
      const b = u.baseline;
      this.call("startPatientCondition", (c) => c.reducers.startPatientCondition({ sessionId, coachSessionId: u.coachSessionId, baselineHr: b.hr, baselineRr: b.rr, baselineSys: b.sys, baselineDia: b.dia, baselineSpo2: b.spo2, baselineSource: baselineSource(b.source), weightKg: u.weightKg, mlPerKg: u.mlPerKg }));
    } else if (u.kind === "baseline") {
      const b = u.baseline;
      this.call("setPatientBaseline", (c) => c.reducers.setPatientBaseline({ sessionId, baselineHr: b.hr, baselineRr: b.rr, baselineSys: b.sys, baselineDia: b.dia, baselineSource: baselineSource(b.source) }));
    } else if (u.kind === "body") {
      this.call("reportBodyState", (c) => c.reducers.reportBodyState({ sessionId, bloodLostMl: u.bloodLostMl, activeBleedsJson: JSON.stringify(u.bleeds.slice(0, 32)) }));
    } else if (u.kind === "injury") {
      this.call("reportInjury", (c) => c.reducers.reportInjury({ sessionId, region: u.region, controlled: u.controlled }));
    } else {
      this.call("endPatientCondition", (c) => c.reducers.endPatientCondition({ sessionId, result: u.result, cause: u.cause.slice(0, 200) }));
    }
  }

  coachMessage(speaker: "learner" | "coach" | "system" | "patient", text: string) {
    if (!text.trim()) return;
    this.call("postCoachMessage", (c) => c.reducers.postCoachMessage({ sessionId: this.sessionId, speaker, text: text.slice(0, 4000) }));
  }

  simLog(entry: SimLogEntry) {
    const text = entry.text.trim().slice(0, 2000);
    if (!text) return;
    let dataJson = "";
    if (entry.data !== undefined) {
      try {
        dataJson = JSON.stringify(entry.data) ?? "";
      } catch {
        dataJson = "";
      }
      // Cutting JSON mid-token would leave it unparseable, so an oversized payload is replaced by a marker.
      if (dataJson.length > 8000) dataJson = JSON.stringify({ truncated: true, length: dataJson.length });
    }
    this.call("appendSimLog", (c) =>
      c.reducers.appendSimLog({ sessionId: this.sessionId, coachSessionId: entry.coachSessionId.slice(0, 120), kind: entry.kind, text, dataJson }),
    );
  }

  robotResult(entry: RobotResultEntry) {
    const count = (n: number) => Math.max(0, Math.min(2 ** 32 - 1, Math.round(n)));
    this.call("postRobotResult", (c) =>
      c.reducers.postRobotResult({
        sessionId: this.sessionId,
        stepId: entry.stepId.slice(0, 120),
        success: entry.success,
        pathErrorMm: entry.pathErrorMm ?? undefined,
        policySuccessRate: entry.policySuccessRate ?? undefined,
        demosHuman: count(entry.demosHuman),
        demosSynthetic: count(entry.demosSynthetic),
        videoUrl: entry.videoUrl ?? undefined,
      }),
    );
  }

  coachStatus(status: "offline" | "connecting" | "listening" | "thinking" | "speaking" | "error", detail?: string) {
    this.call("setCoachStatus", (c) => c.reducers.setCoachStatus({ sessionId: this.sessionId, status, detail: detail?.slice(0, 500) }));
  }

  attachEncounter(e: EncounterSession) {
    if (!this.bound) return;
    const p = e.encounter.persona;
    const sessionAtStart = this.sessionId;
    this.call("startEncounter", (c) =>
      c.reducers.startEncounter({ encounterId: e.id, sessionId: this.sessionId, patientId: e.kase.patientId, patientName: p.patientName, speaker: p.speaker, speakerName: p.name }),
    );
    // Calls are ordered on the connection, so events follow the start in order.
    e.subscribe(({ kind, payload }) => {
      if (this.sessionId !== sessionAtStart) return;
      if (kind === "log") {
        const entry = payload as EncounterLogEntry;
        if (entry.kind === "assessment") return; // carried by the result
        this.call("appendEncounterEvent", (c) => c.reducers.appendEncounterEvent({ encounterId: e.id, kind: entry.kind, itemId: entry.id, speaker: undefined, text: entry.text.slice(0, 4000) }));
      } else if (kind === "transcript") {
        const line = payload as { speaker: "learner" | "patient" | "coach"; text: string };
        this.call("appendEncounterEvent", (c) => c.reducers.appendEncounterEvent({ encounterId: e.id, kind: "transcript", itemId: "", speaker: line.speaker, text: line.text.slice(0, 4000) }));
        this.coachMessage(line.speaker, line.text);
      }
    });
  }

  encounterPhase(e: EncounterSession) {
    this.call("setEncounterPhase", (c) => c.reducers.setEncounterPhase({ encounterId: e.id, phase: e.phase }));
  }

  attachInterview(i: InterviewSession) {
    if (!this.bound) return;
    const sessionAtStart = this.sessionId;
    this.call("startEncounter", (c) =>
      c.reducers.startEncounter({ encounterId: i.id, sessionId: this.sessionId, patientId: i.kase.patientId, patientName: i.patientName, speaker: "patient", speakerName: i.patientName }),
    );
    i.subscribe(({ kind, payload }) => {
      if (this.sessionId !== sessionAtStart) return;
      if (kind === "pick") {
        const { pick, choice } = payload as { pick: { roundId: string; key: string }; choice: { text: string } };
        this.call("appendEncounterEvent", (c) => c.reducers.appendEncounterEvent({ encounterId: i.id, kind: "choice", itemId: pick.roundId, speaker: "learner", text: `${pick.key}: ${choice.text}`.slice(0, 4000) }));
      } else if (kind === "transcript") {
        const line = payload as { speaker: "learner" | "patient"; text: string };
        this.call("appendEncounterEvent", (c) => c.reducers.appendEncounterEvent({ encounterId: i.id, kind: "transcript", itemId: "", speaker: line.speaker, text: line.text.slice(0, 4000) }));
        this.coachMessage(line.speaker, line.text);
      } else if (kind === "phase") {
        this.call("setEncounterPhase", (c) => c.reducers.setEncounterPhase({ encounterId: i.id, phase: String(payload) }));
      }
    });
  }

  interviewResult(i: InterviewSession, card: InterviewScorecard) {
    this.call("setEncounterResult", (c) => c.reducers.setEncounterResult({ encounterId: i.id, scoreTotal: card.total, grade: card.grade, scorecardJson: JSON.stringify(card).slice(0, 32000) }));
    this.coachMessage("system", `Pre-op interview ${card.total}/100 (${card.grade}).`);
  }

  encounterResult(e: EncounterSession, card: Scorecard) {
    const a = e.assessment;
    if (a) {
      this.call("appendEncounterEvent", (c) =>
        c.reducers.appendEncounterEvent({ encounterId: e.id, kind: "assessment", itemId: "assessment", speaker: undefined, text: JSON.stringify(a).slice(0, 4000) }),
      );
    }
    this.call("setEncounterResult", (c) => c.reducers.setEncounterResult({ encounterId: e.id, scoreTotal: card.total, grade: card.grade, scorecardJson: JSON.stringify(card).slice(0, 32000) }));
    this.coachMessage("system", `Pre-op score ${card.total}/100 (${card.grade}). ${card.feedback[0] ?? ""}`);
  }

  startCondition(coachSessionId: string, p: ConditionStart) {
    if (!this.bound) return;
    this.conditionOwner = coachSessionId;
    const b = p.baseline;
    this.call("startPatientCondition", (c) =>
      c.reducers.startPatientCondition({
        sessionId: this.sessionId,
        coachSessionId: coachSessionId.slice(0, 120),
        baselineHr: b.hr,
        baselineRr: b.rr,
        baselineSys: b.sys,
        baselineDia: b.dia,
        baselineSpo2: p.spo2 ?? -1,
        baselineSource: conditionSource(b),
        weightKg: p.weightKg,
        mlPerKg: p.mlPerKg,
      }),
    );
  }

  setConditionBaseline(coachSessionId: string, b: Baseline) {
    if (coachSessionId !== this.conditionOwner) return;
    this.call("setPatientBaseline", (c) =>
      c.reducers.setPatientBaseline({ sessionId: this.sessionId, baselineHr: b.hr, baselineRr: b.rr, baselineSys: b.sys, baselineDia: b.dia, baselineSource: conditionSource(b) }),
    );
  }

  reportBody(coachSessionId: string, bloodLostMl: number, bleeds: { name: string; rateMlPerMin: number }[]) {
    if (coachSessionId !== this.conditionOwner) return;
    const activeBleedsJson = JSON.stringify(bleeds.slice(0, 32).map((b) => ({ name: b.name.slice(0, 80), rateMlPerMin: Math.max(0, b.rateMlPerMin) })));
    this.call("reportBodyState", (c) => c.reducers.reportBodyState({ sessionId: this.sessionId, bloodLostMl: Math.max(0, bloodLostMl), activeBleedsJson }));
  }

  reportInjury(coachSessionId: string, region: RegionId, controlled: boolean) {
    if (coachSessionId !== this.conditionOwner) return;
    this.call("reportInjury", (c) => c.reducers.reportInjury({ sessionId: this.sessionId, region, controlled }));
  }

  endCondition(coachSessionId: string, result: "completed" | "ended", cause: string) {
    if (coachSessionId !== this.conditionOwner) return;
    this.call("endPatientCondition", (c) => c.reducers.endPatientCondition({ sessionId: this.sessionId, result, cause: cause.slice(0, 500) }));
  }

  readCondition(coachSessionId: string): { view: ConditionView; ageMs: number } | null {
    const conn = this.conn;
    if (!conn || !this.bound || coachSessionId !== this.conditionOwner) return null;
    const row = [...conn.db.sessionPatientCondition.iter()].find((r) => r.sessionId === this.sessionId && r.coachSessionId === coachSessionId);
    return row ? { view: conditionFromRow(row), ageMs: Date.now() - this.conditionSeenAt } : null;
  }

  async highlight(targetId: string, timeoutMs = 2000): Promise<{ status: string; reason: string } | null> {
    const conn = this.conn;
    if (!conn || !this.bound) return null;
    const state = [...conn.db.sessionExerciseState.iter()].find((s) => s.sessionId === this.sessionId);
    if (!state) return null; // no headset publishing state in this session; use the HTTP command path
    const commandId = `jarvis-${Date.now().toString(36)}-${(++this.commandSeq).toString(36)}`;
    try {
      await conn.reducers.requestCommand({
        commandId,
        sessionId: this.sessionId,
        action: "highlightStructure",
        targetId,
        argBool: undefined,
        argNumber: undefined,
        expectedStepVersion: state.stepVersion,
      });
    } catch (err) {
      this.lastError = `requestCommand: ${String(err)}`;
      return null;
    }
    for (let waited = 0; waited < timeoutMs; waited += 50) {
      const cmd = [...conn.db.sessionCommands.iter()].find((x) => x.commandId === commandId);
      if (cmd && cmd.status !== "pending") return { status: cmd.status, reason: cmd.reason ?? "" };
      await wait(50);
    }
    return { status: "pending", reason: "" };
  }
}

export function realtimeFromEnv(env: NodeJS.ProcessEnv): RealtimeBridge | null {
  if (!env.SPACETIMEDB_URI) return null;
  return new RealtimeBridge({
    uri: env.SPACETIMEDB_URI,
    database: env.SPACETIMEDB_DB || "scalpal",
    tokenFile: env.SPACETIMEDB_COACH_TOKEN_FILE || ".coach-token",
    inviteCode: env.SPACETIMEDB_COACH_INVITE || undefined,
  });
}
