import type { BodyState, OpenBodyCase } from './open-body.js';
import type { StepMistake } from './types.js';

export interface BodyGrade {
  readonly rubric: 'illustrative_v1_uncalibrated';
  readonly reason: 'goals_reached' | 'learner_finished';
  readonly complete: boolean;
  readonly metMilestones: readonly string[];
  readonly missingMilestones: readonly string[];
  readonly guardrailIds: readonly string[];
  readonly correctDecisions: number;
  readonly decisionCount: number;
  readonly safetyPoints: number;
  readonly decisionPoints: number;
  readonly tissuePoints: number;
  readonly economyPoints: -1;
  readonly economyMeasured: false;
  readonly earnedPoints: number;
  readonly availablePoints: 80;
  readonly unscoredEconomyWeight: 20;
  readonly actionCount: number;
  readonly durationMs: number;
  readonly bloodLostMl: number;
  readonly activeBleeds: number;
  readonly contamination: boolean;
  readonly missingMetrics: readonly string[];
}
// Authored teaching rubric, not calibrated proficiency or clinical competence. Economy is
// intentionally unscored: timestamps/action counts do not establish per-hand path efficiency.
export function gradeBody(plan: OpenBodyCase, body: BodyState, mistakes: readonly StepMistake[], reason: BodyGrade['reason']): BodyGrade {
  const met = plan.milestones.filter(m => m.predicates.every(p => body.test(p))).map(m => m.id);
  const missing = plan.milestones.filter(m => !met.includes(m.id)).map(m => m.id);
  const correct = plan.decisions.filter(d => [...body.log].reverse().find(r => r.action.verb === 'decide' && d.choices.includes(r.action.choice))?.action.choice === d.correctChoice).length;
  const blood = body.get('', 'bloodLostMl'), active = body.get('', 'activeBleeds'), contaminated = body.get('', 'contamination') > 0;
  const penalties = mistakes.reduce((n, m) => n + (m.severity === 'high' ? 10 : m.severity === 'moderate' ? 5 : 2), 0);
  const safety = Math.max(0, 50 - penalties - Math.min(10, blood / 10) - (active > 0 ? 5 : 0) - (contaminated ? 5 : 0));
  const decisions = plan.decisions.length ? 20 * correct / plan.decisions.length : 20;
  const tissue = Math.max(0, 10 - body.log.reduce((n,r) => n + r.outcomes.reduce((p,o) => p + (o === 'muscle_cut' ? 5 : o === 'rough_handling' || o === 'across_fibers' ? 1 : 0), 0), 0));
  return Object.freeze({rubric:'illustrative_v1_uncalibrated', reason, complete:missing.length === 0,
    metMilestones:Object.freeze(met), missingMilestones:Object.freeze(missing), guardrailIds:Object.freeze(mistakes.map(m => m.id)),
    correctDecisions:correct, decisionCount:plan.decisions.length, safetyPoints:safety, decisionPoints:decisions,
    tissuePoints:tissue, economyPoints:-1, economyMeasured:false, earnedPoints:safety + decisions + tissue, availablePoints:80,
    unscoredEconomyWeight:20, actionCount:body.log.length,
    durationMs:body.log.length ? body.log[body.log.length-1]!.action.timeMs - body.log[0]!.action.timeMs : 0,
    bloodLostMl:blood, activeBleeds:active, contamination:contaminated,
    missingMetrics:Object.freeze(['leftHandPathLengthM','rightHandPathLengthM','calibratedEconomyThresholds','hintsUsed'])});
}
