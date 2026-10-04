# Native Diagnosis Office

This separate full-VR encounter experience uses Matthew's existing patient-interview and attending assessment engine. It does not install a second local scorer or change the surgery scene, tissue mechanics, registration, or scored instrument path.

## Source and Ownership

The isolated branch starts at `0145544` (the stable abdominal tissue checkpoint, based on main `fc69836`). It merges Matthew's actual encounter/browser/realtime producer at `f61b1ceb96bd0ae46a8ff5771a48b745fa610c0f`, including interview `1ed5f76`, patient voice tools `94b399e`, clean summary `00926a9`, and browser flow `988d825`. The draft PR is stacked on `codex/abdominal-tissue-physics` so its diff contains the encounter dependencies and office work rather than repeating the prior AR/tissue changes.

Owned art: `assets/environments/doctor-office/` and `Assets/Scalpal/EncounterOffice/Art/`. Owned runtime and dedicated scene: `Assets/Scalpal/EncounterOffice/`. The working built-in renderer and Android OpenXR configuration are reused. The floral direction follows the user's request for a flowery MHacks theme; flowers and pastel botanical styling are original art, without claiming official branding.

## Authoritative Encounter Route

`POST /encounters` creates a fresh authored encounter. The office shows Priya Ramaswamy (female, 40, `patient-demo-multi-source`) or Jonah Okoye (male, 30, `patient-demo-sparse`). These are approved fictional synthetic demo cases. They must not be silently substituted with real FinchNode records or different case demographics.

Patient questions, selected examination maneuvers, and orders call `/encounters/:id/tools/answer`, `examine`, and `order_test`. Examination findings and test results render exclusively from the returned encounter state. Selecting an examination is an authored simulated action; it does not measure a learner's real physical examination technique.

`POST /encounters/:id/attending` changes the role to Jarvis. `get_encounter_summary` exposes what the learner actually gathered. `record_assessment` records the learner's diagnosis, differential, procedure and urgency; `/encounters/:id/score` supplies the deterministic attending scorecard. There is one authoritative server encounter engine. The clinical wording and rubric are authored teaching content, not a validated medical assessment.

The native voice transport reuses `QuestJarvisVoice`. `ConnectEncounter` verifies exact encounter/patient/phase identity. Patient connections use `/jarvis/connection?agent=patient`, the server's patient prompt/greeting and selected TTS voice. Attending connections use the existing Jarvis agent and attending prompt. Encounter tools reach the office's bounded handler; coach-session tools keep the existing surgery route. Changing role or case must disconnect the previous conversation and invalidate pending responses.

## Delivery Checklist

- [x] Isolated checkout and fixed source audit; preserved active volumetric checkout.
- [x] Native transport extension compiles against installed Unity Android assemblies; PCM and encounter identity checks pass.
- [ ] Original floral Blender office, editable sources, optimized exports, provenance and preview.
- [ ] Dedicated native encounter scene and usable visual fallback.
- [ ] Scene/client exchanges, lifecycle/role failures, scorecard and service gates.
- [ ] Verified commits, draft PR, concise final source and evidence handoff.

Physical headset comfort, XR controller usability, frame time, microphone/speaker behavior and live provider conversation are separate acceptance checkpoints. No participant capture is started by this experience. Provider credentials remain on the service. The final handoff records only checks actually run.
