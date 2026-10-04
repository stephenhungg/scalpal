import { readFileSync } from 'node:fs';
import { EncounterSession } from '../../../services/preop/src/encounter.ts';
import { buildCase } from '../../../services/preop/src/case-builder.ts';
import { ENCOUNTERS_BY_PLAN } from '../../../services/preop/src/catalog/encounters.ts';
const subject = 'patient-demo-multi-source';
export function producedScorecard() {
  const chart = JSON.parse(readFileSync(new URL(`../../../services/preop/test/fixtures/${subject}.json`, import.meta.url), 'utf8'));
  const encounter = ENCOUNTERS_BY_PLAN.get(subject)!;
  const session = new EncounterSession('recap-contract-encounter', buildCase(chart, '', new Date('2026-10-03T12:00:00Z')), encounter);
  session.answer('onset'); session.answer('medications'); session.examine('abdomen_palpation');
  return session.score();
}

import { StepEngine, perfectEvents } from '../../../services/preop/src/engine.ts';
import { bodyAction } from '../../../services/preop/src/open-body.ts';
import { PROCEDURES_BY_ID } from '../../../services/preop/src/catalog/procedures.ts';
// Same authored incomplete-attempt route exercised by preop's open-body-grade.test.ts.
export function producedBodyGrade() {
  const procedure = PROCEDURES_BY_ID.get('open_appendectomy')!;
  const engine = new StepEngine(procedure);
  for (const step of procedure.steps.slice(0, 3)) for (const event of perfectEvents(step)) engine.handle(event);
  engine.handle({ type: 'surgery', evidence: bodyAction('cut', 'muscle', { actionId: 'harm', lengthMm: 4, timeMs: 1000 }) });
  engine.handle({ type: 'finish' });
  if (!engine.grade) throw new Error('Real open-body grader did not produce a completed record.');
  return engine.grade;
}
