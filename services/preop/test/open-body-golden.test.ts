import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import type { BodyAction } from '../src/open-body.js';
import { PROCEDURES_BY_ID } from '../src/catalog/procedures.js';
import { StepEngine } from '../src/engine.js';

// The same file is replayed through the Unity reducer by OpenBodyValidation.VerifyGoldenLog. Any
// semantic change to either reducer must regenerate this log and keep both engines identical.
interface Golden {
  procedureId: string; actions: BodyAction[];
  expected: { outcomes: string[]; facts: { key: string; value: number }[]; milestones: string[]; mistakes: string[]; completed: boolean; currentStep: string };
}
const golden = JSON.parse(readFileSync(new URL('./fixtures/open-body-golden.json', import.meta.url), 'utf8')) as Golden;
function replay() {
  const engine = new StepEngine(PROCEDURES_BY_ID.get(golden.procedureId)!);
  const outcomes = golden.actions.map(a => { engine.handle({ type: 'surgery', evidence: a }); const r = engine.body!.log.at(-1)!; return r.action.actionId === a.actionId ? r.outcomes.join(',') : 'REJECTED'; });
  return { engine, outcomes };
}
describe('shared golden open-body log', () => {
  it('replays to the frozen facts, outcomes, milestones and guardrails', () => {
    const { engine, outcomes } = replay();
    expect(outcomes).toEqual(golden.expected.outcomes);
    expect([...engine.body!.facts].map(([key, value]) => ({ key, value })).sort((x, y) => x.key < y.key ? -1 : 1)).toEqual(golden.expected.facts);
    expect([...engine.completedMilestones].sort()).toEqual(golden.expected.milestones);
    expect(engine.mistakes.map(m => m.id)).toEqual(golden.expected.mistakes);
    expect(engine.completed).toBe(golden.expected.completed);
    expect(engine.current?.id ?? '').toBe(golden.expected.currentStep);
  });
  it('pins the boundary cases the log exists for', () => {
    const { engine } = replay(), body = engine.body!;
    expect(body.get('skin', 'cutCoverage')).toBe(0.8); // 48/60: float thresholds in C# once made this unmet.
    expect(engine.completedMilestones.has('incise_skin')).toBe(true);
    expect(engine.mistakes.filter(m => m.id === 'rough_handling')).toHaveLength(2); // Cooldown: 3 fast reports over 3.2 s.
    expect(body.get('appendix', 'decision_appendix_tip')).toBe(0); // Latest answer wins.
    expect(body.get('mesoappendix', 'clampCount')).toBe(0);
    expect(engine.completedMilestones.has('divide_mesoappendix')).toBe(true);
    expect(body.get('appendicular_artery', 'measuredLossMl')).toBe(0.23);
  });
});
