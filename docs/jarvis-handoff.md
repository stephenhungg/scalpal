# Jarvis Handoff: Unity, Blender, and SpacetimeDB

Updated October 3, 2026. Owner: Matthew. This is what other lanes need to do so Jarvis (the voice coach) knows exactly what is happening in the headset. Jarvis code lives in `services/preop/` (coach engine, routes, `/jarvis` page) and `apps/quest/Assets/Scalpal/Exercises/Coach/CoachRelay.cs`.

Status: the coach engine, per-case prompts, routes, laptop voice page, and Unity relay are implemented and tested in code (`npm test`, `npm run test:unity`). The ElevenLabs agent (Claude Sonnet 5.5, "Jarvis" library voice) was exercised live in text mode over its websocket on October 3: the per-case prompt override applied, it called `get_hint` and `highlight_structure` on its own, answered an urgent [SIM EVENT] with "Stop" plus the correction, and refused a structure from another surgery. Measured text response time was 1.4 to 3.2 s, before speech synthesis; spoken end-to-end latency has not been measured. Nothing has run on the Quest yet.

## How it fits together

```text
Quest (Unity)                          Mac (services/preop :8787)                 Laptop browser /jarvis
CaseRunner.Handle(e) ──Forward(e)──▶  POST /coach/sessions/:id/events ──▶ CoachSession ──SSE──▶ ElevenLabs agent (voice)
gaze raycast ──────────Focus(id)──▶   (focus, tracking, touch, identify...)       │                 │
torso tracker ─────────Tracking(ok)─▶                                             │                 ▼ client tools
highlight anat_<id> ◀─CommandRequested── GET /coach/sessions/:id/commands ◀── highlight_structure
                       Ack(id, ok) ──▶  POST .../commands/:cid/ack
```

The laptop page starts the session for a patient. The headset loads the same patient's case and adopts that session. Jarvis never decides progress itself: the coach replays the same events as `CaseRunner` (identical semantics, tested in both languages) and tells Jarvis the step, what is left, danger structures, and how stuck the learner is.

## Stephen: Blender

Build one generic adult torso anatomy set. Each structure is its **own object**, named exactly `anat_<id>`; Unity finds meshes by these names, and Jarvis can only highlight what exists.

**Required meshes per surgery** (generated from `services/preop/src/catalog/procedures.ts`, do not rename):

### Laparoscopic cholecystectomy (`lap_cholecystectomy`)

| Mesh name | Structure | Role |
| --- | --- | --- |
| `anat_abdominal_wall` | Anterior abdominal wall | context |
| `anat_umbilicus` | Umbilicus | context |
| `anat_liver` | Liver | danger |
| `anat_gallbladder` | Gallbladder | focus |
| `anat_cystic_duct` | Cystic duct | focus |
| `anat_cystic_artery` | Cystic artery | focus |
| `anat_common_hepatic_duct` | Common hepatic duct | danger |
| `anat_common_bile_duct` | Common bile duct | focus |
| `anat_right_hepatic_artery` | Right hepatic artery | danger |
| `anat_duodenum` | Duodenum | context |
| `anat_stomach` | Stomach | context |
| `anat_transverse_colon` | Transverse colon | context |
| `anat_greater_omentum` | Greater omentum | context |

### Laparoscopic appendectomy (`lap_appendectomy`)

| Mesh name | Structure | Role |
| --- | --- | --- |
| `anat_abdominal_wall` | Anterior abdominal wall | context |
| `anat_umbilicus` | Umbilicus | context |
| `anat_cecum` | Cecum | focus |
| `anat_appendix` | Vermiform appendix | focus |
| `anat_mesoappendix` | Mesoappendix | focus |
| `anat_appendicular_artery` | Appendicular artery | focus |
| `anat_terminal_ileum` | Terminal ileum | danger |
| `anat_small_bowel` | Small bowel | context |
| `anat_right_ureter` | Right ureter | context |
| `anat_urinary_bladder` | Urinary bladder | danger |
| `anat_greater_omentum` | Greater omentum | context |

### Laparoscopic sigmoid colectomy (`lap_sigmoid_colectomy`)

