// Scalpal case-package tool. Run from services/preop (so the repo's own modules and deps resolve):
//
//   npx tsx ../../.claude/skills/authoring-scalpal-cases/scripts/scalpal-case.mts <command> [args]
//
// Commands
//   list                         FinchNode subjects the repo knows, with their plan, procedure and status
//   chart   <subject>            the chart Scalpal builds for a subject (flags, gaps, age, sex) from its fixture
//   export  <subject> <dir>      write an existing case as a package (a starting template)
//   check   <dir>                validate a package against the repo's real validators, without touching the repo
//   trial   <dir>                install into a throwaway git worktree, run the full test suite there, report failures
//   install <dir> [--write]      dry run by default; --write copies content and edits the TS catalogs
//                  [--force]     install despite check errors (only alongside the code change an error asks for)
//   gates   [--skip-unity]       run the repo gates in order and summarize
//
// It never talks to FinchNode or any model provider. Charts come from services/preop/test/fixtures.

import { spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const SCHEMA = "scalpal.case-package.v1";
const NOW = new Date("2026-10-03T12:00:00Z"); // same clock as the preop tests (test/helpers.ts)

// ---------- locate the repo ----------
function findRepo(): string {
  for (let dir = process.cwd(); dir !== dirname(dir); dir = dirname(dir)) {
    if (existsSync(join(dir, "services", "preop", "src", "case-builder.ts"))) return dir;
  }
  const here = dirname(fileURLToPath(import.meta.url));
  for (let dir = here; dir !== dirname(dir); dir = dirname(dir)) {
    if (existsSync(join(dir, "services", "preop", "src", "case-builder.ts"))) return dir;
  }
  throw new Error("Run this inside the scalpal repo (services/preop/src/case-builder.ts not found).");
}
const REPO = findRepo();
const PREOP = join(REPO, "services", "preop");
const FIXTURES = join(PREOP, "test", "fixtures");
const CONTENT = join(PREOP, "content", "patients");
const imp = (rel: string) => import(pathToFileURL(join(PREOP, rel)).href);

// ---------- repo modules (the single source of truth) ----------
const { buildCase } = await imp("src/case-builder.ts");
const { validateInterview } = await imp("src/interview-types.ts");
const { validateCatalog } = await imp("src/validate.ts");
const { demographicsMatch, DEFAULT_PATIENT_VOICES } = await imp("src/encounter.ts");
const { buildCarryoverItems } = await imp("src/encounter-carryover.ts");
const { wordMatch } = await imp("src/answer-classifier.ts");
const { CASE_PLANS, CHECKLIST_LABELS } = await imp("src/catalog/cases.ts");
const { PROCEDURES_BY_ID } = await imp("src/catalog/procedures.ts");
const enc = await imp("src/catalog/encounters.ts");
const { ENCOUNTERS, ENCOUNTER_EXCLUSIONS, ENCOUNTERS_BY_PLAN, HISTORY_TOPICS, EXAM_MANEUVERS, TESTS, PATIENT_VOICE_KEYS } = enc;

type Json = any;
const SUBJECT_RE = /^[a-z0-9][a-z0-9_-]{0,79}$/; // app.ts ID_PATTERN
const CONTENT_RE = /^[a-z0-9-]{3,80}$/; // interview-content.ts: no underscores
const URGENCY = ["elective", "urgent", "emergency"];
const GAP_CODES = [
  "not_shared_medications", "not_shared_allergies", "not_shared_conditions", "not_shared_labs",
  "missing_medications", "missing_allergies", "missing_conditions", "missing_labs",
  "no_demographics", "source_unavailable", "uncoded_medication", "medication_status_unknown",
  "lab_no_value", "no_kidney_function", "identity_mismatch",
];
const AGE_FLAGS = new Set(["pediatric", "elderly", "incomplete_chart"]); // never "missed" in QA (qa-interview.test.ts)

const readJson = (p: string) => JSON.parse(readFileSync(p, "utf8"));
const scenarios = (): Json[] => readJson(join(FIXTURES, "scenarios.json")).data;

// The OR allowlist lives in C#; read it so the tier answer tracks the app.
function supportedProcedures(): string[] {
  const p = join(REPO, "apps/quest/Assets/Scalpal/Handoff/Runtime/HandoffRun.cs");
  if (!existsSync(p)) return ["open_appendectomy", "lap_appendectomy"];
  const line = readFileSync(p, "utf8").split("\n").find((l) => l.includes("bool Supported("));
  return line ? [...line.matchAll(/"([a-z0-9_]+)"/g)].map((m) => m[1]) : [];
}

// ---------- package io ----------
interface Pkg {
  dir: string;
  manifest: Json;
  patientMd: string;
  statusMd: string;
  interview: Json;
  dossier: string;
  fixture: Json | null;
  scenario: Json | null;
}
function readPkg(dir: string): Pkg {
  const abs = resolve(dir);
  const m = join(abs, "case.json");
  if (!existsSync(m)) throw new Error(`${m} not found`);
  const opt = (rel: string) => (existsSync(join(abs, rel)) ? readFileSync(join(abs, rel), "utf8") : "");
  const optJson = (rel: string) => (existsSync(join(abs, rel)) ? readJson(join(abs, rel)) : null);
  return {
    dir: abs,
    manifest: readJson(m),
    patientMd: opt("content/patient.md"),
    statusMd: opt("content/patient_status.md"),
    interview: optJson("content/interview.json"),
    dossier: opt("dossier.md"),
    fixture: optJson("fixture/record.json"),
    scenario: optJson("fixture/scenario.json"),
  };
}

function recordFor(pkg: Pkg): Json | null {
  if (pkg.fixture) return pkg.fixture;
  const p = join(FIXTURES, `${pkg.manifest.subject}.json`);
  return existsSync(p) ? readJson(p) : null;
}
function scenarioFor(pkg: Pkg): Json | null {
  return pkg.scenario ?? scenarios().find((s) => s.subject === pkg.manifest.subject) ?? null;
}

// ---------- in-memory merge, so repo validators see the package as if installed ----------
function withPackage<T>(pkg: Pkg, fn: () => T): T {
  const s = pkg.manifest.subject;
  const prevPlan = CASE_PLANS[s];
  const prevEnc = ENCOUNTERS_BY_PLAN.get(s);
  const prevIdx = ENCOUNTERS.findIndex((e: Json) => e.planSubject === s);
  const prevExcl = ENCOUNTER_EXCLUSIONS[s];
  CASE_PLANS[s] = pkg.manifest.plan;
  if (pkg.manifest.encounter) {
    if (prevIdx >= 0) ENCOUNTERS[prevIdx] = pkg.manifest.encounter;
    else ENCOUNTERS.push(pkg.manifest.encounter);
    ENCOUNTERS_BY_PLAN.set(s, pkg.manifest.encounter);
    delete ENCOUNTER_EXCLUSIONS[s];
  }
  try {
    return fn();
  } finally {
    if (prevPlan) CASE_PLANS[s] = prevPlan;
    else delete CASE_PLANS[s];
    if (pkg.manifest.encounter) {
      if (prevIdx >= 0) ENCOUNTERS[prevIdx] = prevEnc;
      else ENCOUNTERS.pop();
      if (prevEnc) ENCOUNTERS_BY_PLAN.set(s, prevEnc);
      else ENCOUNTERS_BY_PLAN.delete(s);
      if (prevExcl) ENCOUNTER_EXCLUSIONS[s] = prevExcl;
    }
  }
}

// ---------- helpers mirroring build-patient-files.ts / encounter.test.ts rules ----------
function forbiddenWords(e: Json): string[] {
  const known = Object.values(e.history ?? {}).join(" ").toLowerCase() + " " + String(e.persona?.opener ?? "").toLowerCase();
  const words = [
    ...(e.diagnosis?.keywords ?? []).flat(),
    ...(e.diagnosis?.partial?.keywords ?? []).flat(),
    ...(e.procedureKeywords ?? []).flat(),
    ...String(e.diagnosis?.label ?? "").toLowerCase().split(/[^a-z]+/).filter((w: string) => w.length > 8),
  ].map((w: string) => w.toLowerCase());
  const generic = new Set(["recurrent", "complicated", "chronic"]);
  return [...new Set(words)].filter((w) => !generic.has(w) && !known.includes(w));
}

// ---------- check ----------
interface Report { errors: string[]; warnings: string[]; info: string[] }

function check(pkg: Pkg): Report {
  const r: Report = { errors: [], warnings: [], info: [] };
  const err = (m: string) => r.errors.push(m);
  const warn = (m: string) => r.warnings.push(m);
  const info = (m: string) => r.info.push(m);
  const m = pkg.manifest;
  const s: string = m.subject;

  // 1. manifest shape
  if (m.schema !== SCHEMA) err(`case.json schema must be "${SCHEMA}"`);
  if (typeof s !== "string" || !SUBJECT_RE.test(s)) err(`subject "${s}" must match ${SUBJECT_RE}`);
  else if (!CONTENT_RE.test(s)) err(`subject "${s}" must also match ${CONTENT_RE} (content folders allow no underscores)`);
  if (s && !s.startsWith("patient-demo-")) warn(`subject "${s}" lacks the patient-demo- prefix, so FinchNode reads it as a sandbox subject (needs FINCHNODE_API_KEY)`);
  const plan = m.plan;
  if (!plan) { err("case.json needs a plan"); return r; }
  if (!m.encounter) err("case.json needs an encounter: without one the patient is hidden on the explore board and patients:build refuses to run");
  if (!pkg.interview) err("content/interview.json is missing: /interviews returns 404 no_interview and the Quest skips the office");
  if (!pkg.patientMd.trim()) err("content/patient.md is missing or empty: /interviews returns 404 no_interview");
  if (!pkg.statusMd.trim()) warn("content/patient_status.md is missing: Scalpal gets no patient context in the OR");

  // 2. FinchNode identity
  const record = recordFor(pkg);
  const scenario = scenarioFor(pkg);
  const repoScenario = scenarios().find((x) => x.subject === s);
  if (!record) err(`no chart for ${s}: add fixture/record.json, or use a subject with services/preop/test/fixtures/${s}.json`);
  if (!scenario) err(`no FinchNode scenario lists ${s}: add fixture/scenario.json or pick a subject from scenarios.json`);
  if (!repoScenario) warn(`${s} is not in the repo's FinchNode scenario list. Live /patients only lists what FinchNode serves, so this patient appears offline (bundle) only, and the Unity Shell gates that count patients will need updating`);
  if (record && record.id !== s) err(`fixture record id "${record.id}" must equal subject "${s}"`);
  if (record && record.synthetic === false) err("the chart is marked synthetic: false. Scalpal only plays synthetic patients");

  // 3. plan
  if (!PROCEDURES_BY_ID.has(plan.procedureId)) err(`plan.procedureId "${plan.procedureId}" is not a catalog procedure (${[...PROCEDURES_BY_ID.keys()].join(", ")})`);
  if (!URGENCY.includes(plan.urgency)) err(`plan.urgency must be one of ${URGENCY.join(", ")}`);
  for (const k of ["indication", "presentation"]) if (!String(plan[k] ?? "").trim()) err(`plan.${k} is required`);
  for (const n of plan.chartNotes ?? []) {
    for (const f of n.whenFlags ?? []) if (!(f in CHECKLIST_LABELS)) err(`chartNotes flag "${f}" is not a FlagType; it would silently never fire`);
    for (const g of n.whenGaps ?? []) if (!GAP_CODES.includes(g)) warn(`chartNotes gap "${g}" is not a known gap code; it would silently never fire`);
  }
  if (m.encounter && m.encounter.urgency !== plan.urgency) err(`encounter.urgency "${m.encounter.urgency}" must equal plan.urgency "${plan.urgency}"`);
  if (s in ENCOUNTER_EXCLUSIONS) info(`${s} is currently excluded ("${ENCOUNTER_EXCLUSIONS[s]}"); install removes the exclusion`);
  if (CASE_PLANS[s]) {
    const old = CASE_PLANS[s];
    info(`${s} already has a case (${old.procedureId}, ${old.urgency}); install replaces its plan, encounter and content`);
    if (old.procedureId !== plan.procedureId || old.urgency !== plan.urgency) {
      // Tests, scripts and Unity code that pin this subject's old case.
      const oldCase = `case_${s}_${old.procedureId}`;
      // Unity and dashboard code that names the old case id (vitest cannot see these; run `trial` for the TS tests).
      const res = spawnSync("git", ["grep", "-n", "-I", "-e", oldCase, "--", "apps", "services/preop/scripts", ":!*.json", ":!*.meta"], { cwd: REPO, encoding: "utf8" });
      const hits = (res.stdout ?? "").split("\n").filter(Boolean);
      if (hits.length) warn(`code outside the test suite names the old case id ${oldCase}:\n        ${hits.map((h) => h.slice(0, 160)).join("\n        ")}`);
      warn(`this re-plans ${s} from ${old.procedureId} (${old.urgency}); tests written for the old case may break. Run \`trial\` to find out exactly which`);
    }
    if (!pkg.dossier && existsSync(join(REPO, "docs", "patients", `${s}.md`)) && old.procedureId !== plan.procedureId) warn(`docs/patients/${s}.md describes the old ${old.procedureId} case and install will leave it; include a rewritten dossier.md or delete that file`);
  }
  for (const k of ["objectives", "sources"]) if (!Array.isArray(m[k]) || !m[k].length) warn(`case.json has no "${k}" list (see references/package-format.md); clinical.md asks for both`);

  // 4. the repo's catalog validator, with the package merged in
  let kase: Json = null;
  withPackage(pkg, () => {
    for (const e of validateCatalog()) err(`validateCatalog: ${e}`);
    if (record) {
      try {
        kase = buildCase(record, scenario?.id ?? "", NOW, s);
      } catch (e) {
        err(`buildCase failed: ${(e as Error).message}`);
      }
    }
  });

  // 5. the built case
  if (kase) {
    info(`chart: ${kase.patient.displayLabel} | status ${kase.status} | flags ${kase.brief.flags.map((f: Json) => `${f.type}(${f.severity})`).join(", ") || "none"} | gaps ${kase.brief.dataGaps.map((g: Json) => g.code).join(", ") || "none"}`);
    if (!["ready", "needs_review"].includes(kase.status)) err(`case status is "${kase.status}": the explore board only lets ready or needs_review patients begin`);
    if (!kase.brief.synthetic) err("brief.synthetic is false: the Quest refuses non-synthetic charts");
    if (/^(Unnamed|Unavailable)/.test(kase.patient.displayLabel)) warn("the chart has no name, so the Quest explore board hides this patient");
    const supported = supportedProcedures();
    if (supported.includes(kase.procedureId)) info(`tier 1: ${kase.procedureId} is playable end to end in the headset`);
    else warn(`tier 2: ${kase.procedureId} is not in HandoffRun.Supported (${supported.join(", ")}). The interview plays; the OR shows "Surgery coming soon" until the Unity work in references/procedures.md lands`);
    const pins = kase.considerations.map((c: Json) => `${c.flagId.replace(/^flag_/, "")} -> ${c.stepId}`);
    info(`considerations (chart risk -> OR step, for patient_status.md): ${pins.join(", ") || "none"}`);
    const steps = new Set(kase.procedure.steps.map((x: Json) => x.id));
    const allSteps = new Set([...PROCEDURES_BY_ID.values()].flatMap((p: Json) => p.steps.map((x: Json) => x.id)));
    for (const id of new Set([...pkg.statusMd.matchAll(/`([a-z][a-z0-9_]+)`/g)].map((x) => x[1]))) {
      if (allSteps.has(id) && !steps.has(id)) err(`patient_status.md names step "${id}", which is not a step of ${kase.procedureId}`);
    }
    if (m.encounter && !demographicsMatch(m.encounter, kase)) err(`persona does not match the chart: chart is ${kase.patient.name || "(no name)"}, ${kase.patient.age}, ${kase.patient.sex || "(no sex)"}; persona says ${m.encounter.persona.patientName}, ${m.encounter.persona.age}, ${m.encounter.persona.sex}`);
  }

  // 6. encounter extras (encounter.test.ts rules the catalog validator does not cover)
  const e = m.encounter;
  if (e && kase) {
    const chartWords = [
      ...kase.brief.chart.filter((l: Json) => ["Problems", "Medications", "Allergies"].includes(l.section)).flatMap((l: Json) => l.text.toLowerCase().split(/[^a-z]+/)),
    ].filter((w: string) => w.length > 5);
    const character = String(e.persona.character ?? "").toLowerCase();
    for (const w of new Set(chartWords)) if (character.includes(w)) err(`persona.character mentions chart word "${w}": character is personality only; facts come through history`);
    const factText = [...Object.values(e.history ?? {}), ...Object.values(e.exam ?? {}).flatMap((x: Json) => [x.reaction, x.finding]), ...Object.values(e.tests ?? {}).map((x: Json) => x.result)].join(" ").toLowerCase().split(/\s+/);
    for (let i = 0; i + 5 <= factText.length; i++) {
      const phrase = factText.slice(i, i + 5).join(" ");
      if (phrase.length > 20 && character.includes(phrase)) { err(`persona.character copies a 5-word fact phrase: "${phrase}"`); break; }
    }
    // encounter.test.ts: saying the opener or chief complaint as a diagnosis must not score it
    for (const said of [e.persona.opener, e.history?.chief_complaint ?? ""]) {
      const t = String(said).toLowerCase();
      const groups: string[][] = e.diagnosis?.keywords ?? [];
      if (groups.length && groups.every((g) => g.some((k) => t.includes(k)))) err(`"${said}" would score as the diagnosis: the opener and chief_complaint must not hand it over`);
    }
    // encounter.test.ts: every charted allergy and active drug appears in the authored answers (same aliases)
    const chartLines = (section: string) => kase.brief.chart.filter((l: Json) => l.section === section).map((l: Json) => l.text.toLowerCase());
    if (e.history?.allergies) {
      const said = e.history.allergies.toLowerCase();
      const aliases: Record<string, string> = { sulfonamide: "sulfa", contrast: "dye", media: "dye" };
      for (const line of chartLines("Allergies")) {
        const ws = line.replace(/^allergy to /, "").replace(/ allergy/, "").replace(/\s*\(.*\)$/, "").split(" ").filter((w: string) => w.length > 3).map((w: string) => aliases[w] ?? w);
        if (!ws.some((w: string) => said.includes(w))) err(`history.allergies must mention the charted allergy "${line}"`);
      }
    }
    if (e.history?.medications) {
      const said = e.history.medications.toLowerCase();
      const aliases: Record<string, string> = { acetaminophen: "tylenol", cholecalciferol: "vitamin d", levothyroxine: "thyroid", pressure: "hydrochlorothiazide" };
      for (const line of chartLines("Medications")) {
        const ws = line.split(/[^a-z]+/).filter((w: string) => w.length > 4).map((w: string) => aliases[w] ?? w);
        if (!ws.some((w: string) => said.includes(w))) err(`history.medications must mention the charted drug "${line}"`);
      }
    }
    if (e.persona.voiceKey && !PATIENT_VOICE_KEYS.includes(e.persona.voiceKey)) err(`voiceKey "${e.persona.voiceKey}" is not one of ${PATIENT_VOICE_KEYS.join(", ")}`);
    const voice = DEFAULT_PATIENT_VOICES[e.persona.voiceKey];
    const before = ENCOUNTERS.find((x: Json) => x.planSubject === s);
    const sharing = ENCOUNTERS.filter((x: Json) => x.planSubject !== s && DEFAULT_PATIENT_VOICES[x.persona.voiceKey] === voice).map((x: Json) => x.planSubject);
    const changed = !before || DEFAULT_PATIENT_VOICES[before.persona.voiceKey] !== voice;
    if (sharing.length && changed) err(`voice ${e.persona.voiceKey} is already used by ${sharing.join(", ")}. encounter.test.ts pins the exact shared-voice groups: add a new voice key (PATIENT_VOICE_KEYS + DEFAULT_PATIENT_VOICES) or update that test`);
  }

  // 7. interview content
  const iv = pkg.interview;
  if (iv && e) {
    for (const p of validateInterview(iv)) err(`validateInterview: ${p}`);
    if (iv.patientId !== s) err(`interview.patientId "${iv.patientId}" must equal "${s}"`);
    if (kase && iv.procedureId !== kase.procedureId) err(`interview.procedureId "${iv.procedureId}" must equal the case procedure "${kase.procedureId}"`);
    const valid = new Set([...HISTORY_TOPICS.map((x: string) => `history:${x}`), ...EXAM_MANEUVERS.map((x: string) => `exam:${x}`), ...TESTS.map((x: string) => `test:${x}`)]);
    const history = new Set<string>();
    const tests = new Set<string>();
    const unclear: string[] = [];
    const longest: string[] = [];
    const letterCount: Record<string, number> = {};
    const plain = (t: string) => String(t ?? "").toLowerCase().replace(/[^a-z0-9]+/g, " ").trim();
    for (const round of iv.rounds ?? []) {
      const correct = (round.choices ?? []).find((c: Json) => c.grade === "correct");
      for (const c of round.choices ?? []) {
        for (const cv of c.covers ?? []) if (!valid.has(cv)) err(`round ${round.id} choice ${c.key}: covers "${cv}" is not a real topic, maneuver or test id`);
      }
      if (correct && (round.stage === "exam" || round.stage === "tests") && !correct.finding) err(`round ${round.id}: the correct ${round.stage} pick needs a finding (the result shown on screen)`);
      for (const cv of correct?.covers ?? []) {
        if (cv.startsWith("history:")) history.add(cv.slice(8));
        if (cv.startsWith("test:")) tests.add(cv.slice(5));
      }
      // Without a model classifier, a learner reading a card aloud must land on that card (answer-classifier.ts wordMatch).
      for (const c of round.choices ?? []) if (wordMatch(String(c.text ?? ""), round.choices) !== c.key) unclear.push(`${round.id}/${c.key}`);
      if (correct) {
        letterCount[correct.key] = (letterCount[correct.key] ?? 0) + 1;
        const lens = (round.choices ?? []).map((c: Json) => String(c.text ?? "").length);
        if (String(correct.text).length === Math.max(...lens) && lens.filter((x: number) => x === Math.max(...lens)).length === 1) longest.push(round.id);
      }
      // findings should carry the encounter's authored text for what they cover
      for (const c of round.choices ?? []) {
        if (!c.finding) continue;
        for (const cv of c.covers ?? []) {
          const [kind, id] = cv.split(":");
          const want = kind === "exam" ? e.exam?.[id]?.finding : kind === "test" ? e.tests?.[id]?.result : null;
          if (want && !plain(c.finding.text).includes(plain(want))) warn(`round ${round.id} choice ${c.key}: finding does not contain the encounter's ${cv} text verbatim`);
        }
      }
    }
    if (unclear.length) warn(`without ANTHROPIC_API_KEY (word-match fallback), these choices read aloud would not classify: ${unclear.join(", ")}. The fallback needs at least 2 distinctive content words shared with the spoken answer and a clear winner over the other cards. Fine with the Claude classifier`);
    if (longest.length) warn(`NBME: the correct choice is the longest option in rounds ${longest.join(", ")}; that cues test-wise learners`);
    for (const [k, n] of Object.entries(letterCount)) if (n > 3) warn(`NBME: ${n} rounds use ${k} as the correct letter; keep each letter to three or fewer`);
    if (kase) {
      const items = buildCarryoverItems(kase, [...history], [...tests]);
      for (const it of items) {
        if (it.status !== "missed" || AGE_FLAGS.has(it.type)) continue;
        // Probe the repo's coverage rules: which single extra cover would mark this risk found?
        const fixes = [
          ...HISTORY_TOPICS.filter((t: string) => buildCarryoverItems(kase, [...history, t], [...tests]).find((x: Json) => x.flagId === it.flagId)?.status === "found").map((t: string) => `history:${t}`),
          ...TESTS.filter((t: string) => buildCarryoverItems(kase, [...history], [...tests, t]).find((x: Json) => x.flagId === it.flagId)?.status === "found").map((t: string) => `test:${t}`),
        ];
        err(`chart risk "${it.type}" (${it.label}) is not covered by any correct pick; add ${fixes.length ? fixes.join(" or ") : "the covers in references/office.md (this risk needs more than one)"} to a correct choice`);
      }
    }
    if (e.diagnosis?.partial && !(iv.rounds ?? []).find((x: Json) => x.stage === "diagnosis")?.choices?.some((c: Json) => c.grade === "partial")) warn("the encounter has a partial diagnosis but the diagnosis round has no partial choice");
    if (iv.openingLine !== e.persona.opener) info("interview.openingLine differs from encounter.persona.opener (fine, but offline speech clips key on the opener)");
  }
  if (e && pkg.patientMd) {
    const md = pkg.patientMd.toLowerCase();
    for (const w of forbiddenWords(e)) if (md.includes(w)) err(`patient.md contains "${w}", a diagnosis or procedure word the patient must not know`);
  }
  for (const [name, text] of [["patient.md", pkg.patientMd], ["patient_status.md", pkg.statusMd], ["interview.json", JSON.stringify(iv ?? "")], ["case.json", JSON.stringify(m)]]) {
    if (/[–—]/.test(text as string)) warn(`${name} contains an em or en dash; house style uses commas, periods, colons or parentheses`);
  }

  // 8. repo lists that do not update themselves
  const qa = readFileSync(join(PREOP, "test", "qa-interview.test.ts"), "utf8");
  if (!qa.includes(`"${s}"`)) warn(`${s} is not in test/qa-interview.test.ts PATIENTS, so the QA suite will not check it (install --write adds it)`);
  if (m.procedure) warn("case.json carries a procedure block: a new procedure is engineering work. See references/procedures.md; install does not write procedures");
  return r;
}

// ---------- export ----------
function exportCase(subject: string, out: string) {
  const plan = CASE_PLANS[subject];
  if (!plan) throw new Error(`${subject} has no case plan. Known: ${Object.keys(CASE_PLANS).join(", ")}`);
  const encounter = ENCOUNTERS_BY_PLAN.get(subject);
  const sc = scenarios().find((x) => x.subject === subject);
  const dir = resolve(out);
  mkdirSync(join(dir, "content"), { recursive: true });
  const manifest = {
    schema: SCHEMA,
    subject,
    scenarioId: sc?.id ?? "",
    title: `${encounter?.persona.patientName ?? subject}: ${plan.indication}`,
    notes: "Exported from the repo as a starting template. Edit, then run check.",
    plan,
    encounter: encounter ?? null,
  };
  writeFileSync(join(dir, "case.json"), JSON.stringify(manifest, null, 2) + "\n");
  for (const f of ["patient.md", "patient_status.md", "interview.json"]) {
    const src = join(CONTENT, subject, f);
    if (existsSync(src)) cpSync(src, join(dir, "content", f));
  }
  const dossier = join(REPO, "docs", "patients", `${subject}.md`);
  if (existsSync(dossier)) cpSync(dossier, join(dir, "dossier.md"));
  console.log(`exported ${subject} -> ${dir}`);
}

// ---------- TS source editing (brace matching that skips strings and comments) ----------
function matchClose(src: string, open: number): number {
  const pair: Record<string, string> = { "{": "}", "[": "]", "(": ")" };
  const stack: string[] = [];
  for (let i = open; i < src.length; i++) {
    const c = src[i];
    if (c === '"' || c === "'" || c === "`") {
      for (i++; i < src.length && src[i] !== c; i++) if (src[i] === "\\") i++;
      continue;
    }
    if (c === "/" && src[i + 1] === "/") { i = src.indexOf("\n", i); if (i < 0) return -1; continue; }
    if (c === "/" && src[i + 1] === "*") { i = src.indexOf("*/", i) + 1; continue; }
    if (pair[c]) stack.push(pair[c]);
    else if (c === "}" || c === "]" || c === ")") {
      if (stack.pop() !== c) throw new Error(`unbalanced ${c} at ${i}`);
      if (!stack.length) return i;
    }
  }
  return -1;
}
const indent = (text: string, pad: string) => text.split("\n").map((l, i) => (i === 0 ? l : pad + l)).join("\n");
const tsValue = (v: unknown, pad: string) => indent(JSON.stringify(v, null, 2), pad);

function upsertCasePlan(src: string, subject: string, plan: Json): string {
  const decl = src.indexOf("export const CASE_PLANS");
  const open = src.indexOf("{", src.indexOf("=", decl));
  const close = matchClose(src, open);
  const body = src.slice(open, close);
  const keyAt = body.indexOf(`\n  "${subject}": {`);
  const entry = `  "${subject}": ${tsValue(plan, "  ")},`;
  if (keyAt >= 0) {
    const start = open + keyAt + 1;
    const objOpen = src.indexOf("{", start);
    let end = matchClose(src, objOpen) + 1;
    if (src[end] === ",") end++;
    return src.slice(0, start) + entry + src.slice(end);
  }
  return src.slice(0, close) + entry + "\n" + src.slice(close);
}

function upsertEncounter(src: string, encounter: Json): string {
  const decl = src.indexOf("export const ENCOUNTERS: Encounter[]");
  const open = src.indexOf("[", src.indexOf("=", decl));
  const close = matchClose(src, open);
  const entry = `  ${tsValue(encounter, "  ")},`;
  // find an existing top-level element whose planSubject matches
  for (let i = open + 1; i < close; ) {
    const objOpen = src.indexOf("{", i);
    if (objOpen < 0 || objOpen > close) break;
    const objClose = matchClose(src, objOpen);
    const chunk = src.slice(objOpen, objClose);
    if (new RegExp(`planSubject:\\s*"${encounter.planSubject}"`).test(chunk) || chunk.includes(`"planSubject": "${encounter.planSubject}"`)) {
      const lineStart = src.lastIndexOf("\n", objOpen) + 1;
      let end = objClose + 1;
      if (src[end] === ",") end++;
      return src.slice(0, lineStart) + entry + src.slice(end);
    }
    i = objClose + 1;
  }
  return src.slice(0, close) + entry + "\n" + src.slice(close);
}

function removeExclusion(src: string, subject: string): string {
  return src.replace(new RegExp(`\\n  "${subject}": "[^"\\n]*",?`), "");
}

function addQaPatient(src: string, subject: string): string {
  if (src.includes(`"${subject}"`)) return src;
  return src.replace(/const PATIENTS = \[\n([\s\S]*?)\] as const;/, (all, list) => `const PATIENTS = [\n${list}  "${subject}",\n] as const;`);
}

function install(pkg: Pkg, write: boolean, force: boolean) {
  const report = check(pkg);
  if (report.errors.length) {
    printReport(report);
    if (!force) throw new Error("install refused: fix the errors above first (--force only when you are also making the code change an error asks for)");
    console.log("--force: installing despite the errors above\n");
  }
  const s = pkg.manifest.subject;
  const casesPath = join(PREOP, "src/catalog/cases.ts");
  const encPath = join(PREOP, "src/catalog/encounters.ts");
  const qaPath = join(PREOP, "test/qa-interview.test.ts");
  const plan: [string, string][] = [];
  const casesSrc = upsertCasePlan(readFileSync(casesPath, "utf8"), s, pkg.manifest.plan);
  let encSrc = upsertEncounter(readFileSync(encPath, "utf8"), pkg.manifest.encounter);
  encSrc = removeExclusion(encSrc, s);
  const qaSrc = addQaPatient(readFileSync(qaPath, "utf8"), s);
  plan.push([casesPath, casesSrc], [encPath, encSrc], [qaPath, qaSrc]);
  const content = join(CONTENT, s);
  const files: [string, string][] = [
    [join(content, "patient.md"), pkg.patientMd],
    [join(content, "patient_status.md"), pkg.statusMd],
    [join(content, "interview.json"), JSON.stringify(pkg.interview, null, 2) + "\n"],
  ];
  if (pkg.dossier) files.push([join(REPO, "docs/patients", `${s}.md`), pkg.dossier]);
  if (pkg.fixture) files.push([join(FIXTURES, `${s}.json`), JSON.stringify(pkg.fixture, null, 2) + "\n"]);
  if (pkg.scenario && !scenarios().some((x) => x.subject === s)) {
    const sc = readJson(join(FIXTURES, "scenarios.json"));
    sc.data.push(pkg.scenario);
    files.push([join(FIXTURES, "scenarios.json"), JSON.stringify(sc, null, 2) + "\n"]);
  }
  const changes = [...plan, ...files].filter(([p, text]) => !existsSync(p) || readFileSync(p, "utf8") !== text);
  for (const [p] of changes) console.log(`${write ? "write" : "would write"} ${p.replace(REPO + "/", "")}`);
  if (!changes.length) console.log("nothing to change: the repo already matches this package");
  if (!write) {
    console.log("\nDry run. Re-run with --write to apply, then run: gates");
    return;
  }
  for (const [p, text] of changes) {
    mkdirSync(dirname(p), { recursive: true });
    writeFileSync(p, text);
  }
  console.log(`\ninstalled ${s}. Next: npx tsx ${relScript()} gates`);
}

// ---------- trial: install into a throwaway worktree and run the test suite there ----------
function trial(dir: string) {
  const pkgDir = resolve(dir);
  const wt = join(PREOP, "node_modules", ".scalpal-case-trial");
  spawnSync("git", ["worktree", "remove", "--force", wt], { cwd: REPO });
  const add = spawnSync("git", ["worktree", "add", "-q", "--detach", wt, "HEAD"], { cwd: REPO, encoding: "utf8" });
  if (add.status !== 0) throw new Error(`git worktree add failed: ${add.stderr}`);
  try {
    // Carry uncommitted catalog/content/test state so the trial matches the working tree.
    const diff = spawnSync("git", ["diff", "HEAD", "--binary", "--", "services/preop"], { cwd: REPO, encoding: "utf8", maxBuffer: 64 << 20 });
    if (diff.stdout) spawnSync("git", ["apply", "--whitespace=nowarn"], { cwd: wt, input: diff.stdout });
    spawnSync("ln", ["-s", join(PREOP, "node_modules"), join(wt, "services", "preop", "node_modules")]);
    const script = fileURLToPath(import.meta.url);
    const inst = spawnSync("node", ["--import", "tsx", script, "install", pkgDir, "--write", "--force"], { cwd: join(wt, "services", "preop"), encoding: "utf8" });
    if (inst.status !== 0) throw new Error(`install in the trial worktree failed:\n${inst.stdout}${inst.stderr}`);
    const test = spawnSync("npx", ["vitest", "run"], { cwd: join(wt, "services", "preop"), encoding: "utf8", maxBuffer: 64 << 20 });
    const out = (test.stdout + test.stderr).split("\n");
    const failures = [...new Set(out.filter((l) => /^\s*(FAIL|×)/.test(l)).map((l) => l.trim()))];
    const summary = out.find((l) => /^\s*Tests\s/.test(l))?.trim() ?? "";
    console.log(`trial install of ${pkgDir} into a throwaway worktree: ${summary}`);
    for (const f of failures) console.log(`  ${f}`);
    if (failures.length) {
      console.log("\nEach failure is a test pinned to the old case or a rule the package breaks. Update the test with the case (tests are part of the change), or fix the package.");
      process.exitCode = 1;
    } else console.log("all preop tests pass with this package installed");
  } finally {
    spawnSync("git", ["worktree", "remove", "--force", wt], { cwd: REPO });
    spawnSync("git", ["worktree", "prune"], { cwd: REPO });
  }
}

// ---------- gates ----------
function gates(skipUnity: boolean) {
  const steps: [string, string][] = [
    ["catalog", "npm run -s validate"],
    ["typecheck", "npm run -s typecheck"],
    ["tests", "npx vitest run"],
    ["unity ids", "npm run -s gen:unity"],
    ["offline bundle", "npm run -s export:unity -- --offline"],
    ["tests after export", "npx vitest run test/unity-contract.test.ts test/anatomy-atlas.test.ts"],
  ];
  const hasDotnet = spawnSync("dotnet", ["--version"]).status === 0;
  if (!skipUnity && hasDotnet) steps.push(["C# playthrough", "npm run -s test:unity"]);
  let failed = 0;
  for (const [name, cmd] of steps) {
    const res = spawnSync(cmd, { cwd: PREOP, shell: true, encoding: "utf8" });
    const ok = res.status === 0;
    if (!ok) failed++;
    const out = (res.stdout + res.stderr).trim().split("\n");
    const failing = out.filter((l) => /FAIL|×|ERROR|Error:/.test(l)).slice(0, 8);
    const tail = (failing.length ? failing : out.slice(-4)).map((l) => l.trim()).join("\n    ");
    console.log(`${ok ? "PASS" : "FAIL"}  ${name}  (${cmd})${ok ? "" : "\n    " + tail}`);
  }
  if (!hasDotnet && !skipUnity) console.log("SKIP  C# playthrough (dotnet not installed; npm run test:unity needs the .NET 10 SDK)");
  console.log("\nNot automatable here: Unity editor validations and a headset run (see references/integration.md).");
  if (failed) process.exitCode = 1;
}

// ---------- list / chart ----------
function list() {
  const supported = supportedProcedures();
  for (const sc of scenarios()) {
    if (!sc.subject) continue;
    const plan = CASE_PLANS[sc.subject];
    const hasContent = existsSync(join(CONTENT, sc.subject, "interview.json"));
    const tier = plan ? (supported.includes(plan.procedureId) ? "headset" : "interview only") : "no plan";
    console.log(`${sc.subject.padEnd(36)} ${(plan?.procedureId ?? "-").padEnd(22)} ${tier.padEnd(15)} encounter:${ENCOUNTERS_BY_PLAN.has(sc.subject) ? "yes" : "no "} content:${hasContent ? "yes" : "no "}  ${sc.title}`);
  }
}
function chart(subject: string) {
  const p = join(FIXTURES, `${subject}.json`);
  if (!existsSync(p)) throw new Error(`no fixture ${p}`);
  const sc = scenarios().find((x) => x.subject === subject);
  const kase = buildCase(readJson(p), sc?.id ?? "", NOW, subject);
  const b = kase.brief;
  console.log(`${b.patient.displayLabel} (age ${b.patient.age} at ${b.dataAsOf || "now"}, sex ${b.patient.sex || "unknown"})`);
  console.log(`repo's current plan (not your package): ${kase.procedureId} (${kase.urgency}) ${kase.indication}`);
  for (const l of b.chart) console.log(`  ${l.flagged ? "!" : " "} ${l.section}: ${l.text}`);
  console.log("flags:");
  for (const f of b.flags) console.log(`  ${f.type} (${f.severity}): ${f.title}`);
  console.log("gaps:");
  for (const g of b.dataGaps) console.log(`  ${g.code}: ${g.message}`);
}

// ---------- cli ----------
const relScript = () => relative(PREOP, fileURLToPath(import.meta.url));
function printReport(r: Report) {
  for (const i of r.info) console.log(`info  ${i}`);
  for (const w of r.warnings) console.log(`warn  ${w}`);
  for (const e of r.errors) console.log(`ERROR ${e}`);
  console.log(`\n${r.errors.length} error(s), ${r.warnings.length} warning(s)`);
}

const [cmd, ...args] = process.argv.slice(2);
try {
  if (cmd === "list") list();
  else if (cmd === "chart" && args[0]) chart(args[0]);
  else if (cmd === "export" && args[0] && args[1]) exportCase(args[0], args[1]);
  else if (cmd === "check" && args[0]) {
    const r = check(readPkg(args[0]));
    printReport(r);
    if (r.errors.length) process.exitCode = 1;
  } else if (cmd === "install" && args[0]) install(readPkg(args[0]), args.includes("--write"), args.includes("--force"));
  else if (cmd === "trial" && args[0]) trial(args[0]);
  else if (cmd === "gates") gates(args.includes("--skip-unity"));
  else {
    console.log("usage: scalpal-case.mts list | chart <subject> | export <subject> <dir> | check <dir> | trial <dir> | install <dir> [--write] [--force] | gates [--skip-unity]");
    process.exitCode = 2;
  }
} catch (e) {
  console.error(`error: ${(e as Error).message}`);
  process.exitCode = 1;
}
