// Synthetic playthrough fixtures only; runtime tool adapters never manufacture ideal measurements.
import { bodyAction, type BodyAction } from './open-body.js';
export function idealBodyActions(step:string):BodyAction[]{
 const a=(verb:string,tissue:string,instrumentId:string,values:Partial<BodyAction>={})=>bodyAction(verb,tissue,{instrumentId,...values});
 let events:BodyAction[]=[];
 switch(step){
 case 'mark_incision':events=[a('mark','skin','skin_marker',{lengthMm:60})];break;
 case 'incise_skin':events=[a('cut','skin','scalpel',{lengthMm:60,depthMm:2}),a('cut','fat','scalpel',{lengthMm:60,depthMm:10})];break;
 case 'open_fascia':events=[a('cut','fascia','metzenbaum_scissors',{lengthMm:60})];break;
 case 'split_muscle':events=[a('retract','muscle','retractor',{separationMm:20,secondaryInstanceId:"retractor-2"})];break;
 case 'open_peritoneum':events=[a('grasp','peritoneum','toothed_forceps',{depthMm:10}),a('cut','peritoneum','scalpel',{lengthMm:4})];break;
 case 'deliver_appendix':events=[a('grasp','appendix','babcock',{depthMm:20})];break;
 case 'divide_mesoappendix':events=[a('clamp','mesoappendix','hemostat',{distanceMm:5,instrumentInstanceId:'clamp-a'}),a('clamp','mesoappendix','hemostat',{distanceMm:15,instrumentInstanceId:'clamp-b'}),a('cut','mesoappendix','metzenbaum_scissors',{distanceMm:10,lengthMm:4}),a('tie','mesoappendix','suture_tie',{distanceMm:5}),a('tie','mesoappendix','suture_tie',{distanceMm:15})];break;
 case 'ligate_base':events=[a('decide','appendix','decision',{choice:'true_base'}),a('clamp','appendix','right_angle_clamp',{distanceMm:3}),a('tie','appendix','suture_tie',{distanceMm:3}),a('clamp','appendix','right_angle_clamp',{distanceMm:8}),a('cut','appendix','scalpel',{distanceMm:4,lengthMm:4})];break;
 case 'inspect_clean':events=[a('suction','mesoappendix','suction_irrigator',{durationMs:1000}),a('inspect','mesoappendix','suction_irrigator',{durationMs:1000}),a('inspect','appendix','suction_irrigator',{durationMs:1000})];break;
 case 'close':events=[a('close','skin','assistant')];break;
 }
 return events.map((e,i)=>({...e,actionId:`${step}-${i}`}));
}
