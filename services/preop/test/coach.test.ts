import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { buildCase } from "../src/case-builder.js";
import { CoachSession, renderContext, type CoachAlert } from "../src/coach.js";
import { buildSystemPrompt } from "../src/coach-prompt.js";
import { resolveStructure } from "../src/coach-routes.js";
import { unitySafetyErrors } from "../src/unity-safe.js";
import { validateCatalog } from "../src/validate.js";
import { NOW, fixture, fixtureClient } from "./helpers.js";

const SUBJECTS = [
  "patient-demo-polypharmacy",
  "patient-demo-001",
  "patient-demo-pediatric-asthma",
  "patient-demo-sparse",
  "patient-demo-messy-coding",
];

function clock() {
  let t = NOW.getTime();
  return { now: () => new Date(t), advance: (seconds: number) => void (t += seconds * 1000) };
}

function session(subject = "patient-demo-polypharmacy") {
  const c = clock();
  return { s: new CoachSession("coach-test", buildCase(fixture(subject), "", NOW), c.now), ...c };
}

describe("coach knowledge", () => {
  it("covers every structure and step with no orphans", () => {
    expect(validateCatalog()).toEqual([]);
  });
});

describe("coach session", () => {
  it.each(SUBJECTS)("plays %s to completion one correct action at a time", (subject) => {
    const { s } = session(subject);
    const alerts: CoachAlert[] = [];
    for (let i = 0; i < 200 && !s.done; i++) {
      const e = s.nextCorrectEvent();
      expect(e).not.toBeNull();
      alerts.push(...s.handle(e!).alerts);
    }
    expect(s.done).toBe(true);
    const snap = s.snapshot();
    expect(snap.status).toBe("completed");
    expect(snap.completedCount).toBe(s.kase.procedure.steps.length);
    expect(alerts.at(-1)?.kind).toBe("case_complete");
    expect(alerts.filter((a) => a.kind === "step_complete")).toHaveLength(s.kase.procedure.steps.length - 1);
    expect(unitySafetyErrors(snap)).toEqual([]);
  });

  it("raises an urgent alert for a high-severity mistake and records it", () => {
    const { s } = session();
    while (s.engine.current?.id !== "critical_view") s.handle(s.nextCorrectEvent()!);
    const out = s.handle({ type: "identify", structureId: "common_bile_duct" });
    expect(out.alerts[0]).toMatchObject({ kind: "mistake", priority: "urgent" });
    expect(out.alerts[0]!.say).toMatch(/common bile duct/i);
    expect(s.snapshot().recentMistakes.at(-1)?.mistakeId).toBe("cbd_as_cystic");
    expect(s.engine.current?.id).toBe("critical_view");
  });

  it("calls out the right structure with the wrong instrument", () => {
    const { s } = session();
    while (s.engine.current?.id !== "clip_artery") s.handle(s.nextCorrectEvent()!);
    const out = s.handle({ type: "touch", structureId: "cystic_artery", instrumentId: "lap_scissors" });
    expect(out.alerts[0]).toMatchObject({ kind: "wrong_instrument" });
    expect(out.alerts[0]!.say).toMatch(/clip applier/i);
    expect(s.snapshot().step.progressText).toBe("0 of 3 applied");
  });

  it("escalates hints as time passes without progress, once per tier", () => {
    const { s, advance } = session();
    expect(s.tick()).toEqual([]);
    advance(21);
    const [nudge] = s.tick();
    expect(nudge).toMatchObject({ kind: "stuck", highlight: [] });
    expect(s.tick()).toEqual([]);
    advance(25);
    const [look] = s.tick();
    expect(look!.say).toMatch(/umbilicus/i);
    expect(look!.highlight).toEqual(["umbilicus"]);
    advance(30);
    const [explicit] = s.tick();
    expect(explicit!.say).toMatch(/12 mm trocar/i);
    expect(s.snapshot().stuckLabel).toBe("walk through");
    s.handle(s.nextCorrectEvent()!);
    expect(s.snapshot().stuckLevel).toBe(0);
  });

  it("escalates on repeated off-target attempts and highlights what is left", () => {
    const { s } = session();
    while (s.engine.current?.id !== "dissect_triangle") s.handle(s.nextCorrectEvent()!);
    s.handle({ type: "touch", structureId: "cystic_duct", instrumentId: "hook_cautery" });
    s.handle({ type: "touch", structureId: "stomach", instrumentId: "hook_cautery" });
    s.handle({ type: "touch", structureId: "duodenum", instrumentId: "hook_cautery" });
    expect(s.snapshot().stuckLevel).toBe(1);
    s.handle({ type: "touch", structureId: "transverse_colon", instrumentId: "hook_cautery" });
    const snap = s.snapshot();
    expect(snap.stuckLevel).toBe(2);
    expect(snap.guidance.highlight).toEqual(["cystic_artery"]);
    expect(snap.step.remaining).toEqual(["Cystic artery"]);
  });

  it("pauses scoring while tracking is lost and does not count the pause as stuck", () => {
    const { s, advance } = session();
    expect(s.handle({ type: "tracking", valid: false }).alerts[0]).toMatchObject({ kind: "tracking_lost", priority: "urgent" });
    expect(s.handle(s.nextCorrectEvent()!)).toMatchObject({ accepted: false, reason: "tracking_invalid" });
    expect(s.snapshot().status).toBe("paused");
    advance(120);
    expect(s.tick()).toEqual([]);
    s.handle({ type: "tracking", valid: true });
    expect(s.snapshot().stuckLevel).toBe(0);
    expect(s.handle(s.nextCorrectEvent()!).accepted).toBe(true);
  });

  it("warns once when the learner looks at a danger structure", () => {
    const { s } = session();
    while (s.engine.current?.id !== "dissect_triangle") s.handle(s.nextCorrectEvent()!);
    expect(s.handle({ type: "focus", structureId: "common_bile_duct" }).alerts[0]?.kind).toBe("danger_focus");
    s.handle({ type: "focus", structureId: "gallbladder" });
    expect(s.handle({ type: "focus", structureId: "common_bile_duct" }).alerts).toEqual([]);
  });

  it("gives hints in increasing tiers when asked, capped at the explicit move", () => {
    const { s } = session();
    while (s.engine.current?.id !== "clip_artery") s.handle(s.nextCorrectEvent()!);
    s.handle(s.nextCorrectEvent()!);
    expect(s.requestHint().tier).toBe(1);
    expect(s.requestHint()).toMatchObject({ tier: 2, highlight: ["cystic_artery"] });
    const explicit = s.requestHint();
    expect(explicit.tier).toBe(3);
    expect(explicit.say).toMatch(/clip the cystic artery 2 more times/i);
    expect(s.requestHint().tier).toBe(3);
  });

  it("only lets the headset highlight this case's anatomy, and tracks the ack", () => {
    const { s } = session();
    expect(s.requestCommand("highlight", "appendix")).toHaveProperty("error");
    const cmd = s.requestCommand("highlight", "cystic_artery");
    expect(cmd).toMatchObject({ status: "pending" });
    if ("error" in cmd) throw new Error(cmd.error);
    expect(s.pendingCommands()).toHaveLength(1);
    s.ackCommand(cmd.commandId, "applied");
    expect(s.pendingCommands()).toHaveLength(0);
  });

  it("renders a live context block with the step, dangers, and patient notes", () => {
    const { s } = session();
    while (s.engine.current?.id !== "liver_bed") s.handle(s.nextCorrectEvent()!);
    const text = renderContext(s.snapshot());
    expect(text).toMatch(/Step 10 of 13: Dissect off the liver bed/);
    expect(text).toMatch(/Danger structures this step: Liver/);
    expect(text).toMatch(/Patient-specific: .*bleed/i);
  });
});

