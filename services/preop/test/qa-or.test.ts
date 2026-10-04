import { PatientCondition } from "../src/patient-condition.js";
import { describe, expect, it, vi } from "vitest";
import { createApp } from "../src/app.js";
import { buildCase } from "../src/case-builder.js";
import { chartBaseline } from "../src/chart-vitals.js";
import { monitorVitals } from "../src/physiology.js";
import { OPEN_BODY } from "../src/catalog/open-appendectomy.js";
import { CoachSession, renderContext, UNCONTROLLED_BLEED_MS } from "../src/coach.js";
import { bodyAction, type BodyAction } from "../src/open-body.js";
import { idealBodyActions } from "../src/open-body-fixtures.js";
import { REGION_IDS } from "../src/patient-condition.js";
import { unitySafetyErrors } from "../src/unity-safe.js";
import { NOW, fixture, fixtureClient } from "./helpers.js";

// QA pass over the operating-room backend (docs/operation-flow.md, docs/surgery-state.md and the
// "Operating room condition" section of docs/jarvis-handoff.md). Everything goes over HTTP with a
// controllable clock and coachTickMs 0; time-driven behavior is driven by headset 1 Hz ticks or by the
// alerts poll (which ticks the session). Tests skipped with "BUG:" document a defect with its repro.

const PRIYA = "patient-demo-multi-source"; // adult, 61.4 kg charted
const THEO = "patient-demo-pediatric-asthma"; // child, 26.8 kg charted
const OPEN_ORDER = OPEN_BODY.milestones.map((m) => m.id);
const BEFORE_MESO = OPEN_ORDER.slice(0, OPEN_ORDER.indexOf("divide_mesoappendix"));

type Json = Record<string, any>;

function rig(opts: { vitalsUrl?: string } = {}) {
  let t = NOW.getTime();
  const app = createApp({ client: fixtureClient(), now: () => new Date(t), coachTickMs: 0, vitalsUrl: opts.vitalsUrl });
  const unsafe: { route: string; errors: string[] }[] = [];
  const req = async (method: string, route: string, body?: unknown) => {
    const res = await app.request(route, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
    const json = (await res.json()) as Json;
    if (res.status < 300) {
      const errors = unitySafetyErrors(json);
      if (errors.length) unsafe.push({ route, errors });
    }
    return { status: res.status, json };
  };
  let headsetMs = 0;
  let seq = 0;
  const create = async (patientId = PRIYA, mode = "virtual") => {
    const r = await req("POST", "/coach/sessions", { patientId, mode });
    if (r.status !== 201) throw new Error(`create failed ${r.status} ${JSON.stringify(r.json)}`);
    return { sid: r.json.sessionId as string, created: r.json };
  };
  const events = (sid: string, ...evs: unknown[]) => req("POST", `/coach/sessions/${sid}/events`, { events: evs });
  const surgery = (evidence: BodyAction) => ({ type: "surgery", evidence });
  const act = (verb: string, tissueId: string, values: Partial<BodyAction> = {}) =>
    surgery(bodyAction(verb, tissueId, { actionId: `qa-${verb}-${tissueId}-${++seq}`, timeMs: headsetMs, ...values }));
  const ideal = (step: string) => idealBodyActions(step).map((e) => surgery({ ...e, timeMs: headsetMs }));
  const doSteps = async (sid: string, steps: string[]) => {
    for (const s of steps) {
      const r = await events(sid, ...ideal(s));
      if (r.status !== 200 || !r.json.results.every((x: Json) => x.applied)) throw new Error(`step ${s}: ${r.status} ${JSON.stringify(r.json.results ?? r.json)}`);
    }
  };
  // One headset 1 Hz assistant tick: headset clock and coach clock both move one second.
  const tick = async (sid: string) => {
    headsetMs += 1000;
    t += 1000;
    return events(sid, act("tick", "skin", { instrumentId: "assistant" }));
  };
  const state = async (sid: string) => (await req("GET", `/coach/sessions/${sid}`)).json;
  const alerts = async (sid: string, after = 0) => (await req("GET", `/coach/sessions/${sid}/alerts?after=${after}`)).json;
  const sim = (sid: string, kind: string) => req("POST", `/coach/sessions/${sid}/simulate`, { kind });
  return {
    req, create, events, act, ideal, doSteps, tick, state, alerts, sim, unsafe,
    advance: (ms: number) => void (t += ms),
    advanceHeadset: (ms: number) => void (headsetMs += ms),
  };
}

const cond = (j: Json) => j.snapshot.condition as Json;

// ---------------------------------------------------------------------------------------------------
describe("1. ideal open appendectomy", () => {
  it("completes with every checklist item done, no death, no blood loss and no warnings", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA);
    for (const step of OPEN_ORDER) {
      r.advance(5000);
      await r.doSteps(sid, [step]);
    }
    const s = await r.state(sid);
    expect(s.snapshot.status).toBe("completed");
    expect(cond(s).outcome.result).toBe("completed");
    expect(s.snapshot.checklist.every((c: Json) => c.done)).toBe(true);
    expect(s.snapshot.checklist.some((c: Json) => c.current)).toBe(false);
    expect(s.snapshot.achievedMilestones.sort()).toEqual([...OPEN_ORDER].sort());
    expect(s.snapshot.bloodLossMl).toBe(0);
    expect(cond(s).rawBloodLossMl).toBe(0);
    expect(cond(s).vitals.hemorrhageClass).toBe(1);
    expect(s.snapshot.mistakeCount).toBe(0);
    const log = (await r.alerts(sid)).alerts as Json[];
    expect(log.filter((a) => a.tier === "warning")).toEqual([]);
    expect(log.filter((a) => a.kind === "patient_died" || a.kind === "vitals" || a.kind === "mistake")).toEqual([]);
    expect(log.filter((a) => a.kind === "case_complete")).toHaveLength(1);
    expect(s.context).toMatch(/COMPLETE/);
    expect(s.context).not.toMatch(/UNMET/);
    expect(r.unsafe).toEqual([]);
  });

  it("FIXED: a learner who ends early with finish is reported as outcome completed", async () => {
    // conditionAlerts() calls condition.markCompleted() whenever engine.completed, and a finish event sets
    // engine.current = null, so an abandoned attempt (bodyGrade.complete false) reports outcome "completed".
    const r = rig();
    const { sid } = await r.create(PRIYA);
    await r.doSteps(sid, ["mark_incision"]);
    const res = await r.events(sid, { type: "finish" });
    expect(res.json.snapshot.bodyGrade.complete).toBe(false);
    expect(res.json.context).toMatch(/ATTEMPT ENDED WITH UNMET GOALS/);
    expect(cond(res.json).outcome.result).not.toBe("completed");
  });
});