| Mesh name | Structure | Role |
| --- | --- | --- |
| `anat_abdominal_wall` | Anterior abdominal wall | context |
| `anat_umbilicus` | Umbilicus | context |
| `anat_small_bowel` | Small bowel | context |
| `anat_greater_omentum` | Greater omentum | context |
| `anat_descending_colon` | Descending colon | context |
| `anat_sigmoid_colon` | Sigmoid colon | focus |
| `anat_sigmoid_mesocolon` | Sigmoid mesocolon | context |
| `anat_inferior_mesenteric_artery` | Inferior mesenteric artery | focus |
| `anat_left_ureter` | Left ureter | focus |
| `anat_left_gonadal_vessels` | Left gonadal vessels | danger |
| `anat_rectum` | Rectum | focus |
| `anat_urinary_bladder` | Urinary bladder | danger |

Optional, used only to highlight chart risks in the pre-op brief: `anat_heart`, `anat_lungs`, `anat_right_kidney`, `anat_left_kidney`.

"Focus" structures are what the learner works on; "danger" structures are what a mistake touches. Both need clean, separately clickable geometry. Small structures (cystic artery, ducts, ureters) should be modeled slightly thicker than life so a hand-held instrument can hit them.

**Modeling, export, and budget:** follow [Unity handoff](unity-handoff.md). It is the source of truth for the torso frame, Blender scale and FBX settings, the 150k triangle budget, materials, and the `Scalpal > Validate Selected Anatomy Rig` check. [Unity asset manifest](unity-asset-manifest.md) is the generated full name list.

**What Jarvis adds:** color by tissue type (arteries red, veins blue, bile ducts green, ureters pale yellow) so a spoken hint like "the red one going into the gallbladder" matches what the learner sees. Every focus and danger structure above must have its own collider, because touches and gaze on them drive Jarvis's warnings.

## Stephen: Unity

1. **Import and validate** the rig as described in [Unity handoff](unity-handoff.md). Keep object names unchanged and parent it under the torso root that registration moves.
2. **Colliders:** every `anat_` object gets a collider (convex MeshCollider or primitives). Every `inst_<id>` instrument prefab gets a small trigger collider on its tip.
3. **Events.** One place in scene code turns physics and UI into `CaseEvent`s, and every event goes to both the runner and the relay:

   ```csharp
   var result = runner.Handle(e);
   relay.Forward(e);
   ```

   | What happens | Event |
   | --- | --- |
   | Instrument tip enters `anat_X` | `CaseEvent.Touch("X", instrumentId)` (strip the `anat_` and `inst_` prefixes) |
   | Trocar placed at a port marker | `CaseEvent.PlacePort(portId)` |
   | Learner points at a structure and selects "identify" | `CaseEvent.Identify("X")` |
   | Learner presses the step's confirm button | `CaseEvent.Confirm()` |
   | Gaze ray or instrument tip hovers a structure | `relay.Focus("X")` (relay sends only changes; `""` for none) |
   | Registration valid or uncertain | `relay.Tracking(isValid)` (also hide anatomy when false) |

4. **Session:** after `ScalpalPreopService.LoadCase(patientId)`, call `relay.AdoptCurrentSession(patientId)`. The laptop must have started Jarvis for that patient first.
5. **Highlights from Jarvis:**

   ```csharp
   relay.CommandRequested += cmd => {
       var go = GameObject.Find("anat_" + cmd.targetId);
       if (cmd.action == "highlight" && go != null) { /* emission or outline on */ }
       relay.Ack(cmd.commandId, go != null, go == null ? "mesh missing" : "");
   };
   ```

   Jarvis only says "highlighted" after an `applied` ack. While tracking is invalid, reject highlights (`Ack(id, false, "tracking lost")`).
6. **Networking:** set `baseUrl` on both `ScalpalPreopService` and `CoachRelay` to the Mac's LAN IP (`http://192.168.x.x:8787`). Android needs **Allow downloads over HTTP** for development builds (your ProjectSettings call) or an HTTPS deployment.
7. **Selection preview:** the same anatomy on a pedestal, rotating, before fitting. Jarvis voice actions for rotate/isolate are not wired yet; highlight is.

**Done when:** on the headset, touching the cystic artery with the clip applier three times advances the step on the laptop page, touching the common bile duct makes Jarvis say stop within a second, looking at a danger structure triggers a warning, and a Jarvis highlight lights up the right mesh and acks.

## Nathan: SpacetimeDB mapping

The coach's HTTP routes are the contract; moving them onto SpacetimeDB rows keeps semantics identical:

