// Explicit advanced-mode fixtures for port, clip-count and legacy headset reconciliation
// regressions. Patient-route tests use the actual open-appendectomy case mapping instead.
import { Hono } from 'hono';
import { buildCase } from '../src/case-builder.js';
import { registerCoachRoutes, type CoachRouteOptions } from '../src/coach-routes.js';
import { PROCEDURES_BY_ID } from '../src/catalog/procedures.js';
import { ANATOMY_BY_ID } from '../src/catalog/anatomy.js';
import { INSTRUMENTS_BY_ID } from '../src/catalog/instruments.js';
import { STEP_ROLES } from '../src/catalog/cases.js';
import type { SurgicalCase } from '../src/types.js';
import { fixture, NOW } from './helpers.js';

export function legacyAppendectomyCase(subject='patient-demo-pediatric-asthma'): SurgicalCase {
  const kase=buildCase(fixture(subject),'',NOW), procedure=PROCEDURES_BY_ID.get('lap_appendectomy')!;
  const openRoles = STEP_ROLES.open_appendectomy!, lapRoles = STEP_ROLES.lap_appendectomy!;
  const roles = Object.keys(openRoles) as (keyof typeof openRoles)[];
  const remap = new Map<string,string>(roles.map(role => [openRoles[role], lapRoles[role]]));
  const structures=[...new Set([...procedure.structures,...kase.brief.highlightStructures])];
  const ids=[...new Set([...procedure.ports.flatMap(p=>p.instrumentIds),...procedure.steps.map(s=>s.instrumentId)])];
  return {...kase,caseId:`case_${subject}_${procedure.id}`,procedureId:procedure.id,procedure,
    considerations:kase.considerations.map(c=>({...c,stepId:remap.get(c.stepId)??c.stepId})),
    anatomy:structures.map(id=>ANATOMY_BY_ID.get(id)!),instruments:ids.map(id=>INSTRUMENTS_BY_ID.get(id)!)};
}
export function legacyAppendectomyApp(options:Partial<CoachRouteOptions>={}) {
  const app=new Hono();
  registerCoachRoutes(app,{loadCase:async id=>legacyAppendectomyCase(id),now:()=>NOW,tickMs:0,...options});
  app.get('/patients/:id/case', c=>c.json(legacyAppendectomyCase(c.req.param('id'))));
  return app;
}
