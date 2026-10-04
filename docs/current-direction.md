# Current Direction

## Latest Experience Flow

The user replaced the selection and presentation flow on October 3, 2026. This section takes precedence over the conversational selection, rotating-preview and two-mode material below and in older documents.

**Launch → press Enter → patient explore page → choose a case → diagnosis office → decide the surgery → full-VR operating room → robot replay.**

1. **Launch.** The Quest app opens on a start screen. Pressing Enter (or the equivalent controller confirm) opens the explore page.
2. **Explore page.** A large browsable view of synthetic patients built from FinchNode demo data (`services/preop` `GET /patients`, `GET /patients/:id/brief`). Each patient is a case card with chart context. Choosing one opens that patient's encounter.
3. **Diagnosis office.** A full-VR doctor's office where the learner has a voice back-and-forth with the patient (voice agent grounded in the FinchNode chart plus authored presentation), examines and orders tests, then presents to the attending (Jarvis), who scores the diagnosis.
4. **Choose the surgery.** The assessment fixes which procedure the patient needs. That decision, not a separate selection conversation, determines the surgical case.
5. **Surgery simulation.** Full-VR operating room with a virtual patient, the shared tool/anatomy/exercise core and Jarvis coaching. The real-person MR body fit is no longer on the main path.
6. **Robot replay (required ending).** Raw passthrough video recorded during the surgery segment is processed into hand motion and replayed on the simulated robot hand. Every run ends with this step.

