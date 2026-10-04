import assert from "node:assert/strict";
import { test } from "node:test";
import { AUTHORED_BASELINE, baselineFrom, hemorrhageClass, monitorVitals, smooth } from "../src/physiology.mjs";

const base = { hr: 68, rr: 13, sys: 120, dia: 78, source: "measured" };
const ebv = 70 * 70; // ml, 70 kg
const lost = (pct) => (pct / 100) * ebv;

test("class boundaries", () => {
  assert.equal(hemorrhageClass(10), 1);
  assert.equal(hemorrhageClass(20), 2);
  assert.equal(hemorrhageClass(35), 3);
  assert.equal(hemorrhageClass(45), 4);
});

test("class 1 stays near the measured baseline", () => {
  const v = monitorVitals({ baseline: base, bloodLostMl: lost(10) });
  assert.equal(v.hemorrhageClass, 1);
  assert.ok(v.hr >= 68 && v.hr <= 78, `hr ${v.hr}`);
  assert.equal(v.rr, 13);
  assert.equal(v.sys, 120);
  assert.match(v.label, /baseline 68 \(measured\)/);
});

test("class 2: tachycardic, faster breathing, narrowing pulse pressure", () => {
  const v = monitorVitals({ baseline: base, bloodLostMl: lost(22) });
  assert.equal(v.hemorrhageClass, 2);
  assert.ok(v.hr >= 100 && v.hr <= 120, `hr ${v.hr}`);
  assert.ok(v.rr >= 20 && v.rr <= 30, `rr ${v.rr}`);
  assert.ok(v.sys - v.dia < base.sys - base.dia, "pulse pressure narrows");
});

test("class 3: 120-140 and falling pressure", () => {
  const v = monitorVitals({ baseline: base, bloodLostMl: lost(35) });
  assert.equal(v.hemorrhageClass, 3);
  assert.ok(v.hr >= 120 && v.hr <= 140, `hr ${v.hr}`);
  assert.ok(v.sys < base.sys - 5, `sys ${v.sys}`);
});

test("class 4: crashing", () => {
  const v = monitorVitals({ baseline: base, bloodLostMl: lost(45) });
  assert.equal(v.hemorrhageClass, 4);
  assert.ok(v.hr > 140, `hr ${v.hr}`);
  assert.ok(v.rr > 35, `rr ${v.rr}`);
  assert.ok(v.sys < 90, `sys ${v.sys}`);
});

test("an active bleed pushes toward the next class", () => {
  const still = monitorVitals({ baseline: base, bloodLostMl: lost(13) });
  const bleeding = monitorVitals({ baseline: base, bloodLostMl: lost(13), bleedMlPerMin: 400 });
  assert.equal(still.hemorrhageClass, 1);
  assert.equal(bleeding.hemorrhageClass, 2);
});

test("a critical injury adds an acute spike", () => {
  const a = monitorVitals({ baseline: base, bloodLostMl: lost(5) });
  const b = monitorVitals({ baseline: base, bloodLostMl: lost(5), criticalInjury: true });
  assert.equal(b.hr - a.hr, 15);
  assert.equal(b.rr - a.rr, 4);
});

test("recovery: after hemostasis values ease back over ~10-30 s", () => {
  const high = monitorVitals({ baseline: base, bloodLostMl: lost(13), bleedMlPerMin: 400 });
  const target = monitorVitals({ baseline: base, bloodLostMl: lost(13) });
  let v = high;
  for (let t = 0; t < 10; t++) v = smooth(v, target, 1);
  assert.ok(v.hr < high.hr && v.hr > target.hr, `partly recovered at 10 s: ${v.hr}`);
  for (let t = 0; t < 50; t++) v = smooth(v, target, 1);
  assert.ok(Math.abs(v.hr - target.hr) <= 1, `recovered by 60 s: ${v.hr}`);
});

test("baseline: median of confident readings, else authored", () => {
  const samples = [70, 72, 71, 90, 69, 71].map((hr) => ({ hr, rr: 14 }));
  const b = baselineFrom(samples);
  assert.equal(b.hr, 71);
  assert.equal(b.source, "measured");
  assert.equal(baselineFrom(samples, { mode: "demo" }).source, "demo");
  assert.deepEqual({ ...baselineFrom([{ hr: 70, rr: 14 }]), note: undefined }, { ...AUTHORED_BASELINE, note: undefined });
});
