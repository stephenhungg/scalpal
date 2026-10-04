import { describe, expect, it } from "vitest";
import { buildCase } from "../src/case-builder.js";
import { PROCEDURES_BY_ID } from "../src/catalog/procedures.js";
import { CoachSession, renderContext } from "../src/coach.js";
import { bodyAction } from "../src/open-body.js";
import { idealBodyActions } from "../src/open-body-fixtures.js";
import { NOW, fixture } from "./helpers.js";

function session() {
  let t = NOW.getTime();
  const kase = buildCase(fixture("patient-demo-pediatric-asthma"), "", NOW);
  const procedure = PROCEDURES_BY_ID.get("open_appendectomy")!;
  const s = new CoachSession("coach-tracker", { ...kase, procedure, procedureId: procedure.id }, () => new Date(t));
  return { s, advance: (sec: number) => void (t += sec * 1000) };
}

describe("state tracker feed", () => {
  it("tracks tools in hand and gives Jarvis a timed, plain-language recent history", () => {
    const { s, advance } = session();
    s.receive({ type: "instrument", instrumentId: "scalpel", hand: "right", held: true });
    advance(3);
    s.receive({ type: "contact", instrumentId: "scalpel", structureId: "skin" });
    advance(2);
    for (const evidence of idealBodyActions("mark_incision")) s.receive({ type: "surgery", evidence }, { eventId: evidence.actionId });
    const snap = s.snapshot();
    expect(snap.held).toEqual([expect.objectContaining({ hand: "right", instrumentId: "scalpel", touching: { id: "skin", name: expect.any(String) } })]);
    expect(snap.timeline[0]).toMatchObject({ atSeconds: 0, text: expect.stringMatching(/^Picked up the .* \(right hand\)\.$/) });
    expect(snap.timeline.map((t) => t.text).join(" ")).toMatch(/touched the skin\..*marked the incision line.*Milestone reached: mark mcburney incision\./);
    const context = renderContext(snap);
    expect(context).toMatch(/In hand: right /);
    expect(context).toMatch(/Recent, oldest first \(session clock, now 0:05\): 0:00 Picked up/);
    s.receive({ type: "instrument", instrumentId: "scalpel", hand: "right", held: false });
    expect(s.snapshot().held).toEqual([]);
  });

  it("describes measured outcomes in words and warns once when a tool touches a critical structure", () => {
    const { s } = session();
    const first = s.receive({ type: "contact", instrumentId: "babcock", structureId: "cecum" });
    expect(first.alerts.map((a) => a.kind)).toEqual(["danger_focus"]);
    expect(s.receive({ type: "contact", instrumentId: "babcock", structureId: "cecum" }).alerts).toEqual([]);
    s.receive({ type: "surgery", evidence: bodyAction("cut", "cecum", { actionId: "cut-cecum", lengthMm: 5 }) }, { eventId: "cut-cecum" });
    expect(s.snapshot().lastEvent).toMatch(/cut the cecum, 5 mm \(blocked: that layer is not exposed yet\)/);
  });
});

describe("salient state card", () => {
  it("says what the current milestone still needs in words, including negatives, and shows tool tips", () => {
    const { s } = session();
    s.receive({ type: "instrument", instrumentId: "skin_marker", hand: "right", held: true });
    s.receive({ type: "contact", instrumentId: "skin_marker", structureId: "skin" });
    const card = renderContext(s.snapshot());
    expect(card).toMatch(/Still needed: skin: NOT yet incision line marked; skin: mark distance from McBurney.s point not measured yet, needs at most 20 mm; skin: mark length not measured yet, needs 50 to 80 mm;/);
    expect(card).toMatch(/In hand: right .* \(tip on the skin\)/);
    expect(card).not.toMatch(/Body facts:/);
    s.receive({ type: "surgery", evidence: bodyAction("mark", "skin", { actionId: "short-mark", instrumentId: "skin_marker", lengthMm: 30, distanceMm: 5 }) }, { eventId: "short-mark" });
    expect(renderContext(s.snapshot())).toMatch(/mark length is 30 mm, needs 50 to 80 mm/);
  });
});

describe("uncontrolled bleeding", () => {
  it("warns again once a bleed stays active for 30 s of headset time, and only once", () => {
    const { s } = session();
    for (const step of ["incise_skin", "open_fascia"]) for (const evidence of idealBodyActions(step)) s.receive({ type: "surgery", evidence }, { eventId: evidence.actionId });
    const cut = s.receive({ type: "surgery", evidence: bodyAction("cut", "muscle", { actionId: "cut-muscle", lengthMm: 20, timeMs: 100000 }) }, { eventId: "cut-muscle" });
    expect(cut.alerts.some((a) => a.kind === "bleeding")).toBe(true);
    const tick = (t: number) => s.receive({ type: "surgery", evidence: bodyAction("tick", "skin", { instrumentId: "assistant", actionId: `tick-${t}`, timeMs: t }) }, { eventId: `tick-${t}` });
    expect(tick(120000).alerts.filter((a) => a.reflexKey.startsWith("bleeding_uncontrolled"))).toEqual([]);
    const late = tick(131000).alerts.filter((a) => a.reflexKey === "bleeding_uncontrolled.muscle");
    expect(late).toHaveLength(1);
    expect(late[0]!.tier).toBe("warning");
    expect(tick(140000).alerts.filter((a) => a.reflexKey.startsWith("bleeding_uncontrolled"))).toEqual([]);
    expect(s.snapshot().timeline.at(-1)?.text ?? "").not.toBe("");
    expect(s.snapshot().timeline.map((t) => t.text).join(" ")).toMatch(/uncontrolled for 31 s/);
  });
});

describe("laptop sim buttons on open surgery", () => {
  it("drive the real body reducer through guardrails, bleeding and every milestone", async () => {
    const { createApp } = await import("../src/app.js");
    const { fixtureClient } = await import("./helpers.js");
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
    const req = async (route: string, body: unknown) => (await (await app.request(route, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) })).json()) as Record<string, any>;
    const sid = (await req("/coach/sessions", { patientId: "patient-demo-multi-source" })).sessionId;
    const sim = (kind: string) => req(`/coach/sessions/${sid}/simulate`, { kind });
    expect((await sim("look_at_danger")).alerts.map((a: any) => a.kind)).toContain("danger_focus");
    for (let i = 0; i < 3; i++) await sim("complete_step");
    const cut = await sim("mistake"); // blade on muscle at split_muscle
    expect(cut.alerts.map((a: any) => a.kind)).toEqual(expect.arrayContaining(["mistake", "bleeding"]));
    expect((await sim("stop_bleed")).alerts.map((a: any) => a.kind)).toContain("bleeding_controlled");
    let last: Record<string, any> = {};
    for (let i = 0; i < 12 && last.snapshot?.status !== "completed"; i++) last = await sim("complete_step");
    expect(last.snapshot.status).toBe("completed");
  });
});
