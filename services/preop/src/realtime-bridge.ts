import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { DbConnection, tables } from "./module_bindings/index.js";
import type { EncounterLogEntry, EncounterSession, Scorecard } from "./encounter.js";

// Jarvis's connection to the shared SpacetimeDB session (Nathan's module), as the `coach` role.
// Everything Jarvis does is mirrored there live, so the companion, the headset, and anyone else
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
  // Resolves to the headset's resolution, or null when the shared session cannot carry the command.
  highlight(targetId: string, timeoutMs?: number): Promise<{ status: string; reason: string } | null>;
}

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
            if (this.cfg.inviteCode && !this.sessionId) void this.join(this.cfg.inviteCode).catch((e) => (this.lastError = String(e)));
          })
          .subscribe([tables.mySessions, tables.myMemberships, tables.sessionExerciseState, tables.sessionCommands, tables.sessionEncounters]);
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
    await conn.reducers.joinSession({ code: code.trim().toUpperCase(), displayName: "Jarvis" });
    for (let i = 0; i < 100; i++) {
      const m = [...conn.db.myMemberships.iter()].find((x) => x.role === "coach" && !before.has(x.sessionId));
      if (m) {
        this.sessionId = m.sessionId;
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
    if (ok) this.sessionId = sessionId;
    return ok;
  }

  private call(name: string, fn: (c: DbConnection) => Promise<unknown>) {
    const conn = this.conn;
    if (!conn || !this.bound) return;
    fn(conn).catch((err) => {
      this.lastError = `${name}: ${String(err)}`;
      this.log(`${name} failed`, String(err));
    });
  }

  coachMessage(speaker: "learner" | "coach" | "system" | "patient", text: string) {
    if (!text.trim()) return;
    this.call("postCoachMessage", (c) => c.reducers.postCoachMessage({ sessionId: this.sessionId, speaker, text: text.slice(0, 4000) }));
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
