import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { EncounterSession, Scorecard } from "../src/encounter.js";
import type { RealtimeBridge, RealtimeSink } from "../src/realtime-bridge.js";
import { NOW, fixtureClient } from "./helpers.js";

// A recording stand-in for the SpacetimeDB bridge: proves every route writes what it should.
function recorder(highlight: { status: string; reason: string } | null = null) {
  const calls: { op: string; args: unknown[] }[] = [];
  const sink: RealtimeSink & { status(): object; join(code: string): Promise<string> } = {
    bound: true,
    coachMessage: (...args) => void calls.push({ op: "coachMessage", args }),
    coachStatus: (...args) => void calls.push({ op: "coachStatus", args }),
    attachEncounter: (e: EncounterSession) => {
      calls.push({ op: "attachEncounter", args: [e.id] });
      e.subscribe((ev) => calls.push({ op: `encounter:${ev.kind}`, args: [ev.payload] }));
    },
    encounterPhase: (e: EncounterSession) => void calls.push({ op: "encounterPhase", args: [e.phase] }),
    encounterResult: (_e: EncounterSession, card: Scorecard) => void calls.push({ op: "encounterResult", args: [card.total] }),
    highlight: async (...args) => (calls.push({ op: "highlight", args }), highlight),
    status: () => ({ configured: true, connected: true, identity: "abc", sessionId: "ses_test", lastError: "" }),
    join: async (code: string) => (calls.push({ op: "join", args: [code] }), "ses_test"),
  };
  return { calls, sink };
}

async function req(app: ReturnType<typeof createApp>, method: string, route: string, body?: unknown) {
  const res = await app.request(route, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
  return { status: res.status, json: (await res.json()) as Record<string, any> };
}

describe("realtime wiring", () => {
  it("mirrors the whole pre-op encounter into the shared session", async () => {
    const { calls, sink } = recorder();
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, realtime: sink as unknown as RealtimeBridge });
    const id = (await req(app, "POST", "/encounters", { patientId: "patient-demo-multi-source" })).json.encounterId;
    await req(app, "POST", `/encounters/${id}/tools/answer`, { topic: "allergies" });
    await req(app, "POST", `/encounters/${id}/tools/examine`, { maneuver: "rebound" });
    await req(app, "POST", `/encounters/${id}/transcript`, { speaker: "patient", text: "I'm allergic to latex." });
    await req(app, "POST", `/encounters/${id}/attending`);
    await req(app, "POST", `/encounters/${id}/tools/record_assessment`, { diagnosis: "appendicitis", differential: ["ectopic"], procedure: "appendectomy", urgency: "urgent" });
    const ops = calls.map((c) => c.op);
    expect(ops).toEqual(["attachEncounter", "encounter:log", "encounter:log", "encounter:transcript", "encounterPhase", "encounter:log", "encounterResult"]);
    expect(calls.find((c) => c.op === "encounterPhase")?.args).toEqual(["attending"]);
  });

  it("mirrors surgery transcript and voice status, and joins with an invite code", async () => {
    const { calls, sink } = recorder();
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, realtime: sink as unknown as RealtimeBridge });
    const sid = (await req(app, "POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" })).json.sessionId;
    await req(app, "POST", `/coach/sessions/${sid}/transcript`, { speaker: "coach", text: "Next step: locate the appendix." });
    await req(app, "POST", `/coach/sessions/${sid}/voice-status`, { status: "speaking" });
    expect((await req(app, "POST", `/coach/sessions/${sid}/transcript`, { speaker: "patient", text: "x" })).status).toBe(400);
    expect((await req(app, "POST", "/realtime/join", { code: "ABC234" })).json.sessionId).toBe("ses_test");
    expect(calls.map((c) => [c.op, ...c.args])).toEqual([
      ["coachMessage", "coach", "Next step: locate the appendix."],
      ["coachStatus", "speaking", undefined],
      ["join", "ABC234"],
    ]);
    expect((await req(app, "GET", "/realtime")).json).toMatchObject({ connected: true, sessionId: "ses_test" });
  });

  it("routes Jarvis highlights through the shared session when the headset is there", async () => {
    const { sink } = recorder({ status: "applied", reason: "" });
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, realtime: sink as unknown as RealtimeBridge });
    const sid = (await req(app, "POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" })).json.sessionId;
    expect((await req(app, "POST", `/coach/sessions/${sid}/tools/highlight_structure`, { structure: "cecum" })).json.result).toBe("Highlighted the cecum in the headset.");
    const rejected = recorder({ status: "rejected", reason: "stale step version" });
    const app2 = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, realtime: rejected.sink as unknown as RealtimeBridge });
    const sid2 = (await req(app2, "POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" })).json.sessionId;
    expect((await req(app2, "POST", `/coach/sessions/${sid2}/tools/highlight_structure`, { structure: "cecum" })).json.result).toMatch(/stale step version/);
  });

  it("reports realtime as unconfigured without a bridge", async () => {
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
    expect((await req(app, "GET", "/realtime")).json.configured).toBe(false);
    expect((await req(app, "POST", "/realtime/join", { code: "ABC234" })).status).toBe(503);
  });
});