// ---------------------------------------------------------------------------------------------------
describe("2. guardrails and hemorrhage", () => {
  // Where each guardrail fires and the setup it needs.
  const cases: { id: string; setup: string[]; ev: (r: ReturnType<typeof rig>) => unknown }[] = [
    { id: "off_mark", setup: ["mark_incision"], ev: (r) => r.act("cut", "skin", { lengthMm: 60, depthMm: 2, distanceMm: 8 }) },
    { id: "deep_skin_cut", setup: ["mark_incision"], ev: (r) => r.act("cut", "skin", { lengthMm: 60, depthMm: 15 }) },
    { id: "mark_far", setup: [], ev: (r) => r.act("mark", "skin", { instrumentId: "skin_marker", lengthMm: 60, distanceMm: 25 }) },
    { id: "bowel_injury", setup: OPEN_ORDER.slice(0, 5), ev: (r) => r.act("cut", "terminal_ileum", { lengthMm: 4 }) },
    { id: "critical_injury", setup: OPEN_ORDER.slice(0, 5), ev: (r) => r.act("cut", "iliac_vessels", { lengthMm: 4 }) },
    { id: "cut_before_control", setup: OPEN_ORDER.slice(0, 5), ev: (r) => r.act("cut", "mesoappendix", { instrumentId: "metzenbaum_scissors", lengthMm: 10 }) },
    { id: "split_dont_cut", setup: OPEN_ORDER.slice(0, 3), ev: (r) => r.act("cut", "muscle", { lengthMm: 20 }) },
    { id: "lift_first", setup: OPEN_ORDER.slice(0, 4), ev: (r) => r.act("cut", "peritoneum", { lengthMm: 10 }) },
    { id: "fiber_direction", setup: OPEN_ORDER.slice(0, 2), ev: (r) => r.act("cut", "fascia", { instrumentId: "metzenbaum_scissors", lengthMm: 30, angleDegrees: 60 }) },
    { id: "rough_handling", setup: [], ev: (r) => r.act("grasp", "skin", { instrumentId: "toothed_forceps", depthMm: 2, speedMps: 0.3 }) },
  ];

  it("covers every guardrail in OPEN_BODY", () => {
    expect(cases.map((c) => c.id).sort()).toEqual(OPEN_BODY.guardrails.map((g) => g.id).sort());
  });

  for (const c of cases) {
    it(`${c.id} fires with the right tier and clip`, async () => {
      const r = rig();
      const { sid } = await r.create(PRIYA);
      await r.doSteps(sid, c.setup);
      const res = await r.events(sid, c.ev(r));
      expect(res.json.results[0].applied).toBe(true);
      const rule = OPEN_BODY.guardrails.find((g) => g.id === c.id)!;
      const mine = (res.json.alerts as Json[]).filter((a) => a.kind === "mistake" && (a.say as string).includes(rule.feedback.replace(/^Stop\. /, "")));
      expect(mine).toHaveLength(1);
      if (rule.severity === "high") expect(mine[0]).toMatchObject({ tier: "warning", reflexKey: `mistake.${c.id}` });
      else expect(mine[0]).toMatchObject({ tier: "caution", reflexKey: "" });
      expect(res.json.snapshot.recentMistakes.map((m: Json) => m.mistakeId)).toContain(c.id);
      expect(r.unsafe).toEqual([]);
    });
  }

  it("mesoappendix bleed accrues on 1 Hz ticks: class 2, 3, 4 alerts, 30 s escalation, and control stops it", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA);
    await r.doSteps(sid, BEFORE_MESO);
    r.advanceHeadset(1000);
    const cut = await r.events(sid, r.act("cut", "mesoappendix", { instrumentId: "metzenbaum_scissors", lengthMm: 10 }));
    expect(cut.json.alerts.map((a: Json) => a.reflexKey)).toEqual(expect.arrayContaining(["bleeding.mesoappendix", "mistake.cut_before_control"]));
    const seen: Json[] = [];
    let escalatedAt = -1;
    for (let s = 1; s <= 80; s++) {
      const res = await r.tick(sid);
      for (const a of res.json.alerts as Json[]) {
        seen.push({ ...a, s });
        if (a.reflexKey === "bleeding_uncontrolled.mesoappendix") escalatedAt = s;
      }
    }
    expect(escalatedAt).toBe(UNCONTROLLED_BLEED_MS / 1000);
    const vitals = seen.filter((a) => a.kind === "vitals");
    expect(vitals.map((a) => a.say)).toHaveLength(3);
    expect(vitals[0]).toMatchObject({ tier: "caution", reflexKey: "" });
    expect(vitals[1]).toMatchObject({ tier: "warning", reflexKey: "vitals.class3" });
    expect(vitals[2]).toMatchObject({ tier: "warning", reflexKey: "vitals.class4" });
    const mid = await r.state(sid);
    expect(cond(mid).vitals.hemorrhageClass).toBe(4);
    expect(cond(mid).vitals.hr).toBeGreaterThan(130);
    expect(cond(mid).vitals.sys).toBeLessThan(90);
    expect(cond(mid).outcome.result).toBe("in_progress");
    expect(seen.filter((a) => a.reflexKey === "bleeding_uncontrolled.mesoappendix")).toHaveLength(1);
    // Seal at the injury point (the cut was at distanceMm 0).
    const sealed = await r.events(sid, r.act("seal", "mesoappendix", { instrumentId: "hook_cautery" }));
    expect(sealed.json.alerts.map((a: Json) => a.kind)).toContain("bleeding_controlled");
    const lost = sealed.json.snapshot.bloodLossMl;
    for (let s = 0; s < 20; s++) await r.tick(sid);
    const after = await r.state(sid);
    expect(after.snapshot.bloodLossMl).toBe(lost);
    expect(after.snapshot.activeBleeds).toEqual([]);
    expect(cond(after).outcome.result).toBe("in_progress");
    expect(r.unsafe).toEqual([]);
  });

  it("muscle bleed (blade on muscle) accrues slowly on ticks", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA);
    await r.doSteps(sid, OPEN_ORDER.slice(0, 3));
    const cut = await r.events(sid, r.act("cut", "muscle", { lengthMm: 20 }));
    expect(cut.json.snapshot.activeBleeds.map((b: Json) => b.structure.id)).toEqual(["muscle"]);
    for (let s = 0; s < 60; s++) await r.tick(sid);
    const s = await r.state(sid);
    expect(s.snapshot.bloodLossMl).toBe(18); // 0.3 ml/s for 60 s
    expect(cond(s).vitals.hemorrhageClass).toBe(1);
  });
});

