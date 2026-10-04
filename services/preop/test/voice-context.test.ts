import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { bodyAction } from "../src/open-body.js";
import { idealBodyActions } from "../src/open-body-fixtures.js";
import { NOW, fixtureClient } from "./helpers.js";

// The native Quest voice polls this every second. Contextual updates accumulate in the agent's
// conversation, so resending the whole state card on each poll floods its context; the laptop page
// avoids that with jarvis/context-feed.js, and the headset must get the same cards and deltas.
describe("native voice context feed", () => {
  const setup = async () => {
    let time = NOW.getTime();
    const app = createApp({ client: fixtureClient(), now: () => new Date(time), coachTickMs: 0 });
    const request = async (method: string, route: string, body?: unknown) => {
      const response = await app.request(route, { method, headers: { "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
      expect(response.status).toBeLessThan(300);
      return (await response.json()) as Record<string, any>;
    };
    const { sessionId } = await request("POST", "/coach/sessions", { patientId: "patient-demo-multi-source", mode: "virtual" });
    const path = `/coach/sessions/${sessionId}`;
    return { request, path, sessionId, advance: (ms: number) => (time += ms) };
  };

  it("sends a full card first, nothing while unchanged, deltas for events, and a full card on a milestone or after ~10 s", async () => {
    const { request, path, sessionId, advance } = await setup();
    await request("POST", `${path}/simulate`, { kind: "correct_action" }); // mark the incision line
    const first = await request("POST", `${path}/voice-context`, { force: true });
    expect(first).toMatchObject({ sessionId, kind: "full" });
    expect(first.text).toMatch(/^\[LIVE SURGERY STATE v\d+\]/);

    // Unchanged state on later polls: no resend. (Stay under the first stuck-hint threshold, which
    // is a real change.)
    for (let poll = 0; poll < 5; poll++) {
      expect((await request("POST", `${path}/voice-context`)).kind).toBe("none");
      advance(1000);
    }

    // A partial cut is a one-line delta carrying only the new event, not another card.
    const post = (evidence: unknown, eventId: string) => request("POST", `${path}/events`, { event: { type: "surgery", evidence }, eventId, stepId: "incise_skin" });
    const [firstCut, finishingCut] = idealBodyActions("incise_skin");
    await post(firstCut, "cut-1");
    const delta = await request("POST", `${path}/voice-context`);
    expect(delta.kind).toBe("delta");
    expect(delta.text).toBe(`[STATE DELTA v${delta.version}] Scalpel: cut the skin, 60 mm.`);
    expect((await request("POST", `${path}/voice-context`)).kind).toBe("none");

    // A milestone is structural: the full card goes out at once, not after 10 s.
    await post(finishingCut, "cut-2");
    const milestone = await request("POST", `${path}/voice-context`);
    expect(milestone.kind).toBe("full");
    expect(milestone.text).toContain(`[LIVE SURGERY STATE v${milestone.version}]`);
    expect(milestone.version).toBeGreaterThan(delta.version);

    // Between cards, events stay deltas; once ~10 s have passed a changed state gets a fresh card.
    advance(2000);
    await post(bodyAction("cut", "cecum", { actionId: "cecum-cut-1", lengthMm: 5 }), "blocked-1");
    expect((await request("POST", `${path}/voice-context`)).kind).toBe("delta");
    advance(10000);
    await post(bodyAction("cut", "cecum", { actionId: "cecum-cut-2", lengthMm: 6 }), "blocked-2");
    expect((await request("POST", `${path}/voice-context`)).kind).toBe("full");
  });

  it("forces a full card for a reconnected voice even when nothing changed", async () => {
    const { request, path } = await setup();
    expect((await request("POST", `${path}/voice-context`, { force: true })).kind).toBe("full");
    expect((await request("POST", `${path}/voice-context`)).kind).toBe("none");
    expect((await request("POST", `${path}/voice-context`, { force: true })).kind).toBe("full");
  });

  it("rejects unknown sessions", async () => {
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
    const response = await app.request("/coach/sessions/coach-notpresent/voice-context", { method: "POST" });
    expect(response.status).toBe(404);
  });
});