describe("per-case prompt isolation", () => {
  const prompt = (subject: string) => buildSystemPrompt(buildCase(fixture(subject), "", NOW));

  it("only carries the anatomy of its own procedure", () => {
    const chole = prompt("patient-demo-polypharmacy");
    expect(chole).toMatch(/Cystic artery:/);
    expect(chole).toMatch(/Rouviere/);
    expect(chole).not.toMatch(/Appendicular artery|Inferior mesenteric artery|Left ureter/);

    const appy = prompt("patient-demo-pediatric-asthma");
    expect(appy).toMatch(/Appendicular artery:/);
    expect(appy).not.toMatch(/Cystic duct|Common bile duct|Sigmoid/);

    const colectomy = prompt("patient-demo-messy-coding");
    expect(colectomy).toMatch(/Left ureter:/);
    expect(colectomy).not.toMatch(/Cystic|Appendi/);
  });

  it("includes this patient's chart risks and none from other patients", () => {
    const harriet = prompt("patient-demo-polypharmacy");
    expect(harriet).toMatch(/apixaban/i);
    expect(prompt("patient-demo-pediatric-asthma")).not.toMatch(/apixaban/i);
  });
});

describe("structure resolution", () => {
  const chole = buildCase(fixture("patient-demo-polypharmacy"), "", NOW);
  it("maps spoken names and abbreviations within the case", () => {
    expect(resolveStructure("the CBD", chole)).toEqual({ kind: "case", id: "common_bile_duct" });
    expect(resolveStructure("Right hepatic artery", chole)).toEqual({ kind: "case", id: "right_hepatic_artery" });
    expect(resolveStructure("appendix", chole)).toEqual({ kind: "other_case", id: "appendix" });
    expect(resolveStructure("spleen", chole)).toEqual({ kind: "none" });
  });
});

