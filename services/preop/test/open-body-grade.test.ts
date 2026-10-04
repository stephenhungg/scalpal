import { describe, expect, it } from 'vitest';
import { StepEngine, perfectEvents } from '../src/engine.js';
import { bodyAction } from '../src/open-body.js';
import { PROCEDURES_BY_ID } from '../src/catalog/procedures.js';
import { CoachSession, renderContext } from '../src/coach.js';
import { buildCase } from '../src/case-builder.js';
import { Hono } from 'hono';
import { registerCoachRoutes } from '../src/coach-routes.js';
import { NOW, fixture } from './helpers.js';

const procedure = PROCEDURES_BY_ID.get('open_appendectomy')!;
function harmed() {
  const engine = new StepEngine(procedure);
  for (const step of procedure.steps.slice(0, 3)) for (const event of perfectEvents(step)) engine.handle(event);
  engine.handle({type:'surgery', evidence:bodyAction('cut','muscle',{actionId:'harm',lengthMm:4,timeMs:1000})});
  return engine;
}
describe('explicit end and deterministic body grade', () => {
  it('ends an irreversible incomplete attempt without fabricating milestones', () => {
    const engine = harmed(), achieved = [...engine.completedMilestones];
    expect(engine.completed).toBe(false);
    expect(engine.handle({type:'finish'})).toMatchObject({advanced:false, completed:true});
    expect([...engine.completedMilestones]).toEqual(achieved);
    expect(engine.grade).toMatchObject({reason:'learner_finished',complete:false,availablePoints:80,
      economyPoints:-1,economyMeasured:false,unscoredEconomyWeight:20});
    expect(engine.grade!.missingMilestones).toContain('split_muscle');
    expect(engine.grade!.guardrailIds).toContain('split_dont_cut');
    expect(engine.grade!.actionCount).toBe(engine.body!.log.length);
    expect(engine.grade!.durationMs).toBe(1000);
    expect(engine.grade!.missingMetrics).toContain('leftHandPathLengthM');
  });
  it('finish is idempotent and later scorer events cannot resume or rewrite the final record', () => {
    const engine = harmed(); engine.handle({type:'finish'});
    const grade = engine.grade, facts = [...engine.body!.facts], log = JSON.stringify(engine.body!.log);
    engine.handle({type:'finish'});
    for (const step of procedure.steps) for (const event of perfectEvents(step)) engine.handle(event);
    expect(engine.grade).toBe(grade);
    expect([...engine.body!.facts]).toEqual(facts);
    expect(JSON.stringify(engine.body!.log)).toBe(log);
    expect(Object.isFrozen(grade)).toBe(true);
    expect(Object.isFrozen(grade!.metMilestones)).toBe(true);
  });
  it('normal completion uses the same grade and freezes subsequent actions', () => {
    const engine = new StepEngine(procedure);
    for (const step of procedure.steps) for (const event of perfectEvents(step)) engine.handle(event);
    expect(engine.grade).toMatchObject({reason:'goals_reached',complete:true,missingMilestones:[],
      correctDecisions:1,decisionCount:1,safetyPoints:50,decisionPoints:20,tissuePoints:10,earnedPoints:80});
    const actions = engine.body!.log.length;
    engine.handle({type:'surgery',evidence:bodyAction('cut','cecum',{lengthMm:4})});
    expect(engine.body!.log).toHaveLength(actions);
  });
  it('grades current predicates rather than historical achievements and scores the latest decision', () => {
    const engine = new StepEngine(procedure);
    for (const step of procedure.steps.slice(0,9)) for (const event of perfectEvents(step)) engine.handle(event);
    engine.handle({type:'surgery',evidence:bodyAction('decide','appendix',{instrumentId:'decision',choice:'appendix_tip',actionId:'wrong-latest'})});
    engine.handle({type:'surgery',evidence:bodyAction('cut','appendicular_artery',{lengthMm:4})});
    expect(engine.completedMilestones.has('divide_mesoappendix')).toBe(true);
    engine.handle({type:'finish'});
    expect(engine.grade!.missingMilestones).toContain('divide_mesoappendix');
    expect(engine.grade!.missingMilestones).toContain('inspect_clean');
    expect(engine.grade!.decisionPoints).toBe(0);
    expect(engine.grade!.activeBleeds).toBe(1);
  });
  it('does not alter legacy step progression', () => {
    const legacy = new StepEngine(PROCEDURES_BY_ID.get('lap_appendectomy')!);
    const current = legacy.current;
    legacy.handle({type:'finish'});
    expect(legacy.current).toBe(current);
    expect(legacy.grade).toBeNull();
  });
  it('coach captions distinguish an ended attempt from completed goals', () => {
    const kase = buildCase(fixture('patient-demo-pediatric-asthma'), '', NOW);
    const session = new CoachSession('coach-finish',{...kase,procedure,procedureId:procedure.id},()=>NOW);
    expect(session.handle({type:'finish'}).accepted).toBe(true);
    expect(session.snapshot().bodyGrade!.missingMilestones).toHaveLength(10);
    expect(renderContext(session.snapshot())).toContain('ATTEMPT ENDED WITH UNMET GOALS');
    const count = session.snapshot().version;
    expect(session.handle({type:'finish'}).accepted).toBe(true);
    expect(session.snapshot().version).toBe(count);
    expect(session.handle({type:'surgery',evidence:bodyAction('mark','skin',{instrumentId:'skin_marker',lengthMm:60})}).reason).toBe('case_completed');
  });
  it('accepts the explicit finish over the real coach HTTP boundary', async () => {
    const app = new Hono();
    const kase = buildCase(fixture('patient-demo-pediatric-asthma'), '', NOW);
    registerCoachRoutes(app,{loadCase:async()=>({...kase,procedure,procedureId:procedure.id}),now:()=>NOW,tickMs:0});
    const created = await app.request('/coach/sessions',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({patientId:'patient-demo-pediatric-asthma',mode:'virtual'})});
    const {sessionId} = await created.json() as {sessionId:string};
    const response = await app.request(`/coach/sessions/${sessionId}/events`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({event:{type:'finish',eventId:'finish-1'}})});
    expect(response.status).toBe(200);
    const json = await response.json() as {results:{accepted:boolean;applied:boolean}[];snapshot:{bodyGrade:{complete:boolean;economyPoints:number}}};
    expect(json.results[0]!.accepted).toBe(true);
    expect(json.results[0]!.applied).toBe(true);
    expect(json.snapshot.bodyGrade).toMatchObject({complete:false,economyPoints:-1});
  });
});
