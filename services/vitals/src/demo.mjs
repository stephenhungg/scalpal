// Demo source: synthetic, clearly labelled vitals for wiring things up without a key or a
// volunteer. Never present these as real.
export function startDemo({ onUpdate }) {
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
    onUpdate({
      pulse: { bpm: hr, confidence: 100, stable: true, t: us },
      breathing: { bpm: rr, confidence: 100, stable: true, t: us },
      traces,
      status: { ok: true, code: 0, reason: "Demo" },
    });
  }, 100);
  return { stop: () => clearInterval(id) };
}