describe("coach routes", () => {
  const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
  const call = async (method: string, route: string, body?: unknown) => {
    const res = await app.request(route, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
    const json = (await res.json()) as Record<string, any>;
    expect(unitySafetyErrors(json), `${method} ${route}`).toEqual([]);
    expect(Array.isArray(json.actions), `${method} ${route} has actions`).toBe(true);
    return { status: res.status, json };
  };

  it("runs a session from patient to alerts over HTTP", async () => {
    const created = await call("POST", "/coach/sessions", { patientId: "polypharmacy-senior" });
    expect(created.status).toBe(201);
    const sid = created.json.sessionId as string;
    expect(created.json.systemPrompt).toMatch(/Laparoscopic cholecystectomy/);
    expect(created.json.firstMessage).toMatch(/Jarvis here/);

    const ev = await call("POST", `/coach/sessions/${sid}/events`, { event: { type: "place_port", portId: "umbilical" } });
    expect(ev.json.alerts[0].kind).toBe("step_complete");
    expect(ev.json.snapshot.step.id).toBe("working_ports");

    expect((await call("POST", `/coach/sessions/${sid}/events`, { event: { type: "touch", structureId: "Spleen!", instrumentId: "x" } })).status).toBe(400);

    // An atlas part outside the catalog, or another procedure's port, is an off-target attempt, not an error.
    const rib = await call("POST", `/coach/sessions/${sid}/events`, { event: { type: "touch", structureId: "skeletal__rib_7_l", instrumentId: "trocar_5mm" } });
    expect(rib.json.results[0]).toMatchObject({ accepted: true, applied: true });
    expect(rib.json.snapshot.lastEvent).toMatch(/rib 7 l/);
    expect(rib.json.snapshot.offTargetAttempts).toBe(1);
    const wrongPort = await call("POST", `/coach/sessions/${sid}/events`, { event: { type: "place_port", portId: "left_lower" } });
    expect(wrongPort.json.results[0]).toMatchObject({ accepted: true, applied: true });
    expect(wrongPort.json.snapshot.step.progressText).toBe("0 of 3 ports placed");

    // A batch with one unknown instrument still applies the valid events around it.
    const mixed = await call("POST", `/coach/sessions/${sid}/events`, {
      events: [
        { type: "touch", structureId: "abdominal_wall", instrumentId: "Laser Scalpel!" },
        { type: "place_port", portId: "epigastric" },
      ],
    });
    expect(mixed.status).toBe(200);
    expect(mixed.json.results[0]).toMatchObject({ accepted: false });
    expect(mixed.json.results[0].reason).toMatch(/not a well-formed id/);
    expect(mixed.json.results[1]).toMatchObject({ accepted: true });
    expect(mixed.json.snapshot.step.progressText).toBe("1 of 3 ports placed");

    const mistake = await call("POST", `/coach/sessions/${sid}/simulate`, { kind: "mistake" });
    expect(mistake.json.alerts[0].kind).toBe("mistake");

    const step = await call("POST", `/coach/sessions/${sid}/simulate`, { kind: "complete_step" });
    expect(step.json.snapshot.step.id).toBe("retract_fundus");
    expect(step.json.alerts.some((a: CoachAlert) => a.kind === "step_complete")).toBe(true);

    expect((await call("POST", `/coach/sessions/${sid}/explain`, { structure: "CBD" })).json).toMatchObject({ found: true, structureId: "common_bile_duct" });
    expect((await call("POST", `/coach/sessions/${sid}/explain`, { structure: "appendix" })).json.found).toBe(false);

    const cmd = await call("POST", `/coach/sessions/${sid}/commands`, { action: "highlight", structure: "gallbladder" });
    expect(cmd.json.command.status).toBe("pending");
    expect((await call("GET", `/coach/sessions/${sid}/commands`)).json.commands).toHaveLength(1);
    const ack = await call("POST", `/coach/sessions/${sid}/commands/${cmd.json.command.commandId}/ack`, { status: "applied" });
    expect(ack.json.command.status).toBe("applied");
    expect((await call("POST", `/coach/sessions/${sid}/commands`, { action: "highlight", structure: "appendix" })).status).toBe(409);

    expect((await call("POST", `/coach/sessions/${sid}/hint`)).json.tier).toBe(1);
    expect((await call("GET", `/coach/sessions/${sid}`)).json.context).toMatch(/LIVE SURGERY STATE/);
  });

  it("lets the headset adopt the newest session for its patient", async () => {
    const a = (await call("POST", "/coach/sessions", { patientId: "patient-demo-001" })).json.sessionId;
    const b = (await call("POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" })).json.sessionId;
    expect((await call("GET", "/coach/current?patientId=patient-demo-001")).json.sessionId).toBe(a);
    expect((await call("GET", "/coach/current")).json.sessionId).toBe(b);
    expect((await call("GET", "/coach/current?patientId=patient-demo-sparse")).status).toBe(404);
  });

  it("rejects unknown patients, unavailable cases, and unknown sessions with a way onward", async () => {
    expect((await call("POST", "/coach/sessions", { patientId: "nobody-here" })).status).toBe(404);
    expect((await call("POST", "/coach/sessions", { patientId: "patient-demo-consent-revoked" })).status).toBe(409);
    expect((await call("GET", "/coach/sessions/coach-doesnotexist")).status).toBe(404);
  });

  it("explains how to configure Jarvis when no agent is set", async () => {
    expect((await call("GET", "/jarvis/connection")).status).toBe(503);
    const page = await app.request("/jarvis");
    expect(page.status).toBe(200);
    expect(await page.text()).toMatch(/<title>/);
  });
});

