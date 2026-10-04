import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { validateInterview, type PatientInterview } from "./interview-types.js";

// Committed, AI-augmented patient content (content/patients/<subjectId>/), built by
// scripts/build-patient-files.ts from FinchNode demo data, Stephen's dossiers and the authored encounter:
//   patient.md         powers the voice agent that embodies the patient
//   patient_status.md  clinical context Scalpal carries into the operating room
//   interview.json     the fixed choice-based interview (interview-types.ts)
// The committed files are the source of truth; nothing is generated at request time.

export interface PatientContent {
  subjectId: string;
  patientMd: string;
  statusMd: string;
  interview: PatientInterview | null;
}

const ROOT = fileURLToPath(new URL("../content/patients/", import.meta.url));
const SUBJECT = /^[a-z0-9-]{3,80}$/;
const cache = new Map<string, PatientContent | null>();

export function loadPatientContent(subjectId: string, root: string = ROOT): PatientContent | null {
  const key = `${root}|${subjectId}`;
  if (cache.has(key)) return cache.get(key)!;
  let content: PatientContent | null = null;
  const dir = join(root, subjectId);
  if (SUBJECT.test(subjectId) && existsSync(dir)) {
    const read = (f: string) => (existsSync(join(dir, f)) ? readFileSync(join(dir, f), "utf8") : "");
    const raw = read("interview.json");
    const interview = raw ? (JSON.parse(raw) as PatientInterview) : null;
    const problems = interview ? validateInterview(interview) : [];
    if (problems.length) throw new Error(`content/patients/${subjectId}/interview.json is invalid: ${problems.join("; ")}`);
    content = { subjectId, patientMd: read("patient.md"), statusMd: read("patient_status.md"), interview };
  }
  if (content) cache.set(key, content); // a patient whose files appear later is picked up without a restart
  return content;
}

// Scalpal's overarching patient context for the operating room, when the patient has authored content.
export function patientStatusFor(subjectId: string, root: string = ROOT): string {
  try {
    return loadPatientContent(subjectId, root)?.statusMd.trim() ?? "";
  } catch {
    return "";
  }
}
