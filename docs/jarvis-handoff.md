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