Every FinchNode demo patient needs an authored diagnosis encounter. Surgical simulations must exist for each procedure a diagnosis can lead to. See the [demo flow](demo-flow.md) for acceptance details and the [integration map](system-integration.md#explore--office--or-route) for the current gaps.

## Abdominal Tissue Direction

The user selected appendectomy/abdomen for the material/physics expansion. The focused tissue milestone adds39 shared-frame exterior, abdominal wall, skeletal and vascular references (81 preview structures total). The current feature revision adds a connected three-layer tetrahedral abdominal wall, finite blade-driven topology cuts, sampled organ contact, per-cell material memory and geometry-driven bleeding controls. Editor mechanics and session-boundary checks passed. Contact preserves authored rest attachments; imported FBX coordinates explicitly convert to meters. These are uncalibrated teaching mechanics: measured material fits, broader contact coverage, vessel wall/lumen mechanics, high-fidelity tissue appearance and actual Quest performance remain unfinished. See [tissue simulation](tissue-simulation.md) and the [physics goal](physics-implementation-plan.md) for routing and evidence.

## Latest Body Registration Correction

The user requires automatic detection and calibration of a real reclining participant. The active revision replaces the previous three-controller-point plane with opt-in MediaPipe image landmarks plus acquisition-time MRUK85 native environment raycasting. After three stable observations the generic organ overlay aligns automatically. B confirms the case. Lost landmarks/depth pause the patient overlay and scoring; valid stable observations automatically reacquire. This remains generic teaching anatomy, not participant-specific organs. Exact depth/image sensor synchronization and physical fit are unverified; see [body registration](body-registration.md).


Updated October 3, 2026 after the user removed the monetary/onchain component. This page takes precedence over older architecture, flow, research, and team-plan documents where they disagree.

## Scope Change

Solana, wallets, onchain challenge programs, Devnet payouts, and money for completing simulated surgeries are removed from the project. They are not required integrations, acceptance checks, or assigned work. Earlier references to these features are historical context, not instructions to implement them.

## Remaining Thesis

AI guides people through mixed-reality learning and practice. Recorded passthrough video can supply estimated hand movements that are mapped onto a simulated robot hand for replay. The longer-term hypothesis is that useful demonstrations could support robot learning. Replay does not itself demonstrate a learned autonomous policy.

The working experience is the [latest experience flow](#latest-experience-flow) above. The earlier sequence (discuss an exercise with Jarvis → rotating anatomy preview → fit anatomy to a real reclining participant → practice → replay) is superseded.

## Latest Body Overlay Direction

The user explicitly requests MediaPipe detection of a person lying on a table and generic virtual anatomy attached to the body landmarks. The current implementation uses an opt-in ephemeral local pose service, calibrated Quest camera rays, an automatically measured anterior torso surface and stable acquisition; it does not infer metric body depth from MediaPipe z. Native presentation now defaults to AR, with a 42-part organ overview and the shared nine-target scored exercise. Physical detection/alignment remains the acceptance checkpoint. See [body registration](body-registration.md).

## Two Presentation Modes

**Superseded for the main flow:** surgery now runs in full VR only. The MR real-person route and its registration work remain in the repository but are not required by the current flow.

The user now explicitly wants both **mixed reality with a real reclining participant and virtual organs** and **full VR with a virtual patient and operating room**. Both use one coach, tool system and authored exercise flow. Surface detection, body landmarks and validated anatomy registration are separate jobs; a table/person box does not determine organ placement. See [mode engineering](environment-modes.md) and [environment-source research](research/surgery-environments.md).

A static CC0 room/patient preview is prepared in `apps/quest/Assets/Scalpal/Environment/`. The native workbench reuses it with OpenXR and the tool station; the [native session](native-session.md) adds nine selected anatomy meshes and an authored full-VR appendectomy rehearsal. The focused AR revision adds a two-mode switch and local body registration; physical reclining-person fit remains unverified.

## Work Status

The user previously asked to wait for Nathan's plan, then requested storage/realtime planning and explicitly assigned the updated implementation lane: **Nathan owns the companion website plus SpacetimeDB and routing; Matthew continues to own Jarvis**. See [Nathan's implementation plan](nathan-plan.md) and [data/storage design](data-and-realtime.md). The earlier blanket wait does not block this assigned lane. The active team implementations are consolidated on main; the [integration map](system-integration.md) identifies their source snapshots and missing connections. The work-order documents describe their assigned target, not a complete deployment. The exact shared exercise, robot model, and other unresolved decisions remain open. No GitHub identity mapping for Nathan is assumed.

The companion website is a live observer view, not another voice agent. It combines shared session/coach/processing state with a separately transported composited headset video feed. The existing Mac mirror is a proposed video source to validate, not the final companion website or a verified network stream.

The repo contains an openable Unity project at `apps/quest`, committed scenes/assets/packages/settings and the [instrument kit](../assets/instruments/README.md). The [hardware test](native-workbench.md) records deployment/tracking evidence and the user-confirmed tool-motion correction. Main now assembles Matthew's case/coach/anatomy, Nathan's shared-state adapter and a native transport for Matthew's existing Jarvis agent. Local service exchanges and synthetic Unity checks are verified; the full physical session and native voice remain unverified. Earlier camera/bottle results concern a separate experiment. Silas's merged processor supports right or left Shadow hands and kinematic MuJoCo replay. Video capture and real-person anatomy fit remain unresolved; the gateway worker/trajectory adapter is now implemented, with physical clip validation still required; see the [integration map](system-integration.md).

## Reusable Anatomy Direction

The user wants broad organ/vessel coverage supporting a growing network of surgery cases. Build on the existing atlas rather than generating a separate body for each case. The [anatomy research](research/comprehensive-anatomy.md) proposes stable identities, verified anatomical relationships, explicit interaction capabilities, regional loading and per-case coverage gates. The manifest names 4,031 parts, but catalog coverage is not a complete runtime or clinically reviewed simulator. The native scene currently uses nine meshes; the focused tissue branch adds three small grasp-deformation cages; no general tissue/vascular behavior graph exists. First complete the physical single-case slice, then expand reusable regional content and behaviors. [TAPNet research](research/tapnet.md) covers an optional visible-point tracking experiment for recorded video, not hidden-organ detection.