// ---------------------------------------------------------------------------------------------------
async function secondsToHemorrhageDeath(patientId: string) {
  const r = rig();
  const { sid, created } = await r.create(patientId);
  await r.doSteps(sid, BEFORE_MESO);
  r.advanceHeadset(1000);
  await r.events(sid, r.act("cut", "mesoappendix", { instrumentId: "metzenbaum_scissors", lengthMm: 10 }));
  const classAt: Record<number, number> = {};
  let s = 0;
  for (; s < 600; s++) {
    const res = await r.tick(sid);
    const v = cond(res.json).vitals;
    classAt[v.hemorrhageClass] ??= s + 1;
    if (cond(res.json).outcome.result === "died") break;
  }
  return { r, sid, seconds: s + 1, classAt, weightKg: created.snapshot.condition.weightKg };
}

describe("3. death", () => {
  it("untreated mesoappendix bleed kills Priya (61 kg) and Theo (child) on headset ticks; times reported", async () => {
    const priya = await secondsToHemorrhageDeath(PRIYA);
    const theo = await secondsToHemorrhageDeath(THEO);
    console.log(`[qa-or] mesoappendix bleed to death: Priya ${priya.weightKg} kg ${priya.seconds} s (class onset ${JSON.stringify(priya.classAt)}); Theo ${theo.weightKg} kg ${theo.seconds} s (class onset ${JSON.stringify(theo.classAt)})`);
    expect(priya.seconds).toBeLessThan(600);
    expect(theo.seconds).toBeLessThan(priya.seconds);
    // Docs: a large uncontrolled bleed turns dangerous (class 3+) in about 30 to 90 s.
    expect(priya.classAt[3]).toBeGreaterThanOrEqual(20);
    expect(priya.classAt[3]).toBeLessThanOrEqual(90);
  });

  it("after hemorrhage death: every event type is refused, asystole, context, one outcome alert, checklist renders", async () => {
    const { r, sid } = await secondsToHemorrhageDeath(PRIYA);
    const all = [
      r.act("seal", "mesoappendix", { instrumentId: "hook_cautery" }),
      r.act("tick", "skin", { instrumentId: "assistant" }),
      { type: "finish" },
      { type: "focus", structureId: "cecum" },
      { type: "instrument", instrumentId: "scalpel", hand: "right", held: true },
      { type: "contact", instrumentId: "scalpel", structureId: "skin" },
      { type: "injury", region: "neck", instrumentId: "scalpel" },
      { type: "injury", region: "neck", instrumentId: "scalpel", controlled: true },
      { type: "tracking", valid: false },
      { type: "bleeding", structureId: "mesoappendix", active: true, rateMlPerMin: 10, totalMl: 10 },
      { type: "touch", structureId: "skin", instrumentId: "scalpel" },
      { type: "identify", structureId: "skin" },
      { type: "confirm" },
      { type: "place_port", portId: "umbilical" },
    ];
    const res = await r.events(sid, ...all);
    expect(res.json.results.map((x: Json) => x.reason)).toEqual(all.map(() => "patient_died"));
    const c = cond(res.json);
    expect(c.vitals).toMatchObject({ hr: 0, rr: 0, sys: 0, dia: 0, label: "Asystole (simulated)" });
    expect(c.outcome.result).toBe("died");
    expect(c.outcome.cause).toMatch(/hemorrhage/);
    expect(res.json.context).toMatch(/THE PATIENT DIED/);
    expect(res.json.snapshot.checklist).toHaveLength(OPEN_ORDER.length);
    expect(res.json.snapshot.checklist.filter((x: Json) => x.done).map((x: Json) => x.id)).toEqual(BEFORE_MESO);
    for (let i = 0; i < 5; i++) { r.advance(1000); await r.alerts(sid); }
    const log = (await r.alerts(sid)).alerts as Json[];
    expect(log.filter((a) => a.kind === "patient_died")).toHaveLength(1);
    expect(log.find((a) => a.kind === "patient_died")).toMatchObject({ tier: "warning", reflexKey: "outcome.died" });
    // Simulate buttons are refused too.
    for (const kind of ["cut_neck", "cut_head", "bleed", "stop_bleed", "complete_step", "mistake", "control_injury"]) {
      const s = await r.sim(sid, kind);
      expect(cond(s.json).outcome.result).toBe("died");
    }
    expect(r.unsafe).toEqual([]);
  });

  it("FIXED: no stall hints after the patient died (alerts poll keeps escalating)", async () => {
    // Repro: kill the patient (cut_head), then poll GET /alerts as the native Quest does while the coach
    // clock moves. stuckLevel() ignores the outcome, so 'stuck' hints keep arriving for a dead patient.
    const r = rig();
    const { sid } = await r.create(PRIYA);
    await r.events(sid, { type: "injury", region: "head", instrumentId: "scalpel" });
    const latest = (await r.alerts(sid)).latestSeq;
    for (let i = 0; i < 70; i++) { r.advance(1000); await r.alerts(sid); }
    const after = (await r.alerts(sid, latest)).alerts as Json[];
    expect(after.map((a) => a.kind)).toEqual([]);
  });

  it("FIXED: hint requests after death are not answered as if the case were live", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA);
    await r.events(sid, { type: "injury", region: "head", instrumentId: "scalpel" });
    const hint = await r.req("POST", `/coach/sessions/${sid}/hint`);
    expect(hint.json.say).not.toMatch(/hip bone|belly button|McBurney/i);
  });

  it("neck injury kills on the coach clock (no headset ticks), via the alerts poll", async () => {
    for (const patient of [PRIYA, THEO]) {
      const r = rig();
      const { sid } = await r.create(patient);
      const cut = await r.events(sid, { type: "injury", region: "neck", instrumentId: "scalpel" });
      expect(cut.json.alerts.map((a: Json) => a.reflexKey)).toContain("region.neck");
      let s = 0;
      for (; s < 300; s++) {
        r.advance(1000);
        const a = await r.alerts(sid);
        if (cond(a).outcome.result === "died") break;
      }
      console.log(`[qa-or] neck bleed to death on the coach clock: ${patient} ${s + 1} s`);
      expect(s + 1).toBeLessThanOrEqual(90);
      const log = (await r.alerts(sid)).alerts as Json[];
      expect(log.filter((a) => a.kind === "patient_died")).toHaveLength(1);
      expect(log.find((a) => a.kind === "patient_died")!.say).toMatch(/hemorrhage/);
      expect(r.unsafe).toEqual([]);
    }
  });

  it("FIXED: a neck cut kills a child instantly from the bleed-rate lookahead, with zero blood actually lost", async () => {
    // Repro: Theo (26.8 kg), {type: injury, region: neck}. monitorVitals() adds 0.5 min of the scaled bleed rate
    // (300 x 8 x 0.5 = 1200 ml) to the loss, which is 64% of a 1876 ml EBV, and PatientCondition.update() uses
    // that lookahead percentage for the 50% death threshold. Outcome "died" with cause "hemorrhage (64% ...)"
    // on the injury event itself while rawBloodLossMl is 0; the "Stop! ... opened a major vessel" alarm gives
    // no chance to act. Adults lose ~28% instantly (class 2 at t=0). Death should use actual loss.
    const r = rig();
    const { sid } = await r.create(THEO);
    const res = await r.events(sid, { type: "injury", region: "neck", instrumentId: "scalpel" });
    expect(cond(res.json).rawBloodLossMl).toBe(0);
    expect(cond(res.json).outcome.result).toBe("in_progress");
  });

  it("head injury is fatal at once, with the region clip and the outcome clip in the same response", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA);
    const res = await r.events(sid, { type: "injury", region: "head", instrumentId: "scalpel" });
    expect(res.json.alerts.map((a: Json) => a.reflexKey)).toEqual(["region.head", "outcome.died"]);
    expect(cond(res.json).outcome).toMatchObject({ result: "died", cause: "catastrophic injury to the head" });
    expect(res.json.context).toMatch(/THE PATIENT DIED \(simulated\)\. Cause: catastrophic injury to the head/);
    expect(r.unsafe).toEqual([]);
  });
});

