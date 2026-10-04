// End-to-end check of the simulated patient living inside SpacetimeDB (patient_condition + the 1 Hz
// scheduled patientTick), with injected data and no headset:
//   operator creates a session, a coach identity starts the condition from a chart baseline (HR 72) and
//   injects an arterial bleed; the module's ticks must raise blood loss and escalate HR/class, log the class
//   change to sim_log, stop accruing once the bleed is controlled, and kill the patient on a catastrophic
//   region. A viewer can read the condition but not write it, and the module's vitals must equal the coach's
//   patient-condition.ts for the same facts.
// Optional: PREOP_URL pointing at a coach bound to nothing yet (e.g. a temporary one on :8798) also checks
// that the coach snapshot serves the module's condition (condition.source === "spacetime").
// Run: SPACETIMEDB_URI=ws://127.0.0.1:3000 SPACETIMEDB_DB=scalpal npx tsx scripts/patient-condition-e2e.ts
import { DbConnection, tables } from "../src/module_bindings/index.js";
import { PatientCondition } from "../src/patient-condition.js";

const URI = process.env.SPACETIMEDB_URI ?? "ws://127.0.0.1:3000";
const DB = process.env.SPACETIMEDB_DB ?? "scalpal";
const PREOP = process.env.PREOP_URL ?? "";
const RATE = 135; // raw ml/min, arterial
const t0 = Date.now();
const ms = () => `${String(Date.now() - t0).padStart(6)}ms`;
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

async function rejected(call: Promise<unknown>): Promise<string> {
  try {
    await call;
    return "";
  } catch (err) {
    return String(err);
  }
}

async function until<T>(get: () => T | undefined | null | false, timeoutMs: number, stepMs = 100): Promise<T | undefined> {
  for (let waited = 0; waited <= timeoutMs; waited += stepMs) {
    const v = get();
    if (v) return v;
    await wait(stepMs);
  }
  return undefined;
}

