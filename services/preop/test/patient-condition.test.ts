import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { chartBaseline } from "../src/chart-vitals.js";
import { PatientCondition } from "../src/patient-condition.js";
import type { RealtimeBridge } from "../src/realtime-bridge.js";
import type { SimLogEntry } from "../src/realtime-bridge.js";
import { NOW, fixture, fixtureClient } from "./helpers.js";

describe("chart baseline", () => {
  it("uses the latest charted vitals and weight, and says what was filled in", () => {
    const vitals = (fixture("patient-demo-multi-source") as any).data.vitals;
    const c = chartBaseline(vitals, 40);
    expect(c.baseline).toMatchObject({ sys: 121, dia: 78 });
    expect(c.weightKg).toBe(61.4);
    expect(c.weightSource).toBe("chart");
    const empty = chartBaseline([], 8);
    expect(empty.baseline.source).toBe("authored");
    expect(empty).toMatchObject({ weightKg: 24, weightSource: "default", spo2: null });
  });
});

describe("patient condition", () => {
  it("turns blood loss into accelerated hemorrhage classes and death", () => {
    let t = 0;
    const c = new PatientCondition(() => t);
    c.setBaseline({ hr: 70, rr: 14, sys: 120, dia: 80, source: "chart" }, { weightKg: 60, spo2: 98 });
    expect(c.update()).toEqual([]);
    c.setBodyBleeding(150, 0); // x8 = 1200 ml of 4200: class 2
    expect(c.update()).toEqual([{ kind: "class", from: 1, to: 2 }]);
    expect(c.view().vitals.hr).toBeGreaterThanOrEqual(100);
    c.setBodyBleeding(300, 0); // 2400 of 4200 = 57%: dead
    const changes = c.update();
    expect(changes.map((x) => x.kind)).toEqual(["class", "died"]);
    expect(c.view().outcome.result).toBe("died");
    expect(c.view().vitals).toMatchObject({ hr: 0, sys: 0, label: "Asystole (simulated)" });
    expect(c.update()).toEqual([]); // nothing after death
  });

  it("bleeds a cut neck on the clock until controlled, and a head injury is fatal at once", () => {
    let t = 0;
    const c = new PatientCondition(() => t);
    c.injure("neck");
    c.update();
    t += 20_000;
    expect(c.update().some((x) => x.kind === "class")).toBe(true);
    expect(c.view().rawBloodLossMl).toBe(100); // 300 ml/min for 20 s
    c.control("neck");
    t += 60_000;
    c.update();
    expect(c.view().rawBloodLossMl).toBe(100);
    const h = new PatientCondition(() => 0);
    h.injure("head");
    expect(h.update()).toContainEqual({ kind: "died", cause: "catastrophic injury to the head" });
  });
});

describe("operating room condition over HTTP", () => {
  function rig() {
    let t = NOW.getTime();
    const logs: SimLogEntry[] = [];
    const realtime = { bound: true, coachMessage() {}, coachStatus() {}, attachEncounter() {}, encounterPhase() {}, encounterResult() {}, highlight: async () => null, simLog: (e: SimLogEntry) => logs.push(e) } as unknown as RealtimeBridge;
    const app = createApp({ client: fixtureClient(), now: () => new Date(t), coachTickMs: 0, realtime });
    const req = async (method: string, route: string, body?: unknown) => {
      const res = await app.request(route, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
      return { status: res.status, json: (await res.json()) as Record<string, any> };
    };
    return { req, logs, advance: (ms: number) => void (t += ms) };
  }

  it("starts from the chart baseline, shows the checklist, kills on a cut neck, then refuses events and logs it all", async () => {
    const { req, logs, advance } = rig();
    const created = (await req("POST", "/coach/sessions", { patientId: "patient-demo-multi-source", mode: "virtual" })).json;
    const sid = created.sessionId;
    expect(created.snapshot.condition.baselineSource).toMatch(/chart/);
    expect(created.snapshot.condition.vitals.sys).toBe(121);
    expect(created.snapshot.checklist[0]).toMatchObject({ id: "mark_incision", done: false, current: true });
    expect(created.context).toMatch(/Vitals \(simulated; baseline chart/);

    const cut = await req("POST", `/coach/sessions/${sid}/events`, { event: { type: "injury", region: "neck", instrumentId: "scalpel" } });
    expect(cut.json.accepted ?? true).toBeTruthy();
    const state = (await req("GET", `/coach/sessions/${sid}`)).json;
    expect(state.snapshot.condition.regions).toEqual([expect.objectContaining({ region: "neck", bleeding: true })]);
    expect(state.context).toMatch(/Injuries outside the surgical field: neck \(bleeding\)/);
    let died = false;
    for (let i = 0; i < 120 && !died; i++) {
      advance(1000);
      await req("POST", `/coach/sessions/${sid}/events`, { event: { type: "contact", instrumentId: "scalpel", structureId: "skin" } }); // any change re-evaluates the condition
      died = (await req("GET", `/coach/sessions/${sid}`)).json.snapshot.condition.outcome.result === "died";
    }
    expect(died).toBe(true);
    const after = (await req("GET", `/coach/sessions/${sid}`)).json;
    expect(after.context).toMatch(/THE PATIENT DIED \(simulated\)/);
    const refused = await req("POST", `/coach/sessions/${sid}/events`, { event: { type: "injury", region: "chest", instrumentId: "scalpel" } });
    expect(JSON.stringify(refused.json)).toMatch(/patient_died/);
    const alerts = (await req("GET", `/coach/sessions/${sid}/alerts?after=0`)).json.alerts.map((a: any) => a.kind);
    expect(alerts).toEqual(expect.arrayContaining(["region_injury", "vitals", "patient_died"]));
    const kinds = new Set(logs.map((l) => l.kind));
    expect([...kinds].sort()).toEqual(expect.arrayContaining(["alert", "event", "outcome", "vitals"]));
    expect(logs.find((l) => l.kind === "outcome")?.text).toMatch(/^Patient died: hemorrhage/);
  });

  it("takes the AR Time-Out baseline from the body and rejects junk", async () => {
    const { req } = rig();
    const sid = (await req("POST", "/coach/sessions", { patientId: "patient-demo-multi-source" })).json.sessionId;
    expect((await req("POST", `/coach/sessions/${sid}/vitals/baseline`, { baseline: { hr: "fast" } })).status).toBe(400);
    const ok = await req("POST", `/coach/sessions/${sid}/vitals/baseline`, { baseline: { hr: 64, rr: 13, sys: 117, dia: 74, source: "measured" } });
    expect(ok.json.condition).toMatchObject({ baselineSource: "measured", vitals: { hr: 64, sys: 117 } });
  });

  it("validates injury events", async () => {
    const { req } = rig();
    const sid = (await req("POST", "/coach/sessions", { patientId: "patient-demo-multi-source" })).json.sessionId;
    const badRegion = await req("POST", `/coach/sessions/${sid}/events`, { event: { type: "injury", region: "spleen", instrumentId: "scalpel" } });
    expect(badRegion.status).toBe(400);
  });
});
