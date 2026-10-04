// Test-owned local database + recorded chart fixtures; no provider or participant data.
import { createRequire } from 'node:module';
import { writeFileSync } from 'node:fs';
import { legacyAppendectomyApp } from '../../../services/preop/test/legacy-appendectomy-fixture.js';
import { publishFresh, connect, eventually, uid, DB, URI } from '../../../services/api/test/harness.js';
const require = createRequire(new URL('../../../services/preop/package.json', import.meta.url));
const { serve } = require('@hono/node-server');
if (!process.env.SCALPAL_PLAYMODE_CONFIG || !DB.startsWith('scalpal-test-')) throw new Error('Missing throwaway fixture configuration');
publishFresh();
const operator = await connect();
const sessionId = uid('ses_playmode');
await operator.conn.reducers.createSession({ sessionId, label: 'Native Play Mode fixture', exerciseId: 'lap_appendectomy', exerciseVersion: '0.1.0', displayName: 'fixture operator' });
const invite = await eventually(() => [...operator.conn.db.sessionInvites.iter()].find(i => i.sessionId === sessionId && i.role === 'headset'), 'owned headset invitation');
globalThis.fetch = async () => { throw new Error('Provider/external fetch is disabled in the Play Mode fixture'); };
// Explicit retained port exercise: both-mode lifecycle/PhysX regression evidence.
// Normal patient routes now select open_appendectomy; do not relabel this as that flow.
const app = legacyAppendectomyApp();
app.get('/fixture/state', c => {
 const state = [...operator.conn.db.sessionExerciseState.iter()].find(i => i.sessionId === sessionId);
 return c.json({ attemptId: state?.attemptId, highlighted: state?.highlightedStructureId ?? '', paused: state?.paused, mode: state?.mode });
});
app.post('/fixture/command/:action', async c => {
 const action = c.req.param('action');
 if (!['highlightStructure', 'pausePractice', 'resumePractice'].includes(action)) return c.json({ error: 'unsupported fixture action' }, 400);
 const state = [...operator.conn.db.sessionExerciseState.iter()].find(i => i.sessionId === sessionId)!;
 const commandId = uid('playmode_command');
 await operator.conn.reducers.requestCommand({ commandId, sessionId, action, targetId: action === 'highlightStructure' ? 'appendix' : undefined, argBool: undefined, argNumber: undefined, expectedStepVersion: state.stepVersion });
 const outcome = await eventually(() => [...operator.conn.db.sessionCommands.iter()].find(i => i.commandId === commandId && i.status !== 'pending'), 'actual headset command outcome');
 return c.json({ status: outcome.status, reason: outcome.reason }, outcome.status === 'applied' ? 200 : 409);
});
const server = serve({ fetch: app.fetch, hostname: '127.0.0.1', port: 0 }, (address: {port:number}) => {
 writeFileSync(process.env.SCALPAL_PLAYMODE_CONFIG!, JSON.stringify({ uri: URI, database: DB, preferredSessionId: sessionId, joinCode: invite.code, coachBaseUrl: `http://127.0.0.1:${address.port}` }));
 console.log('SCALPAL_PLAYMODE_FIXTURE_READY');
});
let stopping=false;
for (const signal of ['SIGTERM','SIGINT'] as const) process.on(signal, async () => {
 if(stopping)return;stopping=true;
 try { await operator.conn.reducers.endSession({ sessionId }); } finally { operator.close();server.close(()=>process.exit(0)); }
});
