// Demo source: synthetic, clearly labelled vitals for wiring things up without a key or a
// volunteer. Never present these as real.
// `fake` is a test hook for the live path: "noface" reports no face, "fail" can't open the camera,
// "dip" drops below the stable threshold after 1 s.
export function startDemo({ onUpdate, fake }) {
  if (fake === "fail") throw new Error("SmartSpectra input is unavailable.");
  const t0 = Date.now();
  const traces = { pulse: [], breathing: [] };
  const id = setInterval(() => {
    const t = (Date.now() - t0) / 1000;
    const hr = 72 + 3 * Math.sin(t / 9);
    const rr = 14 + 1.5 * Math.sin(t / 13);
    const us = Math.round(t * 1e6);
    traces.pulse.push([us, Math.sin(2 * Math.PI * (hr / 60) * t) ** 9]);
    traces.breathing.push([us, Math.sin(2 * Math.PI * (rr / 60) * t)]);
    for (const k of ["pulse", "breathing"]) if (traces[k].length > 300) traces[k].splice(0, traces[k].length - 300);
    const stable = !(fake === "dip" && t > 1);
    if (fake === "noface") {
      onUpdate({
        pulse: { bpm: null, confidence: null, stable: false, t: us },
        breathing: { bpm: null, confidence: null, stable: false, t: us },
        traces: { pulse: [], breathing: [] },
        status: { ok: false, code: 1, noFace: true, reason: "No face found." },
      });
      return;
    }
    onUpdate({
      pulse: { bpm: hr, confidence: 100, stable, t: us },
      breathing: { bpm: rr, confidence: 100, stable, t: us },
      traces,
      status: { ok: true, code: 0, reason: "Demo" },
    });
  }, 100);
  return { stop: () => clearInterval(id) };
}
