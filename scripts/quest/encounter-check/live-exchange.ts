// Real loopback HTTP -> single EncounterSession -> actual SpacetimeDB coach bridge.
// Recorded synthetic FinchNode cases, fresh identities, test-owned session. No providers.
import { createRequire } from 'node:module';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createApp } from '../../../services/preop/src/app.js';
import { fixtureClient, NOW } from '../../../services/preop/test/helpers.js';
import { DbConnection, tables } from '../../../services/preop/src/module_bindings/index.js';
import { RealtimeBridge } from '../../../services/preop/src/realtime-bridge.js';

const requirePreop = createRequire(new URL('../../../services/preop/package.json', import.meta.url));
const { serve } = requirePreop('@hono/node-server');
let checks = 0;
function check(name: string, valid: unknown) {
  checks++;
  if (!valid) throw new Error(`FAIL ${name}`);
  console.log(`PASS ${name}`);
}
async function eventually<T>(read: () => T, label: string): Promise<NonNullable<T>> {
  const end = Date.now() + 8000;
  while (Date.now() < end) {
    const value = read();
    if (value) return value as NonNullable<T>;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw new Error(`timed out: ${label}`);
}

async function main() {
  const uri = process.env.TEST_SPACETIMEDB_URI ?? '';
  const database = process.env.TEST_SPACETIMEDB_DB ?? '';
  const url = new URL(uri);
  if (url.protocol !== 'ws:' || !['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname) || !database.startsWith('scalpal-test-'))
    throw new Error('require explicit loopback TEST_SPACETIMEDB_URI and scalpal-test-* database');
  const directory = mkdtempSync(join(tmpdir(), 'scalpal-encounter-'));
  const connections: DbConnection[] = [];
  const connect = () => new Promise<DbConnection>((resolve, reject) => {
    DbConnection.builder().withUri(uri).withDatabaseName(database)
      .onConnect(conn => {
        connections.push(conn);
        conn.subscriptionBuilder().onApplied(() => resolve(conn)).onError(reject)
          .subscribe([tables.mySessions, tables.myMemberships, tables.sessionInvites, tables.sessionEncounters, tables.sessionEncounterEvents, tables.sessionCoachMessages]);
      }).onConnectError((_conn, error) => reject(error)).build();
  });
  const operator = await connect();
  const outsider = await connect();
  const sessionId = `office_${crypto.randomUUID().replaceAll('-', '')}`;
  let bridge: RealtimeBridge | undefined;
  let server: ReturnType<typeof serve> | undefined;
  try {
    await operator.reducers.createSession({ sessionId, label: 'synthetic office encounter exchange', exerciseId: 'lap_appendectomy', exerciseVersion: '0.1.0', displayName: 'fixture operator' });
    const invite = await eventually(() => [...operator.db.sessionInvites.iter()].find(row => row.sessionId === sessionId && row.role === 'coach'), 'fresh coach invitation');
    bridge = new RealtimeBridge({ uri, database, tokenFile: join(directory, 'coach-token'), inviteCode: invite.code, log: () => {} });
    bridge.start();
    await eventually(() => bridge!.bound, 'actual coach bridge binding');
    check('fresh coach bridge joins owned session', bridge.status().sessionId === sessionId);
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, realtime: bridge });
    server = serve({ fetch: app.fetch, hostname: '127.0.0.1', port: 0 });
    if (!server.listening) await new Promise<void>(resolve => server!.once('listening', resolve));
    const address = server.address();
    if (!address || typeof address === 'string') throw new Error('missing HTTP address');
    const call = async (method: string, path: string, body?: unknown) => {
      const response = await fetch(`http://127.0.0.1:${address.port}${path}`, { method, headers: { 'Content-Type': 'application/json', connection: 'close' }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
      return { status: response.status, json: await response.json() as Record<string, any> };
    };
    for (const [subject, name] of [['patient-demo-multi-source', 'Priya Ramaswamy'], ['patient-demo-sparse', 'Jonah Okoye']]) {
      const created = await call('POST', '/encounters', { patientId: subject });
      check(`${name} authored creation`, created.status === 201 && created.json.patientName === name && created.json.speaker === 'patient');
      const id = created.json.encounterId as string;
      const row = () => [...operator.db.sessionEncounters.iter()].find(e => e.encounterId === id);
      await eventually(row, 'encounter visible to operator');
      check(`${name} real shared encounter identity`, row()!.patientId === subject && row()!.sessionId === sessionId && row()!.attemptId === `${sessionId}-a1`);
      check(`${name} unjoined identity cannot read encounter`, ![...outsider.db.sessionEncounters.iter()].some(e => e.encounterId === id));
      check(`${name} score hidden during interview`, (await call('GET', `/encounters/${id}/score`)).status === 409);
      const answered = await call('POST', `/encounters/${id}/tools/answer`, { topic: 'allergies' });
      check(`${name} authored patient history and clean visual display`, answered.status === 200 && answered.json.result.toLowerCase().includes(subject.includes('sparse') ? 'penicillin' : 'latex') && answered.json.display.toLowerCase().includes(subject.includes('sparse') ? 'penicillin' : 'latex') && !answered.json.display.includes('FACT for you'));
      await call('POST', `/encounters/${id}/tools/examine`, { maneuver: 'rebound' });
      await call('POST', `/encounters/${id}/tools/order_test`, { test: 'cbc' });
      await call('POST', `/encounters/${id}/transcript`, { speaker: 'patient', text: 'Synthetic patient transcript fixture.' });
      const events = () => [...operator.db.sessionEncounterEvents.iter()].filter(e => e.encounterId === id);
      await eventually(() => events().length === 4, 'real mirrored action events');
      check(`${name} actual interview/exam/test events preserved`, ['history', 'exam', 'test', 'transcript'].every(kind => events().some(e => e.kind === kind)));
      check(`${name} patient and Jarvis transcript roles distinct`, events().find(e => e.kind === 'transcript')!.speaker === 'patient');
      await call('POST', `/encounters/${id}/attending`);
      await eventually(() => row()!.phase === 'attending', 'real attending phase');
      check(`${name} late patient action rejected`, (await call('POST', `/encounters/${id}/tools/answer`, { topic: 'onset' })).status === 409);
      const summary = (await call('POST', `/encounters/${id}/tools/get_encounter_summary`, {})).json.result as string;
      check(`${name} Jarvis summary contains only gathered facts`, summary.includes('allergies') && summary.includes('rebound tenderness') && !summary.includes('pain migration') && !summary.includes('Say this in your own'));
      const assessment = { diagnosis: subject.includes('sparse') ? 'perforated appendicitis' : 'acute appendicitis', differential: ['gastroenteritis', 'kidney stone', 'diverticulitis'], procedure: 'laparoscopic appendectomy', urgency: subject.includes('sparse') ? 'emergency' : 'urgent' };
      check(`${name} attending assessment accepted`, (await call('POST', `/encounters/${id}/tools/record_assessment`, assessment)).status === 200);
      const card = (await call('GET', `/encounters/${id}/score`)).json.scorecard;
      await eventually(() => row()!.phase === 'scored', 'real scored result');
      check(`${name} shared score matches authoritative HTTP card`, row()!.scoreTotal === card.total && JSON.stringify(JSON.parse(row()!.scorecardJson!)) === JSON.stringify(card));
      check(`${name} score based on actual missing actions`, card.criticalMissed.length > 0 && card.total < 100);
      check(`${name} repeated assessment rejected`, (await call('POST', `/encounters/${id}/tools/record_assessment`, assessment)).status === 409);
      await eventually(() => events().some(e => e.kind === 'assessment'), 'real assessment event');
      check(`${name} rejected attempts add no history events`, events().filter(e => e.kind === 'history').length === 1);
    }
    check('voice provider remains unconfigured', (await call('GET', '/jarvis/connection?agent=patient')).status === 503);
    console.log(`SCALPAL_ENCOUNTER_EXCHANGE_OK checks=${checks}; actual loopback HTTP and SpacetimeDB, synthetic cases only; no headset/provider test`);
  } finally {
    bridge?.stop();
    if (server) await new Promise<void>(resolve => server!.close(resolve));
    await operator.reducers.endSession({ sessionId }).catch(() => {});
    for (const conn of connections) conn.disconnect();
    rmSync(directory, { recursive: true, force: true });
  }
}
main().catch(error => { console.error(String(error)); process.exitCode = 1; });