async function main() {
  let failures = 0;
  const check = (ok: boolean, what: string) => {
    console.log(`${ok ? "PASS" : "FAIL"}  ${what}`);
    if (!ok) failures += 1;
  };
  console.log(`${ms()} target ${URI} / ${DB}`);

  const operator = await connect("operator", [tables.mySessions, tables.sessionInvites]);
  const sessionId = `ses_pc_${Date.now().toString(36)}`;
  await operator.reducers.createSession({ sessionId, label: "Patient condition e2e", exerciseId: "open_appendectomy", exerciseVersion: "1", displayName: "Operator" });
  const invites = await until(() => {
    const list = [...operator.db.sessionInvites.iter()].filter((x) => x.sessionId === sessionId);
    return list.length >= 4 ? list : undefined;
  }, 5000, 30);
  const code = (role: string) => invites?.find((x) => x.role === role)?.code ?? "";
  console.log(`${ms()} operator created ${sessionId}`);

  const coach = await connect("coach", [tables.myMemberships, tables.sessionPatientCondition]);
  await coach.reducers.joinSession({ code: code("coach"), displayName: "Scalpal e2e" });
  const viewer = await connect("viewer", [tables.myMemberships, tables.sessionPatientCondition, tables.sessionSimLogs]);
  await viewer.reducers.joinSession({ code: code("viewer"), displayName: "Laptop" });
  await until(() => [...viewer.db.myMemberships.iter()].some((m) => m.sessionId === sessionId), 5000, 30);

  const row = () => [...viewer.db.sessionPatientCondition.iter()].find((r) => r.sessionId === sessionId);
  const physiologyLogs = () => [...viewer.db.sessionSimLogs.iter()].filter((r) => r.sessionId === sessionId && r.coachSessionId === "spacetimedb-physiology");

  // 1. Start from the chart baseline (HR 72).
  const baseline = { hr: 72, rr: 14, sys: 118, dia: 76, source: "chart" };
  await coach.reducers.startPatientCondition({
    sessionId, coachSessionId: "coach_e2e", baselineHr: 72, baselineRr: 14, baselineSys: 118, baselineDia: 76, baselineSpo2: 98, baselineSource: "chart", weightKg: 70, mlPerKg: 70,
  });
  const started = await until(row, 5000);
  check(Boolean(started) && started!.hr === 72 && started!.hemorrhageClass === 1 && started!.outcomeResult === "in_progress", `viewer reads the started condition: HR ${started?.hr}, class ${started?.hemorrhageClass}, EBV ${started?.ebvMl} ml, ${started?.outcomeResult}`);

  // 2. Viewer can read but not write.
  const vErr = await rejected(viewer.reducers.reportBodyState({ sessionId, bloodLostMl: 9999, activeBleedsJson: "[]" }));
  const vErr2 = await rejected(viewer.reducers.startPatientCondition({ sessionId, coachSessionId: "x", baselineHr: 72, baselineRr: 14, baselineSys: 118, baselineDia: 76, baselineSpo2: -1, baselineSource: "chart", weightKg: 70, mlPerKg: 70 }));
  const vErr3 = await rejected(viewer.reducers.reportInjury({ sessionId, region: "head", controlled: false }));
  check(/requires role/.test(vErr) && /requires role/.test(vErr2) && /requires role/.test(vErr3), `viewer writes rejected: ${vErr.slice(0, 60)}`);

  // 3. Inject an arterial bleed; the scheduled tick must accrue and escalate on its own.
  await coach.reducers.reportBodyState({ sessionId, bloodLostMl: 0, activeBleedsJson: JSON.stringify([{ name: "appendiceal artery", rateMlPerMin: RATE }]) });
  const samples: { lost: number; hr: number; cls: number; version: bigint }[] = [];
  for (let i = 0; i < 24; i++) {
    await wait(1000);
    const r = row()!;
    samples.push({ lost: r.bodyLostMl, hr: r.hr, cls: r.hemorrhageClass, version: r.version });
    if (r.hemorrhageClass >= 2 && samples.length >= 6 && samples.at(-1)!.hr > samples.at(-2)!.hr) break;
  }
  console.log(`${ms()} samples (raw lost ml / HR / class): ${samples.map((s) => `${s.lost.toFixed(1)}/${s.hr}/${s.cls}`).join("  ")}`);
  const versions = new Set(samples.map((s) => s.version)).size;
  check(versions >= samples.length - 1, `scheduled ticks advanced the row ${versions} times in ${samples.length} s with no client writes`);
  check(samples.every((s, i) => i === 0 || s.lost > samples[i - 1]!.lost), `blood loss rises every tick (${samples[0]!.lost.toFixed(1)} -> ${samples.at(-1)!.lost.toFixed(1)} ml raw)`);
  check(samples.every((s, i) => i === 0 || (s.hr >= samples[i - 1]!.hr && s.cls >= samples[i - 1]!.cls)), `HR and class escalate monotonically (HR ${samples[0]!.hr} -> ${samples.at(-1)!.hr}, class ${samples[0]!.cls} -> ${samples.at(-1)!.cls})`);
  check(samples.at(-1)!.cls >= 2, `hemorrhage class reached ${samples.at(-1)!.cls}`);
  const classLog = await until(() => physiologyLogs().find((l) => l.kind === "vitals" && /Hemorrhage class 1 -> 2/.test(l.text)), 3000);
  check(Boolean(classLog), `sim_log class-change row: ${classLog?.text.slice(0, 90)}`);

  // 4. Parity: the module's vitals equal patient-condition.ts for the same facts.
  const r = row()!;
  const local = new PatientCondition(() => 0);
  local.setBaseline(baseline, { weightKg: 70, spo2: 98, mlPerKg: 70 });
  local.setBodyBleeding(r.bodyLostMl, r.bodyBleedMlPerMin);
  local.update();
  const lv = local.view().vitals;
  const same = lv.hr === r.hr && lv.rr === r.rr && lv.sys === r.sys && lv.dia === r.dia && lv.spo2 === r.spo2 && lv.hemorrhageClass === r.hemorrhageClass && Math.abs(lv.bloodLossPct - r.bloodLossPct) <= 0.1;
  check(same, `module vitals match patient-condition.ts: module HR ${r.hr} BP ${r.sys}/${r.dia} RR ${r.rr} SpO2 ${r.spo2} ${r.bloodLossPct}% c${r.hemorrhageClass} | local HR ${lv.hr} BP ${lv.sys}/${lv.dia} RR ${lv.rr} SpO2 ${lv.spo2} ${lv.bloodLossPct}% c${lv.hemorrhageClass}`);

  // 5. Controlling the bleed stops accrual (ticks keep running).
  await coach.reducers.reportBodyState({ sessionId, bloodLostMl: r.bodyLostMl, activeBleedsJson: "[]" });
  await wait(600);
  const a = row()!;
  await wait(3000);
  const b = row()!;
  check(b.bodyLostMl === a.bodyLostMl && b.version > a.version && b.bodyBleedMlPerMin === 0, `bleed controlled: loss held at ${b.bodyLostMl.toFixed(1)} ml raw over ${Number(b.version - a.version)} ticks`);

  // 6. A catastrophic region kills the patient.
  await coach.reducers.reportInjury({ sessionId, region: "head", controlled: false });
  const dead = await until(() => (row()?.outcomeResult === "died" ? row() : undefined), 3000);
  check(Boolean(dead) && dead!.hr === 0 && /head/.test(dead!.outcomeCause), `catastrophic head injury -> outcome ${dead?.outcomeResult} (${dead?.outcomeCause}), HR ${dead?.hr}`);
  const deathLog = await until(() => physiologyLogs().find((l) => l.kind === "outcome"), 3000);
  check(Boolean(deathLog), `sim_log death row: ${deathLog?.text.slice(0, 90)}`);
  const lateErr = await rejected(coach.reducers.reportBodyState({ sessionId, bloodLostMl: 0, activeBleedsJson: JSON.stringify([{ name: "x", rateMlPerMin: 500 }]) }));
  await wait(1500);
  check(!lateErr && row()!.outcomeResult === "died" && row()!.bodyBleedMlPerMin === 0, "a dead patient ignores later facts");

  // 7. Optional: the coach serves the module's condition in its snapshot.
  if (PREOP) {
    const http = async (method: string, path: string, body?: unknown) => {
      const res = await fetch(PREOP + path, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
      return { status: res.status, json: (await res.json()) as Record<string, any> };
    };
    const sid2 = `ses_pcc_${Date.now().toString(36)}`;
    await operator.reducers.createSession({ sessionId: sid2, label: "Patient condition coach e2e", exerciseId: "open_appendectomy", exerciseVersion: "1", displayName: "Operator" });
    const coachCode = await until(() => [...operator.db.sessionInvites.iter()].find((x) => x.sessionId === sid2 && x.role === "coach")?.code, 5000, 30);
    const viewerCode = [...operator.db.sessionInvites.iter()].find((x) => x.sessionId === sid2 && x.role === "viewer")?.code ?? "";
    await viewer.reducers.joinSession({ code: viewerCode, displayName: "Laptop" });
    const joined = await http("POST", "/realtime/join", { code: coachCode });
    check(joined.json.sessionId === sid2, `coach ${PREOP} joined ${sid2}`);
    const created = await http("POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" });
    const coachSid = created.json.sessionId as string;
    const coachRow = await until(() => [...viewer.db.sessionPatientCondition.iter()].find((x) => x.sessionId === sid2), 5000);
    check(coachRow?.coachSessionId === coachSid, `coach started the module's patient from ${coachRow?.baselineSource}: HR ${coachRow?.hr}, ${coachRow?.weightKg} kg, EBV ${coachRow?.ebvMl} ml`);
    await wait(1500);
    const snap = (await http("GET", `/coach/sessions/${coachSid}`)).json;
    const cond = (snap.snapshot ?? snap).condition;
    check(cond?.source === "spacetime" && cond.vitals.hr === coachRow?.hr, `coach snapshot condition.source = ${cond?.source} (HR ${cond?.vitals?.hr})`);
    await http("POST", `/coach/sessions/${coachSid}/simulate`, { kind: "cut_neck" });
    const neck = await until(() => {
      const x = [...viewer.db.sessionPatientCondition.iter()].find((y) => y.sessionId === sid2);
      return x && /neck/.test(x.regionInjuriesJson) && x.regionLostMl > 0 ? x : undefined;
    }, 5000);
    check(Boolean(neck), `coach forwarded the neck injury; module accrues region loss (${neck?.regionLostMl.toFixed(1)} ml raw, HR ${neck?.hr})`);
    const snap2 = (await http("GET", `/coach/sessions/${coachSid}`)).json;
    const cond2 = (snap2.snapshot ?? snap2).condition;
    check(cond2?.source === "spacetime" && cond2.regions.some((x: { region: string }) => x.region === "neck"), `coach snapshot shows the module's neck injury (HR ${cond2?.vitals?.hr}, source ${cond2?.source})`);
  }

  operator.disconnect();
  coach.disconnect();
  viewer.disconnect();
  console.log(failures ? `${failures} check(s) FAILED` : "all patient-condition checks passed");
  process.exit(failures ? 1 : 0);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
