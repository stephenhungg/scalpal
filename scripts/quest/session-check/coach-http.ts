// Real production routes/engine over loopback HTTP, with recorded synthetic records.
// No live FinchNode client, voice provider, environment keys, or operator session.
import { createRequire } from 'node:module';
import { createApp } from '../../../services/preop/src/app.js';
import { legacyAppendectomyApp } from '../../../services/preop/test/legacy-appendectomy-fixture.js';
import { fixtureClient, NOW } from '../../../services/preop/test/helpers.js';

const requirePreop = createRequire(new URL('../../../services/preop/package.json', import.meta.url));
const { serve } = requirePreop('@hono/node-server');
type Json = Record<string, any>;
let checks = 0;
const failures: string[] = [];
function check(name: string, valid: boolean, observed = '') {
  checks++;
  console.log(`${valid ? 'PASS' : 'FAIL'} ${name}${!valid && observed ? ': ' + observed : ''}`);
  if (!valid) failures.push(name);
}

async function main() {
  let milliseconds = NOW.getTime();
  const app = createApp({ client: fixtureClient(), now: () => new Date(milliseconds), coachTickMs: 0 });
  // The port/clip mechanics below are the retained advanced procedure, never the default open case.
  app.route('/advanced', legacyAppendectomyApp({ now: () => new Date(milliseconds) }));
  const server = serve({ fetch: app.fetch, hostname: '127.0.0.1', port: 0 });
  if (!server.listening) await new Promise<void>(resolve => server.once('listening', resolve));
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('missing local listening port');
  const productionBase = `http://127.0.0.1:${address.port}`;
  const base = productionBase + '/advanced';
  const call = async (method: string, route: string, body?: unknown): Promise<Json> => {
    const response = await fetch(base + route, {
      method, headers: { 'content-type': 'application/json', connection: 'close' },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    const json = await response.json() as Json;
    if (!response.ok) throw new Error(`${method} ${route} failed with ${response.status}: ${json.error?.code ?? "unknown"}`);
    return json;
  };
  const start = async () => (await call('POST', '/coach/sessions', {
    patientId: 'patient-demo-pediatric-asthma', mode: 'virtual',
  })).sessionId as string;
  const event = (sid: string, value: Json) => call('POST', `/coach/sessions/${sid}/events`, { event: value });
  const state = async (sid: string) => (await call('GET', `/coach/sessions/${sid}`)).snapshot as Json;
  const command = async (sid: string) => (await call('POST', `/coach/sessions/${sid}/commands`, {
    action: 'highlight', structure: 'appendix',
  })).command as Json;
  const ack = async (sid: string, cid: string, status: string, reason: string) =>
    (await call('POST', `/coach/sessions/${sid}/commands/${cid}/ack`, { status, reason })).command as Json;
  try {
    const normal = await fetch(productionBase + '/coach/sessions', {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ patientId: 'patient-demo-pediatric-asthma', mode: 'virtual' }),
    });
    const normalSession = await normal.json() as Json;
    check('normal patient keeps the main-path open procedure', normal.ok && normalSession.snapshot?.procedureId === 'open_appendectomy');
    if (!normal.ok) throw new Error('normal coach session unavailable');
    const obsolete = await fetch(productionBase + `/coach/sessions/${normalSession.sessionId}/events`, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ event: { type: 'place_port', portId: 'umbilical' } }),
    });
    check('main-path open procedure refuses obsolete port mechanics', obsolete.status === 400);
    const paused = await start();
    await event(paused, { type: 'tracking', valid: false });
    const rejectedInput = await event(paused, { type: 'place_port', portId: 'umbilical',
      eventId: 'paused-activation', stepId: 'access_umbilical' });
    check('paused activation cannot score', !rejectedInput.results[0].applied && rejectedInput.snapshot.completedCount === 0);
    await event(paused, { type: 'tracking', valid: true });
    const retry = await event(paused, { type: 'place_port', portId: 'umbilical',
      eventId: 'paused-activation', stepId: 'access_umbilical' });
    check('old paused activation cannot become a fresh action after resume',
      retry.results[0].reason === 'duplicate' && retry.snapshot.completedCount === 0);
    const fresh = await event(paused, { type: 'place_port', portId: 'umbilical',
      eventId: 'new-activation', stepId: 'access_umbilical' });
    check('fresh activation after resume advances exactly one step', fresh.snapshot.completedCount === 1);
    // A later request uses a new HTTP connection, while identity is the explicit session id.
    const reconnectRead = await state(paused);
    check('HTTP reconnect preserves existing progression', reconnectRead.completedCount === 1 && reconnectRead.eventCount === 1);

    const independent = await start();
    check('new session resets progression and event deduplication', (await state(independent)).completedCount === 0);
    const reusedId = await event(independent, { type: 'place_port', portId: 'umbilical', eventId: 'new-activation' });
    check('event ids are scoped to a session', reusedId.results[0].applied && reusedId.snapshot.completedCount === 1);
    await event(independent, { type: 'place_port', portId: 'left_lower' });
    await event(independent, { type: 'place_port', portId: 'suprapubic' });
    check('second session does not mutate the first session', (await state(paused)).step.id === 'working_ports');

    const terminal = await command(paused);
    const rejected = await ack(paused, terminal.commandId, 'rejected', 'tracking unavailable');
    check('rejected scene result is a terminal acknowledgement', rejected.status === 'rejected');
    const terminalVersion = (await state(paused)).version;
    const duplicateAck = await ack(paused, terminal.commandId, 'rejected', 'different retry reason');
    check('duplicate terminal acknowledgement preserves original reason', duplicateAck.reason === 'tracking unavailable',
      `reason=${duplicateAck.reason}`);
    const lateAck = await ack(paused, terminal.commandId, 'applied', 'delayed old reply');
    check('late contradictory acknowledgement cannot rewrite rejected as applied', lateAck.status === 'rejected',
      `status=${lateAck.status}`);
    check('duplicate and contradictory terminal acknowledgements do not bump state version',
      (await state(paused)).version === terminalVersion);

    // At find_appendix, these are valid current-target identifies carrying stale metadata.
    const beforeStale = await state(independent);
    const stale = await event(independent, { type: 'identify', structureId: 'cecum',
      eventId: 'stale-step', stepId: 'working_ports' });
    check('behind stepId is flagged as desynchronized', stale.snapshot.desynced === true);
    check('behind stepId cannot score the current step', !stale.results[0].applied && stale.snapshot.step.remaining.length === 2,
      `applied=${stale.results[0].applied}, remaining=${stale.snapshot.step.remaining.length}`);
    check('behind stepId returns a specific non-scoring reason without increasing input count',
      !stale.results[0].accepted && !stale.results[0].applied && stale.results[0].reason === 'step_desynchronized'
      && stale.snapshot.eventCount === beforeStale.eventCount);
    const staleRetry = await event(independent, { type: 'identify', structureId: 'cecum',
      eventId: 'stale-step', stepId: 'find_appendix' });
    check('retry of rejected event preserves rejection even with changed metadata',
      !staleRetry.results[0].accepted && !staleRetry.results[0].applied
      && staleRetry.results[0].reason === 'step_desynchronized' && staleRetry.snapshot.eventCount === beforeStale.eventCount
      && staleRetry.snapshot.step.remaining.length === 2);
    const unknown = await event(independent, { type: 'identify', structureId: 'appendix',
      eventId: 'unknown-step', stepId: 'nonexistent_step' });
    check('unknown stepId cannot advance the current step', !unknown.results[0].applied && unknown.snapshot.step.id === 'find_appendix',
      `applied=${unknown.results[0].applied}, step=${unknown.snapshot.step.id}`);
    check('unknown stepId returns a specific non-scoring reason without increasing input count',
      !unknown.results[0].accepted && !unknown.results[0].applied && unknown.results[0].reason === 'step_desynchronized'
      && unknown.snapshot.eventCount === beforeStale.eventCount);
    const unknownRetry = await event(independent, { type: 'identify', structureId: 'appendix',
      eventId: 'unknown-step', stepId: 'find_appendix' });
    check('unknown-step rejected event remains rejected on retry',
      !unknownRetry.results[0].accepted && !unknownRetry.results[0].applied
      && unknownRetry.results[0].reason === 'step_desynchronized' && unknownRetry.snapshot.eventCount === beforeStale.eventCount);
    const aligned = await event(independent, { type: 'identify', structureId: 'cecum',
      eventId: 'fresh-aligned-cecum', stepId: 'find_appendix' });
    check('fresh aligned event restores synchronization and scores normally',
      aligned.results[0].accepted && aligned.results[0].applied && !aligned.snapshot.desynced
      && aligned.snapshot.eventCount === beforeStale.eventCount + 1 && aligned.snapshot.step.remaining.length === 1);

    // Commands currently carry no expected-step/version or expiry; document rather than invent a contract.
    const aged = await command(paused);
    milliseconds += 60_000;
    const pending = await call('GET', `/coach/sessions/${paused}/commands`);
    console.log(`OBSERVED coach command remains pending after 60 seconds=${pending.commands.some((c: Json) => c.commandId === aged.commandId)}; no expiry/version contract`);
  } finally {
    await new Promise<void>((resolve, reject) => server.close((error: Error | undefined) => error ? reject(error) : resolve()));
  }
  console.log(`SCALPAL_COACH_HTTP_CHECK checks=${checks} passed=${checks - failures.length} failed=${failures.length}; isolated loopback routes with fixture records`);
  if (failures.length) process.exitCode = 1;
}
main().catch(error => { console.error(String(error)); process.exitCode = 1; });
