// QA for the companion dashboard's live path, end to end with real processes:
//   HTTP calls (what the headset and the laptop send) -> coach service (:8787) -> SpacetimeDB -> a viewer.
// The viewer is a separate SpacetimeDB identity joined with the session's viewer invite code and
// subscribed to the views the companion reads: session_sim_logs, session_coach_messages,
// session_encounters, session_encounter_events.
//
// Prereqs: `spacetime start` with the module published as `scalpal`, and the coach service running with
// SPACETIMEDB_URI=ws://127.0.0.1:3000 (npm run dev). Then: npm run qa:dashboard
// It creates its own fresh sessions and rebinds the coach service to QA_REBIND_SESSION (if set) at the end.
// QA_SKIP_VOLUME=1 skips the 2000-row cap check; QA_DEATH_TIMEOUT_MS bounds the wait for death (120 s).
import { writeFileSync } from "node:fs";
import { DbConnection, tables } from "../src/module_bindings/index.js";

const URI = process.env.SPACETIMEDB_URI ?? "ws://127.0.0.1:3000";
const DB = process.env.SPACETIMEDB_DB ?? "scalpal";
const PREOP = process.env.PREOP_URL ?? "http://localhost:8787";
const PATIENT = process.env.QA_PATIENT ?? "patient-demo-multi-source";
const DEATH_TIMEOUT_MS = Number(process.env.QA_DEATH_TIMEOUT_MS ?? 120_000);
const REPORT = process.env.QA_REPORT_FILE ?? "";

const t0 = performance.now();
const ms = () => `${String(Math.round(performance.now() - t0)).padStart(6)}ms`;
const wait = (n: number) => new Promise((r) => setTimeout(r, n));

let failures = 0;
const results: { ok: boolean; what: string }[] = [];
const check = (ok: boolean, what: string) => {
  console.log(`${ok ? "PASS" : "FAIL"}  ${what}`);
  results.push({ ok, what });
  if (!ok) failures += 1;
};
const note = (what: string) => console.log(`NOTE  ${what}`);

