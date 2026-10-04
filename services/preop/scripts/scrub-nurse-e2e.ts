// End-to-end check of the shared operating room's non-surgeon actors on SpacetimeDB:
//   a browser scrub nurse (viewer role) hands and highlights stand instruments through requestCommand,
//   the headset resolves them, the coach posts robot-learner verdicts, and every member sees it live.
// Needs only `spacetime start` with the module published as `scalpal`. Run: npm run realtime:nurse
import { DbConnection, tables } from "../src/module_bindings/index.js";

const URI = process.env.SPACETIMEDB_URI ?? "ws://127.0.0.1:3000";
const DB = process.env.SPACETIMEDB_DB ?? "scalpal";
const wait = (n: number) => new Promise((r) => setTimeout(r, n));

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

/** The reducer's rejection reason, or '' when it succeeded. */
async function reason(call: Promise<unknown>): Promise<string> {
  try {
    await call;
    return "";
  } catch (err) {
    return String(err);
  }
}

async function until(ok: () => boolean, ms = 3000) {
  for (let i = 0; i < ms / 25 && !ok(); i++) await wait(25);
}

async function main() {
  let failures = 0;
  const check = (ok: boolean, what: string) => {
    console.log(`${ok ? "PASS" : "FAIL"}  ${what}`);
    if (!ok) failures += 1;
  };

  const operator = await connect("operator", [tables.mySessions, tables.sessionInvites]);
  const sessionId = `ses_nurse_${Date.now().toString(36)}`;
  await operator.reducers.createSession({ sessionId, label: "Scrub nurse e2e", exerciseId: "open_appendectomy", exerciseVersion: "1", displayName: "Operator" });
  await until(() => [...operator.db.sessionInvites.iter()].filter((x) => x.sessionId === sessionId).length >= 4);
  const code = (role: string) => [...operator.db.sessionInvites.iter()].find((x) => x.sessionId === sessionId && x.role === role)?.code ?? "";

  const headset = await connect("headset", [tables.mySessions, tables.sessionCommands, tables.sessionExerciseState]);
  await headset.reducers.joinSession({ code: code("headset"), displayName: "Quest" });
  const coach = await connect("coach", [tables.mySessions]);
  await coach.reducers.joinSession({ code: code("coach"), displayName: "Scalpal" });
  const nurse = await connect("nurse", [tables.mySessions, tables.sessionCommands, tables.sessionExerciseState, tables.sessionRobotResults]);
  await nurse.reducers.joinSession({ code: code("viewer"), displayName: "Scrub nurse" });

  await headset.reducers.publishExerciseState({
    sessionId, attemptId: `${sessionId}-a1`, mode: "Practicing", stepId: "mark_incision", stepIndex: 1, stepCount: 10,
    selectedStructureId: undefined, clearSelectedStructure: false, highlightedStructureId: undefined, clearHighlightedStructure: false,
    previewRotating: false, paused: false, registration: "valid", registrationReason: undefined, recording: "off",
  });
  // The headset applies stand-instrument commands, and rejects a hand-over to a hand already holding that tool.
  headset.db.sessionCommands.onInsert((_c, row) => {
    if (row.sessionId !== sessionId || row.status !== "pending") return;
    const busy = row.action === "handInstrument" && row.targetId === "scalpel" && row.argNumber === 1;
    void headset.reducers.resolveCommand({ commandId: row.commandId, status: busy ? "rejected" : "applied", reason: busy ? "scalpel already in your right hand" : undefined });
  });
  await until(() => [...nurse.db.sessionExerciseState.iter()].some((x) => x.sessionId === sessionId && x.stepId === "mark_incision"));
  const stepVersion = () => [...nurse.db.sessionExerciseState.iter()].find((x) => x.sessionId === sessionId)?.stepVersion ?? 0n;

  const ask = (who: DbConnection, commandId: string, action: string, targetId?: string, argNumber?: number) =>
    reason(who.reducers.requestCommand({ commandId, sessionId, action, targetId, argBool: undefined, argNumber, expectedStepVersion: stepVersion() }));
  const row = (id: string) => [...nurse.db.sessionCommands.iter()].find((x) => x.commandId === id);

  // A nurse in the browser is a viewer, and may hand instruments: that is the point of the role.
  const tag = Date.now().toString(36);
  check((await ask(nurse, `hand_l_${tag}`, "handInstrument", "hemostat", 0)) === "", "viewer (scrub nurse) can request handInstrument");
  check((await ask(nurse, `lite_${tag}`, "highlightInstrument", "metzenbaum_scissors")) === "", "viewer can request highlightInstrument");
  check((await ask(coach, `hand_e_${tag}`, "handInstrument", "suction_irrigator")) === "", "coach (Jarvis) can request handInstrument for either hand");
  await until(() => ["hand_l_", "lite_", "hand_e_"].every((p) => row(`${p}${tag}`)?.status === "applied"));
  const handed = row(`hand_l_${tag}`);
  if (handed?.status !== "applied") console.log("hand-over row:", handed?.status, handed?.reason);
  check(handed?.status === "applied" && handed.requestedRole === "viewer" && handed.argNumber === 0, "nurse's hand-over reached the headset and the applied status came back to the nurse, attributed to viewer");
  check(row(`hand_e_${tag}`)?.requestedRole === "coach" && row(`hand_e_${tag}`)?.argNumber === undefined, "coach's either-hand request carries no hand and is attributed to coach");
  check(row(`lite_${tag}`)?.status === "applied", "highlight resolved by the headset");

  check((await ask(nurse, `busy_${tag}`, "handInstrument", "scalpel", 1)) === "", "nurse can ask for the scalpel in the right hand");
  await until(() => row(`busy_${tag}`)?.status === "rejected");
  check(row(`busy_${tag}`)?.reason === "scalpel already in your right hand", "headset rejection and its reason reach the nurse");

  // Validation: only stand instruments, only real hands, and a viewer gets no other actions.
  check(/invalid instrument/.test(await ask(nurse, `bad_${tag}`, "handInstrument", "chainsaw")), "unknown instrument ids are rejected");
  check(/requires targetId/.test(await ask(nurse, `none_${tag}`, "highlightInstrument")), "instrument actions need a targetId");
  check(/0 \(left\) or 1 \(right\)/.test(await ask(nurse, `hand9_${tag}`, "handInstrument", "scalpel", 2)), "hand must be 0 (left) or 1 (right)");
  check(/requires role/.test(await ask(nurse, `struct_${tag}`, "highlightStructure", "cecum")), "viewer still cannot request structure actions");
  check((await ask(coach, `struct_${tag}`, "highlightStructure", "cecum")) === "", "coach keeps its structure actions");

  // The robot learner is an actor too: the coach posts its verdict, everyone sees it.
  const robot = (who: DbConnection) =>
    reason(who.reducers.postRobotResult({ sessionId, stepId: "mark_incision", success: true, pathErrorMm: 3.2, policySuccessRate: 0.8, demosHuman: 5, demosSynthetic: 120, videoUrl: undefined }));
  check((await robot(coach)) === "", "coach can post a robot result");
  await until(() => [...nurse.db.sessionRobotResults.iter()].some((x) => x.sessionId === sessionId));
  const verdict = [...nurse.db.sessionRobotResults.iter()].find((x) => x.sessionId === sessionId);
  check(verdict?.success === true && verdict.demosSynthetic === 120 && verdict.attemptId === `${sessionId}-a1`, "viewer sees the robot verdict, stamped with the current attempt");
  check(/requires role/.test(await robot(nurse)), "viewer cannot post robot results");
  check(/between 0 and 1/.test(await reason(coach.reducers.postRobotResult({ sessionId, stepId: "x", success: false, pathErrorMm: undefined, policySuccessRate: 2, demosHuman: 0, demosSynthetic: 0, videoUrl: undefined }))), "policy success rate must be a fraction");

  await operator.reducers.endSession({ sessionId });
  for (const c of [operator, headset, coach, nurse]) c.disconnect();
  console.log(failures ? `\n${failures} check(s) failed` : "\nall checks passed");
  process.exit(failures ? 1 : 0);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
