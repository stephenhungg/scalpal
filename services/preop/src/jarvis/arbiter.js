// Decides who speaks when. Pure logic (no DOM, no SDK) so it is unit tested in test/arbiter.test.ts.
//
// Tiers follow FAA AC 25.1322-1 alerting practice:
//   warning  -> plays a pre-rendered reflex clip immediately (or an immediate LLM turn if no clip)
//   caution  -> queued; spoken as one LLM turn when the agent is idle and the learner is quiet
//   advisory -> never spoken; the silent context update already carries it
// Queued cautions are coalesced, rate limited, cooled down per type, and re-validated against the
// latest state before they are spoken, so stale hints for a finished step are dropped.

const RANK = { mistake: 0, wrong_instrument: 1, danger_focus: 2, case_complete: 3, step_complete: 4, stuck: 5 };
const STEP_SCOPED = new Set(["mistake", "wrong_instrument", "danger_focus", "stuck"]);

export const DEFAULTS = {
  minGapMs: 6000, // between proactive turns
  quickGapMs: 2500, // for milestones and mistakes, which should not wait the full gap
  userQuietMs: 1500, // learner must have been quiet this long
  idleSettleMs: 700, // after the agent stops speaking, when no response-complete event is available
  cooldownMs: { stuck: 12000, danger_focus: 8000, wrong_instrument: 6000, mistake: 3000 },
};

export function coalesceKey(alert) {
  if (alert.kind === "step_complete" || alert.kind === "stuck" || alert.kind === "case_complete") return alert.kind;
  return `${alert.kind}:${alert.highlight?.[0] ?? ""}`;
}

export function createArbiter(options = {}) {
  const cfg = { ...DEFAULTS, ...options, cooldownMs: { ...DEFAULTS.cooldownMs, ...(options.cooldownMs ?? {}) } };
  const now = options.now ?? (() => Date.now());
  const queue = [];
  const lastDelivered = new Map();
  const stats = { reflex: 0, spokeNow: 0, queued: 0, delivered: 0, silent: 0, droppedCooldown: 0, droppedStale: 0, coalesced: 0 };
  const st = {
    mode: "listening",
    modeListeningAt: -Infinity,
    sawCompleteEvent: false,
    responseComplete: true,
    lastUserSpeechAt: -Infinity,
    lastProactiveAt: -Infinity,
    reflexPlaying: false,
    snapshot: null,
  };

  function isStale(alert) {
    const snap = st.snapshot;
    if (!snap) return false;
    if (snap.status === "completed") return alert.kind !== "case_complete";
    if (snap.status === "paused") return true;
    if (STEP_SCOPED.has(alert.kind) && alert.stepId && alert.stepId !== snap.step.id) return true;
    if (alert.kind === "stuck" && snap.stuckLevel === 0) return true;
    return false;
  }

  function gapFor(alert) {
    return alert.kind === "step_complete" || alert.kind === "case_complete" || alert.kind === "mistake" ? cfg.quickGapMs : cfg.minGapMs;
  }

  function agentIdle() {
    if (st.mode === "speaking") return false;
    return st.sawCompleteEvent ? st.responseComplete : now() - st.modeListeningAt >= cfg.idleSettleMs;
  }

  return {
    stats,
    get queueSize() {
      return queue.length;
    },
    setSnapshot(snapshot) {
      st.snapshot = snapshot;
    },
    setMode(mode) {
      if (mode !== "speaking" && st.mode === "speaking") st.modeListeningAt = now();
      if (mode === "speaking") st.responseComplete = false;
      st.mode = mode;
    },
    responseComplete() {
      st.sawCompleteEvent = true;
      st.responseComplete = true;
    },
    // A delivery that produced no agent turn (a clip) is finished.
    deliveryDone() {
      st.responseComplete = true;
    },
    userSpoke() {
      st.lastUserSpeechAt = now();
    },
    setReflexPlaying(playing) {
      st.reflexPlaying = playing;
    },

    // Returns what the caller should do right now: "reflex", "speak_now", "queued", "silent", or "dropped".
    offer(alert) {
      if (alert.tier === "advisory") {
        stats.silent += 1;
        return { action: "silent", reason: "advisory" };
      }
      if (alert.tier === "warning") {
        // A warning supersedes queued cautions from before it.
        for (let i = queue.length - 1; i >= 0; i--) if (queue[i].version <= alert.version) queue.splice(i, 1);
        if (alert.reflexKey) {
          stats.reflex += 1;
          return { action: "reflex", reason: "" };
        }
        stats.spokeNow += 1;
        st.lastProactiveAt = now();
        return { action: "speak_now", reason: "" };
      }
      const key = coalesceKey(alert);
      const cooldown = cfg.cooldownMs[alert.kind] ?? 0;
      if (cooldown && now() - (lastDelivered.get(key) ?? -Infinity) < cooldown) {
        stats.droppedCooldown += 1;
        return { action: "dropped", reason: "cooldown" };
      }
      for (let i = queue.length - 1; i >= 0; i--) {
        if (coalesceKey(queue[i]) === key) {
          queue.splice(i, 1);
          stats.coalesced += 1;
        }
      }
      if (alert.kind === "case_complete") for (let i = queue.length - 1; i >= 0; i--) if (queue[i].kind === "step_complete") queue.splice(i, 1);
      queue.push(alert);
      stats.queued += 1;
      return { action: "queued", reason: "" };
    },

    // The next caution to speak, or null if nothing should be said yet.
    next() {
      if (!queue.length || st.reflexPlaying || !agentIdle()) return null;
      if (now() - st.lastUserSpeechAt < cfg.userQuietMs) return null;
      queue.sort((a, b) => (RANK[a.kind] ?? 9) - (RANK[b.kind] ?? 9) || b.version - a.version);
      while (queue.length) {
        const alert = queue[0];
        if (isStale(alert)) {
          queue.shift();
          stats.droppedStale += 1;
          continue;
        }
        if (now() - st.lastProactiveAt < gapFor(alert)) return null;
        queue.shift();
        st.lastProactiveAt = now();
        st.responseComplete = false;
        lastDelivered.set(coalesceKey(alert), now());
        stats.delivered += 1;
        return alert;
      }
      return null;
    },
  };
}

// Only these fields change what Jarvis should know; timers ticking do not.
export function semanticKey(snapshot) {
  if (!snapshot) return "";
  return JSON.stringify([
    snapshot.status,
    snapshot.step?.id,
    snapshot.step?.progressText,
    snapshot.completedCount,
    snapshot.mistakeCount,
    snapshot.focusStructure?.id,
    snapshot.stuckLevel,
    snapshot.trackingValid,
    snapshot.hintTier,
    (snapshot.commands ?? []).map((c) => `${c.commandId}:${c.status}`).join(","),
  ]);
}

export function percentile(values, p) {
  if (!values.length) return NaN;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))];
}