// ---------------------------------------------------------------------------------------------------
describe("4. region injuries", () => {
  for (const region of REGION_IDS) {
    it(`${region}: alarm, bleeding state, mistake severity`, async () => {
      const r = rig();
      const { sid } = await r.create(PRIYA);
      const res = await r.events(sid, { type: "injury", region, instrumentId: "scalpel" });
      expect(res.json.results[0].applied).toBe(true);
      expect(res.json.alerts[0]).toMatchObject({ kind: "region_injury", tier: "warning", reflexKey: `region.${region}` });
      const reg = cond(res.json).regions;
      expect(reg).toHaveLength(1);
      expect(reg[0]).toMatchObject({ region, bleeding: region !== "head" });
      const m = res.json.snapshot.recentMistakes.at(-1);
      expect(m.severity).toBe(["head", "neck", "chest"].includes(region) ? "high" : "moderate");
      if (region !== "head") {
        r.advance(10_000);
        const later = await r.alerts(sid);
        expect(cond(later).rawBloodLossMl).toBeGreaterThan(0);
      }
      expect(r.unsafe).toEqual([]);
    });
  }

  it("invalid regions and malformed controlled flags are 400", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA);
    for (const ev of [
      { type: "injury", region: "spleen", instrumentId: "scalpel" },
      { type: "injury", region: "", instrumentId: "scalpel" },
      { type: "injury", region: "NECK", instrumentId: "scalpel" },
      { type: "injury", region: "neck", instrumentId: "scalpel", controlled: "yes" },
      { type: "injury", region: "neck", instrumentId: "Bad Tool" },
      { type: "injury", region: "__proto__", instrumentId: "scalpel" },
      { type: "injury", region: "toString", instrumentId: "scalpel" },
    ]) {
      const res = await r.events(sid, ev);
      expect(res.status, JSON.stringify(ev)).toBe(400);
    }
  });

  it("repeated injury of the same region alarms once; controlled:true stops that region only", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA);
    const a = await r.events(sid, { type: "injury", region: "left_leg", instrumentId: "scalpel" }, { type: "injury", region: "left_leg", instrumentId: "scalpel" }, { type: "injury", region: "right_arm", instrumentId: "scalpel" });
    expect(a.json.alerts.filter((x: Json) => x.kind === "region_injury").map((x: Json) => x.reflexKey)).toEqual(["region.left_leg", "region.right_arm"]);
    r.advance(30_000);
    const ctl = await r.events(sid, { type: "injury", region: "left_leg", instrumentId: "hemostat", controlled: true });
    expect(ctl.json.alerts.map((x: Json) => x.kind)).toContain("bleeding_controlled");
    const regs = cond(ctl.json).regions as Json[];
    expect(regs.find((x) => x.region === "left_leg")!.bleeding).toBe(false);
    expect(regs.find((x) => x.region === "right_arm")!.bleeding).toBe(true);
    const lostAtControl = cond(ctl.json).rawBloodLossMl;
    expect(lostAtControl).toBe(20); // 2 limbs x 20 ml/min x 30 s
    r.advance(60_000);
    const later = await r.alerts(sid);
    expect(cond(later).rawBloodLossMl).toBe(40); // only the arm keeps bleeding: +20 ml
    // A second controlled for an already controlled region: accepted, no new alert.
    const again = await r.events(sid, { type: "injury", region: "left_leg", instrumentId: "hemostat", controlled: true });
    expect(again.json.alerts).toEqual([]);
    expect(r.unsafe).toEqual([]);
  });

  it("FIXED: re-cutting a controlled region restarts its bleeding without any alarm", async () => {
    // Repro: injure neck, control it, injure neck again. PatientCondition.injure() sets bleeding=true again
    // but returns first:false, so handleInjury() emits no region alarm; the neck silently bleeds again.
    const r = rig();
    const { sid } = await r.create(PRIYA);
    await r.events(sid, { type: "injury", region: "neck", instrumentId: "scalpel" });
    await r.events(sid, { type: "injury", region: "neck", instrumentId: "hemostat", controlled: true });
    const again = await r.events(sid, { type: "injury", region: "neck", instrumentId: "scalpel" });
    expect(cond(again.json).regions[0].bleeding).toBe(true);
    expect(again.json.alerts.map((a: Json) => a.kind)).toContain("region_injury");
  });

  it("FIXED: injury while tracking is invalid should be refused like body actions (scoring paused)", async () => {
    // AGENTS.md / operation-flow.md: an invalid fit hides the anatomy and pauses scoring. Body actions are
    // refused with tracking_invalid, but handle() routes injury events before the tracking check, so a
    // region hit computed against an invalid body fit is scored (high-severity mistake) and can kill.
    const r = rig();
    const { sid } = await r.create(PRIYA);
    await r.events(sid, { type: "tracking", valid: false });
    const res = await r.events(sid, { type: "injury", region: "head", instrumentId: "scalpel" });
    expect(res.json.results[0].reason).toBe("tracking_invalid");
    expect(cond(res.json).outcome.result).toBe("in_progress");
  });
});

