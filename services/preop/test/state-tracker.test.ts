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
    expect(snap.held).toEqual([{ hand: "right", instrumentId: "scalpel", name: expect.any(String) }]);
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