// /jarvis/connection hands out a credential for an agent billed to the owner. It must only be minted for a
// live session, and the prompt the voice runs with must be the one the server built for that session.
describe("voice connection", () => {
  const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, elevenLabs: { apiKey: "", agentId: "agent-jarvis", patientAgentId: "agent-patient" } });
  const call = async (method: string, route: string, body?: unknown) => {
    const res = await app.request(route, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
    return { status: res.status, json: (await res.json()) as Record<string, any> };
  };

  it("refuses to mint a connection when nothing is live", async () => {
    expect((await call("GET", "/jarvis/connection")).status).toBe(409);
    expect((await call("GET", "/jarvis/connection?agent=patient")).status).toBe(409);
    expect((await call("GET", "/jarvis/connection?sessionId=coach-doesnotexist")).status).toBe(404);
    expect((await call("GET", "/jarvis/connection?encounterId=enc-doesnotexist")).status).toBe(404);
  });

  it("returns the server-built prompt for a coach session", async () => {
    const created = (await call("POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" })).json;
    const conn = await call("GET", `/jarvis/connection?sessionId=${created.sessionId}`);
    expect(conn.status).toBe(200);
    expect(conn.json).toMatchObject({ agentId: "agent-jarvis", prompt: created.systemPrompt, firstMessage: created.firstMessage });
    expect((await call("GET", "/jarvis/connection")).status).toBe(200); // the headset's legacy call while a session is live
  });

  it("picks the agent and prompt from the encounter phase, never from the client", async () => {
    const enc = (await call("POST", "/encounters", { patientId: "patient-demo-sparse" })).json;
    const patient = await call("GET", `/jarvis/connection?encounterId=${enc.encounterId}&agent=jarvis`);
    expect(patient.json).toMatchObject({ agentId: "agent-patient", role: "patient", prompt: enc.patientPrompt, firstMessage: enc.patientFirstMessage, voiceId: enc.voiceId });
    expect((await call("GET", "/jarvis/connection?agent=patient")).status).toBe(200);
    const attending = (await call("POST", `/encounters/${enc.encounterId}/attending`)).json;
    const jarvis = await call("GET", `/jarvis/connection?encounterId=${enc.encounterId}`);
    expect(jarvis.json).toMatchObject({ agentId: "agent-jarvis", role: "attending", prompt: attending.attendingPrompt, firstMessage: attending.attendingFirstMessage, voiceId: "" });
    await call("POST", `/encounters/${enc.encounterId}/tools/record_assessment`, { diagnosis: "appendicitis", differential: [], procedure: "appendectomy", urgency: "emergency" });
    expect((await call("GET", `/jarvis/connection?encounterId=${enc.encounterId}`)).status).toBe(409);
  });
});