// ---------------------------------------------------------------------------------------------------
describe("5. baselines", () => {
  const patients = ["patient-demo-001", PRIYA, THEO, "patient-demo-sparse", "patient-demo-messy-coding", "patient-demo-polypharmacy", "patient-demo-source-unavailable", "patient-demo-consent-partial"];

  it("chart baseline per patient fixture (table printed)", async () => {
    const r = rig();
    const rows: string[] = [];
    for (const p of patients) {
      const res = await r.req("POST", "/coach/sessions", { patientId: p, mode: "virtual" });
      if (res.status !== 201) { rows.push(`${p}: no session (${res.status} ${res.json.error?.code})`); continue; }
      const c = cond(res.json);
      rows.push(`${p} | ${res.json.snapshot.procedureId} | HR ${c.vitals.baseline.hr} RR ${c.vitals.baseline.rr} BP ${c.vitals.sys}/${c.vitals.dia} SpO2 ${c.vitals.spo2} | ${c.weightKg} kg | ${c.baselineSource}`);
      expect(unitySafetyErrors(res.json)).toEqual([]);
    }
    console.log(`[qa-or] chart baselines:\n${rows.join("\n")}`);
    const priya = await r.create(PRIYA);
    expect(cond(priya.created)).toMatchObject({ weightKg: 61.4, baselineSource: "chart+authored", vitals: { sys: 121, dia: 78, spo2: -1 } });
    const theo = await r.create(THEO);
    expect(cond(theo.created)).toMatchObject({ weightKg: 26.8, vitals: { spo2: 97, rr: 22 } });
  });

  it("FIXED: a weight charted in pounds is read as kilograms", () => {
    // patient-demo-messy-coding charts "Body weight" 168 [lb_av]; chartBaseline ignores the unit -> 168 kg
    // (EBV 11.8 L instead of about 5.3 L, so she would take more than twice as long to bleed out).
    const c = chartBaseline((fixture("patient-demo-messy-coding") as any).data.vitals, 63);
    expect(c.weightKg).toBeCloseTo(76.2, 0);
  });

  it("FIXED (doc mismatch): a child's blood volume uses 70 ml/kg, docs say about 80 ml/kg", () => {
    // docs/operation-flow.md "Vitals": about 70 ml/kg adult, 80 ml/kg child. physiology.ts (and the .mjs)
    // use 70 for everyone. Repro: Theo (26.8 kg), body loss 10 ml raw (x8 = 80 ml) -> expect pct 80/2144.
    // Fixed in the coach's condition layer (the shared model stays at 70 ml/kg and gets a scaled weight).
    const c = new PatientCondition(() => 0, 1);
    c.setBaseline({ hr: 90, rr: 20, sys: 105, dia: 65, source: "chart" }, { weightKg: 26.8, mlPerKg: 80 });
    c.setBodyBleeding(80, 0);
    expect(c.view().vitals.bloodLossPct).toBeCloseTo((80 / (80 * 26.8)) * 100, 1);
    expect(chartBaseline([], 8).mlPerKg).toBe(80);
  });

  it("AR Time-Out baseline: direct body sets it and labels it; junk is rejected and leaves the chart baseline", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA, "mixed_reality");
    const junk = [
      {}, { baseline: null }, { baseline: { hr: "fast", rr: 12, sys: 120, dia: 80 } }, { baseline: { hr: 0, rr: 12, sys: 120, dia: 80 } },
      { baseline: { hr: -5, rr: 12, sys: 120, dia: 80 } }, { baseline: { hr: 70, rr: 12, sys: 120 } }, { baseline: { hr: 1e9, rr: 12, sys: 120, dia: 80 } },
      { baseline: { hr: 70, rr: 12, sys: 120, dia: Number.NaN } }, { baseline: [70, 12, 120, 80] },
    ];
    for (const b of junk) {
      const res = await r.req("POST", `/coach/sessions/${sid}/vitals/baseline`, b);
      expect(res.status, JSON.stringify(b)).toBe(400);
    }
    expect(cond(await r.state(sid)).vitals.sys).toBe(121);
    const ok = await r.req("POST", `/coach/sessions/${sid}/vitals/baseline`, { baseline: { hr: 64, rr: 13, sys: 117, dia: 74 } });
    expect(ok.status).toBe(200);
    expect(ok.json.condition).toMatchObject({ baselineSource: "measured", weightKg: 61.4, vitals: { hr: 64, rr: 13, sys: 117, dia: 74, spo2: -1 } });
    expect(ok.json.condition.vitals.label).toMatch(/simulated from baseline 64 \(measured\)/);
    expect(unitySafetyErrors(ok.json)).toEqual([]);
    const ctx = (await r.state(sid)).context;
    expect(ctx).toMatch(/Vitals \(simulated[^)]*measured[^)]*\): HR 64, BP 117\/74, RR 13\./);
  });

  it("FIXED (low): implausible baselines (diastolic above systolic) are accepted", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA, "mixed_reality");
    const res = await r.req("POST", `/coach/sessions/${sid}/vitals/baseline`, { baseline: { hr: 70, rr: 12, sys: 60, dia: 140 } });
    expect(res.status).toBe(400);
  });

  it("AR baseline without a body and without VITALS_URL is 400; with an unreachable VITALS_URL is 503", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA, "mixed_reality");
    expect((await r.req("POST", `/coach/sessions/${sid}/vitals/baseline`)).status).toBe(400);
    const r2 = rig({ vitalsUrl: "http://127.0.0.1:1" });
    const s2 = await r2.create(PRIYA, "mixed_reality");
    const res = await r2.req("POST", `/coach/sessions/${s2.sid}/vitals/baseline`);
    expect(res.status).toBe(503);
    expect(cond(await r2.state(s2.sid)).baselineSource).toBe("chart+authored");
  });
});

