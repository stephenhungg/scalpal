import { AUTHORED_BASELINE, type Baseline } from "./physiology.js";

// VR baseline vitals come from the patient's chart (latest charted values per measure). Any measure the
// chart lacks falls back to the authored baseline, and the source says so. Weight sets the estimated
// blood volume; without a charted weight, an age-appropriate default is used and flagged.

interface Observation {
  name?: string;
  value?: string | number;
  unit?: string;
  effectiveDateTime?: string;
  date?: string;
}

export interface ChartBaseline {
  baseline: Baseline;
  spo2: number | null;
  weightKg: number;
  weightSource: "chart" | "default";
  missing: string[]; // measures filled from the authored baseline
}

const num = (v: unknown): number | null => {
  const n = typeof v === "number" ? v : parseFloat(String(v ?? ""));
  return Number.isFinite(n) ? n : null;
};

export function chartBaseline(vitals: Observation[] = [], ageYears = -1): ChartBaseline {
  const latest = new Map<string, Observation>();
  const when = (o: Observation) => Date.parse(o.effectiveDateTime ?? o.date ?? "") || 0;
  for (const o of vitals) {
    const key = (o.name ?? "").toLowerCase();
    const prev = latest.get(key);
    if (!prev || when(o) >= when(prev)) latest.set(key, o);
  }
  const find = (...needles: string[]) => [...latest.entries()].filter(([k]) => needles.some((n) => k.includes(n))).sort((a, b) => when(b[1]) - when(a[1]))[0]?.[1];

  const hr = num(find("heart rate")?.value);
  const rr = num(find("respiratory rate")?.value);
  const spo2 = num(find("oxygen saturation")?.value);
  let sys: number | null = null;
  let dia: number | null = null;
  const bp = find("blood pressure");
  if (bp) {
    const text = String(bp.value ?? "");
    sys = num(text.match(/systolic[^:]*:\s*([\d.]+)/i)?.[1] ?? text.match(/^\s*([\d.]+)\s*\//)?.[1]);
    dia = num(text.match(/diastolic[^:]*:\s*([\d.]+)/i)?.[1] ?? text.match(/\/\s*([\d.]+)/)?.[1]);
  }
  const weight = num(find("body weight")?.value);

  const missing: string[] = [];
  const pick = (v: number | null, fallback: number, label: string) => {
    if (v == null || v <= 0) {
      missing.push(label);
      return fallback;
    }
    return Math.round(v);
  };
  const baseline: Baseline = {
    hr: pick(hr, AUTHORED_BASELINE.hr, "heart rate"),
    rr: pick(rr, AUTHORED_BASELINE.rr, "respiratory rate"),
    sys: pick(sys, AUTHORED_BASELINE.sys, "systolic pressure"),
    dia: pick(dia, AUTHORED_BASELINE.dia, "diastolic pressure"),
    source: missing.length === 4 ? "authored" : missing.length ? "chart+authored" : "chart",
  };
  const defaultWeight = ageYears >= 0 && ageYears < 18 ? Math.max(10, Math.round(2 * ageYears + 8)) : 70;
  return { baseline, spo2: spo2 == null ? null : Math.round(spo2), weightKg: weight && weight > 0 ? weight : defaultWeight, weightSource: weight && weight > 0 ? "chart" : "default", missing };
}