async function http(method: string, path: string, body?: unknown): Promise<Record<string, any>> {
  const res = await fetch(PREOP + path, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
  const json = (await res.json().catch(() => ({}))) as Record<string, any>;
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status} ${JSON.stringify(json.error)}`);
  return json;
}

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

// Everything the viewer receives, with the local arrival time.
type Arrival = { t: number; table: "sim" | "msg" | "enc" | "encUpd" | "encEvt"; row: any };
const arrivals: Arrival[] = [];
const waiters = new Set<() => void>();
const push = (a: Arrival) => {
  arrivals.push(a);
  for (const w of [...waiters]) w();
};

// Resolves with the first arrival at or after index `from` matching `pred`, or null on timeout.
function waitFor(from: number, pred: (a: Arrival) => boolean, timeoutMs = 5000): Promise<Arrival | null> {
  return new Promise((resolve) => {
    const scan = () => {
      for (let i = from; i < arrivals.length; i++) if (pred(arrivals[i]!)) return arrivals[i]!;
      return null;
    };
    const hit = scan();
    if (hit) return resolve(hit);
    const timer = setTimeout(() => {
      waiters.delete(fn);
      resolve(null);
    }, timeoutMs);
    const fn = () => {
      const h = scan();
      if (h) {
        clearTimeout(timer);
        waiters.delete(fn);
        resolve(h);
      }
    };
    waiters.add(fn);
  });
}

const latencies: Record<string, number[]> = {};
// Sends one HTTP call and measures until the viewer sees the matching row.
async function timed(label: string, call: () => Promise<Record<string, any>>, pred: (a: Arrival) => boolean, timeoutMs = 5000) {
  const mark = arrivals.length;
  const sent = performance.now();
  const out = await call();
  const hit = await waitFor(mark, pred, timeoutMs);
  if (hit) (latencies[label] ??= []).push(hit.t - sent);
  else console.log(`${ms()} [timeout] ${label}: no matching viewer row within ${timeoutMs} ms`);
  return { out, hit };
}

const pct = (xs: number[], p: number) => {
  if (!xs.length) return NaN;
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.ceil((p / 100) * s.length) - 1)]!;
};

async function createSession(operator: DbConnection, label: string) {
  const sessionId = `ses_qa_${Date.now().toString(36)}${Math.random().toString(36).slice(2, 5)}`;
  await operator.reducers.createSession({ sessionId, label, exerciseId: "lap_appendectomy", exerciseVersion: "1", displayName: "QA operator" });
  let invites: { role: string; code: string }[] = [];
  for (let i = 0; i < 100 && invites.length < 4; i++) {
    invites = [...operator.db.sessionInvites.iter()].filter((x) => x.sessionId === sessionId).map((x) => ({ role: x.role, code: x.code }));
    await wait(30);
  }
  const code = (role: string) => invites.find((x) => x.role === role)?.code ?? "";
  return { sessionId, code };
}

async function main() {
  const operator = await connect("operator", [tables.mySessions, tables.sessionInvites, tables.sessionSimLogs]);
  const { sessionId, code } = await createSession(operator, "Dashboard QA");
  console.log(`${ms()} session ${sessionId}: coach ${code("coach")} viewer ${code("viewer")}`);

  const viewer = await connect("viewer", [tables.mySessions, tables.sessionSimLogs, tables.sessionCoachMessages, tables.sessionEncounters, tables.sessionEncounterEvents]);
  await viewer.reducers.joinSession({ code: code("viewer"), displayName: "QA viewer" });
  const mine = (r: { sessionId: string }) => r.sessionId === sessionId;
  viewer.db.sessionSimLogs.onInsert((_c, r) => mine(r) && push({ t: performance.now(), table: "sim", row: r }));
  viewer.db.sessionCoachMessages.onInsert((_c, r) => mine(r) && push({ t: performance.now(), table: "msg", row: r }));
  viewer.db.sessionEncounters.onInsert((_c, r) => mine(r) && push({ t: performance.now(), table: "enc", row: r }));
  viewer.db.sessionEncounters.onUpdate((_c, _o, r) => mine(r) && push({ t: performance.now(), table: "encUpd", row: r }));
  viewer.db.sessionEncounterEvents.onInsert((_c, r) => mine(r) && push({ t: performance.now(), table: "encEvt", row: r }));

  const joined = await http("POST", "/realtime/join", { code: code("coach") });
  check(joined.sessionId === sessionId, `coach service bound to the fresh session (${joined.sessionId})`);

  // ---------------------------------------------------------------- 1. Office interview
  console.log(`\n== 1. office interview (${PATIENT})`);
  const started = await timed("interview start -> encounter row", () => http("POST", "/interviews", { patientId: PATIENT }), (a) => a.table === "enc");
  const iv = started.out;
  const ivId = iv.interviewId as string;
  const rounds = (iv.round?.of as number) ?? 0;
  console.log(`${ms()} interview ${ivId}, ${rounds} rounds`);

  {
    const mark = arrivals.length;
    const sent = performance.now();
    await timed("transcript -> encounter_event", () => http("POST", `/interviews/${ivId}/transcript`, { speaker: "patient", text: iv.openingLine }), (a) => a.table === "encEvt" && a.row.kind === "transcript");
    const m = await waitFor(mark, (a) => a.table === "msg" && a.row.text === iv.openingLine, 3000);
    if (m) (latencies["transcript -> coach_message"] ??= []).push(m.t - sent);
  }

  let last: Record<string, any> = {};
  for (let i = 0; i < rounds; i++) {
    const roundId = last.next?.roundId ?? iv.round.roundId;
    const keys = ["B", "A", "C", "D"];
    const key = keys[i % 4]!;
    const isLast = i === rounds - 1;
    const r = await timed("answer (tap) -> choice event", () => http("POST", `/interviews/${ivId}/answer`, { key }), (a) => a.table === "encEvt" && a.row.itemId === roundId, isLast ? 3000 : 1500);
    last = r.out;
    if (!isLast) {
      const line = `QA patient reply ${i + 1}`;
      await timed("transcript -> encounter_event", () => http("POST", `/interviews/${ivId}/transcript`, { speaker: "patient", text: line }), (a) => a.table === "encEvt" && a.row.text === line);
    }
  }
  check(Boolean(last.scorecard), `HTTP interview scored after ${rounds} taps (${last.scorecard?.total}/100 ${last.scorecard?.grade})`);
  await waitFor(0, (a) => a.table === "encUpd" && a.row.scoreTotal != null, 3000);

  const encRow = [...viewer.db.sessionEncounters.iter()].find((x) => x.encounterId === ivId);
  const encEvents = [...viewer.db.sessionEncounterEvents.iter()].filter((x) => x.encounterId === ivId);
  const choiceEvents = encEvents.filter((e) => e.kind === "choice");
  const transcriptEvents = encEvents.filter((e) => e.kind === "transcript");
  const msgs = [...viewer.db.sessionCoachMessages.iter()].filter(mine);
  check(Boolean(encRow), `viewer sees the encounter row (${encRow?.patientName}, phase ${encRow?.phase})`);
  check(choiceEvents.length === rounds, `viewer sees one choice event per round (${choiceEvents.length}/${rounds})`);
  check(transcriptEvents.length === rounds, `viewer sees every transcript line as an encounter event (${transcriptEvents.length}/${rounds})`);
  check(msgs.filter((m) => m.speaker === "patient").length === rounds, `transcript lines mirrored to coach_message (${msgs.filter((m) => m.speaker === "patient").length}/${rounds})`);
  check(encRow?.phase === "scored", `encounter phase scored (got ${encRow?.phase})`);
  check(encRow?.scoreTotal === last.scorecard?.total && encRow?.grade === last.scorecard?.grade, `encounter result matches HTTP score (${encRow?.scoreTotal} ${encRow?.grade} vs ${last.scorecard?.total} ${last.scorecard?.grade})`);
  check(Boolean(encRow?.scorecardJson && JSON.parse(encRow.scorecardJson).total === last.scorecard?.total), "scorecard JSON stored and parseable");
  check(msgs.some((m) => m.speaker === "system" && /Pre-op interview \d+\/100/.test(m.text)), "system coach_message announces the interview score");
  const rt1 = await http("GET", "/realtime");
  if (rt1.lastError) note(`coach bridge lastError after office: ${rt1.lastError}`);

  // ---------------------------------------------------------------- 2. Operating room
  console.log(`\n== 2. operating room`);
  const coach = await http("POST", "/coach/sessions", { patientId: PATIENT, encounterId: ivId });
  const sid = coach.sessionId as string;
  const simRows = () => arrivals.filter((a) => a.table === "sim" && a.row.coachSessionId === sid);
  console.log(`${ms()} coach session ${sid}, step ${coach.snapshot?.step?.id}`);
  const sim = (kind: string) => http("POST", `/coach/sessions/${sid}/simulate`, { kind });
  const isSim = (a: Arrival) => a.table === "sim" && a.row.coachSessionId === sid;

  for (let i = 0; i < 3; i++) {
    const r = await timed("simulate complete_step -> sim_log", () => sim("complete_step"), (a) => isSim(a) && a.row.kind === "checklist");
    console.log(`${ms()} complete_step ${i + 1}: now ${r.out.snapshot?.step?.id}`);
  }
  const mistake = await timed("simulate mistake -> sim_log", () => sim("mistake"), (a) => isSim(a) && a.row.kind === "alert");
  const bleedingAfterMistake = mistake.out.alerts?.some((x: any) => x.kind === "bleeding");
  console.log(`${ms()} mistake on step ${mistake.out.snapshot?.step?.id}: alerts ${mistake.out.alerts?.map((x: any) => x.kind).join(",")}; loss ${mistake.out.snapshot?.condition?.vitals?.bloodLossPct}%`);
  check(Boolean(bleedingAfterMistake), "mistake (blade on muscle) opens a bleed");

  // 5 s of real time with no SSE client attached: do ticks run by themselves?
  const preTick = simRows().length;
  await wait(5000);
  const noStreamVitals = simRows().slice(preTick).filter((a) => a.row.kind === "vitals").length;
  check(noStreamVitals >= 2, `vitals keep sampling during 5 s of bleeding with no SSE client attached (${noStreamVitals} vitals rows)`);

  // Attach an SSE client the way the laptop Scalpal page does; the ticker starts with the first stream.
  const ac = new AbortController();
  const stream = fetch(`${PREOP}/coach/sessions/${sid}/stream`, { signal: ac.signal })
    .then(async (res) => {
      const reader = res.body!.getReader();
      for (;;) if ((await reader.read()).done) break;
    })
    .catch(() => {});
  const streamFrom = simRows().length;
  const snapOf = async () => (await http("GET", `/coach/sessions/${sid}`).catch((e) => {
    check(false, `coach session still live (the coach service restarted mid-run? ${String(e).slice(0, 80)})`);
    return { snapshot: { timeline: [], condition: {} } };
  })).snapshot;
  const mlBefore = await snapOf();
  await wait(6000);
  const mlAfter = await snapOf();
  console.log(`${ms()} muscle bleed over 6 s with ticks: bloodLossMl ${mlBefore.bloodLossMl} -> ${mlAfter.bloodLossMl}, rawBloodLossMl ${mlBefore.condition?.rawBloodLossMl} -> ${mlAfter.condition?.rawBloodLossMl}, loss ${mlBefore.condition?.vitals?.bloodLossPct}% -> ${mlAfter.condition?.vitals?.bloodLossPct}%`);
  check(mlAfter.condition?.rawBloodLossMl > mlBefore.condition?.rawBloodLossMl, `an open muscle bleed keeps losing blood on coach ticks (raw ${mlBefore.condition?.rawBloodLossMl} -> ${mlAfter.condition?.rawBloodLossMl} mL)`);
  const streamVitals = simRows().slice(streamFrom).filter((a) => a.row.kind === "vitals");
  const gaps = streamVitals.slice(1).map((a, i) => a.t - streamVitals[i]!.t);
  console.log(`${ms()} with SSE attached: ${streamVitals.length} vitals rows in 6 s, gaps ${gaps.map((g) => Math.round(g)).join(", ")} ms`);
  check(streamVitals.length >= 2 && gaps.every((g) => g > 1500 && g < 3500), `vitals sampled about every 2 s while bleeding (${streamVitals.length} rows, gaps ${gaps.map((g) => Math.round(g)).join("/")} ms)`);
  const lossSeries = streamVitals.map((a) => JSON.parse(a.row.dataJson).bloodLossPct as number);
  check(lossSeries.length >= 2 && lossSeries.at(-1)! >= lossSeries[0]!, `blood loss does not fall while bleeding (${lossSeries.join(" -> ")} %)`);

  const stop = await timed("simulate stop_bleed -> sim_log", () => sim("stop_bleed"), (a) => isSim(a) && a.row.kind === "alert");
  console.log(`${ms()} stop_bleed alerts: ${stop.out.alerts?.map((x: any) => x.kind).join(",")}`);
  check(Boolean(stop.out.alerts?.some((x: any) => x.kind === "bleeding_controlled")), "stop_bleed controls the bleed");

  const neckSent = performance.now();
  const neck = await timed("simulate cut_neck -> sim_log alert", () => sim("cut_neck"), (a) => isSim(a) && a.row.kind === "alert");
  console.log(`${ms()} cut_neck alerts: ${neck.out.alerts?.map((x: any) => x.kind).join(",")}`);
  const neckFrom = simRows().length;
  const death = await waitFor(0, (a) => isSim(a) && a.row.kind === "outcome" && /died/i.test(a.row.text), DEATH_TIMEOUT_MS);
  console.log(`${ms()} death outcome ${death ? `after ${Math.round((death.t - neckSent) / 1000)} s: ${death.row.text}` : "not seen"}`);
  const neckVitals = simRows().slice(neckFrom).filter((a) => a.row.kind === "vitals" && (!death || a.row.id < death.row.id));
  const neckGaps = neckVitals.slice(1).map((a, i) => Math.round(a.t - neckVitals[i]!.t));
  console.log(`${ms()} neck bleed vitals: ${neckVitals.length} rows, gaps ${neckGaps.join(", ")} ms; loss ${neckVitals.map((a) => JSON.parse(a.row.dataJson).bloodLossPct).join(" -> ")} %`);
  const median = pct(neckGaps.filter((g) => g > 1200), 50);
  check(median > 1500 && median < 2500, `while bleeding, the routine vitals sample comes about every 2 s (median gap ${median} ms excluding class-change samples)`);
  check(Boolean(death), `viewer sees the death outcome within ${DEATH_TIMEOUT_MS / 1000} s of cut_neck`);
  await wait(3000); // anything after death should be quiet

  const or = simRows();
  const kinds = new Set(or.map((a) => a.row.kind));
  check(["event", "alert", "vitals", "checklist", "outcome"].every((k) => kinds.has(k)), `viewer sees every sim_log kind (${[...kinds].join(", ")}; ${or.length} rows)`);
  const ids = or.map((a) => a.row.id as bigint);
  check(ids.every((id, i) => i === 0 || id > ids[i - 1]!), "sim_log rows arrive at the viewer in insertion (id) order");
  const deaths = or.filter((a) => a.row.kind === "outcome" && /died/i.test(a.row.text));
  check(deaths.length === 1, `exactly one death outcome row (${deaths.length})`);
  const afterDeath = death ? or.filter((a) => a.row.id > death.row.id) : [];
  check(afterDeath.filter((a) => a.row.kind === "vitals").length <= 1, `no vitals stream after death (${afterDeath.map((a) => a.row.kind).join(",") || "none"})`);
  const lastVitals = [...or].reverse().find((a) => a.row.kind === "vitals");
  if (death) check(Boolean(lastVitals && JSON.parse(lastVitals.row.dataJson).hr === 0), `latest vitals sample after death shows asystole (hr ${lastVitals ? JSON.parse(lastVitals.row.dataJson).hr : "?"})`);
  for (const a of or) {
    if (!a.row.dataJson) continue;
    try {
      JSON.parse(a.row.dataJson);
    } catch {
      check(false, `sim_log ${a.row.id} dataJson parses`);
    }
  }
  // Per update, forwardLogs writes event, alert, vitals, checklist, outcome in that order; the death update ends with outcome.
  if (death) {
    const before = or.filter((a) => a.row.id < death.row.id).slice(-6).map((a) => a.row.kind);
    console.log(`${ms()} rows just before death: ${before.join(", ")}`);
  }
  // Timeline lines: each should appear once as an event row.
  const events = or.filter((a) => a.row.kind === "event").map((a) => `${JSON.parse(a.row.dataJson || "{}").atSeconds}|${a.row.text}`);
  const dupes = events.filter((e, i) => events.indexOf(e) !== i);
  check(dupes.length === 0, `no duplicated event rows (${dupes.length} dupes${dupes.length ? `: ${dupes.slice(0, 3).join(" ; ")}` : ""})`);
  const snap = await snapOf();
  const timeline = (snap.timeline as { atSeconds: number; text: string }[]).map((t) => `${t.atSeconds}|${t.text}`);
  const missing = timeline.filter((t) => !events.includes(t));
  check(missing.length === 0, `every coach timeline line reached the viewer as an event row (${timeline.length - missing.length}/${timeline.length}${missing.length ? `; missing: ${missing.slice(0, 3).join(" ; ")}` : ""})`);
  const orMsgs = arrivals.filter((a) => a.table === "msg" && a.t > neckSent - 60_000);
  note(`coach_message rows during the OR run: ${orMsgs.length}`);
  ac.abort();
  await stream;
  const rt2 = await http("GET", "/realtime");
  if (rt2.lastError) note(`coach bridge lastError after OR: ${rt2.lastError}`);

  // ---------------------------------------------------------------- 3. Latency
  console.log(`\n== 3. latency (HTTP call sent -> viewer onInsert/onUpdate)`);
  const all: number[] = [];
  for (const [label, xs] of Object.entries(latencies)) {
    all.push(...xs);
    console.log(`      ${label.padEnd(40)} n=${String(xs.length).padStart(2)} p50 ${pct(xs, 50).toFixed(1)} ms  p95 ${pct(xs, 95).toFixed(1)} ms  max ${Math.max(...xs).toFixed(1)} ms`);
  }
  console.log(`      ${"all".padEnd(40)} n=${String(all.length).padStart(2)} p50 ${pct(all, 50).toFixed(1)} ms  p95 ${pct(all, 95).toFixed(1)} ms`);
  check(pct(all, 95) < 500, `p95 end-to-end latency under 500 ms (${pct(all, 95).toFixed(1)} ms)`);

  // ---------------------------------------------------------------- 4. Volume
  let volume: Record<string, unknown> = { skipped: true };
  if (!process.env.QA_SKIP_VOLUME) {
    console.log(`\n== 4. volume: 2000-row cap`);
    const vol = await createSession(operator, "Dashboard QA volume");
    const N = 2100;
    const tStart = performance.now();
    const pending: Promise<unknown>[] = [];
    for (let i = 0; i < N; i++) {
      pending.push(operator.reducers.appendSimLog({ sessionId: vol.sessionId, coachSessionId: "qa_volume", kind: "event", text: `row ${i}`, dataJson: "" }));
      if (pending.length >= 200) await Promise.all(pending.splice(0));
    }
    await Promise.all(pending);
    const elapsed = performance.now() - tStart;
    await wait(500);
    const rows = [...operator.db.sessionSimLogs.iter()].filter((x) => x.sessionId === vol.sessionId);
    const texts = new Set(rows.map((r) => r.text));
    volume = { inserted: N, kept: rows.length, oldestKept: [...rows].sort((a, b) => (a.id < b.id ? -1 : 1))[0]?.text, ms: Math.round(elapsed) };
    console.log(`${ms()} appended ${N} rows in ${Math.round(elapsed)} ms (${(N / (elapsed / 1000)).toFixed(0)}/s); kept ${rows.length}`);
    check(rows.length === 2000, `session keeps exactly 2000 rows after ${N} inserts (${rows.length})`);
    check(!texts.has("row 0") && !texts.has(`row ${N - 2001}`) && texts.has(`row ${N - 2000}`) && texts.has(`row ${N - 1}`), "the oldest rows are the ones dropped");
    await operator.reducers.endSession?.({ sessionId: vol.sessionId }).catch(() => {});
  }

  // ---------------------------------------------------------------- cleanup
  const rebind = process.env.QA_REBIND_SESSION;
  if (rebind) {
    const r = await http("POST", "/realtime/join", { sessionId: rebind }).catch((e) => ({ error: String(e) }));
    console.log(`${ms()} rebound coach service: ${JSON.stringify(r).slice(0, 160)}`);
  }
  if (REPORT) writeFileSync(REPORT, JSON.stringify({ sessionId, interviewId: ivId, coachSessionId: sid, results, latencies, volume }, null, 2));
  viewer.disconnect();
  operator.disconnect();
  console.log(failures ? `\n${failures} check(s) failed` : "\nall checks passed");
  process.exit(failures ? 1 : 0);
}

main().catch(async (err) => {
  console.error(err);
  const rebind = process.env.QA_REBIND_SESSION;
  if (rebind) await http("POST", "/realtime/join", { sessionId: rebind }).then(() => console.log(`rebound coach service to ${rebind}`)).catch((e) => console.error(`rebind failed: ${e}`));
  process.exit(1);
});