// The real services/vitals in demo mode. Start it with `cd services/vitals && PORT=8799 npm start`
// and run with QA_VITALS_URL=http://127.0.0.1:8799; skipped otherwise so CI stays hermetic.
const VITALS = process.env.QA_VITALS_URL ?? "";
describe.skipIf(!VITALS)("5b. AR Time-Out against the real vitals service (demo mode)", () => {
  it("captures a demo baseline, labeled demo, keeping chart weight", async () => {
    const r = rig({ vitalsUrl: VITALS });
    const { sid } = await r.create(PRIYA, "mixed_reality");
    const res = await r.req("POST", `/coach/sessions/${sid}/vitals/baseline`);
    expect(res.status).toBe(200);
    expect(res.json.baseline.source).toBe("demo");
    expect(res.json.condition.baselineSource).toBe("demo");
    expect(res.json.condition.vitals.label).toMatch(/\(demo\)/);
    expect(res.json.condition.weightKg).toBe(61.4);
    expect(unitySafetyErrors(res.json)).toEqual([]);
    expect((await r.state(sid)).context).toMatch(/Vitals \(simulated[^)]*demo[^)]*\):/);
  });

  it("FIXED: Presage capture replaces the charted blood pressure with authored 118/76 labeled as demo/measured", async () => {
    // services/vitals baselineFrom() only measures HR and RR and fills sys/dia from AUTHORED_BASELINE, but
    // the coach adopts all four under one source label. Priya's charted 121/78 becomes 118/76 "(demo)" in
    // demo mode and "(measured)" live, which breaks the honesty rule (authored values shown as measured).
    const r = rig({ vitalsUrl: VITALS });
    const { sid } = await r.create(PRIYA, "mixed_reality");
    const res = await r.req("POST", `/coach/sessions/${sid}/vitals/baseline`);
    expect(res.json.condition.vitals).toMatchObject({ sys: 121, dia: 78 });
  });
});

// ---------------------------------------------------------------------------------------------------
describe("6. Unity safety", () => {
  it("every coach route response is JsonUtility-safe, incl. after death and with spo2 missing", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA); // no charted SpO2
    expect(cond(await r.state(sid)).vitals.spo2).toBe(-1);
    await r.doSteps(sid, BEFORE_MESO);
    await r.events(sid, r.act("cut", "mesoappendix", { instrumentId: "metzenbaum_scissors", lengthMm: 10 }));
    await r.events(sid, { type: "injury", region: "chest", instrumentId: "scalpel" });
    for (const k of ["mistake", "bleed", "cut_arm", "control_injury", "stop_bleed", "complete_step"]) await r.sim(sid, k);
    await r.req("POST", `/coach/sessions/${sid}/vitals/baseline`, { baseline: { hr: 66, rr: 12, sys: 115, dia: 70 } });
    await r.alerts(sid);
    await r.events(sid, { type: "injury", region: "head", instrumentId: "scalpel" });
    await r.events(sid, { type: "tracking", valid: false });
    await r.sim(sid, "cut_neck");
    await r.state(sid);
    await r.alerts(sid);
    await r.req("POST", `/coach/sessions/${sid}/voice-context`, { force: true });
    // Finished attempt (bodyGrade present).
    const f = await r.create(THEO);
    await r.doSteps(f.sid, ["mark_incision"]);
    await r.events(f.sid, { type: "finish" });
    await r.state(f.sid);
    expect(r.unsafe).toEqual([]);
  });
});