| Coach route today | SpacetimeDB equivalent |
| --- | --- |
| `POST /coach/sessions` `{patientId}` | session row with patient/case/procedure ids |
| `POST /coach/sessions/:id/events` `{events:[{type, portId, structureId, instrumentId, valid}]}` | ordered `exercise_event` rows (session id, sequence, fields as listed) written by the headset |
| `GET /coach/sessions/:id/stream` (SSE snapshots and alerts) | a coach worker subscribes to events, runs `CoachSession`, and writes `coach_state` (version, context text, snapshot JSON) and `coach_alert` rows |
| `POST .../commands`, `GET .../commands`, `POST .../commands/:cid/ack` | `scene_command` rows (command id, action, target id, status, reason); Jarvis inserts, headset acks |

Event types: `place_port`, `touch`, `identify`, `confirm`, `focus`, `tracking`. Ids are catalog ids (`services/preop/src/catalog/`); unknown ids are rejected. `CoachSession` stays in TypeScript as the worker, so we do not need to reimplement it in a reducer.

## Matthew: running Jarvis

```bash
cd services/preop
cp .env.example .env          # add ELEVENLABS_API_KEY
npm run jarvis:setup          # creates the agent and tools, prints ELEVENLABS_AGENT_ID for .env
npm run dev                   # open http://localhost:8787/jarvis
```

The simulate buttons on the page drive the coach without the headset. The agent LLM defaults to `claude-sonnet-5-5` (`JARVIS_LLM`); switch to `claude-haiku-4-5` if turn latency is noticeable.

## Feedback on Nathan's realtime contract v1 (from the Jarvis lane)

Reviewed `packages/contracts/realtime-v1.md` on `nathan/companion-realtime` (9bd6517). It fits Jarvis well. Four changes would let the coach run on it without losing signal:

1. **Raw exercise input, not only interpreted events.** `appendExerciseEvent` carries `kind`, `message`, `stepId`, `structureId`. Jarvis's wrong-instrument, off-target, and stuck detection need the raw input that produced those outcomes. Proposal: add input kinds `touch`, `place_port`, `identify`, `confirm`, `focus` with two more optional fields, `instrumentId` and `portId`, and keep the existing interpreted kinds. The headset appends both; the coach worker replays the input kinds through `CoachSession`.
2. **One command vocabulary.** Adopt Nathan's names (`highlightStructure`, `isolateStructure`, `restoreContext`, ...) and his `expectedStepVersion` stale check in the coach and `CoachRelay`; `clear_highlight` maps to `restoreContext`. His `applied | rejected | unavailable | failed` resolutions replace our `applied | rejected`.
3. **Step agreement check.** The headset publishes `stepId`/`stepVersion`; the coach derives its own step from input. If they disagree, the coach tells Jarvis to trust the headset and flags the desync instead of coaching the wrong step.
4. **Jarvis bridge identity.** The `/jarvis` page takes the `coach` role: it posts `postCoachMessage` for every learner and coach line, mirrors ElevenLabs mode changes into `setCoachStatus`, and gets its signed URL from a `voice` service grant instead of `/jarvis/connection`.

Until this is agreed, the HTTP coach routes stay as the working path.

## Resolved: HTTP from the headset

Resolved for the session build: `NativeSessionBuild` sets `InsecureHttpOption.DevelopmentOnly`. The workbench-only build still uses `NotAllowed`, which is fine because it does not talk to the coach. Original note kept below for context.


`codex/native-quest-workbench` (a895dab) sets `PlayerSettings.insecureHttpOption = InsecureHttpOption.NotAllowed` in `NativeQuestBuild.cs`. The coach and pre-op service run as plain HTTP on the Mac (`http://<mac-lan-ip>:8787`), so on the Quest every `CoachRelay` and `ScalpalPreopService` request would be refused: no events reach Jarvis, no highlights reach the headset, and cases load only from the offline bundle.

Pick one before the first integrated headset run:

1. **Development builds allow HTTP** (`InsecureHttpOption.DevelopmentOnly`). One line; release builds stay locked down. Recommended for the demo: no internet dependency between headset and laptop.
2. **HTTPS tunnel** (`cloudflared tunnel --url http://localhost:8787`) and point both `baseUrl` fields at the `https://` URL. Works with the current setting but adds an internet round trip to every event.

## Request for Stephen: tip proximity

