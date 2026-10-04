// Fresh identities and test-owned sessions on an already running local database.
// Never publishes/resets a database, connects to an operator identity, prints invites,
// or calls gateway/provider APIs. Ends only the sessions it creates.
let checks = 0, failures = 0;
function check(name: string, valid: boolean, observed = '') {
  checks++;
  console.log(`${valid ? 'PASS' : 'FAIL'} ${name}${!valid && observed ? ': ' + observed : ''}`);
  if (!valid) failures++;
}
async function main() {
  const uri = process.env.SCALPAL_CHECK_URI ?? 'ws://127.0.0.1:3000';
  const url = new URL(uri);
  if (!['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname) || url.protocol !== 'ws:')
    throw new Error('this check is restricted to unencrypted loopback SpacetimeDB');
  process.env.TEST_SPACETIMEDB_URI = uri;
  process.env.TEST_SPACETIMEDB_DB = process.env.SCALPAL_CHECK_DATABASE ?? 'scalpal';
  const { connect, eventually, uid } = await import('../../../services/api/test/harness.js');
  const clients: Awaited<ReturnType<typeof connect>>[] = [];
  const sessions: string[] = [];
  const deadlineConnect = async () => {
    const c = await Promise.race([connect(), new Promise<never>((_, reject) => {
      const timer = setTimeout(() => reject(new Error('local database connection timed out')), 8000);
      timer.unref();
    })]);
    clients.push(c); return c;
  };
  const operator = await deadlineConnect();
  const state = (sessionId: string) => [...operator.conn.db.sessionExerciseState.iter()].find(s => s.sessionId === sessionId)!;
  const rejected = async (name: string, operation: Promise<unknown>) => {
    try { await operation; check(name, false, 'reducer committed'); }
    catch { check(name, true); }
  };
  try {
    const first = uid('native_boundary');
    const second = uid('native_boundary_other');
    for (const id of [first, second]) {
      await operator.conn.reducers.createSession({ sessionId: id, label: 'isolated native boundary check',
        exerciseId: 'lap_appendectomy', exerciseVersion: '0.1.0', displayName: 'boundary fixture operator' });
      sessions.push(id);
    }
    const invites = await eventually(() => {
      const rows = [...operator.conn.db.sessionInvites.iter()].filter(row => row.sessionId === first);
      return rows.length === 4 ? rows : null;
    }, 'owned test invitations');
    const headset = await deadlineConnect();
    await headset.conn.reducers.joinSession({ code: invites.find(row => row.role === 'headset')!.code,
      displayName: 'boundary fixture headset' });
    const coach = await deadlineConnect();
    await coach.conn.reducers.joinSession({ code: invites.find(row => row.role === 'coach')!.code,
      displayName: 'boundary fixture coach' });
    await eventually(() => [...headset.conn.db.mySessions.iter()].some(row => row.sessionId === first), 'owned headset membership');
    const firstAttempt = `${first}-a1`;
    const args = {
      sessionId: first, attemptId: firstAttempt, mode: 'Practicing', stepId: 'inspect', stepIndex: 3, stepCount: 10,
      selectedStructureId: 'appendix', clearSelectedStructure: false,
      highlightedStructureId: 'appendix', clearHighlightedStructure: false,
      previewRotating: false, paused: true, registration: 'valid', registrationReason: 'synthetic authored fit', recording: 'off',
    };
    await headset.conn.reducers.publishExerciseState(args);
    await eventually(() => state(first)?.mode === 'Practicing', 'native-shaped snapshot');
    check('live companion state preserves pause independently of registration', state(first).paused && state(first).registration === 'valid');
    const commandId = uid('boundary_command');
    await coach.conn.reducers.requestCommand({ commandId, sessionId: first, action: 'highlightStructure',
      targetId: 'cecum', argBool: undefined, argNumber: undefined, expectedStepVersion: state(first).stepVersion });
    await eventually(() => [...headset.conn.db.sessionCommands.iter()].some(row => row.commandId === commandId), 'owned pending command');
    // The same sequence used by QuestSessionBridge: publish then reducer callback then resolve.
    await headset.conn.reducers.publishExerciseState({ ...args, highlightedStructureId: 'cecum' });
    await eventually(() => state(first).highlightedStructureId === 'cecum', 'confirmed scene snapshot');
    const committedVersion = state(first).stateVersion;
    await headset.conn.reducers.resolveCommand({ commandId, status: 'applied', reason: undefined });
    const acknowledged = await eventually(() => [...coach.conn.db.sessionCommands.iter()].find(row => row.commandId === commandId && row.status === 'applied'), 'confirmed command result');
    check('live applied acknowledgement points at committed scene state', acknowledged.resolvedStateVersion === committedVersion);
    await rejected('live terminal command acknowledgement cannot be overwritten',
      headset.conn.reducers.resolveCommand({ commandId, status: 'rejected', reason: 'late duplicate fixture reply' }));

    const token = headset.token; const identity = headset.identity.toHexString();
    headset.close();
    const again = await connect(token); clients.push(again);
    check('live headset reconnect restores same identity and scene state', again.identity.toHexString() === identity
      && [...again.conn.db.sessionExerciseState.iter()].some(row => row.sessionId === first && row.highlightedStructureId === 'cecum'));
    const pendingId = uid('boundary_pending');
    await coach.conn.reducers.requestCommand({ commandId: pendingId, sessionId: first, action: 'requestHint',
      targetId: undefined, argBool: undefined, argNumber: undefined, expectedStepVersion: state(first).stepVersion });
    await again.conn.reducers.startAttempt({ sessionId: first, exerciseId: 'lap_appendectomy', exerciseVersion: '0.1.0' });
    await eventually(() => state(first).attemptId !== firstAttempt, 'fresh owned attempt');
    check('new attempt expires prior pending command', [...operator.conn.db.sessionCommands.iter()]
      .find(row => row.commandId === pendingId)?.status === 'expired');
    check('new attempt clears previous anatomy selection', state(first).selectedStructureId === undefined,
      `selection=${state(first).selectedStructureId}`);
    check('new attempt invalidates previous registration', state(first).registration === 'unaligned',
      `registration=${state(first).registration}`);
    await rejected('prior attempt snapshot cannot update the fresh attempt', again.conn.reducers.publishExerciseState(args));
    await rejected('late authored event cannot attach to abandoned attempt', again.conn.reducers.appendExerciseEvent({
      sessionId: first, attemptId: firstAttempt, kind: 'touch', stepId: 'inspect', structureId: 'appendix',
      message: 'isolated stale-event regression', deviceTimeMs: 10,
    }));
    await rejected('authored event cannot attach an attempt from another session', again.conn.reducers.appendExerciseEvent({
      sessionId: first, attemptId: `${second}-a1`, kind: 'touch', stepId: 'inspect', structureId: 'appendix',
      message: 'isolated cross-session regression', deviceTimeMs: 11,
    }));
  } finally {
    for (const sessionId of sessions) {
      try { await operator.conn.reducers.endSession({ sessionId }); }
      catch { console.error('failed to end an owned synthetic test session'); process.exitCode = 1; }
    }
    for (const client of clients) client.close();
  }
  console.log(`SCALPAL_LIVE_REALTIME_CHECK checks=${checks} passed=${checks - failures} failed=${failures}; fresh test identities/sessions, existing local module, no database publish/reset`);
  if (failures) process.exitCode = 1;
}
main().catch(error => { console.error(String(error)); process.exitCode = 1; });
