# Surgery Procedure Build Spec

Updated October 3, 2026. **Decision (Stephen): the operating room procedure is an open appendectomy**: scalpel through the abdominal wall, work directly in the wound. It runs in both OR modes (AR on a real reclining person, or full VR). Laparoscopic appendectomy is kept as a later "advanced" mode; its design and evidence are in [surgery procedure research](research/surgery-procedure.md). Handoff into the OR: [office to OR handoff](office-to-or-handoff.md). UX rules: [experience UX](experience-ux.md#5-operating-room-ar-or-vr).

Why open: in AR the learner cuts into the belly they can see and works with their hands in the wound, which is more legible to a first-time viewer than watching a camera monitor through ports. It needs no port pivoting, scope monitor or virtual camera assistant. It turns the existing layered abdominal-wall volume, blade cutting and vessel bleeding (`Anatomy/Runtime/Tissue/**`, `NativeVolumeSimulation`, `NativeVesselSimulation`) from unscored extras into the main path. Open appendectomy is a real, still-taught operation (preferred when laparoscopy is unavailable or converted); most appendectomies today are laparoscopic, which the recap can say.

## Architecture: Open Body, Cases on Top (Stephen, latest)

The body is not a scripted step sequence. The learner may do anything to it with any tool, and the simulation responds the same way in every case. Cases only define goals, guardrails and guidance; the coach and grader watch what actually happened.

1. **Body simulation (case-agnostic).** Every tool is a small set of verbs: cut, grasp/retract, clamp, tie, cauterize/seal, suction, mark, place. Every tissue has properties: layer and order (skin, fat, fascia with fiber direction, muscle that splits, peritoneum, bowel, cecum, appendix, mesoappendix, vessels), cuttable/splittable, perfused (bleeds when cut, stops when clamped/tied/sealed), hollow (cutting bowel leaks and contaminates), critical (iliac vessels, ureter). Tool × tissue rules produce the outcome: cutting a perfused vessel bleeds at a rate until it is clamped; cutting the appendix base without a tie leaks; splitting muscle along fibers is clean, cutting across it bleeds more. The learner can cut in the wrong place, open the wrong layer, nick bowel, or skip a step, and sees the real consequence. No case code decides what a tool does.
2. **Event log and body state.** Every action emits a structured event (time, tool, verb, tissue, layer, location in the registered torso frame, speed/force proxy, outcome), and the body keeps queryable state (layers opened, incision line and length, structures clamped/tied/divided, organs delivered, active bleeds and blood lost, contamination, specimen removed).
3. **Case = goals + guardrails + guidance (data, not code).** A case declares milestones as predicates over body state (appendix removed; stump tied within 5 mm of the cecum; mesoappendix vessels secured; no active bleed; wound closed), safety guardrails as predicates over events (bowel or cecum injured, iliac vessel cut, cutting before clamping, blade below the peritoneum without tenting), an expected order as a soft reference rather than a gate, and decision prompts (true base). Adding a case means authoring data, not new step code.
4. **Coach (Scalpal voice) and grader.** Deterministic detectors evaluate milestones and guardrails from the event log in real time; Scalpal receives those structured facts and gives guidance ("you're cutting across the muscle fibers; split them instead") and Socratic prompts, never inventing events or executing scene code. The grader scores at the end from the same log: milestones reached, guardrail violations, blood loss, order deviations, decisions, economy, hints. The LLM writes the recap feedback only from those facts.

Consequence of this design: the 10-row table below is the *expected* open appendectomy path that drives milestones, coaching lines and the fast path. It does not gate what the learner may do. A learner who goes off-path is coached and graded, not blocked; only physically impossible actions (cutting a layer you have not exposed) are prevented by the simulation itself.

## Where It Stands (origin/main, audited)

`open_appendectomy` is catalog data over the shared body-state engine. Appendicitis patient plans and encounter decisions now select it; the laparoscopic catalog remains available as an advanced reference. Ten milestone predicates and event guardrails observe case-independent actions without blocking off-order actions. Both service and Unity engines retain structured history, bleeding, contamination, clamp/tie geometry, true-base decisions and incomplete-attempt review.

The additive `Surgery/` runtime binds selected cases to tracked instruments, five teaching layers, actual atlas organ colliders, placed clamps/retractors, the existing vessel-fluid model, deterministic coach alerts/reflex clips and wound/audio feedback. The duplicated tip adapter and sandbox TrainingPatch were removed upstream in `5668231`; legacy tip scoring is disabled for an open-body attempt.

**Not a complete physical open operation yet.** Appendix delivery uses organ mobilization: the native atlas appendix sits about 163.2 mm below the wound and its local cage alone moves at most 18.1 mm, so a scene-authored mobile group (cecum, appendix, mesoappendix, appendicular artery) translates and slightly tilts as a unit while a grasper holds any member. The group follows the tool at a bounded speed within a 200 mm tether, stays out after release only if the appendix is above the wound plane, otherwise eases back, and returns to its exact rest pose on retry. Delivery depth is still measured on the moved tissue surface. Wall-layer cuts, the two-retractor muscle split and peritoneal tenting are now measured on the open-wall volume (see the open wall coupling checkpoint below); the coarse wall mesh still limits where a split can open. Landmark defaults and base-axis fallback are explicitly authored approximations. Physical AR/VR registration, tool feel, audio and a complete headset run remain unverified. See the runtime checkpoint below for exact verification and the owner interfaces still needed.

## Open Appendectomy Technique (source: StatPearls, Appendectomy, NBK580514, 2025)

McBurney incision one-third of the way from the anterior superior iliac spine to the umbilicus along Langer lines (or a transverse Rockey-Davis incision near it). Divide skin and subcutaneous fat; open the external oblique aponeurosis along its fibers; split (not cut) the internal oblique and transversus abdominis along their fibers; grasp the peritoneum with forceps and incise it with a scalpel. Locate the cecum and follow the taeniae coli to the appendix; deliver it into the wound. Dissect the mesoappendix, divide the appendiceal vessels between clamps and ligate them. Crush the base with a right-angle clamp, move the clamp distally, ligate the base, and excise the appendix with a blade; optionally invert the stump with a purse-string. Confirm hemostasis, close peritoneum and fascia, close skin. Keep the stump under 5 mm. Most common complication: surgical site infection.

## Expected Path (milestones and guidance, not a script; 9 learner steps, ~4–6 min)

| # | Step | Tool | Learner action | Success check | Mistake → feedback | Coach line |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | Landmarks + mark | Skin marker | Touch the hip bone (ASIS) and the umbilicus, then draw the incision line at McBurney's point | Line within 2 cm of McBurney's point, oblique along Langer lines, 5–8 cm | Line midline or too high → line glows amber, ghost line shown | "Find the hip bone and belly button. Mark a third of the way." |
| 1 | Skin incision | Scalpel | Draw the blade along the marked line in one controlled stroke | Skin layer divided along ≥ 80% of the line, depth only through skin + fat | Stroke off the line or too deep (blade below fascia) → red flash, buzz | "One smooth stroke along your line. Skin only." |
| 2 | Open the fascia | Scalpel or scissors | Nick the external oblique aponeurosis and extend along its fibers | Aponeurosis opened along the fiber direction (± 25°) | Cutting across fibers → amber, recap note | "Open the aponeurosis along its fibers." |
| 3 | Split the muscle | Retractors (two hands) | Insert both retractors and pull apart to split internal oblique and transversus | Split gap ≥ authored width (blade use stays a recorded guardrail and tissue penalty, not a gate) | Using the blade on muscle → "split, don't cut" | "Now split the muscle. Pull, don't cut." |
| 4 | Open the peritoneum | Forceps + scalpel | Tent the peritoneum with forceps, then nick it | Peritoneum tented before the nick; opening made | Nicking without tenting → bowel flashes red, "lift first" | "Lift the peritoneum first, then nick it." |
| 5 | Deliver the appendix | Babcock / grasper | Find the cecum, follow the taenia to the appendix, lift it out into the wound | Appendix delivered above the wound plane | Grabbing the inflamed body hard → perforation warning | "Follow the taenia to the appendix. Lift it out gently." |
| 6 | Divide the mesoappendix | Clamps ×2 + scissors + tie | Clamp the mesoappendix vessels twice, cut between, tie each side | Two clamps placed, cut between them, both tied; no active bleed | Cutting before clamping → arterial bleed, pool grows, coach alert | "Clamp twice, cut between, then tie." |
| 7 | **Decision + base** | Right-angle clamp + tie + scalpel | Answer "where is the true base?", crush the base, move the clamp up, tie the base, cut above the tie | Correct base; tie within 5 mm of the cecum; cut between tie and clamp | Tie too high → "stump too long" (stump appendicitis risk); cut below the tie → leak, red | "Crush, tie at the base, cut above your tie." |
| 8 | Check + clean | Suction / sponge | Suction or sponge the field, dwell on the stump and the mesoappendix | Pool below threshold; both dwells done | Leaving blood in the field → amber, recap note | "Dry field? Check the stump and the vessels." |
| 9 | Close | Auto (or confirm) | — | Auto: layers close in sequence, skin closes | — | "Close in layers. Nice work." |

Judge fast path: pre-marked line (skip 0) and auto-close; keep the skin incision, split, delivery and base ligation as the live beats.

## What Exists vs What to Build

| Need | Reuse | Build |
| --- | --- | --- |
| Layered wall (skin, fat, fascia, muscle, peritoneum) | `Tissue/Volume/*` three-layer volume and blade cutting | Add fascia/muscle/peritoneum layer identities and per-layer success checks; fiber direction for fascia; split (retract) behavior for muscle instead of cut; tenting for peritoneum |
| Incision placement | AR body registration landmarks (ASIS/umbilicus proxies), VR mannequin fit | Skin marker tool, McBurney landmark computation, line-quality check |
| Wound + retraction | Grasp/deform cage | Retractor tool that holds the wound open and stays put when released |
| Appendix delivery | Grasp deformation on appendix/mesoappendix | Lift-out-of-wound check; Babcock (reuse grasper with open-instrument look) |
| Clamps and ties | Clip applier logic (`apply_count`), vessel bleed model | Hemostat/right-angle clamp that stays clamped; "tie" action (tap clamp with suture tool) |
| Cut between clamps / base excision | Blade cut and vessel bleed | Authored division state changes driven by clamp + tie order |
| Bleeding | `NativeVesselSimulation` (opens on blade, sealing stops, suction drains) | Score it: active bleed blocks step success; counts as a safety error |
| Coaching | Server alerts, hint escalation, reflex clips | Deliver to the Quest; stall escalation ~15/25/40/60 s ending in auto-complete offer |

Open-surgery instruments to add (simple original models, same prefab pipeline as `InstrumentAssetBuilder`): skin marker, toothed forceps, two retractors (Army-Navy style), Babcock, two hemostats, right-angle clamp, Metzenbaum scissors, suture tie. Reuse scalpel, suction and electrocautery (hook cautery as a cautery pen).

## Mechanics Without Haptics

- Blade cuts follow the stroke; depth comes from how far the tip goes below the surface, shown by which layer opens. Snap only on commit actions (clamp lock, tie, stapler/bag in the lap mode).
- Force proxy: tissue deforms → blanches → red strain; hand speed over ~10 cm/s on tissue counts as rough handling.
- Controller vibration on blade contact, clamp lock, tie, and errors.
- Target zones and a ghost-instrument demo appear only after a stall.
- Feel, in order of payoff: sound (blade on skin, clamp ratchet click, suction slurp, monitor beeps), layered wound visuals with wet specular shading and fat texture, bleeding that responds to clamps, the existing grasp deformation.
- AR safety: keep controllers and virtual instruments at least 10 cm above a real participant's skin (untested precaution); the wound is virtual.

## Scoring and Coaching

Metrics: incision placement error, layers divided in order, step order, wrong-structure contacts (bowel, cecum, iliac vessels), max tissue stretch, active-bleed time and blood volume lost, stump length, decision answer, time per step, path length per hand, hints used. Proposed weighting: safety 50%, decisions 20%, economy 20%, tissue respect 10%, shown as a proficiency band. Coaching speaks one line under 12 words at step start, again only after a stall or an error; critique waits for the recap.

## Catalog Changes (`services/preop/src/catalog/procedures.ts`)

`open_appendectomy` is now the OR procedure for appendicitis case plans; `lap_appendectomy` remains in the catalog. The single `body_predicate` check evaluates the authored state goals through the existing engine and `CaseRunner.cs`; individual maneuvers do not add competing progression engines. Recap note: laparoscopic is the modern default; open is valid and preferred when laparoscopy is unavailable.

## Ownership

The surgery Codex thread owns open-surgery instruments, the step mechanics and checks, the catalog `open_appendectomy` procedure and engine check types, bleeding scoring and coach delivery to the Quest. The main Codex thread owns the tissue volume/layer physics and AR body registration; the surgery thread requests layer behaviors (fascia fibers, muscle split, peritoneum tenting) through it or coordinates small focused edits. The handoff thread owns the run context into the OR. Thresholds are design guesses to tune on the headset.

## Open-Body Build Checkpoint — October 3

Built: `open_appendectomy` now contains ten state-predicate milestones, event guardrails and the true-base decision as data. `engine.ts` and `CaseRunner` reduce the same case-independent tool verbs into persistent body facts and an copied action history. Actions carry registered-torso coordinates, an active-practice monotonic clock, measured geometry and tool instance IDs; there is no step ID in physical evidence. All milestones are evaluated after every action, off-path harms remain effective, and a later bleed invalidates the live no-bleed predicate. Missing measurements do not satisfy threshold predicates. Two distinct retractors, two clamps and ties on both sides, a distal moved clamp, stump length and the base answer have explicit checks. Expected order is soft guidance and recorded separately.

Component evidence: service suite244 passing / two live-provider skips and TypeScript typecheck; Unity batch65 synthetic body-state assertions; instrument build311 Editor assertions. The old scripted checks were removed. The catalog's older laparoscopic cases remain available while native open-body interaction/handoff integration is built. This checkpoint does not yet establish a physical tool-to-wound session, existing-vessel measurement exchange, AR registration, sound playback or complete OR run. The semantic reducer's authored bleeding rates and lumped tissue-level control are teaching approximations, not calibrated physiology.

Instrument assets are now generated and included: marker, toothed forceps, Army-Navy-style retractors, Babcock, hemostats, right-angle clamp, Metzenbaum scissors and suture tie. Two physical prefab instances each of retractor/hemostat are provided by `Resources/OpenSurgeryInstruments`; scalpels/suction/cautery use the existing assets. Original model source stays in `OpenInstrumentModels.cs`; no downloaded model license is introduced.

## Runtime Build Checkpoint — October 3

Built in `apps/quest/Assets/Scalpal/Surgery/`:

- Selected-case installation in either presentation mode; ten-instance original tool tray joins the existing15 tools and preserves equipment reset poses. Legacy tip scoring does not also score the open attempt.
- Measured marking, finite blade strokes, forceps lift/release, paired retraction, actual organ contact, placed clamps/ties, true-base choices, inspection and explicit finish through the existing scored event path. Two distinct tools are required for paired actions. A second dummy case uses the same verbs. Base-less contact still causes injury but cannot fabricate longitudinal milestone measurements.
- Five-layer teaching volume plus body-state-driven wound lips, measured marker line, original procedural blade/ratchet/suction audio, held-controller haptic requests, and a world-space decision/review panel. “Premark line (judge demo)” logs an assisted marker action; auto-close is recorded layer by layer only after all other live goals are met.
- Existing vessel-fluid model snapshots carry cumulative blood loss, pool and flow into body state. Clamp/tie/seal stop flow only at or proximal to the injury (a seal only at its own point; 3 mm tolerance along the structure), and removing a clamp re-bleeds an untied cut; physical pool suction cannot erase blood loss. Off-path bowel cuts contaminate and unsecured vessel cuts bleed.
- Existing coach alert polling delivers captions, hint escalation and reflex clips, with tracking/reset/old-step gates. The service receives structured facts; it cannot invent actions or complete an open milestone as a hint.
- Explicit finish freezes an illustrative, uncalibrated grade and lists missing goals. Safety, decisions and tissue-respect weights total80 available points; economy's20 points are unscored because hand paths and a validated economy rubric are unavailable. No proficiency claim is made.

Verification: preop270 passing / two provider skips and typecheck; Unity85 body-state checks and118 measured-adapter/real-fluid checks plus coach and actual scene binding audits. Original instrument validation311 checks remains the asset milestone. [Incision preview](../experiments/open-surgery-preview/incision.png) and [delivered appendix preview](../experiments/open-surgery-preview/delivered-appendix.png) are labeled synthetic virtual-camera component images.

Organ mobilization (October 3, later): `OrganMobilization` plus `OpenSurgerySession.mobileOrganGroups`. `OpenBodyDeliveryValidation` (run inside `OpenBodyInteractionValidation.Run`) uses the actual native scene, adapter and cage simulation. It shows that an unmobilized pull earns no delivery. A gentle Babcock pull delivers the appendix in both the default VR fit and a re-posed registered AR fit, including a mid-pull registration correction. Clamp/cut/tie on the moved mesoappendix and base then reach `divide_mesoappendix` and `ligate_base` with a 4.00 mm stump. Release below the plane springs back gradually to exact rest; retry restores rest; registration loss freezes the group; tracking loss and pose jumps release without a jump; a fast yank is still rough handling. These are synthetic tracked poses, not headset evidence.

Open wall coupling (October 4): `OpenSurgerySession` binds its open-wall `NativeVolumeSimulation` through `OpenBodyInteraction.BindWall`. Wall-layer contact comes from each layer's current exposed boundary/cut faces (`TryContactLayer`), sampled along the blade edge or at the tool tip. The shallowest touched layer the body has not opened is engaged; a stroke keeps its layer while every touched layer is open, and a blade passing through opened layers to a deeper one does not re-score them. A wall-layer blade stroke scores only if the volume reported newly broken faces of that layer (`LayerFractured`, verb `cut`) since the blade engaged the wall; fascia/muscle cut angles come from `OpenWallLayers.TryFiberAngle` on the stroke chord. Grasp/retract on a wall layer acquires a material grip (`TryBeginLayerHandle`), requests the hand's pose and reads accepted geometry after the solver step. Peritoneal `depthMm` is the gripped membrane's accepted outward lift (largest live grip, including a latched retractor's), never controller travel. Two distinct muscle grips that actually part by 0.5 mm request one finite split through their midpoint (authored ±30 mm along the opening line, muscle depth ±0.5 mm), which the volume refuses across the fibers; `separationMm` is `TryMeasureMuscleSplit` and `angleDegrees` is the opening line's fiber angle. A latched retractor keeps its grip after the hand lets go until it is picked up or unlatched. Without a bound wall, wall layers keep authored `OpenWallLayers` contact planes for marking and cutting but can never be tented or split. The reducers, thresholds and golden log are unchanged; only the producer's measurement source changed.