`InstrumentTipContact.TouchApplied` fires only when the instrument is activated (trigger at least 0.7). Jarvis can warn before a mistake if it also knows what the tip is hovering over. Please call `relay.Focus(structureId)` when the held instrument's tip enters an `anat_` collider without activation, and `relay.Focus("")` when it leaves. The coach already turns focus on a danger structure into a one-time "careful, that's the common bile duct" warning.

Note: `inst_scalpel` exists in the Unity prefabs but not in the catalog. The coach rejects scalpel touches individually (the rest of the batch still applies). If the scalpel should count in a step, add it to `services/preop/src/catalog/instruments.ts`.

## Coach event contract for the headset relay

Updated after reviewing the relay rewrite on `codex/anatomy-atlas` (6057689). These are the server semantics the relay can rely on:

- **Event results.** `POST /coach/sessions/:id/events` returns one result per event: `{accepted, applied, reason}`.
  - `accepted: false` only for a malformed event (bad type, an id that does not match `^[a-z0-9][a-z0-9_.:-]{0,119}$`). That is a relay bug worth failing on.
  - `accepted: true, applied: false` means the coach received it and deliberately ignored it, exactly as `CaseRunner` does locally: `tracking_invalid` (registration lost) or `case_completed`. This is **not** a delivery failure; do not stop syncing on it.
  - Touches on atlas parts outside the catalog (`skeletal__rib_7_l`) and ports from another procedure are accepted and applied as off-target attempts, which feed Jarvis's stuck detection.
