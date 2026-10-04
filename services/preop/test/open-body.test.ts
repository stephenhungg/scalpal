import { describe,it,expect } from 'vitest';
import { BodyState,bodyAction } from '../src/open-body.js';
import { OPEN_BODY } from '../src/catalog/open-appendectomy.js';
import { PROCEDURES_BY_ID } from '../src/catalog/procedures.js';
import { StepEngine,perfectEvents } from '../src/engine.js';
const procedure=PROCEDURES_BY_ID.get('open_appendectomy')!;
const plan=()=>procedure.openBody!;
function exposed(){const engine=new StepEngine(procedure);for(const s of procedure.steps.slice(0,5))for(const e of perfectEvents(s))engine.handle(e);return engine;}
describe('case-agnostic body and predicate scorer',()=>{
 it('completes expected path without touch shortcuts',()=>{const e=new StepEngine(procedure);e.handle({type:'confirm'});expect(e.current?.id).toBe('mark_incision');for(const s of procedure.steps)for(const a of perfectEvents(s))e.handle(a);expect(e.completed).toBe(true);expect(e.mistakes).toEqual([]);});
 it('records bowel injury and contamination while coaching an unrelated milestone',()=>{const e=exposed();const r=e.handle({type:'surgery',evidence:bodyAction('cut','terminal_ileum',{lengthMm:4})});expect(r.mistake?.id).toBe('bowel_injury');expect(e.body!.get('','contamination')).toBe(1);expect(e.mistakes.map(m=>m.id)).toContain('critical_injury');});
 // Hemostasis is positional: distanceMm is measured from the structure's base, where inflow comes from.
 // A clamp/tie stops an injury only at or proximal to it; the old version of this test cut and clamped at
 // the default distance 0, so it passed even though any clamp anywhere stopped the bleed.
 it('cut before clamp bleeds, accumulates loss, and only a clamp/tie at or proximal to the cut stops it',()=>{const e=exposed();
  e.handle({type:'surgery',evidence:bodyAction('cut','mesoappendix',{lengthMm:4,distanceMm:10})});expect(e.body!.get('','activeBleeds')).toBe(1);
  e.handle({type:'surgery',evidence:bodyAction('clamp','mesoappendix',{instrumentId:'hemostat',instrumentInstanceId:'clamp-distal',distanceMm:20,timeMs:1000})});
  expect(e.body!.get('','activeBleeds')).toBe(1); // A distal stump clamp leaves the injury upstream of it bleeding.
  e.handle({type:'surgery',evidence:bodyAction('clamp','mesoappendix',{instrumentId:'hemostat',instrumentInstanceId:'clamp-at-cut',actionId:'clamp-at-cut',distanceMm:10,timeMs:2000})});
  expect(e.body!.get('','bloodLostMl')).toBe(4);expect(e.body!.get('','activeBleeds')).toBe(0);
  e.handle({type:'surgery',evidence:bodyAction('cut','appendicular_artery',{timeMs:2000,lengthMm:4,distanceMm:30})});
  e.handle({type:'surgery',evidence:bodyAction('tie','appendicular_artery',{instrumentId:'suture_tie',timeMs:3000,distanceMm:20})});
  expect(e.body!.get('','activeBleeds')).toBe(0);expect(e.body!.get('','bloodLostMl')).toBe(6);expect(e.mistakes.length).toBe(2);});
 it('a seal controls only its own point, so a later cut through unsealed vessel bleeds again',()=>{const e=exposed();
  e.handle({type:'surgery',evidence:bodyAction('seal','iliac_vessels',{instrumentId:'hook_cautery',distanceMm:10})});
  e.handle({type:'surgery',evidence:bodyAction('cut','iliac_vessels',{lengthMm:4,distanceMm:40,timeMs:1})});
  expect(e.body!.get('iliac_vessels','bleeding')).toBe(1);expect(e.mistakes.map(m=>m.id)).toContain('cut_before_control');
  e.handle({type:'surgery',evidence:bodyAction('seal','iliac_vessels',{instrumentId:'hook_cautery',actionId:'seal-at-cut',distanceMm:41,timeMs:2})});
  expect(e.body!.get('iliac_vessels','bleeding')).toBe(0);});
 it('unmeasured clamps cannot control a measured injury',()=>{const e=exposed();
  e.handle({type:'surgery',evidence:bodyAction('cut','mesoappendix',{lengthMm:4,distanceMm:10})});
  e.handle({type:'surgery',evidence:bodyAction('clamp','mesoappendix',{instrumentId:'hemostat',choice:'longitudinal_unmeasured',timeMs:1})});
  expect(e.body!.get('mesoappendix','bleeding')).toBe(1);});
 it('removing clamps after tying both sides keeps the mesoappendix milestone',()=>{const e=exposed();for(const a of perfectEvents(procedure.steps[6]!))e.handle(a);
  expect(e.completedMilestones.has('divide_mesoappendix')).toBe(true);
  for(const id of ['clamp-a','clamp-b'])e.handle({type:'surgery',evidence:bodyAction('release','mesoappendix',{instrumentId:'hemostat',instrumentInstanceId:id,actionId:`off-${id}`})});
  expect(e.body!.get('mesoappendix','clampCount')).toBe(0);expect(e.body!.get('','activeBleeds')).toBe(0);
  expect(plan().milestones.find(m=>m.id==='divide_mesoappendix')!.predicates.every(p=>e.body!.test(p))).toBe(true);});
 it('releasing a clamp removes it and re-bleeds an untied cut, but not a tied one',()=>{const e=exposed();
  const act=(verb:string,values:Record<string,unknown>)=>e.handle({type:'surgery',evidence:bodyAction(verb,'mesoappendix',values)});
  act('clamp',{instrumentId:'hemostat',instrumentInstanceId:'clamp-a',actionId:'a',distanceMm:5});act('clamp',{instrumentId:'hemostat',instrumentInstanceId:'clamp-b',actionId:'b',distanceMm:15});
  act('cut',{lengthMm:4,distanceMm:10,actionId:'cut'});expect(e.body!.get('mesoappendix','bleeding')).toBe(0);
  act('release',{instrumentId:'hemostat',instrumentInstanceId:'clamp-a',actionId:'release-a',timeMs:1000});
  expect(e.body!.get('mesoappendix','clampCount')).toBe(1);expect(e.body!.get('mesoappendix','bleeding')).toBe(1);expect(e.body!.log.at(-1)!.outcomes).toEqual(['rebleed']);
  act('tie',{instrumentId:'suture_tie',actionId:'tie',distanceMm:5,timeMs:2000});expect(e.body!.get('mesoappendix','bleeding')).toBe(0);
  act('release',{instrumentId:'hemostat',instrumentInstanceId:'clamp-b',actionId:'release-b',timeMs:3000});
  expect(e.body!.get('mesoappendix','clampCount')).toBe(0);expect(e.body!.get('mesoappendix','bleeding')).toBe(0);
  act('release',{instrumentId:'hemostat',instrumentInstanceId:'clamp-b',actionId:'release-again',timeMs:3000});expect(e.body!.log.at(-1)!.outcomes).toEqual(['not_clamped']);});
 it('recognizes milestones out of order and records soft deviations',()=>{const e=exposed();for(const a of perfectEvents(procedure.steps[7]!))e.handle(a);expect(e.completedMilestones.has('ligate_base')).toBe(true);expect(e.current?.id).toBe('deliver_appendix');expect(e.orderDeviations).toContain('ligate_base');});
 it('requires measured stump length and decision; a long stump does not pass',()=>{const e=exposed();for(const a of perfectEvents(procedure.steps[7]!)){if(a.type==='surgery'&&a.evidence.verb==='cut')a.evidence.distanceMm=8;e.handle(a);}expect(e.completedMilestones.has('ligate_base')).toBe(false);expect(e.body!.get('appendix','stumpLengthMm')).toBe(8);expect(e.body!.test({tissueId:'missing',fact:'length',op:'lte',value:5})).toBe(false);});
 it('same verbs produce identical consequences under a second case',()=>{const a=exposed(),other={...procedure,id:'dummy_case',openBody:{...OPEN_BODY,milestones:[],guardrails:[]}};const b=new StepEngine(other);for(const s of procedure.steps.slice(0,5))for(const e of perfectEvents(s))b.handle(e);const injury={type:'surgery' as const,evidence:bodyAction('cut','terminal_ileum',{lengthMm:4})};a.handle(injury);b.handle(injury);expect([...b.body!.facts]).toEqual([...a.body!.facts]);expect(b.body!.log).toEqual(a.body!.log);});
 // A held tool reports about 10 times a second; one fast lift must not become a burst of mistakes.
 it('rough handling is one mistake per tool and tissue per cooldown window',()=>{const e=exposed();
  const lift=(id:string,timeMs:number,values:Record<string,unknown>={})=>e.handle({type:'surgery',evidence:bodyAction('grasp','appendix',{instrumentId:'babcock',instrumentInstanceId:'babcock-1',actionId:id,timeMs,speedMps:.3,depthMm:5,...values})});
  for(let i=0;i<10;i++)lift(`fast-${i}`,i*100);
  expect(e.mistakes.filter(m=>m.id==='rough_handling').length).toBe(1);
  lift('other-tool',950,{instrumentInstanceId:'babcock-2'});lift('other-tissue',960,{tissueId:'cecum',layer:'cecum'});
  expect(e.mistakes.filter(m=>m.id==='rough_handling').length).toBe(3);
  lift('after-window',3000);expect(e.mistakes.filter(m=>m.id==='rough_handling').length).toBe(4);});
 it('only tissues with a declared fiber direction can be cut across their fibers',()=>{const e=exposed();
  e.handle({type:'surgery',evidence:bodyAction('cut','mesoappendix',{lengthMm:4,distanceMm:5,angleDegrees:90,actionId:'transect'})});
  expect(e.body!.log.at(-1)!.outcomes).not.toContain('across_fibers'); // A correct 90-degree transection has no fibers to cross.
  const fascia=new StepEngine(procedure);for(const s of procedure.steps.slice(0,2))for(const a of perfectEvents(s))fascia.handle(a);
  fascia.handle({type:'surgery',evidence:bodyAction('cut','fascia',{lengthMm:40,angleDegrees:40,instrumentId:'metzenbaum_scissors'})});
  expect(fascia.mistakes.map(m=>m.id)).toEqual(['fiber_direction']);});
 it('a blade on muscle stays a recorded guardrail but the muscle can still be split for the milestone',()=>{const e=new StepEngine(procedure);
  for(const s of procedure.steps.slice(0,3))for(const a of perfectEvents(s))e.handle(a);
  e.handle({type:'surgery',evidence:bodyAction('cut','muscle',{lengthMm:10,actionId:'blade-on-muscle'})});
  for(const a of perfectEvents(procedure.steps[3]!))e.handle(a);
  expect(e.body!.get('muscle','bladeUsed')).toBe(1);expect(e.mistakes.map(m=>m.id)).toContain('split_dont_cut');
  expect(e.completedMilestones.has('split_muscle')).toBe(true);});
 it('guardrails are labelled as guardrails, not order deviations',()=>{const e=exposed();
  e.handle({type:'surgery',evidence:bodyAction('cut','terminal_ileum',{lengthMm:4})});
  expect(e.mistakes.length).toBeGreaterThan(0);expect(e.mistakes.every(m=>m.trigger==='guardrail')).toBe(true);});
 it('the latest decision answer wins for milestones',()=>{const e=exposed();for(const a of perfectEvents(procedure.steps[7]!))e.handle(a);
  expect(e.completedMilestones.has('ligate_base')).toBe(true);
  e.handle({type:'surgery',evidence:bodyAction('decide','appendix',{instrumentId:'decision',choice:'appendix_tip',actionId:'changed-mind'})});
  expect(e.body!.get('appendix','decision_true_base')).toBe(0);
  expect(plan().milestones.find(m=>m.id==='ligate_base')!.predicates.every(p=>e.body!.test(p))).toBe(false);});
 it('rejects invalid registration, replay, old clock, wrong tool, and blocks hidden layers',()=>{const b=new BodyState(OPEN_BODY.tissues);expect(b.apply(bodyAction('cut','skin',{registered:false}))).toBeNull();expect(b.apply(bodyAction('cut','skin',{instrumentId:'skin_marker'}))).toBeNull();const hidden=b.apply(bodyAction('cut','fascia',{timeMs:2}));expect(hidden?.outcomes).toEqual(['not_exposed']);expect(b.get('fascia','opened')).toBe(0);expect(b.apply(bodyAction('cut','fascia',{timeMs:2}))).toBeNull();expect(b.apply(bodyAction('cut','skin',{timeMs:1}))).toBeNull();});
 it('a new bleed invalidates the no-active-bleed milestone without erasing history',()=>{const e=exposed();for(const a of perfectEvents(procedure.steps[6]!))e.handle(a);expect(e.completedMilestones.has('divide_mesoappendix')).toBe(true);e.handle({type:'surgery',evidence:bodyAction('cut','appendicular_artery',{lengthMm:4})});expect(e.body!.test({tissueId:'',fact:'activeBleeds',op:'eq',value:0})).toBe(false);});
});