`OpenWallCouplingValidation` (inside `OpenBodyInteractionValidation.Run`, 92 checks) drives the actual native scene, composition and volume in the default VR fit and a re-posed registered AR fit. Skin, fat and fascia strokes each score once and open only that layer's volume faces at the stroke; the fascia stroke exposes muscle without a muscle cut. Two retractors 16 mm apart split the actual muscle (38.0 mm measured in both fits, fiber angle 0°), the rendered wall surface follows both lips and latched retractors keep it open. One retractor, or two pulled apart along the fibers, does not split. A real forceps lift tents the membrane (11 mm accepted) and the nick is tented; nicking a held but unlifted membrane is `untented_cut`/`lift_first`. Held grips allocate 0 managed bytes per adapter frame. Before this change the same scene validation failed at the split. Interaction suite 466 checks, `OpenBodyValidation` 147, `NativeOpenWallValidation` 10,627.

Limits: fracture follows the wall's 20 mm cells, so two grips on one tetrahedron (both on the x = 0 cell boundary at the wound centre, or a close pair) cannot be parted and earn no split. In a 12-placement sweep 9 split to about 38 mm; three (x = 0 with grips 10 or 16 mm apart, one 10 mm pair at x = -15 mm) did not. A finer or incision-aligned muscle mesh from the tissue owner is needed before headset use. Blade contact scans up to 6 samples × 5 layers per held blade near the wall; Quest frame time is unmeasured. The semantic wound view still draws from body state beside the generated wall.

