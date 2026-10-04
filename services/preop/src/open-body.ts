import type { Vec3, Severity } from './types.js';

// Shared semantic simulator contract. Measurements are torso-local metres, mm, degrees and
// device monotonic milliseconds. No case/step ID participates in physical consequences.
export interface BodyAction {
  actionId: string; instrumentId: string; instrumentInstanceId: string; secondaryInstanceId: string; verb: string;
  tissueId: string; layer: string; coordinateFrame: string; timeMs: number; position: Vec3;
  registered: boolean; speedMps: number; forceProxy: number; distanceMm: number;
  lengthMm: number; angleDegrees: number; depthMm: number; durationMs: number;
  separationMm: number; choice: string; bloodLostMl: number; poolMl: number; flowMlPerSecond: number;
}
export interface TissueDefinition {
  id: string; layer: string; order: number; cuttable: boolean; splittable: boolean;
  perfused: boolean; hollow: boolean; critical: boolean; tentable: boolean;
  flowMlPerSecond: number;
  structureIds?: string[]; // Explicit source-atlas binding; simulation identity remains id.
}
export interface BodyPredicate { tissueId: string; fact: string; op: string; value: number; }
export interface BodyMilestone { id: string; predicates: BodyPredicate[]; }
export interface BodyGuardrail { id: string; outcome: string; tissueId: string; severity: Severity; feedback: string; verb?: string; eventPredicate?: BodyPredicate; }
export interface BodyDecision { id: string; prompt: string; choices: string[]; correctChoice: string; }
export interface OpenBodyCase {
  version: number; tissues: TissueDefinition[]; milestones: BodyMilestone[];
  guardrails: BodyGuardrail[]; decisions: BodyDecision[]; fastPathPremarked: boolean;
}
export interface BodyRecord { action: BodyAction; outcomes: string[]; }
export const TOOL_VERBS: Record<string, string[]> = {
  scalpel: ['cut'], metzenbaum_scissors: ['cut'], skin_marker: ['mark'],
  toothed_forceps: ['grasp','retract'], retractor: ['retract'], babcock: ['grasp','retract'],
  atraumatic_grasper: ['grasp','retract'], hemostat: ['clamp','release'], right_angle_clamp: ['clamp','release'],
  suture_tie: ['tie','place'], hook_cautery: ['seal'], vessel_sealer: ['seal'],
  suction_irrigator: ['suction','inspect'], decision: ['decide'], assistant: ['close','tick','fluid'],
};
export function validBodyAction(e: BodyAction): boolean {
  return !!e && [e.actionId,e.instrumentId,e.instrumentInstanceId,e.secondaryInstanceId,e.verb,e.tissueId,e.layer,e.coordinateFrame,e.choice].every(v=>typeof v==='string') &&
    !!e.actionId && e.coordinateFrame==='registered_torso_m' && typeof e.registered==='boolean' && !!e.position &&
    [e.position.x,e.position.y,e.position.z].every(Number.isFinite) &&
    [e.timeMs,e.speedMps,e.forceProxy,e.distanceMm,e.lengthMm,e.angleDegrees,e.depthMm,e.durationMs,e.separationMm,e.bloodLostMl,e.poolMl,e.flowMlPerSecond].every(v=>Number.isFinite(v)&&v>=0) &&
    !!TOOL_VERBS[e.instrumentId]?.includes(e.verb);
}
// Clamps/ties control an injury at or proximal to it (smaller distance from the base); a seal
// controls only its own point. Unmeasured occluders cannot control a measured injury.
export const HEMOSTASIS_TOLERANCE_MM = 3;
// One rough-handling outcome per tool instance and tissue in this window, however often a held tool reports.
export const ROUGH_HANDLING_COOLDOWN_MS = 3000;
interface Injury { positionMm: number; measured: boolean; }
export class BodyState {
  readonly facts = new Map<string, number>();
  readonly log: BodyRecord[] = [];
  private seen = new Set<string>();
  private clamps = new Map<string, Map<string,number>>();
  private ties = new Map<string, number[]>();
  private looseClamps = new Map<string, Set<string>>(); // Clamp instances without a longitudinal measurement.
  private looseControls = new Map<string, number>(); // Unmeasured ties and seals.
  private seals = new Map<string, number[]>();
  private injuries = new Map<string, Injury[]>();
  private roughAt = new Map<string, number>();
  private clock = -1;
  constructor(readonly tissues: TissueDefinition[]) { for(const t of tissues)if(t.splittable)this.set(t.id,'bladeUsed',0); }
  get(tissueId: string, fact: string) { return this.facts.get(`${tissueId}:${fact}`) ?? 0; }
  set(tissueId: string, fact: string, value: number) { this.facts.set(`${tissueId}:${fact}`,value); }
  test(p: BodyPredicate): boolean {
    const key = `${p.tissueId}:${p.fact}`;
    // Missing measurements never satisfy a <= threshold (notably stump length).
    if (!this.facts.has(key)) return false;
    const v = this.get(p.tissueId,p.fact);
    return p.op==='gte'?v>=p.value:p.op==='lte'?v<=p.value:p.op==='eq'?v===p.value:false;
  }
  apply(e: BodyAction): BodyRecord | null {
    if (!validBodyAction(e)||!e.registered||this.seen.has(e.actionId)||e.timeMs<this.clock) return null;
    const t=this.tissues.find(t=>t.id===e.tissueId);
    if (!t || e.layer!==t.layer) return null;
    this.seen.add(e.actionId);
    const dt=this.clock<0?0:(e.timeMs-this.clock)/1000;
    this.clock=e.timeMs;
    let lost=0, active=0;
    for(const tissue of this.tissues) if(this.get(tissue.id,'bleeding')>0){if(!this.get(tissue.id,'fluidDriven'))lost+=dt*tissue.flowMlPerSecond;active++;}
    this.set('','bloodLostMl',this.get('','bloodLostMl')+lost);
    this.set('','poolMl',this.get('','poolMl')+lost);
    this.set('','activeBleedSeconds',this.get('','activeBleedSeconds')+(active?dt:0));
    const outcomes:string[]=[];
    const put=(fact:string,value=1)=>this.set(t.id,fact,value);
    const clamp=this.clamps.get(t.id)??new Map<string,number>();
    const ties=this.ties.get(t.id)??[];
    // Exposure is physical, never based on case milestones. Organs (order < 0) require the wall open.
    const blocked=this.tissues.some(other=>other.order>=0 && (t.order<0||other.order<t.order) && this.get(other.id,'opened')===0);
    if(blocked && !['decide','tick','close','fluid'].includes(e.verb)) outcomes.push('not_exposed');
    else switch(e.verb){
      case 'fluid': {
        if(!t.perfused||e.bloodLostMl<this.get(t.id,'measuredLossMl')||e.poolMl>e.bloodLostMl){outcomes.push('invalid_fluid');break;}
        this.set('','bloodLostMl',this.get('','bloodLostMl')+e.bloodLostMl-this.get(t.id,'measuredLossMl'));
        this.set('','poolMl',Math.max(0,this.get('','poolMl')+e.poolMl-this.get(t.id,'measuredPoolMl')));
        put('measuredLossMl',e.bloodLostMl);put('measuredPoolMl',e.poolMl);put('measuredFlowMlPerSecond',e.flowMlPerSecond);put('fluidDriven');put('bleeding',e.flowMlPerSecond>0?1:0);break;
      }
      case 'mark': put('marked');put('markErrorMm',e.distanceMm);put('markLengthMm',e.lengthMm);put('markAngleDegrees',e.angleDegrees);break;
      case 'cut':
        if(!t.cuttable){outcomes.push('not_cuttable');break;}
        if(e.lengthMm<1){outcomes.push('no_cut');break;}
        put('cutPositionMm',e.distanceMm);
        put('opened');put('divided');put('cutLengthMm',Math.max(this.get(t.id,'cutLengthMm'),e.lengthMm));
        if(this.get(t.id,'markLengthMm')>0)put('cutCoverage',Math.min(1,this.get(t.id,'cutLengthMm')/this.get(t.id,'markLengthMm')));
        put('cutErrorMm',e.distanceMm);put('cutAngleDegrees',e.angleDegrees);put('cutDepthMm',e.depthMm);
        if(t.splittable) {put('bladeUsed');outcomes.push('muscle_cut');}
        if(e.angleDegrees>25)outcomes.push('across_fibers');
        if(t.tentable){put('tentedBeforeCut',this.get(t.id,'tented'));if(!this.get(t.id,'tented'))outcomes.push('untented_cut');}
        const positions=[...clamp.values()].sort((a,b)=>a-b);
        const measured=e.choice!=='longitudinal_unmeasured';
        const between=measured&&positions.length>=2&&e.distanceMm>positions[0]!&&e.distanceMm<positions[positions.length-1]!;
        put('cutBetweenClamps',between?1:0);
        const proximal=measured?ties.filter(p=>p<e.distanceMm):[];
        put('cutBetweenTieAndClamp',proximal.length>0&&positions.some(p=>p>e.distanceMm)?1:0);
        put('tiedBothSides',ties.some(p=>p<e.distanceMm)&&ties.some(p=>p>e.distanceMm)?1:0);
        if(t.perfused){const injury={positionMm:e.distanceMm,measured};this.injuries.set(t.id,[...this.injuries.get(t.id)??[],injury]);
          this.refreshBleeding(t);if(!this.controlled(t.id,injury))outcomes.push('cut_unsecured');}
        if(t.hollow){
          put('removed');
          const secured=proximal.length>0;
          if(!secured){put('leaking');this.set('','contamination',1);outcomes.push('hollow_leak');}
          else {put('stumpLengthMm',e.distanceMm);put('removed');put('cutAboveTie');}
        }
        if(t.critical)outcomes.push('critical_injury');
        break;
      case 'clamp': {
        if(!e.instrumentInstanceId){outcomes.push('missing_instance');break;}
        const loose=this.looseClamps.get(t.id)??new Set<string>();
        if(e.choice==='longitudinal_unmeasured'){clamp.delete(e.instrumentInstanceId);loose.add(e.instrumentInstanceId);this.looseClamps.set(t.id,loose);}
        else {loose.delete(e.instrumentInstanceId);clamp.set(e.instrumentInstanceId,e.distanceMm);this.clamps.set(t.id,clamp);if(e.instrumentId==='right_angle_clamp')put('crushed');}
        put('clampCount',clamp.size);this.refreshBleeding(t);break;
      }
      case 'release': {
        const loose=this.looseClamps.get(t.id);
        const removed=clamp.delete(e.instrumentInstanceId)||!!loose?.delete(e.instrumentInstanceId);
        if(!removed){outcomes.push('not_clamped');break;}
        const before=this.get(t.id,'bleeding');put('clampCount',clamp.size);this.refreshBleeding(t);
        if(!before&&this.get(t.id,'bleeding'))outcomes.push('rebleed');break;
      }
      case 'tie':
        if(e.choice==='longitudinal_unmeasured'){this.looseControls.set(t.id,(this.looseControls.get(t.id)??0)+1);put('leaking',0);this.refreshBleeding(t);break;}
        if(!ties.some(p=>Math.abs(p-e.distanceMm)<1))ties.push(e.distanceMm);
        this.ties.set(t.id,ties);put('tieCount',ties.length);
        if(this.get(t.id,'divided'))put('tiedBothSides',ties.some(p=>p<this.get(t.id,'cutPositionMm'))&&ties.some(p=>p>this.get(t.id,'cutPositionMm'))?1:0);
        put('tieDistanceMm',Math.min(...ties));put('leaking',0);this.refreshBleeding(t);break;
      case 'seal':
        put('sealed');
        if(e.choice==='longitudinal_unmeasured')this.looseControls.set(t.id,(this.looseControls.get(t.id)??0)+1);
        else this.seals.set(t.id,[...this.seals.get(t.id)??[],e.distanceMm]);
        this.refreshBleeding(t);break;
      case 'grasp': case 'retract':
        put('liftMm',e.depthMm);if(t.tentable)put('tented',e.depthMm>=8?1:0);
        if(e.depthMm>=15)put('delivered');
        if(t.splittable&&e.instrumentInstanceId&&e.secondaryInstanceId&&e.instrumentInstanceId!==e.secondaryInstanceId&&e.separationMm>=15&&e.angleDegrees<=25){put('opened');put('splitWidthMm',e.separationMm);}
        if(e.speedMps>.1||e.forceProxy>1){const key=`${e.instrumentInstanceId}|${t.id}`,last=this.roughAt.get(key);
          if(last===undefined||e.timeMs-last>=ROUGH_HANDLING_COOLDOWN_MS){this.roughAt.set(key,e.timeMs);outcomes.push('rough_handling');}}
        break;
      case 'suction':if(!this.tissues.some(t=>this.get(t.id,'fluidDriven')>0))this.set('','poolMl',Math.max(0,this.get('','poolMl')-e.durationMs*.005));break;
      case 'inspect':put('inspectionMs',Math.max(this.get(t.id,'inspectionMs'),e.durationMs));break;
      case 'decide':put(`decision_${e.choice}`);break;
      case 'close':put('closed');break;
      case 'place':put('placed');break;
    }
    this.set('','activeBleeds',this.tissues.filter(t=>this.get(t.id,'bleeding')>0).length);
    // Snapshot prevents callers from rewriting history after submission.
    const record={action:{...e,position:{...e.position}},outcomes};this.log.push(record);return record;
  }
  private controlled(tissueId: string, injury: Injury): boolean {
    const clamps=[...this.clamps.get(tissueId)?.values()??[]], ties=this.ties.get(tissueId)??[], seals=this.seals.get(tissueId)??[];
    // Without injury geometry, any control on the structure is the best available evidence.
    if(!injury.measured)return clamps.length+ties.length+seals.length+(this.looseClamps.get(tissueId)?.size??0)+(this.looseControls.get(tissueId)??0)>0;
    return [...clamps,...ties].some(p=>p<=injury.positionMm+HEMOSTASIS_TOLERANCE_MM)||seals.some(p=>Math.abs(p-injury.positionMm)<=HEMOSTASIS_TOLERANCE_MM);
  }
  private refreshBleeding(t: TissueDefinition) {
    if(!t.perfused)return;
    const open=(this.injuries.get(t.id)??[]).filter(i=>!this.controlled(t.id,i)).length;
    this.set(t.id,'openInjuries',open);this.set(t.id,'bleeding',open>0?1:0);
  }
}
export function bodyAction(verb:string,tissueId:string,values:Partial<BodyAction>={}):BodyAction {
  return {actionId:`${tissueId}-${verb}`,instrumentId:'scalpel',instrumentInstanceId:'tool-1',secondaryInstanceId:'',verb,tissueId,layer:tissueId,
    coordinateFrame:'registered_torso_m',timeMs:0,position:{x:0,y:0,z:0},registered:true,speedMps:0,forceProxy:0,
    distanceMm:0,lengthMm:0,angleDegrees:0,depthMm:0,durationMs:0,separationMm:0,choice:'',bloodLostMl:0,poolMl:0,flowMlPerSecond:0,...values};
}
