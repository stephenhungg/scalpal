// End-to-end check of Jarvis on the shared SpacetimeDB session, with three real identities:
//   operator (the companion), coach (this service, via HTTP), headset (a simulated Quest).
// Prereqs: `spacetime start`, the module published as `scalpal`, and this service running with
// SPACETIMEDB_URI=ws://127.0.0.1:3000 (npm run dev). Then: npm run realtime:e2e
import { DbConnection, tables } from "../src/module_bindings/index.js";

const URI = process.env.SPACETIMEDB_URI ?? "ws://127.0.0.1:3000";
const DB = process.env.SPACETIMEDB_DB ?? "scalpal";
const PREOP = process.env.PREOP_URL ?? "http://localhost:8787";
const t0 = Date.now();
const ms = () => `${String(Date.now() - t0).padStart(5)}ms`;
const wait = (n: number) => new Promise((r) => setTimeout(r, n));
const http = async (method: string, path: string, body?: unknown) => {
  const res = await fetch(PREOP + path, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
  const json = (await res.json()) as Record<string, any>;
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status} ${JSON.stringify(json.error)}`);
  return json;
};

type View = (typeof tables)[keyof typeof tables];

function connect(name: string, views: View[]): Promise<DbConnection> {
  return new Promise((resolve, reject) => {
    DbConnection.builder()
      .withUri(URI)
      .withDatabaseName(DB)
      .onConnect((c) => c.subscriptionBuilder().onApplied(() => resolve(c)).subscribe(views as never))
      .onConnectError((_c, err) => reject(new Error(`${name}: ${String(err)}`)))
      .build();
  });
}

async function main() {
  let failures = 0;
  const check = (ok: boolean, what: string) => {
    console.log(`${ok ? "PASS" : "FAIL"}  ${what}`);
    if (!ok) failures += 1;
  };

  // 1. Operator creates the session and reads the invite codes.
  const operator = await connect("operator", [tables.mySessions, tables.sessionInvites, tables.sessionEncounters, tables.sessionEncounterEvents, tables.sessionCoachMessages, tables.sessionCoachStatus, tables.sessionCommands]);
  const sessionId = `ses_e2e_${Date.now().toString(36)}`;
  await operator.reducers.createSession({ sessionId, label: "Jarvis e2e", exerciseId: "lap_appendectomy", exerciseVersion: "1", displayName: "Operator" });
  let invites: { role: string; code: string }[] = [];
  for (let i = 0; i < 100 && invites.length < 4; i++) {
    invites = [...operator.db.sessionInvites.iter()].filter((x) => x.sessionId === sessionId).map((x) => ({ role: x.role, code: x.code }));
    await wait(30);
  }
  const code = (role: string) => invites.find((x) => x.role === role)?.code ?? "";
  console.log(`${ms()} operator created ${sessionId}; coach code ${code("coach")}, headset code ${code("headset")}`);

  // Print everything the operator sees, live.
  operator.db.sessionEncounters.onInsert((_c, row) => console.log(`${ms()} [live] encounter ${row.encounterId} ${row.patientName} (${row.phase})`));
  operator.db.sessionEncounters.onUpdate((_c, _old, row) => console.log(`${ms()} [live] encounter -> ${row.phase}${row.scoreTotal != null ? ` score ${row.scoreTotal} ${row.grade}` : ""}`));
  operator.db.sessionEncounterEvents.onInsert((_c, row) => console.log(`${ms()} [live] encounter_event ${row.kind}${row.itemId ? `:${row.itemId}` : ""}${row.speaker ? ` (${row.speaker})` : ""} ${row.text.slice(0, 60)}`));
  operator.db.sessionCoachMessages.onInsert((_c, row) => console.log(`${ms()} [live] coach_message ${row.speaker}: ${row.text.slice(0, 70)}`));
  operator.db.sessionCommands.onUpdate((_c, _old, row) => console.log(`${ms()} [live] command ${row.action}(${row.targetId}) -> ${row.status}`));

  // 2. Jarvis (this service) joins as coach.
  const joined = await http("POST", "/realtime/join", { code: code("coach") });
  check(joined.sessionId === sessionId, "coach service joined the session with the coach invite code");

  // 3. Simulated headset joins, publishes state, and resolves Jarvis's commands.
  const headset = await connect("headset", [tables.mySessions, tables.sessionCommands, tables.sessionExerciseState]);
  await headset.reducers.joinSession({ code: code("headset"), displayName: "Quest" });
  await wait(200);
  const attemptId = `${sessionId}-a1`;
  await headset.reducers.publishExerciseState({
    sessionId, attemptId, mode: "Practicing", stepId: "find_appendix", stepIndex: 2, stepCount: 10,
    selectedStructureId: undefined, clearSelectedStructure: false, highlightedStructureId: undefined, clearHighlightedStructure: false,
    previewRotating: false, paused: false, registration: "valid", registrationReason: undefined, recording: "off",
  });
  headset.db.sessionCommands.onInsert((_c, row) => {
    if (row.status !== "pending") return;
    console.log(`${ms()} [headset] received ${row.action}(${row.targetId}); applying`);
    void headset.reducers.resolveCommand({ commandId: row.commandId, status: "applied", reason: undefined });
  });

  // 4. Pre-op encounter over HTTP; everything should appear in the shared session.
  const enc = await http("POST", "/encounters", { patientId: "patient-demo-multi-source" });
  await http("POST", `/encounters/${enc.encounterId}/transcript`, { speaker: "learner", text: "Any allergies?" });
  await http("POST", `/encounters/${enc.encounterId}/tools/answer`, { topic: "allergies" });
  await http("POST", `/encounters/${enc.encounterId}/transcript`, { speaker: "patient", text: "I'm allergic to latex." });
  await http("POST", `/encounters/${enc.encounterId}/tools/examine`, { maneuver: "rebound" });
  await http("POST", `/encounters/${enc.encounterId}/tools/order_test`, { test: "pregnancy_test" });
  await http("POST", `/encounters/${enc.encounterId}/attending`);
  const scored = await http("POST", `/encounters/${enc.encounterId}/tools/record_assessment`, {
    diagnosis: "acute appendicitis", differential: ["ectopic pregnancy", "ovarian torsion", "kidney stone"], procedure: "laparoscopic appendectomy", urgency: "urgent",
  });
  console.log(`${ms()} coach scored the encounter: ${scored.result.slice(0, 60)}`);

  // 5. Surgery: Jarvis highlights through the shared session; the headset applies it.
  const coach = await http("POST", "/coach/sessions", { patientId: "patient-demo-multi-source", mode: "mixed_reality" });
  await http("POST", `/coach/sessions/${coach.sessionId}/voice-status`, { status: "speaking" });
  await http("POST", `/coach/sessions/${coach.sessionId}/transcript`, { speaker: "coach", text: "Follow the taeniae down to the appendix base." });
  const lit = await http("POST", `/coach/sessions/${coach.sessionId}/tools/highlight_structure`, { structure: "cecum" });
  console.log(`${ms()} Jarvis tool result: ${lit.result}`);
  await wait(500);

  // 6. Verify from the operator's point of view.
  const encRow = [...operator.db.sessionEncounters.iter()].find((x) => x.encounterId === enc.encounterId);
  const events = [...operator.db.sessionEncounterEvents.iter()].filter((x) => x.encounterId === enc.encounterId);
  const messages = [...operator.db.sessionCoachMessages.iter()].filter((x) => x.sessionId === sessionId);
  const status = [...operator.db.sessionCoachStatus.iter()].find((x) => x.sessionId === sessionId);
  const commands = [...operator.db.sessionCommands.iter()].filter((x) => x.sessionId === sessionId);
  check(encRow?.phase === "scored" && encRow.scoreTotal != null, `encounter row scored (${encRow?.scoreTotal}/100 ${encRow?.grade})`);
  check(Boolean(encRow?.scorecardJson && JSON.parse(encRow.scorecardJson).sections?.length === 6), "full scorecard stored as JSON");
  check(["history", "exam", "test", "transcript", "assessment"].every((k) => events.some((e) => e.kind === k)), `encounter events cover history, exam, test, transcript, assessment (${events.length} rows)`);
  check(messages.some((m) => m.speaker === "patient") && messages.some((m) => m.speaker === "learner") && messages.some((m) => m.speaker === "coach") && messages.some((m) => m.speaker === "system"), "coach transcript has learner, patient, coach, and system lines");
  check(status?.status === "speaking", "voice status mirrored");
  check(commands.some((c) => c.action === "highlightStructure" && c.targetId === "cecum" && c.status === "applied"), "Jarvis highlight went through the shared session and the headset applied it");
  check(lit.result === "Highlighted the cecum in the headset.", "Jarvis only claimed the highlight after the headset's resolution");

  operator.disconnect();
  headset.disconnect();
  console.log(failures ? `\n${failures} check(s) failed` : "\nall checks passed");
  process.exit(failures ? 1 : 0);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