describe("danger focus", () => {
  it("does not warn when the learner looks at the step's own target", () => {
    const s = new CoachSession("coach-f", buildCase(fixture("patient-demo-pediatric-asthma"), "", NOW), () => NOW);
    while (s.engine.current?.id !== "find_appendix") s.handle(s.nextCorrectEvent()!);
    expect(s.handle({ type: "focus", structureId: "appendix" }).alerts).toEqual([]);
    while ((s.engine.current?.id as string) !== "divide_mesoappendix") s.handle(s.nextCorrectEvent()!);
    expect(s.handle({ type: "focus", structureId: "terminal_ileum" }).alerts[0]?.kind).toBe("danger_focus");
  });
});

describe("presentation modes", () => {
  it("describes the scene and tracking loss for the chosen mode", async () => {
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
    const start = async (mode?: string) =>
      app.request("/coach/sessions", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ patientId: "patient-demo-pediatric-asthma", ...(mode ? { mode } : {}) }) });
    const mr = (await (await start()).json()) as { systemPrompt: string; snapshot: { mode: string } };
    expect(mr.snapshot.mode).toBe("mixed_reality");
    expect(mr.systemPrompt).toMatch(/real person reclining/);
    const vr = (await (await start("virtual")).json()) as { sessionId: string; systemPrompt: string };
    expect(vr.systemPrompt).toMatch(/fully virtual operating room/);
    expect(vr.systemPrompt).not.toMatch(/real person/);
    const lines = (await (await app.request(`/jarvis/reflex/${vr.sessionId}`)).json()) as { lines: { key: string; text: string }[] };
    expect(lines.lines.find((l) => l.key === "tracking_lost")?.text).toMatch(/headset tracking/);
    expect((await start("hologram")).status).toBe(400);
  });
});

describe("headset relay compatibility", () => {
  const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
  const post = async (route: string, body: unknown) =>
    (await (await app.request(route, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) })).json()) as Record<string, any>;

  it("reports events ignored during tracking loss as received but not applied", async () => {
    const { sessionId: sid } = await post("/coach/sessions", { patientId: "patient-demo-pediatric-asthma" });
    const r = await post(`/coach/sessions/${sid}/events`, { events: [{ type: "tracking", valid: false }, { type: "place_port", portId: "umbilical" }] });
    expect(r.results).toEqual([
      { accepted: true, applied: true, reason: "" },
      { accepted: true, applied: false, reason: "tracking_invalid" },
    ]);
  });

  it("counts exercise input so the headset can tell an untouched attempt from a busy one", async () => {
    const created = await post("/coach/sessions", { patientId: "patient-demo-pediatric-asthma" });
    const sid = created.sessionId;
    expect(created.snapshot.eventCount).toBe(0);
    await post(`/coach/sessions/${sid}/hint`, {});
    await post(`/coach/sessions/${sid}/commands`, { action: "highlight", structure: "cecum" });
    const busyButUntouched = (await (await app.request(`/coach/sessions/${sid}`)).json()) as Record<string, any>;
    expect(busyButUntouched.snapshot.version).toBeGreaterThan(0); // hints and highlights change state
    expect(busyButUntouched.snapshot.eventCount).toBe(0); // but nothing was done to the patient yet
    const r = await post(`/coach/sessions/${sid}/events`, { event: { type: "focus", structureId: "cecum" } });
    expect(r.snapshot.eventCount).toBe(0); // looking is not exercise input
    expect((await post(`/coach/sessions/${sid}/events`, { event: { type: "place_port", portId: "umbilical" } })).snapshot.eventCount).toBe(1);
  });
});

