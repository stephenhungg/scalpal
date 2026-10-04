import { describe, expect, it } from "vitest";
import { createContextFeed } from "../src/jarvis/context-feed.js";

const snap = (over: Record<string, unknown> = {}) => ({
  status: "active", version: 1, step: { id: "incise_skin" }, completedCount: 1, achievedMilestones: ["mark_incision"],
  activeBleeds: [], trackingValid: true, desynced: false, stuckLevel: 0, stuckLabel: "on track", scene: { summary: "" },
  timeline: [{ atSeconds: 0, text: "Picked up the scalpel (right hand)." }], ...over,
});

describe("context feed", () => {
  it("sends the full card first, deltas for new events, and a full card on structural change or after 10 s", () => {
    let t = 0;
    const feed = createContextFeed({ now: () => t });
    expect(feed.next(snap(), "CARD 1")).toEqual({ kind: "full", text: "CARD 1" });
    t = 2000;
    const touched = snap({ version: 2, timeline: [...snap().timeline, { atSeconds: 2, text: "Scalpel touched the skin." }] });
    expect(feed.next(touched, "CARD 2")).toEqual({ kind: "delta", text: "[STATE DELTA v2] Scalpel touched the skin." });
    expect(feed.next(touched, "CARD 2")).toBeNull(); // nothing new
    t = 3000;
    const bleeding = snap({ version: 3, activeBleeds: [{ structure: { id: "muscle" } }] });
    expect(feed.next(bleeding, "CARD 3")?.kind).toBe("full");
    t = 14000;
    expect(feed.next(snap({ version: 4, activeBleeds: [{ structure: { id: "muscle" } }], stuckLevel: 1, stuckLabel: "nudge" }), "CARD 4")).toEqual({ kind: "full", text: "CARD 4" });
  });

  it("forces a full card on reconnect", () => {
    const feed = createContextFeed({ now: () => 0 });
    feed.next(snap(), "CARD");
    expect(feed.next(snap(), "CARD", { force: true })).toEqual({ kind: "full", text: "CARD" });
  });
});
