import { describe, expect, it } from 'vitest';
import { createApp } from '../src/app.js';
import type { CoachSnapshot } from '../src/coach.js';
import { debriefText } from '../src/debrief.js';
import type { ReflexAudio } from '../src/reflex.js';
import { NOW, fixtureClient } from './helpers.js';

// The recap debrief is what the learner hears first: it must name the real outcome and the real mistakes
// from the session, never praise a run that went wrong, and stay short enough to listen to (2-4 sentences).
const snap = (over: Partial<CoachSnapshot> = {}): CoachSnapshot => ({
  procedureTitle: 'Open Appendectomy', stepCount: 4, completedCount: 4, elapsedSeconds: 290, mistakeCount: 0, hintsUsed: 0, bloodLossMl: 20,
  recentMistakes: [],
  checklist: ['a', 'b', 'c', 'd'].map((id) => ({ id, title: id, done: true, current: false })),
  condition: { outcome: { result: 'completed', cause: '', at: '' } },
  ...over,
} as unknown as CoachSnapshot);
const sentences = (t: string) => t.split(/(?<=\.)\s+/).filter(Boolean).length;
const mistake = (feedback: string, severity = 'high') => ({ stepId: 'x', mistakeId: 'm', severity, structure: 's', feedback, at: '' });

describe('debriefText', () => {
  it('summarises a clean run with milestones, time and no mistakes', () => {
    const t = debriefText(snap());
    expect(t).toContain('5 minutes');
    expect(t).toContain('4 of 4 milestones with no mistakes');
    expect(sentences(t)).toBeGreaterThanOrEqual(1);
  });

  it('leads with the high-severity mistake feedback, counts the rest, and gives one improvement', () => {
    const t = debriefText(snap({ mistakeCount: 3, recentMistakes: [mistake('Grazed the bowel', 'moderate'), mistake('You cut the artery before clamping it.')] as CoachSnapshot['recentMistakes'] }));
    expect(t).toContain('Main mistake: You cut the artery before clamping it.');
    expect(t).toContain('2 other mistakes');
    expect(t).not.toContain('no mistakes');
    expect(t).toContain('Next time');
  });

  it('reports death and blood loss honestly and appends a ready robot result, max 4 sentences', () => {
    const t = debriefText(snap({ bloodLossMl: 1234, mistakeCount: 1, recentMistakes: [mistake('Cut the iliac artery')] as CoachSnapshot['recentMistakes'],
      condition: { outcome: { result: 'died', cause: 'hemorrhage', at: '' } } as CoachSnapshot['condition'] }),
      { status: 'ready', stepTitle: 'Mark McBurney incision', success: true });
    expect(t.startsWith('We lost the patient: hemorrhage.')).toBe(true);
    expect(t).toContain('about 1230 millilitres');
    expect(t).toContain('The robot completed mark mcburney incision');
    expect(sentences(t)).toBeLessThanOrEqual(4);
  });

  it('omits a pending robot and reports an early end with unmet milestones', () => {
    const checklist = [{ id: 'a', title: 'a', done: true, current: false }, { id: 'b', title: 'b', done: false, current: true }];
    const t = debriefText(snap({ checklist, condition: { outcome: { result: 'ended', cause: '', at: '' } } as CoachSnapshot['condition'] }), { status: 'pending', stepTitle: 'x', success: null });
    expect(t).toContain('ended the open appendectomy early with 1 of 2 milestones');
    expect(t).not.toContain('robot');
  });
});

describe('GET /coach/sessions/:id/debrief', () => {
  it('returns text plus a cached mp3 route in the reflex voice, and text only when TTS fails', async () => {
    const spoken: string[] = [];
    let fail = false;
    const reflex = { configured: true, render: async (text: string) => { if (fail) throw new Error('down'); spoken.push(text); return Buffer.from('mp3'); } } as unknown as ReflexAudio;
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, reflex });
    const created = await app.request('/coach/sessions', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ patientId: 'patient-demo-polypharmacy' }) });
    const { sessionId } = await created.json() as { sessionId: string };
    const res = await app.request(`/coach/sessions/${sessionId}/debrief`);
    expect(res.status).toBe(200);
    const body = await res.json() as { text: string; audioUrl: string | null };
    expect(body.text.length).toBeGreaterThan(10);
    expect(body.audioUrl).toBe(`/coach/sessions/${sessionId}/debrief.mp3`);
    const audio = await app.request(body.audioUrl!);
    expect(audio.status).toBe(200);
    expect(audio.headers.get('content-type')).toBe('audio/mpeg');
    expect(spoken.every((t) => t === body.text)).toBe(true);
    fail = true;
    const degraded = await (await app.request(`/coach/sessions/${sessionId}/debrief`)).json() as { text: string; audioUrl: string | null };
    expect(degraded.audioUrl).toBeNull();
    expect(degraded.text).toBe(body.text);
    expect((await app.request('/coach/sessions/coach-missing123/debrief')).status).toBe(404);
  });
});