describe("headset authority and retries", () => {
  const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
  const post = async (route: string, body: unknown) =>
    (await (await app.request(route, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) })).json()) as Record<string, any>;
  const start = async () => (await post("/coach/sessions", { patientId: "patient-demo-pediatric-asthma" })).sessionId as string;

  it("applies a retried event once", async () => {
    const sid = await start();
    const event = { type: "place_port", portId: "umbilical", eventId: "evt-1" };
    expect((await post(`/coach/sessions/${sid}/events`, { event })).results[0]).toMatchObject({ accepted: true, applied: true });
    const again = await post(`/coach/sessions/${sid}/events`, { event });
    expect(again.results[0]).toMatchObject({ accepted: true, applied: false, reason: "duplicate" });
    expect(again.snapshot.eventCount).toBe(1);
  });

  it("catches up to a headset that is one step ahead (its completing event was lost)", async () => {
    const sid = await start();
    await post(`/coach/sessions/${sid}/simulate`, { kind: "complete_step" });
    await post(`/coach/sessions/${sid}/simulate`, { kind: "complete_step" });
    await post(`/coach/sessions/${sid}/simulate`, { kind: "complete_step" });
    const r = await post(`/coach/sessions/${sid}/events`, { event: { type: "touch", structureId: "mesoappendix", instrumentId: "maryland_dissector", stepId: "mesoappendix_window" } });
    expect(r.snapshot).toMatchObject({ desynced: false, resyncCount: 1, headsetStepId: "mesoappendix_window" });
    expect(r.snapshot.step.id).toBe("divide_mesoappendix"); // caught up, then the touch completed the window
    expect(r.alerts[0].kind).toBe("step_complete");
  });

  // One forged or buggy event must not award the whole procedure: skipped steps were never performed, so
  // the coach flags the gap instead of synthesizing a perfect record for them.
  it("refuses to skip steps on a single event's stepId", async () => {
    const sid = await start();
    const last = (await post(`/coach/sessions/${sid}/simulate`, { kind: "tracking_restored" })).snapshot.stepCount as number;
    expect(last).toBeGreaterThan(2);
    const kase = (await (await app.request("/patients/patient-demo-pediatric-asthma/case")).json()) as { procedure: { steps: { id: string }[] } };
    const finalStep = kase.procedure.steps.at(-1)!.id;
    const r = await post(`/coach/sessions/${sid}/events`, { event: { type: "confirm", stepId: finalStep, eventId: "evt-skip" } });
    expect(r.results[0]).toMatchObject({ accepted: false, reason: "step_desynchronized" });
    expect(r.snapshot.status).not.toBe("completed");
    expect(r.snapshot.completedCount).toBe(0);
    expect(r.snapshot.desynced).toBe(true);
    const jump = await post(`/coach/sessions/${sid}/events`, { event: { type: "touch", structureId: "mesoappendix", instrumentId: "maryland_dissector", stepId: "mesoappendix_window" } });
    expect(jump.snapshot.completedCount).toBe(0);
    expect(jump.snapshot.step.id).toBe(kase.procedure.steps[0]!.id);
  });

  it("does not catch up while tracking is invalid", async () => {
    const sid = await start();
    await post(`/coach/sessions/${sid}/simulate`, { kind: "tracking_lost" });
    const r = await post(`/coach/sessions/${sid}/events`, { event: { type: "place_port", portId: "left_lower", stepId: "working_ports" } });
    expect(r.results[0]).toMatchObject({ applied: false, reason: "tracking_invalid" });
    expect(r.snapshot.completedCount).toBe(0);
  });

  it("flags a headset that is behind and tells Jarvis to trust it", async () => {
    const sid = await start();
    await post(`/coach/sessions/${sid}/simulate`, { kind: "complete_step" });
    await post(`/coach/sessions/${sid}/simulate`, { kind: "complete_step" });
    const r = await post(`/coach/sessions/${sid}/events`, { event: { type: "place_port", portId: "left_lower", stepId: "working_ports" } });
    expect(r.snapshot.desynced).toBe(true);
    expect(r.context).toMatch(/HEADSET DISAGREES/);
    const back = await post(`/coach/sessions/${sid}/events`, { event: { type: "confirm", stepId: "find_appendix" } });
    expect(back.snapshot.desynced).toBe(false);
  });
});