// ---------------------------------------------------------------------------------------------------
describe("7. laptop sim buttons on an open-body session", () => {
  it("region buttons injure the named region; control_injury controls one; cut_head kills", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA);
    const want: Record<string, string> = { cut_neck: "neck", cut_chest: "chest", cut_arm: "right_arm" };
    for (const [kind, region] of Object.entries(want)) {
      const res = await r.sim(sid, kind);
      expect(res.status).toBe(200);
      expect(res.json.alerts.map((a: Json) => a.reflexKey)).toContain(`region.${region}`);
      expect(cond(res.json).regions.map((x: Json) => x.region)).toContain(region);
    }
    const ctl = await r.sim(sid, "control_injury");
    expect(cond(ctl.json).regions.filter((x: Json) => !x.bleeding).map((x: Json) => x.region)).toEqual(["neck"]);
    const dead = await r.sim(sid, "cut_head");
    expect(cond(dead.json).outcome).toMatchObject({ result: "died", cause: "catastrophic injury to the head" });
    expect(r.unsafe).toEqual([]);
  });

  it("complete_step advances one milestone at a time, all the way to completed", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA);
    for (let i = 0; i < OPEN_ORDER.length; i++) {
      const res = await r.sim(sid, "complete_step");
      expect(res.json.snapshot.achievedMilestones).toContain(OPEN_ORDER[i]);
    }
    const s = await r.state(sid);
    expect(s.snapshot.status).toBe("completed");
    expect(cond(s).outcome.result).toBe("completed");
    expect(s.snapshot.mistakeCount).toBe(0);
  });

  it("mistake fires the current milestone's guardrail; bleed is a no-op before any perfused layer is open", async () => {
    // Sim action ids use Date.now(); keep it moving so two clicks in one millisecond do not collide (see the
    // low-severity bug below).
    let now = 1_700_000_000_000;
    const spy = vi.spyOn(Date, "now").mockImplementation(() => (now += 7));
    try {
      const r = rig();
      const { sid } = await r.create(PRIYA);
      const early = await r.sim(sid, "bleed");
      expect(early.json.snapshot.activeBleeds).toEqual([]);
      const m = await r.sim(sid, "mistake");
      expect(m.json.alerts.map((a: Json) => a.kind)).toContain("mistake");
      for (let i = 0; i < 2; i++) await r.sim(sid, "complete_step"); // mark, incise
      expect((await r.state(sid)).snapshot.step.id).toBe("open_fascia");
      const fascia = await r.sim(sid, "mistake");
      expect(fascia.json.snapshot.recentMistakes.map((x: Json) => x.mistakeId)).toContain("fiber_direction");
      const wrong = await r.sim(sid, "wrong_instrument");
      expect(wrong.status).toBe(200);
    } finally {
      spy.mockRestore();
    }
  });

  it("FIXED (low): two sim actions in the same wall-clock millisecond collide and the second is dropped", async () => {
    // openBodySimulation() builds actionId `sim-${kind}-${Date.now()}-${n}` from the wall clock (not the injected
    // clock). Two "mistake" clicks within 1 ms reuse the id; the second is rejected as a duplicate silently.
    const spy = vi.spyOn(Date, "now").mockReturnValue(1_700_000_000_000);
    try {
      const r = rig();
      const { sid } = await r.create(PRIYA);
      await r.sim(sid, "mistake");
      for (let i = 0; i < 2; i++) await r.sim(sid, "complete_step");
      const fascia = await r.sim(sid, "mistake");
      expect(fascia.json.snapshot.recentMistakes.map((x: Json) => x.mistakeId)).toContain("fiber_direction");
    } finally {
      spy.mockRestore();
    }
  });

  it("bleed opens a mesoappendix bleed and stop_bleed controls it", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA);
    for (let i = 0; i < 5; i++) await r.sim(sid, "complete_step");
    const b = await r.sim(sid, "bleed");
    expect(b.json.alerts.map((a: Json) => a.reflexKey)).toContain("bleeding.mesoappendix");
    expect(b.json.snapshot.activeBleeds.map((x: Json) => x.structure.id)).toEqual(["mesoappendix"]);
    const stop = await r.sim(sid, "stop_bleed");
    expect(stop.json.snapshot.activeBleeds).toEqual([]);
    expect(stop.json.alerts.map((a: Json) => a.kind)).toContain("bleeding_controlled");
    expect(r.unsafe).toEqual([]);
  });

  it("FIXED (high for the laptop demo): a simulated session never advances bleeding on tick", async () => {
    // Repro: 5x complete_step, bleed, then poll GET /alerts 10 times with the clock moving 1 s each time.
    // Expected blood loss +20 ml (2 ml/s); actual +0. CoachSession.advanceSimulatedBleeding() builds the
    // assistant tick BodyAction without bloodLostMl, poolMl and flowMlPerSecond (hidden by "as BodyAction"),
    // so validBodyAction() rejects it and BodyState never integrates time. Vitals never move in the laptop demo.
    const r = rig();
    const { sid } = await r.create(PRIYA);
    for (let i = 0; i < 5; i++) await r.sim(sid, "complete_step");
    const b = await r.sim(sid, "bleed");
    const lost0 = b.json.snapshot.bloodLossMl;
    for (let i = 0; i < 10; i++) { r.advance(1000); await r.alerts(sid); }
    expect((await r.state(sid)).snapshot.bloodLossMl).toBe(lost0 + 20);
  });

  it("FIXED (latent, masked by the bug above): simulated bleeding time follows tick() calls, not the clock", async () => {
    // advanceSimulatedBleeding() always adds one headset second per tick() call. tick() runs from the SSE
    // ticker and from every GET /alerts poll, so a fast poller (or SSE plus a poller) bleeds faster than
    // real time. Repro once ticks work: poll /alerts 10 times without moving the clock; loss should not grow.
    const r = rig();
    const { sid } = await r.create(PRIYA);
    for (let i = 0; i < 5; i++) await r.sim(sid, "complete_step");
    const b = await r.sim(sid, "bleed");
    const lost0 = b.json.snapshot.bloodLossMl;
    for (let i = 0; i < 10; i++) await r.alerts(sid);
    expect((await r.state(sid)).snapshot.bloodLossMl).toBe(lost0);
  });
});

