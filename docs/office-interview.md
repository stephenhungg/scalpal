# Office Interview (current flow)

Updated October 4, 2026. Decision (Matthew, final): the pre-op office is a 1:1 interview between the learner and the patient voice, driven by fixed rounds of four clinician moves. **Jarvis is a separate entity and is not in the office**: no attending phase, no Jarvis voice, the score is on screen only. Jarvis first speaks in the operating room, where he already has the patient's `patient_status.md` and how the interview went.

This replaces the free-form interview plus attending flow (`/encounters`). Those routes still work so the current Quest office build keeps running; move the office to `/interviews` and then they can be retired.

## Content (committed, same every run)

`services/preop/content/patients/<subjectId>/`, built by `npm run patients:build` from FinchNode demo data, the dossiers in `docs/patients/` and the authored encounters, then reviewed. The committed files are the source of truth; nothing is generated at request time.

| File | Used by | What it is |
| --- | --- | --- |
| `patient.md` | The patient voice agent | Who the patient is and everything they know, in their words. Never today's diagnosis or results. |
| `patient_status.md` | Jarvis in the OR | Clinical summary: presentation, findings, diagnosis, procedure, chart risks, what the learner should have elicited. |
| `interview.json` | The interview engine | `PatientInterview` (`services/preop/src/interview-types.ts`): opening line, 7 to 9 rounds (history, exam, tests, diagnosis, plan), four choices each, exactly one correct, at most one partial, weights summing to 100. |

## Flow

1. `POST /interviews {patientId}` returns `interviewId`, `patientName`, `speakerName`, `speaker` (`patient` or `parent`), `openingLine`, and `round` (the first round: `number`, `of`, `stage`, `prompt`, `choices: [{key, text}]`). Grades never leave the server. `404 no_interview` means the patient has no authored interview: go straight to surgery.
2. `GET /interviews/:id/connection` returns the patient voice binding: `agentId` (`INTERVIEW_PATIENT_AGENT_ID`, a tool-less ElevenLabs agent), `signedUrl`, `prompt`, `firstMessage` (the opening line), `voiceId`. Start the conversation with those overrides and **mute the mic** (`setMicMuted(true)`): the patient hears only the picks.
3. When the patient finishes speaking, show the round. The learner taps a choice, or holds a button and says it.
4. `POST /interviews/:id/answer` with one of:
   - `{key: "A" | "B" | "C" | "D"}` for a tap;
   - `{text}` for speech already transcribed on device;
   - `{audio, mimeType}` (base64, under ~20 s) for raw speech. The server transcribes it (ElevenLabs `scribe_v1`) and matches it to a choice (a bare letter or "option b" directly; otherwise Claude Haiku).

   `422 unclear_answer` with `heard` means re-ask ("Say A, B, C or D, or tap one"). Success returns (optional fields are omitted, never `null`, because Unity's JsonUtility turns null into an empty object):
   - `pick` (`{roundId, key, via, heard}`, no grade: scores show only at the end), `choice`, `heard`, `done`;
   - `finding` (only when the pick revealed an exam or test result; show it on screen, never spoken);
   - `next` (the next round, omitted after the last pick);
   - `scorecard` (only on the last pick);
   - `patient: {clinicianMove, direction, closing}`.
5. Make the patient reply: send `[DIRECTION] <direction>` as a contextual update, then `[CLINICIAN] <clinicianMove>` as the user message. On the last pick, append the closing line to the direction. Wait for the reply to finish, then show the next round.
6. After the last round, show the scorecard on screen (`GET /interviews/:id/score` returns it again). Nobody speaks it.
7. Scrub in: `POST /coach/sessions {patientId, mode, encounterId: <interviewId>}`. The coach opens with the Time-Out line; his prompt includes the interview carryover and `patient_status.md`.
8. Optional mirroring: `POST /interviews/:id/transcript {speaker: "patient" | "learner", text}`. Picks, transcript, phase and the result are mirrored to SpacetimeDB through the existing encounter tables (`kind: "choice"` events).

## Scorecard

`kind: "interview"`, plus fields compatible with the handoff:
- totals: `total`, `max` (100), `grade`;
- procedure: `procedureId` (always the surgery the case needs), `procedureTitle`, `procedureChosenCorrectly` (the plan round);
- diagnosis: `diagnosisResult` (`correct`, `partial`, `incorrect` or `missing`);
- context: `urgency`, `site`, `carryoverItems` (Time-Out risk chips, from the items the correct and partial picks cover);
- breakdown: `sections` per stage, and `rounds[]` with `{prompt, picked, best, points, max}`;
- `feedback[]` lines;
- `spoken: ""`.

## Quest office (Stephen)

- **Patient voice:** keep the patient seated and voiced as today. Use the `/interviews` connection, with mic muted.
- **Choices:** show them in the new dialogue box. A ray or tap posts `{key}`. For voice, hold a controller button and post audio (or on-device text).
- **Attending:** remove the attending step and the Jarvis attending connection.
- **Scorecard:** a panel, then the theatre card as today. `carryoverItems` and `procedureId` are unchanged.
