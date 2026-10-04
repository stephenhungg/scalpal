// Real encounter/coach HTTP and RealtimeBridge; every record and identity belongs to this fixture.
import { createRequire } from 'node:module';
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { createApp } from '../../../services/preop/src/app.js';
import { RealtimeBridge } from '../../../services/preop/src/realtime-bridge.js';
import { fixtureClient, NOW } from '../../../services/preop/test/helpers.js';
import { tables } from '../../../services/api/src/module_bindings/index.js';
import { publishFresh, connect, eventually, uid, DB, URI } from '../../../services/api/test/harness.js';

const configPath = process.env.SCALPAL_PLAYMODE_CONFIG;
if (!configPath || !/^scalpal-test-office-[a-z0-9]+$/.test(DB) || new URL(URI).hostname !== '127.0.0.1')
  throw new Error('Office Play Mode requires its own loopback throwaway database and temporary configuration');
publishFresh();
const operator = await connect();
operator.conn.subscriptionBuilder().subscribe([tables.sessionEncounters]);
const sessionId = uid('ses_office_playmode');
await operator.conn.reducers.createSession({ sessionId, label: 'Office to OR synthetic fixture', exerciseId: 'lap_appendectomy', exerciseVersion: '0.1.0', displayName: 'fixture operator' });
const headsetInvite = await eventually(() => [...operator.conn.db.sessionInvites.iter()].find(i => i.sessionId === sessionId && i.role === 'headset'), 'owned headset invitation');
const coachInvite = await eventually(() => [...operator.conn.db.sessionInvites.iter()].find(i => i.sessionId === sessionId && i.role === 'coach'), 'owned coach invitation');
const initial = await eventually(() => [...operator.conn.db.mySessions.iter()].find(s => s.sessionId === sessionId), 'initial fixture session');
const initialAttemptId = initial.currentAttemptId, initialAttemptCount = initial.attemptCount;
const bridge = new RealtimeBridge({ uri: URI, database: DB, tokenFile: join(dirname(configPath), 'coach.token'), inviteCode: coachInvite.code, log: () => {} });
bridge.start();
await eventually(() => bridge.bound, 'real coach bridge membership');
// Provider calls are prohibited. SpacetimeDB uses the loopback ws transport independently.
globalThis.fetch = async () => { throw new Error('Provider/external fetch is disabled in the office fixture'); };
const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, realtime: bridge });
const routes: string[] = [];
const coachCreates: { body: Record<string, unknown>; reply: Record<string, any> }[] = [];
let encounterCreates = 0, providerUnavailableCount = 0;
app.get('/fixture/state', c => {
  const session = [...operator.conn.db.mySessions.iter()].find(s => s.sessionId === sessionId);
  const encounters = [...operator.conn.db.sessionEncounters.iter()].filter(e => e.sessionId === sessionId);
  const encounter = encounters[0];
  const coach = coachCreates[0];
  return c.json({ sessionId, initialAttemptId, initialAttemptCount, currentAttemptId: session?.currentAttemptId,
    attemptCount: session?.attemptCount, attemptRows: [...operator.conn.db.sessionAttempts.iter()].filter(a => a.sessionId === sessionId).length,
    encounterRows: encounters.length, encounterCreates, providerUnavailableCount, encounterId: encounter?.encounterId ?? '',
    encounterAttemptId: encounter?.attemptId ?? '', encounterPatientId: encounter?.patientId ?? '', encounterPhase: encounter?.phase ?? '',
    coachCreateCount: coachCreates.length, coachPatientId: coach?.body.patientId ?? '', coachEncounterId: coach?.body.encounterId ?? '',
    coachMode: coach?.body.mode ?? '', coachSessionId: coach?.reply.sessionId ?? '',
    coachCarryover: String(coach?.reply.systemPrompt ?? '').includes('FROM THE PRE-OP OFFICE'),
    coachContainsWrongProposal: String(coach?.reply.systemPrompt ?? '').includes('ureteroscopy'),
    coachContainsPatient: String(coach?.reply.systemPrompt ?? '').includes('Priya Ramaswamy'),
    coachProcedureId: coach?.reply.snapshot?.procedureId ?? '', routes });
});
const require = createRequire(new URL('../../../services/preop/package.json', import.meta.url));
const { serve } = require('@hono/node-server');
const server = serve({ hostname: '127.0.0.1', port: 0, fetch: async (request: Request) => {
  const path = new URL(request.url).pathname;
  if (!path.startsWith('/fixture/')) routes.push(`${request.method} ${path}`);
  if (path === '/jarvis/connection') {
    providerUnavailableCount++;
    return Response.json({ error: 'provider_unavailable', message: 'Synthetic fixture: provider voice is unavailable' }, { status: 503 });
  }
  const body = request.method === 'POST' && path === '/coach/sessions' ? await request.clone().json() : undefined;
  const reply = await app.fetch(request);
  if (request.method === 'POST' && path === '/encounters' && reply.status === 201) encounterCreates++;
  if (body && reply.status === 201) coachCreates.push({ body, reply: await reply.clone().json() });
  return reply;
}}, (address: { port: number }) => {
  writeFileSync(configPath, JSON.stringify({ uri: URI, database: DB, preferredSessionId: sessionId, joinCode: headsetInvite.code,
    coachBaseUrl: `http://127.0.0.1:${address.port}`, encounterBaseUrl: `http://127.0.0.1:${address.port}` }), { mode: 0o600 });
  console.log('SCALPAL_OFFICE_PLAYMODE_FIXTURE_READY');
});
let stopping = false;
for (const signal of ['SIGTERM', 'SIGINT'] as const) process.on(signal, async () => {
  if (stopping) return; stopping = true;
  try { await operator.conn.reducers.endSession({ sessionId }); }
  finally { bridge.stop(); operator.close(); server.close(() => process.exit(0)); }
});