- **Identity check on adoption.** Before joining, compare `snapshot.patientId`, `snapshot.procedureId`, `snapshot.caseId`, and `snapshot.mode` (`mixed_reality` or `virtual`) with what the headset loaded. A mismatch means the laptop started a different case or mode; do not join.
- **Non-case contacts (proposed policy).** Forward an activated touch on any atlas part as a `touch` event, even outside the case. Neither engine scores it (no target, no mistake), but the coach counts it as an off-target attempt, which is what tells Jarvis the learner is lost. Raw hover stays `focus` only. Dropping these locally is safe but leaves Jarvis blind to that signal.
- **Fresh-attempt check.** Do not require `snapshot.version == 0` before joining. The version changes whenever Jarvis requests a highlight, gives a hint, or the stuck timer fires (20 s after start), so a headset joining late would always be refused. Use `snapshot.eventCount == 0` (no exercise input has reached the coach yet) together with `completedCount == 0` and `stepNumber == 1`.
- **Retries.** Add an `eventId` (unique per event, well-formed id) to each event. A repeat returns `{accepted: true, applied: false, reason: "duplicate"}`, so a timed-out batch can be resent without double-counting a clip.
- **Step authority.** Add `stepId` (the headset `CaseRunner`'s current step before handling the event) to each exercise event. The headset is the authority, per the integration map: if it is ahead, the coach catches up silently (`resyncCount` increments); if it is behind or unknown, `snapshot.desynced` is true and Jarvis is told to trust the headset until they agree. This addresses the two-engine divergence risk in `docs/system-integration.md`.

## Native Quest Voice: Current Main Status

Main consolidates Jarvis `64edc9e` and the native integration. `QuestJarvisVoice.ExecuteTool` already handled six built-in tools before this consolidation; the earlier claim that every tool returned unavailable incorrectly inferred behavior from the unknown-tool fallback in `NativeCaseSession`. Those six tools now use the shared `POST /coach/sessions/:sid/tools/:name` implementation with full JSON parameters. `NativeCaseSession` sends context only when `contextKey` changes. Fifty actual Unity coroutine/HTTP checks passed against isolated coach fixtures; authenticated voice and physical headset operation remain unverified.

Alert/reflex pacing below remains proposed native work. Keep the synthetic patient for this bounded scene; real-record selection requires its own deliberately integrated review/consent flow. The remaining section records the earlier branch review and recommendations, not current main behavior.

## Earlier Native Voice Handoff

Reviewed `codex/headset-session-integration` (c41372a, still current at 6ca92b5). That branch vendors an older copy of `services/preop` from before these endpoints existed: take `services/preop` from `matthew/jarvis` (b1fc5ed or later) before wiring the C# below, or `/tools`, `/alerts`, and `contextKey` will be missing. `QuestJarvisVoice` is a solid native transport, and the session build already allows development HTTP. As wired, though, the headset Jarvis loses most of its coaching. The coach server now exposes everything the headset needs, so the fixes are small C# changes in `NativeCaseSession`:

**1. Shared tools (implemented on main; earlier fallback diagnosis was incorrect).** Forward each tool call to the coach, which implements all six tools for every client:

```csharp
[Serializable] class ToolReply { public string result; }

void VoiceTool(QuestJarvisVoice.ToolRequest request) => StartCoroutine(RunTool(request, generation, coach.SessionId));

IEnumerator RunTool(QuestJarvisVoice.ToolRequest request, int epoch, string sid)
{
    string json = null;
    var body = string.IsNullOrEmpty(request.ParametersJson) ? "{}" : request.ParametersJson;
    yield return Request("POST", "/coach/sessions/" + Uri.EscapeDataString(sid) + "/tools/" + Uri.EscapeDataString(request.ToolName), body, v => json = v);
    if (epoch != generation) yield break;
    ToolReply reply = null;
    try { if (json != null) reply = JsonUtility.FromJson<ToolReply>(json); } catch (ArgumentException) { }
    voice.ResolveClientTool(request, reply?.result ?? "That tool is unavailable right now.", reply?.result == null);
}
```

`highlight_structure` waits up to 2 s for the headset's own ack (through `CoachRelay` command polling), so keep `CoachCommand` acking as it is.

**2. Context only on change (implemented on main).** The context text contains ticking timers, so every send is new. `GET /coach/sessions/:id` now returns `contextKey`, which changes only when something meaningful changes. Add `public string contextKey;` to `ContextReply` and send only when it differs from the last one sent.

**3. Alerts instead of "Simulator feedback".** Remove the `voice.SendUserMessage("Simulator feedback: ...")` call in `EventHandled` (it sends every mistake through the LLM, 1.5 to 3 s, with no pacing). Poll `GET /coach/sessions/:id/alerts?after=<latestSeq>` every 250 ms. Each alert has `tier`, `kind`, `stepId`, `reflexRoute` (a pre-rendered clip in Jarvis's voice, or ""), `reflexText`, and `simEvent` (the exact user message to send):

| Alert | Do |
| --- | --- |
| `warning` with `reflexRoute` (dangerous mistake, tracking loss) | Play the clip immediately on its own `AudioSource`, then `voice.SendContext("[JARVIS SAID] \"" + reflexText + "\"")` so Jarvis does not repeat it |
| `warning` without a clip | `voice.SendUserMessage(simEvent)` immediately |
| `caution` with `reflexRoute` (next-step callouts) | Play the clip when Jarvis is not speaking |
| other `caution` (moderate mistake, wrong tool, danger focus, stuck hint, case complete) | Keep only the newest per `kind`; drop it if `stepId` is no longer the current step; send `simEvent` when Jarvis is not speaking, the learner has been quiet for 1.5 s, and 6 s have passed since the last proactive turn (2.5 s for mistakes and completions) |
| `advisory` | Nothing; the context already carries it |

Download all clips at session start from `GET /jarvis/reflex/:id` (load with `UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG)`), so a warning plays with no network wait. The browser page's `services/preop/src/jarvis/arbiter.js` is the reference implementation of these rules, with tests in `test/arbiter.test.ts`.

**4. Demo patient.** `NativeCaseSession.PatientId` is hardcoded to `patient-demo-multi-source`. The real-data demo patient is sandbox Priya, `u_115958ef4e58c641` (needs `FINCHNODE_API_KEY` on the service). Make it configurable next to `coachBaseUrl` and keep the demo id as the fallback.


## Shared session: Jarvis on SpacetimeDB

Jarvis joins the shared SpacetimeDB session as the `coach` role (`services/preop/src/realtime-bridge.ts`) and writes everything live:

| What | Where in SpacetimeDB |
| --- | --- |
| Pre-op encounter: patient, phase (`interview` -> `attending` -> `scored`), score, full scorecard JSON | `encounter` (view `session_encounters`) |
| Every question topic, exam, test, transcript line, and the final assessment | `encounter_event` (view `session_encounter_events`) |
| What the learner, patient, Jarvis, and the simulator said (including instant warning clips) | `coach_message` (speakers `learner`, `patient`, `coach`, `system`) |
| Jarvis's voice status | `coach_status` |
| Jarvis highlights during surgery | `command` via `requestCommand(highlightStructure)`, resolved by the headset; Jarvis only says "highlighted" after `applied` |

The module changes are additive (two tables, four reducers for coach or operator, two views, `patient` speaker). Bindings are regenerated for the companion, gateway, Unity C# (`services/realtime/bindings/csharp`), and the coach service.

**Run it locally:** `spacetime start`, then in `services/realtime` run `npm run publish:local`. Start the coach service with `SPACETIMEDB_URI=ws://127.0.0.1:3000` and join a session with its coach invite code (`POST /realtime/join {"code": "..."}` or `SPACETIMEDB_COACH_INVITE`). `npm run realtime:e2e` in `services/preop` checks the whole path with three identities (operator, coach, simulated headset). Measured October 4 locally: all checks pass; highlight round trip (Jarvis -> SpacetimeDB -> headset applied -> Jarvis) about 50 ms.

**For Nathan:** the companion can show the encounter and scorecard from `session_encounters` / `session_encounter_events` with no new plumbing; the coach panel already shows `coach_message` and now includes patient lines. **For Stephen:** recopy `services/realtime/bindings/csharp` into `apps/quest/Assets/Scalpal/Realtime/Generated` only if the headset needs the encounter tables; nothing in the headset path requires it.

## Diagnosis office (Stephen's `codex/diagnosis-office`): transcript mirroring

Reviewed 59f1d98. The office uses the encounter engine as intended (one server scorer, patient voice per encounter, attending through Jarvis). One gap: `QuestJarvisVoice.Transcript` lines are not posted anywhere, so the shared session gets every question topic, exam, test, phase, and score, but not what was actually said. Post each final line:

- Encounter: `POST /encounters/:id/transcript {"speaker": "learner" | "patient" | "coach", "text": ...}` (user lines are `learner`; agent lines are `patient` while interviewing and `coach` while presenting to the attending).
- Surgery: `POST /coach/sessions/:id/transcript {"speaker": "learner" | "coach", "text": ...}` and `POST /coach/sessions/:id/voice-status {"status": "listening" | "speaking" | ...}` on mode changes.

The coach service writes these to `coach_message` and `encounter_event` in SpacetimeDB, so the companion shows the live conversation. Strip expressive tags like `[wince]` before posting (the browser uses `/\[[a-z ]{2,24}\]\s*/gi`).

## Bleeding and checkpoints (for Stephen's tissue model)

The coach accepts a `bleeding` event: `{"type": "bleeding", "structureId": "<anatomy id>", "active": true | false, "rateMlPerMin": <number>, "totalMl": <cumulative ml this attempt>}`. Send it through `CoachRelay` (or `POST /coach/sessions/:id/events`) when `VesselBleeding` opens an injury (`active: true`), periodically while it bleeds (rate updates are silent), and when it is controlled (`active: false`).

- Opening a bleed plays an instant warning clip in Jarvis's voice ("Stop. Bleeding from the appendicular artery. Get control first."), pre-rendered for every vessel in the case.
- While any bleed is active, Jarvis's context leads with it and his guidance becomes "control the bleeding first".
- Control is acknowledged silently in context with the running total.

Every completed step is now a checkpoint (`completedSteps[]`: seconds, mistakes, hints, blood loss at the time), and the context reminds Jarvis of earlier rough steps so he can refer back.

## State tracker events (tools in hand, tool contact)

Jarvis has two parts. The state tracker (`CoachSession` in `services/preop/src/coach.ts`) is deterministic: it turns headset events into facts and pushes a fresh `[LIVE SURGERY STATE vN]` context to the voice agent whenever something meaningful changes (300 ms debounce on the laptop page). The voice agent only talks from that context and its tools. Two events feed the tracker without scoring anything:

- `{"type": "instrument", "instrumentId": "scalpel", "hand": "left" | "right", "held": true | false}` when a tool is picked up or put down.
- `{"type": "contact", "instrumentId": "scalpel", "structureId": "skin"}` when a tool tip first touches a tissue or structure (send once per contact, not every frame). In open surgery, the first contact with a critical structure (cecum, terminal ileum, iliac vessels, ureter) gives a one-time caution.

Send them through `CoachRelay` / `POST /coach/sessions/:id/events` like the others. With them, the context shows "In hand: right scalpel" and a timed recent history ("0:03 Scalpel touched the skin. 0:05 Scalpel: cut the skin, 52 mm (cut across the fibers)."). Body actions (`surgery` events) and milestones appear in the same history in plain words.
