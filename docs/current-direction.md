# Current Direction

**October 4 AR setup update:** Stephen removed the extra volunteer-consent confirmation for AR mode selection. AR availability depends on camera/spatial permissions and pose/coach readiness; accepted body registration still gates practice. Recording remains optional with separate learner and participant-in-frame permissions. The AR flow does not open permission dialogs; missing OS grants are restored in Quest settings. No checkbox is automatically accepted.


## Latest Experience Flow

**Final vision (October 4, 2026, Matthew): [operation flow](operation-flow.md).** It takes precedence over everything below and in older documents.

**Launch → explore patients → 1:1 patient interview with choice rounds (no Scalpal) → interview scorecard → operating room in AR (real reclining person, Presage baseline vitals) or VR (layered virtual patient): Scalpal Time-Out, surgery breakdown and flythrough → free-form surgery with a step checklist, simulated vitals from blood loss, and possible patient death → recap → robot hand replay from VR controller motion.**

The office spec is [office interview](office-interview.md). Live state and coach context are in [surgery state](surgery-state.md). The dashboard is localhost and shows logs only, with no live POV on the web. Robot input is VR controller motion only; there is no hand camera or passthrough video.

Every FinchNode demo patient needs an authored diagnosis encounter: FinchNode lists 12 scenarios, 10 of them with a patient (`connect-cancelled` and `connect-failed` have none); 8 have an authored case plan and 3 have an authored encounter, so 7 encounters are missing. Surgical simulations must exist for each procedure a diagnosis can lead to. See the [demo flow](demo-flow.md) for acceptance details, [experience UX](experience-ux.md) for the recommended interaction design, [office to OR handoff](office-to-or-handoff.md) for the AR/VR handoff spec, [surgery procedure](surgery-procedure.md) for the OR procedure build spec, [surgery state](surgery-state.md) for live state tracking and coach context, and the [integration map](system-integration.md#explore--office--or-route) for the current gaps.

## Diagnosis Office Component

The Quest 3S diagnosis office is a core stage of the latest flow. Its current independently buildable checkpoint supports two fictional adult cases, Matthew's existing encounter engine, distinct patient/attending voices and a usable visual fallback. Its Blender room follows the user's flowery MHacks theme request, with CC0 MakeHuman characters and rounded glass panels using Inter. Explore-selected subject IDs, remaining patient encounters and a same-attempt OR handoff are the next integration work; the forced-versus-learner-choice procedure policy remains unresolved. Existing surgical/tissue work is preserved. See [diagnosis office](diagnosis-office.md) for exact sources and verified limits.

## Abdominal Tissue Direction

The user selected appendectomy/abdomen for the material/physics expansion. The focused tissue milestone adds39 shared-frame exterior, abdominal wall, skeletal and vascular references (81 preview structures total). The current feature revision adds a connected three-layer tetrahedral abdominal wall, finite blade-driven topology cuts, sampled organ contact, per-cell material memory and geometry-driven bleeding controls. Editor mechanics and session-boundary checks passed. Contact preserves authored rest attachments; imported FBX coordinates explicitly convert to meters. These are uncalibrated teaching mechanics: measured material fits, broader contact coverage, vessel wall/lumen mechanics, high-fidelity tissue appearance and actual Quest performance remain unfinished. See [tissue simulation](tissue-simulation.md) and the [physics goal](physics-implementation-plan.md) for routing and evidence.

## Latest Body Registration Correction

The user requires automatic detection and calibration of a real reclining participant. The active revision replaces the previous three-controller-point plane with opt-in MediaPipe image landmarks plus acquisition-time MRUK85 native environment raycasting. After three stable observations the generic organ overlay aligns automatically. B confirms the case. Lost landmarks/depth pause the patient overlay and scoring; valid stable observations automatically reacquire. This remains generic teaching anatomy, not participant-specific organs. Exact depth/image sensor synchronization and physical fit are unverified; see [body registration](body-registration.md).


Updated October 3, 2026 after the user removed the monetary/onchain component. This page takes precedence over older architecture, flow, research, and team-plan documents where they disagree.

## Scope Change

Solana, wallets, onchain challenge programs, Devnet payouts, and money for completing simulated surgeries are removed from the project. They are not required integrations, acceptance checks, or assigned work. Earlier references to these features are historical context, not instructions to implement them.

## Remaining Thesis

AI guides people through mixed-reality learning and practice. Recorded passthrough video can supply estimated hand movements that are mapped onto a simulated robot hand for replay. The longer-term hypothesis is that useful demonstrations could support robot learning. Replay does not itself demonstrate a learned autonomous policy.

The working experience is the [latest experience flow](#latest-experience-flow) above. The earlier sequence (discuss an exercise with Scalpal → rotating anatomy preview → fit anatomy to a real reclining participant → practice → replay) is superseded.

## Latest Body Overlay Direction

The user explicitly requests MediaPipe detection of a person lying on a table and generic virtual anatomy attached to the body landmarks. The current implementation uses an opt-in ephemeral local pose service, calibrated Quest camera rays, an automatically measured anterior torso surface and stable acquisition; it does not infer metric body depth from MediaPipe z. Native presentation now defaults to AR, with a 42-part organ overview and the shared nine-target scored exercise. Physical detection/alignment remains the acceptance checkpoint. See [body registration](body-registration.md).

## Two Presentation Modes

**Latest:** the operating room offers both modes as a choice: AR on a real reclining person (MediaPipe body registration is main-path) or full VR with a virtual patient. The explore hub and diagnosis office are full VR.

The user now explicitly wants both **mixed reality with a real reclining participant and virtual organs** and **full VR with a virtual patient and operating room**. Both use one coach, tool system and authored exercise flow. Surface detection, body landmarks and validated anatomy registration are separate jobs; a table/person box does not determine organ placement. See [mode engineering](environment-modes.md) and [environment-source research](research/surgery-environments.md).

A static CC0 room/patient preview is prepared in `apps/quest/Assets/Scalpal/Environment/`. The native workbench reuses it with OpenXR and the tool station; the [native session](native-session.md) adds nine selected anatomy meshes and an authored full-VR appendectomy rehearsal. The focused AR revision adds a two-mode switch and local body registration; physical reclining-person fit remains unverified.

## Work Status

The user previously asked to wait for Nathan's plan, then requested storage/realtime planning and explicitly assigned the updated implementation lane: **Nathan owns the companion website plus SpacetimeDB and routing; Matthew continues to own Scalpal**. See [Nathan's implementation plan](nathan-plan.md) and [data/storage design](data-and-realtime.md). The earlier blanket wait does not block this assigned lane. The active team implementations are consolidated on main; the [integration map](system-integration.md) identifies their source snapshots and missing connections. The work-order documents describe their assigned target, not a complete deployment. The exact shared exercise, robot model, and other unresolved decisions remain open. No GitHub identity mapping for Nathan is assumed.

The companion website is a live observer view, not another voice agent. It combines shared session/coach/processing state with a separately transported composited headset video feed. The existing Mac mirror is a proposed video source to validate, not the final companion website or a verified network stream.

The repo contains an openable Unity project at `apps/quest`, committed scenes/assets/packages/settings and the [instrument kit](../assets/instruments/README.md). The [hardware test](native-workbench.md) records deployment/tracking evidence and the user-confirmed tool-motion correction. Main now assembles Matthew's case/coach/anatomy, Nathan's shared-state adapter and a native transport for Matthew's existing Scalpal agent. Local service exchanges and synthetic Unity checks are verified; the full physical session and native voice remain unverified. Earlier camera/bottle results concern a separate experiment. Silas's merged processor supports right or left Shadow hands and kinematic MuJoCo replay. Video capture and real-person anatomy fit remain unresolved; the gateway worker/trajectory adapter is now implemented, with physical clip validation still required; see the [integration map](system-integration.md).

## Reusable Anatomy Direction

The user wants broad organ/vessel coverage supporting a growing network of surgery cases. Build on the existing atlas rather than generating a separate body for each case. The [anatomy research](research/comprehensive-anatomy.md) proposes stable identities, verified anatomical relationships, explicit interaction capabilities, regional loading and per-case coverage gates. The manifest names 4,031 parts, but catalog coverage is not a complete runtime or clinically reviewed simulator. The native scene currently uses nine meshes; the focused tissue branch adds three small grasp-deformation cages; no general tissue/vascular behavior graph exists. First complete the physical single-case slice, then expand reusable regional content and behaviors. [TAPNet research](research/tapnet.md) covers an optional visible-point tracking experiment for recorded video, not hidden-organ detection.