Incisions and visible blood (October 4): presentation of facts the sim already owns; nothing new is scored or sent.

- **Incisions off the field.** `OpenBodyInteraction.BladeOutsideField` fires every frame a held, triggered scalpel or Metzenbaum is outside the field. `PatientIncisions` casts from the tool's grip to its tip against the mannequin's `PatientCollision` hull; where the blade has entered the skin it records the entry point, the tip's depth under the surface and the coarse body region in a bounded set of 64 persistent segments in registered torso metres (about one per 4 mm of travel, a puncture on first touch). The ring is fed to `Scalpal/PatientSkin` as two global vector arrays. The fragment shader draws a dark parted cut (opening grows with depth and stroke length, 0.3 to 2.2 mm), a reddened margin, a bead of blood along the cut, and rivulets that run downhill along gravity in torso space. A segment array was chosen over painting a UV mask: the collision hull has no UVs, the 1,578-triangle mannequin's UVs are not laid out for painting, and torso-space segments survive refits, give gravity directly and clear by zeroing a count. The loop is skipped entirely with no incisions; each segment costs a sphere reject when far. Cuts in an injured region stay wet until the interaction reports the region controlled, then dry dark; cuts outside any region have no accepted control fact and do not expire automatically. Inside the actual finite incision aperture the skin is cut away and the open wall and `OpenWoundView` keep the incision, so no segment is recorded there.
- **Blood flow.** `SurgeryBlood` owns one world-space `ParticleSystem` (300 drops, octahedral mesh, opaque `#8a0303` glossy tissue material, emission only from script). Sources are read every frame: perfused tissues the body reports `bleeding`, at their last cut, with `measuredFlowMlPerSecond` (or the tissue's authored rate before a vessel snapshot); and regions `OpenBodyInteraction.IsRegionInjured` reports, at their most recent incision, with the fresh coach condition's `rawBleedMlPerMin` (falling back to a mirror of `patient-condition.ts` REGIONS offline). Drops per second are `rateMlPerMin / 60 / 0.05 ml`, at most 120 per source (larger drops above that). At 60 ml/min or more a source is arterial and spurts in the first 30% of each beat at the condition's `vitals.hr` (72 bpm authored baseline without a fresh sample, no pulse once flatlined); below it a vein wells up. Control (clamp, tie or seal in the body; a hemostatic tool in the region) removes the source the same frame. Drops collide with static world colliders (patient hull, table, floor) with bounce 0.05, dampen 0.9 and lifetime loss 1, and each landing leaves a splat in a bounded 64-stain mesh that preserves older stains; nearby landings grow a splat instead.
- **Pool.** `OpenBodyBleeding`'s cavity pool now follows the reducer's `poolMl` (eased over 0.35 s across the 1 Hz snapshots): it spreads to its 4 cm cap and then deepens toward the opening (`PoolLevelMeters`). Pool suction lowers `poolMl` and the visible pool with it.
- **AR and retry.** In AR (passthrough) there is no virtual body: incisions are not recorded or drawn and no drops or splats are shown; the existing in-field wound visuals and pool remain. A genuinely new attempt re-composes both views empty; rebinding the same Body identity preserves them. Registration loss hides/pauses rather than clearing; inspection pauses retain the registered cuts, stains and cavity pool.

`OpenBodyBloodValidation` (inside `OpenSurgeryBuild.Verify`, 391 checks) uses the actual native scene, mannequin hull and session composition with synthetic tracked poses: a 40 mm chest stroke records 11 segments on the skin at the blade's 3 mm depth and feeds them to the shader; an in-field stroke still opens the wall's skin and adds no off-field segment; the cut appendicular artery (135 ml/min measured) emits 224 drops in 5 s against 224 expected and none after a proximal clamp; the pool tracks `poolMl` and suction lowers both (14.6 to 7.3 ml); neck, chest and arm emit 500, 249 and 33 drops in 5 s and a hemostat in the neck stops its flow and dries its cut; caps hold at 300 drops, 64 splats and 64 segments; AR draws nothing; retry clears everything. Shading, spurt arcs, particle-collision splats (Play Mode only) and Quest frame time are not verified on a headset. The interaction's coarse neck region needs the tip about 4 cm under this mannequin's neck skin before it reports an injury, so a shallow neck cut shows an incision but no neck bleed until it goes deeper.

Pending: a muscle mesh that splits wherever retractors are placed (above), headset tuning of the authored split onset/extent, reviewed base axes and actual registered ASIS/umbilicus inputs, headset AR/VR completion/performance/audio/haptic verification, and live provider/reflex playback. The handoff owner must update `NativeCaseSession.EventHandled` to report all open-body milestones/guardrails and distinguish incomplete finish from goal completion; its old one-event/one-step aggregate and warning narration remain separate from the correct local/service grade. Hash/sequence/resync and richer hand-path telemetry in [surgery state](surgery-state.md) are the subsequent approved work, not established by this checkpoint.

The shell packaging gate currently has a stale exact-count assertion (10 offline cases versus11 with the retained advanced variant). Its owner must update that fixture; the separate surgery packaging method does not claim the shell gate passed.

The standalone native coach HTTP exchange also passed168 assertions, including open-body injury, incomplete finish, lost-response retry and local/service grade parity. This uses the actual relay and an isolated service, without a live voice provider.

Android packaging succeeded at `91467b1`:79,779,574-byte ARM64 IL2CPP development APK, locally `artifacts/open-surgery.apk`, not installed. It contains the three scenes enabled at that source snapshot; later merged recap changes are outside this artifact. The open verification gate passed again during packaging.

Final synchronized checks: preop274 passing / two skipped, typecheck, and the open Unity gate85 body /118 interaction-fluid assertions plus coach and scene bindings.

## Persistent Injuries and Operative Appearance — October 4

The [visual audit](surgery-visual-audit.md) records the actual ten-stage graphics-backed Editor sequence and [research criteria](research/open-appendectomy-visuals.md). Wounds and stains persist through elapsed time, accepted bleeding control, inspection and registration recovery. A puncture beside the incision is no longer swallowed by the former rectangular skin exclusion. Fixed mark budgets retain old wounds instead of overwriting them; additional disconnected marks beyond64 remain a limitation.

The shared AR/VR composition now uses incision-local ordered tissue surfaces, a local measured membrane tent, a render-only cavity lining, source-envelope cecum appearance, accepted ligatures/divided source views and an explicit assisted closure seam. These views read existing facts and emit no progress. The actual wall/contact/topology remains active; semantic wound surfaces replace its rectangular draw. Source anatomy/contact is not modified by ligature, specimen or closure presentation. A specimen is an assisted separated display rather than a new grabbable physical body. The cecum/source layout and rigid mobile group still obscure the base from one learner angle; the mannequin remains low polygon. Neither the appearance nor the physics is clinically calibrated.

## Synthetic Demo Visual Exports — October 4

`Scalpal.Surgery.Editor.OpenStepVisualsValidation.ExportDemo` optionally exports
1920×1080 views while running the actual synthetic tracked-pose step fixture.
Set `SCALPAL_DEMO_RENDERS` to the output directory and run the Unity 6000.0.66f2
Editor with `-batchmode -quit -projectPath apps/quest -executeMethod
Scalpal.Surgery.Editor.OpenStepVisualsValidation.ExportDemo -logFile <log>`.
The existing `Run` validation views, pixel thresholds and scene anchors remain
unchanged. The export uses the same overhead camera for incision and closure,
with the 60 mm incision spanning approximately 40% of frame width. Organ stages
5–8 widen the camera to keep the delivered structures in view; scene text is
hidden only for the export and assisted-display provenance moves to the screen
caption. Runtime
`OpenWoundView` gloss is .20 skin, .35 fat/fascia, .46 muscle, .84 membrane.

For this synthetic export only, a depth visibility overlay draws the original
closure seam vertices and widths above the visible mannequin skin. The coarse
visible surface and collision hull do not exactly agree, so camera/gloss alone
left sutures occluded. This is disclosed on the closure image; it is not a new
closure mechanic or a relocation of stitches, tools, wound anchors or organs.
The tissue and mannequin remain illustrative, not photorealistic or calibrated.

Then run `services/motion/.venv/bin/python scripts/anatomy/export_demo_reel.py
<output-directory> --video` to label the surgery views and produce a sequential
Skin → Muscle → Organs → operative-region atlas reveal. The atlas is a crop of
`assets/anatomy/briefing-preview.png`, with a source hash in
`demo-visual-provenance.json`; it does not infer hidden anatomy. The reveal is
three seconds, 1920×1080 H.264/yuv420p with faststart. Each view states synthetic
Editor/atlas provenance; none is physical-headset evidence. Launch is unchanged
and should occupy only one second in the reel.

The main exports are `demo-step-0-mark.png` through `demo-step-9-closed.png`,
plus before/retry views; intermediate existing exports also receive synthetic
labels. `demo-atlas-reveal-1-skin.png`, `-2-muscle.png`, `-3-organs.png`,
`-4-operative-region.png` and `demo-atlas-reveal.mp4` are ready for editing.
Assets are exported outside Git into the reel assets directory.

Verification: the final optional graphics export passed 147 existing assertions;
`python3 scripts/quest/verify_session.py --suite player` passed on main `403b917`
plus the presentation edits (`SCALPAL_PLAYER_VERIFY_OK`, no failed checks).
The gate explicitly reports physical playthrough unverified.
