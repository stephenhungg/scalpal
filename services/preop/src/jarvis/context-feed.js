// Decides what live context the voice agent gets on each state change (docs/surgery-state.md, point 4).
// Contextual updates pile up in the conversation, so a full state card goes out only when the picture
// changes structurally (status, step, milestones, bleeds, tracking) or about every 10 s; in between,
// one-line deltas carry the new events. Pure logic, unit tested in test/context-feed.test.ts.

export const FULL_EVERY_MS = 10000;

const structuralKey = (s) =>
  JSON.stringify([s.status, s.step?.id, s.completedCount, s.achievedMilestones ?? [], (s.activeBleeds ?? []).map((b) => b.structure.id), s.trackingValid, s.desynced]);

export function createContextFeed({ now = () => Date.now(), fullEveryMs = FULL_EVERY_MS } = {}) {
  let lastFullAt = -Infinity;
  let lastStructural = "";
  let lastTimelineKey = "";
  let lastScene = "";
  let lastStuck = 0;
  let lastSent = "";

  const timelineKey = (t) => (t ? `${t.atSeconds}|${t.text}` : "");

  function markFull(s) {
    lastFullAt = now();
    lastStructural = structuralKey(s);
    lastTimelineKey = timelineKey(s.timeline?.at(-1));
    lastScene = s.scene?.summary ?? "";
    lastStuck = s.stuckLevel ?? 0;
  }

  return {
    // Returns { kind: "full" | "delta", text } to send, or null when nothing new is worth sending.
    next(snapshot, fullText, { force = false } = {}) {
      if (!snapshot) return null;
      const changed = fullText !== lastSent;
      if (force || structuralKey(snapshot) !== lastStructural || (changed && now() - lastFullAt >= fullEveryMs)) {
        if (!force && !changed) return null;
        markFull(snapshot);
        lastSent = fullText;
        return { kind: "full", text: fullText };
      }
      const parts = [];
      const timeline = snapshot.timeline ?? [];
      const from = timeline.findIndex((t) => timelineKey(t) === lastTimelineKey);
      const fresh = from === -1 ? timeline.slice(-3) : timeline.slice(from + 1);
      if (fresh.length) parts.push(fresh.map((t) => t.text).join(" "));
      if ((snapshot.scene?.summary ?? "") !== lastScene && snapshot.scene?.summary) parts.push(`In view: ${snapshot.scene.summary}`);
      if ((snapshot.stuckLevel ?? 0) !== lastStuck) parts.push(`Coaching level: ${snapshot.stuckLabel}.`);
      lastTimelineKey = timelineKey(timeline.at(-1));
      lastScene = snapshot.scene?.summary ?? "";
      lastStuck = snapshot.stuckLevel ?? 0;
      if (!parts.length) return null;
      return { kind: "delta", text: `[STATE DELTA v${snapshot.version}] ${parts.join(" ")}` };
    },
  };
}
