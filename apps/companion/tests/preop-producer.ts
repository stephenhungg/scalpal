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
