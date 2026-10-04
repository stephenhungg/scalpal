import type { OpenBodyCase, BodyPredicate, TissueDefinition } from '../open-body.js';
import type { ProcedureStep } from '../types.js';
const p=(tissueId:string,fact:string,op:string,value:number):BodyPredicate=>({tissueId,fact,op,value});
const tissue=(id:string,order:number,extra:Partial<TissueDefinition>={}):TissueDefinition=>({id,layer:id,order,cuttable:true,splittable:false,perfused:false,hollow:false,critical:false,tentable:false,flowMlPerSecond:0,fiberAxis:'',...extra});
export const OPEN_BODY:OpenBodyCase={version:1,fastPathPremarked:false,
 tissues:[tissue('skin',0),tissue('fat',1),tissue('fascia',2,{fiberAxis:'incision_line'}),tissue('muscle',3,{splittable:true,perfused:true,flowMlPerSecond:.3}),tissue('peritoneum',4,{tentable:true}),
 tissue('appendix',-1,{hollow:true}),tissue('mesoappendix',-1,{perfused:true,flowMlPerSecond:2}),tissue('appendicular_artery',-1,{perfused:true,flowMlPerSecond:2}),
 tissue('cecum',-1,{hollow:true,critical:true}),tissue('terminal_ileum',-1,{hollow:true,critical:true}),tissue('iliac_vessels',-1,{critical:true,perfused:true,flowMlPerSecond:8,structureIds:['cardiovascular__common_iliac_artery_r','cardiovascular__common_iliac_vein_r']}),tissue('ureter',-1,{critical:true,hollow:true,structureIds:['right_ureter']})],
 milestones:[
 {id:'mark_incision',predicates:[p('skin','marked','eq',1),p('skin','markErrorMm','lte',20),p('skin','markLengthMm','gte',50),p('skin','markLengthMm','lte',80),p('skin','markAngleDegrees','lte',25)]},
 {id:'incise_skin',predicates:[p('skin','cutCoverage','gte',.8),p('skin','cutErrorMm','lte',5),p('skin','cutDepthMm','lte',14),p('fat','opened','eq',1)]},
 {id:'open_fascia',predicates:[p('fascia','opened','eq',1),p('fascia','cutAngleDegrees','lte',25)]},
 // Blade use stays a recorded guardrail (split_dont_cut) and tissue penalty; it must not make the split unreachable.
 {id:'split_muscle',predicates:[p('muscle','splitWidthMm','gte',15)]},
 {id:'open_peritoneum',predicates:[p('peritoneum','opened','eq',1),p('peritoneum','tentedBeforeCut','eq',1)]},
 {id:'deliver_appendix',predicates:[p('appendix','delivered','eq',1)]},
 // cutBetweenClamps records the two measured clamps at division time; clampCount is live and drops when clamps come off.
 {id:'divide_mesoappendix',predicates:[p('mesoappendix','cutBetweenClamps','eq',1),p('mesoappendix','tieCount','gte',2),p('mesoappendix','tiedBothSides','eq',1),p('','activeBleeds','eq',0)]},
 {id:'ligate_base',predicates:[p('appendix','decision_true_base','eq',1),p('appendix','crushed','eq',1),p('appendix','tieDistanceMm','lte',5),p('appendix','stumpLengthMm','lte',5),p('appendix','cutAboveTie','eq',1),p('appendix','cutBetweenTieAndClamp','eq',1),p('appendix','removed','eq',1)]},
 {id:'inspect_clean',predicates:[p('','activeBleeds','eq',0),p('','poolMl','lte',1),p('appendix','inspectionMs','gte',1000),p('mesoappendix','inspectionMs','gte',1000)]},
 {id:'close',predicates:[p('skin','closed','eq',1)]}],
 guardrails:[
 {id:'off_mark',outcome:'',tissueId:'skin',verb:'cut',eventPredicate:p('','distanceMm','gte',5.001),severity:'moderate',feedback:'Follow the marked line. This cut is off target.'},
 {id:'deep_skin_cut',outcome:'',tissueId:'skin',verb:'cut',eventPredicate:p('','depthMm','gte',14.001),severity:'high',feedback:'Too deep. Skin and fat only for this stroke.'},
 {id:'mark_far',outcome:'',tissueId:'skin',verb:'mark',eventPredicate:p('','distanceMm','gte',20.001),severity:'moderate',feedback:'Recheck the hip and belly button landmarks.'},
 {id:'bowel_injury',outcome:'hollow_leak',tissueId:'',severity:'high',feedback:'Hollow tissue is leaking. Contamination is recorded.'},
 {id:'critical_injury',outcome:'critical_injury',tissueId:'',severity:'high',feedback:'Critical structure injured. Control bleeding and reassess.'},
 {id:'cut_before_control',outcome:'cut_unsecured',tissueId:'',severity:'high',feedback:'Active bleeding. Clamp, tie, or seal the injured vessel.'},
 {id:'split_dont_cut',outcome:'muscle_cut',tissueId:'muscle',severity:'moderate',feedback:'Split the muscle along its fibers; avoid the blade.'},
 {id:'lift_first',outcome:'untented_cut',tissueId:'peritoneum',severity:'high',feedback:'Lift the peritoneum before nicking it.'},
 {id:'fiber_direction',outcome:'across_fibers',tissueId:'fascia',severity:'moderate',feedback:'Open the fascia along its fibers.'},
 {id:'rough_handling',outcome:'rough_handling',tissueId:'',severity:'moderate',feedback:'Slow down. Lift gently.'}],
 decisions:[{id:'true_base',prompt:'Where is the true base?',choices:['true_base','appendix_tip','appendix_midpoint'],correctChoice:'true_base'}]};
const rows=[
 ['mark_incision','Mark McBurney incision','skin_marker','abdominal_wall','Find the hip bone and belly button. Mark a third.'],
 ['incise_skin','Incise skin','scalpel','abdominal_wall','One smooth stroke along your line. Skin only.'],
 ['open_fascia','Open fascia','metzenbaum_scissors','abdominal_wall','Open the aponeurosis along its fibers.'],
 ['split_muscle','Split muscle','retractor','abdominal_wall',"Now split the muscle. Pull, don’t cut."],
 ['open_peritoneum','Open peritoneum','toothed_forceps','abdominal_wall','Lift the peritoneum first, then nick it.'],
 ['deliver_appendix','Deliver appendix','babcock','appendix','Follow the taenia to the appendix. Lift it out gently.'],
 ['divide_mesoappendix','Secure mesoappendix','hemostat','mesoappendix','Clamp twice, cut between, then tie.'],
 ['ligate_base','Identify and secure base','right_angle_clamp','appendix','Crush, tie at the base, cut above your tie.'],
 ['inspect_clean','Inspect and clean','suction_irrigator','mesoappendix','Dry field? Check the stump and the vessels.'],
 ['close','Close wound','suture_tie','abdominal_wall','Close in layers. Nice work.']];
export const OPEN_STEPS:ProcedureStep[]=rows.map((r,i)=>({id:r[0]!,title:r[1]!,instrumentId:r[2]!,targets:[r[3]!],instruction:r[4]!,action:i===9?'close':'dissect',portIds:[],check:{type:'body_predicate',targets:[r[3]!],count:1},mistakes:[],hints:[r[4]!,'Inspect the tissue and the tool position.','Follow the expected action; other actions still have consequences.'],next:rows[i+1]?.[0]??''}));