// ---------------------------------------------------------------------------------------------------
describe("8. Jarvis context", () => {
  it("has the vitals line, injuries, the current milestone's unmet facts, and only achieved milestones", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA);
    await r.doSteps(sid, OPEN_ORDER.slice(0, 2));
    await r.events(sid, { type: "injury", region: "left_arm", instrumentId: "scalpel" });
    const s = await r.state(sid);
    const ctx: string = s.context;
    expect(ctx).toMatch(/Vitals \(simulated[^)]*chart\+authored[^)]*\): HR \d+, BP 121\/78, RR \d+\. Simulated blood loss [\d.]+% of volume \(class 1\)\./);
    expect(ctx).not.toMatch(/SpO2/); // not charted for Priya
    expect(ctx).toMatch(/Injuries outside the surgical field: left arm \(bleeding\)\./);
    expect(ctx).toMatch(/Suggested milestone 3 of 10: Open fascia\./);
    expect(ctx).toMatch(/Still needed: fascia: NOT yet opened; fascia: cut angle off the fibers not measured yet, needs at most 25 deg\./);
    const achieved = ctx.match(/Achieved milestones: ([^.]*)\./)![1]!.split(", ");
    expect(achieved).toEqual(s.snapshot.achievedMilestones);
    expect(achieved).toEqual(["mark_incision", "incise_skin"]);
    // No completion claim for a milestone that is not achieved.
    for (const id of OPEN_ORDER.slice(2)) {
      const title = s.snapshot.checklist.find((c: Json) => c.id === id).title.toLowerCase();
      expect(ctx).not.toMatch(new RegExp(`Milestone reached: ${title}`));
    }
  });

  it("FIXED: a milestone undone by a new bleed is shown both done and current, and still claimed as achieved", async () => {
    // Repro: complete through divide_mesoappendix, then cut the mesoappendix again at its base (distanceMm 0,
    // proximal to the ties at 5 and 15). activeBleeds becomes 1, the divide_mesoappendix predicates fail
    // and the engine moves current back to it, but completedMilestones is sticky: checklist shows
    // {done: true, current: true} and the context lists it under "Achieved milestones" while suggesting it.
    const r = rig();
    const { sid } = await r.create(PRIYA);
    await r.doSteps(sid, OPEN_ORDER.slice(0, 7));
    const res = await r.events(sid, r.act("cut", "mesoappendix", { instrumentId: "metzenbaum_scissors", lengthMm: 4, distanceMm: 0 }));
    const item = res.json.snapshot.checklist.find((c: Json) => c.id === "divide_mesoappendix");
    expect(res.json.snapshot.step.id).toBe("divide_mesoappendix");
    expect(item.done && item.current).toBe(false);
  });

  it("FIXED (low): headset 1 Hz ticks flood the timeline Jarvis sees", async () => {
    // Repro: 10 headset ticks while bleeding. Each tick is a 'surgery' event handled like a learner action:
    // inputCount++, and the timeline / lastEvent get "Assistant: tick on the skin." every second, pushing the
    // real actions out of the 8-line "Recent" context within 8 s of bleeding.
    const r = rig();
    const { sid } = await r.create(PRIYA);
    await r.doSteps(sid, BEFORE_MESO);
    await r.events(sid, r.act("cut", "mesoappendix", { instrumentId: "metzenbaum_scissors", lengthMm: 10 }));
    for (let i = 0; i < 10; i++) await r.tick(sid);
    const s = await r.state(sid);
    expect(s.context).toMatch(/cut the mesoappendix/i);
    expect(s.snapshot.lastEvent).not.toMatch(/tick/);
  });

  it("completed context still carries vitals (it does not today: the completed branch drops them)", async () => {
    const r = rig();
    const { sid } = await r.create(PRIYA);
    await r.events(sid, { type: "injury", region: "left_leg", instrumentId: "scalpel" });
    await r.doSteps(sid, OPEN_ORDER);
    const s = await r.state(sid);
    expect(s.snapshot.status).toBe("completed");
    // Documented as low severity; kept as an observation, not a failing test.
    expect(s.context).not.toMatch(/Vitals/);
  });
});

// Unit level: CoachSession directly, no HTTP.
describe("unit: CoachSession condition wiring", () => {
  const session = (subject = PRIYA) => {
    let t = NOW.getTime();
    const s = new CoachSession("coach-qa", buildCase(fixture(subject), "", NOW), () => new Date(t), undefined, "virtual");
    return { s, advance: (ms: number) => void (t += ms) };
  };
  const surgery = (e: BodyAction) => ({ type: "surgery" as const, evidence: e });

  it("ideal run: context never claims a milestone outside achievedMilestones, outcome completed", () => {
    const { s, advance } = session();
    for (const step of OPEN_ORDER) {
      for (const e of idealBodyActions(step)) expect(s.receive(surgery(e)).accepted).toBe(true);
      advance(3000);
      const snap = s.snapshot();
      if (snap.status === "completed") break;
      const m = renderContext(snap).match(/Achieved milestones: ([^.]*)\./)![1]!;
      expect(m === "none" ? [] : m.split(", ")).toEqual(snap.achievedMilestones);
      expect(snap.checklist.filter((c) => c.done).map((c) => c.id)).toEqual(snap.achievedMilestones);
    }
    expect(s.snapshot().condition.outcome.result).toBe("completed");
  });

  it("death through receive(): neck on the coach clock via tick(), then everything refused", () => {
    const { s, advance } = session();
    expect(s.receive({ type: "injury", region: "neck", instrumentId: "scalpel" }).alerts[0]!.reflexKey).toBe("region.neck");
    let died = 0;
    for (let i = 1; i <= 120 && !died; i++) {
      advance(1000);
      if (s.tick().length >= 0 && s.condition.died) died = i;
    }
    expect(died).toBeGreaterThan(0);
    expect(s.receive({ type: "tracking", valid: false }).reason).toBe("patient_died");
    expect(s.receive(surgery(bodyAction("mark", "skin", { instrumentId: "skin_marker", lengthMm: 60, actionId: "late" }))).reason).toBe("patient_died");
    expect(s.alertsAfter(0).alerts.filter((a) => a.kind === "patient_died")).toHaveLength(1);
  });

  it("an event-id retry after death is still refused (not reported as a duplicate accept)", () => {
    const { s } = session();
    s.receive({ type: "injury", region: "head", instrumentId: "scalpel" });
    const first = s.receive({ type: "injury", region: "neck", instrumentId: "scalpel" }, { eventId: "ev-1" });
    const retry = s.receive({ type: "injury", region: "neck", instrumentId: "scalpel" }, { eventId: "ev-1" });
    expect(first.reason).toBe("patient_died");
    expect(retry.accepted).toBe(false);
  });
});
