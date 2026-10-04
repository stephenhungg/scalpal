import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compactVitals, parseData, toneOfLog } from '../src/lib/simlog.ts';

test('compact vitals from the coach MonitorVitals shape', () => {
  const d = { hr: 118.4, rr: 22, sys: 92, dia: 60, hemorrhageClass: 2, bloodLossPct: 18.2 };
  assert.equal(compactVitals(d), 'HR 118 · BP 92/60 · RR 22 · loss 18%');
});

test('compact vitals accepts a nested vitals object and aliases', () => {
  assert.equal(compactVitals({ vitals: { heartRate: 80, bp: '120/80', spo2: 97 } }), 'HR 80 · BP 120/80 · SpO2 97%');
  assert.equal(compactVitals({ note: 'nothing here' }), null);
  assert.equal(compactVitals(null), null);
});

test('parseData tolerates empty and invalid JSON', () => {
  assert.equal(parseData(''), null);
  assert.equal(parseData('{oops'), null);
  assert.deepEqual(parseData('{"a":1}'), { a: 1 });
});

test('alerts are red unless marked as a warning', () => {
  assert.equal(toneOfLog('alert', { severity: 'critical' }), 'bad');
  assert.equal(toneOfLog('alert', null), 'bad');
  assert.equal(toneOfLog('alert', { kind: 'mistake', tier: 'warning' }), 'bad');
  assert.equal(toneOfLog('alert', { kind: 'vitals', tier: 'caution' }), 'warn');
  assert.equal(toneOfLog('alert', { kind: 'bleeding_controlled', tier: 'advisory' }), 'muted');
  assert.equal(compactVitals({ hr: 72, sys: 121, dia: 78, rr: 14, spo2: -1 }).includes('SpO2'), false);
  assert.equal(toneOfLog('alert', { severity: 'warning' }), 'warn');
  assert.equal(toneOfLog('vitals', null), 'info');
});
