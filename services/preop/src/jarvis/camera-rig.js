// Coach-session plumbing for the camera test rig, kept apart from the vision code so it runs without a
// browser (test/camera-rig.test.ts).
//
// The rig only observes by default. It acts as the headset (posts tracking/focus/touch events into the
// live coach session and acks Jarvis's highlight commands as "applied") only while actsAsHeadset() is
// true. Otherwise a camera tab left open during a real Quest run would ack commands the headset never
// rendered and pause scoring when the webcam loses the body.

export function createRigSession({ api, actsAsHeadset, onAttach, onSnapshot, onCommand, feed }) {
  let sid = "";
  let busy = false; // attach() runs on an interval; never overlap on a slow server

  async function attach() {
    if (busy) return;
    busy = true;
    try {
      const cur = await api("GET", "/coach/current");
      if (cur.ok && cur.json.sessionId && cur.json.sessionId !== sid) {
        const kaseRes = await api("GET", `/patients/${encodeURIComponent(cur.json.patientId)}/case`);
        // Attach only once the case loaded, so a failed fetch is retried on the next tick.
        if (!kaseRes.ok || !Array.isArray(kaseRes.json.anatomy)) {
          feed(`could not load the case: ${kaseRes.json.error?.message ?? "error"}`, "warning");
          return;
        }
        sid = cur.json.sessionId;
        onAttach(kaseRes.json);
      }
      if (!sid) return;
      const s = await api("GET", `/coach/sessions/${sid}`);
      if (s.ok) onSnapshot(s.json.snapshot);
      if (!actsAsHeadset()) return;
      const cmds = await api("GET", `/coach/sessions/${sid}/commands`);
      for (const c of cmds.json.commands ?? []) {
        const ack = await api("POST", `/coach/sessions/${sid}/commands/${c.commandId}/ack`, { status: "applied" });
        if (ack.ok) onCommand(c);
        else feed(`ack failed: ${ack.json.error?.message ?? "error"}`, "warning");
      }
    } catch (e) {
      feed(`coach session unreachable: ${e?.message ?? e}`, "warning");
    } finally {
      busy = false;
    }
  }

  // Returns the service response, or null when nothing was sent (no session, or observing only).
  async function send(event) {
    if (!sid || !actsAsHeadset()) return null;
    return api("POST", `/coach/sessions/${sid}/events`, { event: { ...event, eventId: `cam-${crypto.randomUUID().replaceAll("-", "")}` } });
  }

  return {
    get sid() { return sid; },
    attach,
    send,
  };
}

// One requestAnimationFrame chain, however often start() is called ("Switch camera" restarts the
// stream, not the loop). A throwing frame is reported and the loop keeps going.
export function createFrameLoop(raf, frame, onError = () => {}) {
  let running = false;
  const tick = () => {
    if (!running) return;
    try {
      frame();
    } catch (e) {
      onError(e);
    }
    raf(tick);
  };
  return {
    get running() { return running; },
    start() {
      if (running) return false;
      running = true;
      raf(tick);
      return true;
    },
    stop() {
      running = false;
    },
  };
}
