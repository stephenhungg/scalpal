# Scalpal OR Interaction Design: Laparoscopic Appendectomy (and Chole, Sigmoid)

Researched 2026-10-03. Scope: how the learner actually performs the procedure on Quest 3S (controllers primary, hand tracking secondary, no haptic devices), in AR (anatomy registered on a real reclining person) and full VR.

Labels: **[S]** = stated by the cited source. **[R]** = our recommendation. **[I]** = our inference from sources. **[C]** = conflict with current repo content, flagged for cleanup.

Repo context read: `docs/experience-ux.md` §5, `docs/research/ux-medical-sim.md` §4, `docs/surgical-tools-research.md`, `docs/tissue-simulation.md`, `services/preop/src/catalog/procedures.ts`. This report builds on, and does not repeat, the existing coaching/metrics recommendations there (one coach line < 12 words per step, stall ~15 s, decision points as single-best-answer, errors non-blocking, critique saved for recap).

---

## 0. Bottom line

1. **Interaction metaphor: hybrid, "port-constrained instruments seen through an anatomy window, with an assistant-driven scope monitor."** [R] The learner docks trocars on port markers on the (real or virtual) abdomen; every instrument is kinematically constrained to pivot at its port (true fulcrum geometry, computed, not faked); the learner sees the instrument tips and anatomy directly through a Bichlmeier-style virtual window cut into the abdominal wall, and also sees a world-locked laparoscope monitor above the patient fed by a virtual camera that a virtual assistant holds and auto-aims. A "scope-only" toggle hides the window for an advanced/test mode. Reasons in §2.
2. **Appendectomy demo = 8 learner steps, ~3–5 min**, with entry/insufflation, the camera, and closure auto-completed (§5 table).
3. **No haptics → readable target zones + visual tissue response + pseudo-haptic lag.** Snap only on commit actions (clip, stapler, bag), never on free dissection. Force proxy = tip speed and stretch distance on grasped tissue, shown as tissue blanching/strain color (§3).
4. **Score on what Quest can measure:** time per step, path length per hand, wrong-structure contacts, instrument-tip-out-of-scope-view time, stretch-limit exceedances, step order, decision-point answers (§4). All of these have precedents in LapSim / LAP Mentor / Seymour 2002.

---

## 1. Canonical step decomposition

### 1.1 Laparoscopic appendectomy

**Positioning and ports [S]**
- Supine, left arm tucked; after ports, Trendelenburg with right side up (left tilt) to let small bowel fall away from the right lower quadrant (StatPearls *Appendectomy*, via search summary: https://www.statpearls.com/point-of-care/139476).
- Usually three ports: umbilical 10–12 mm camera; suprapubic (10/12 mm if a linear stapler will pass through it); third 5 mm port variable, commonly left lower quadrant (LLQ) (https://pmc.ncbi.nlm.nih.gov/articles/PMC3796724/; port figure https://www.researchgate.net/figure/Port-placement-for-laparoscopic-appendectomy_fig1_235679495).
- Entry: no significant difference among direct optical entry, Hasson (open) and Veress; lower-quality evidence favors direct trocar entry (Bessoff et al., *Surgery Open Science* 2021, stepwise systematic review: https://pmc.ncbi.nlm.nih.gov/articles/PMC8473533/).

**Steps (consensus across sources) [S]**
1. Entry, pneumoperitoneum, camera port.
2. Working ports under vision.
3. Exploration of the abdomen to exclude other pathology; position patient.
4. Identify appendix: follow the taeniae of the cecum to where they converge on the appendix base. "Appendiceal critical view": appendix held at 10 o'clock, taenia at 3 o'clock, terminal ileum at 6 o'clock, with the taenia clearly running into the base (Burke, "Stump Appendicitis and the Critical View of Safety", *J Surg* (Avens), https://www.avensonline.org/wp-content/uploads/jsur-2332-4139-07-0046.pdf; lower-tier journal, treat as expert opinion).
5. Grasp mesoappendix / appendix tip and elevate; window the mesoappendix at the base.
6. Divide the mesoappendix with the appendicular artery: LigaSure, clips, harmonic, or sharp; insufficient data to prefer one (Bessoff 2021).
7. Secure the base. WSES 2020: "no clinical advantages in the use of endostaplers over endoloops" (1B); "simple ligation should be preferred to stump inversion" (1A); endoloops/suture or polymeric clips for adults and children, stapler at surgeon judgment in complicated cases (Di Saverio et al., WSES Jerusalem guidelines 2020 update, https://pmc.ncbi.nlm.nih.gov/articles/PMC7386163/). Leave stump < 5 mm; stumps > 0.5 cm are associated with stump appendicitis (Keller et al., *Radiol Case Rep* 2022, https://pmc.ncbi.nlm.nih.gov/articles/PMC9118493; Burke).
8. Divide the appendix between loops/clips or with the stapler.
9. Place specimen in retrieval bag and extract through the 10–12 mm port. Bag use associated with lower odds of intra-abdominal abscess (OR 0.6, NSQIP n=11,475; GRADE 1C, secondary analysis not significant) (Bessoff 2021).
10. Suction; WSES 2020: "peritoneal irrigation does not have any advantage over suction alone in complicated appendicitis" (1B); drains discouraged (1B).
11. Inspect stump and mesoappendix for hemostasis; remove ports under vision; close fascia at ≥10 mm sites (suture passer outperforms hand-sewn for seroma, Bessoff 2021).

**CTA-derived structure [S]**
- Expert CTA of lap appendectomy: **24 operative steps and 27 decision points**; 18/24 steps named by all 3 experts but only 5/27 decision points. Experts chose 9 steps + 6 decisions as junior-resident teaching points, 4 steps + 13 decisions for seniors (Smink/Peyre et al., "Utilization of a cognitive task analysis for laparoscopic appendectomy…", *Am J Surg* 2012, https://www.sciencedirect.com/science/article/abs/pii/S0002961012000074).
- A VR appendicectomy simulator was content-validated with CTA questions covering **8 operative steps and 4 decision points** (*Ir J Med Sci* 2019, https://pubmed.ncbi.nlm.nih.gov/30456516/).
- Simbionix LAP Mentor appendectomy module teaches exposure of the appendix, division of mesoappendix and appendix, specimen retrieval, hemostasis; variants: regular, retrocecal, pre-ileal; moderately inflamed, gangrenous, perforated; all tasks showed construct validity (https://www.prnewswire.com/news-releases/new-appendectomy-cases-on-the-simbionix-lap-mentor-226509981.html; https://surgicalscience.com/simulators/lap-mentor/).
- [I] The "8 steps / 4 decisions" granularity matches our 6–10 step budget and is the closest published precedent for a validated VR lap appy step list.

**Decision points worth teaching [I from the above]**
- Is this the true base? (appendiceal critical view before securing base.)
- How to close the stump given inflammation at the base (loop/clip vs stapler; stapler if base is inflamed/necrotic).
- Normal-looking appendix: WSES suggests removal if no other pathology found (2C).
- Suction vs irrigation in a perforated case (suction alone, 1B).

**Common errors and complications [S unless marked]**
- Misidentified base → long stump → stump appendicitis (Keller 2022; Burke).
- Grasping/tearing an inflamed appendix → perforation and contamination [I; standard teaching, reflected in repo `grab_appendix` mistake].
- Mesoappendix bleeding from incompletely sealed appendicular artery (bleeding/hemostasis is an explicit LAP Mentor task).
- Suprapubic trocar bladder injury (repo `port_into_bladder`; plausible, not verified here against a primary source).
- Thermal injury to cecum/ileum from energy device; stapling terminal ileum or too much cecum (repo mistakes).
- Intra-abdominal abscess, wound infection (< 2% laparoscopic) (StatPearls summary).

### 1.2 Laparoscopic cholecystectomy (brief)
- Four ports: 10 mm umbilical camera, epigastric/subxiphoid operating port, two right subcostal 5 mm ports (midclavicular, anterior axillary). Fundus retracted cephalad over the liver, infundibulum laterally to open the hepatocystic triangle (StatPearls *Laparoscopic Cholecystectomy*, https://www.statpearls.com/point-of-care/24022).
- **Critical View of Safety (Strasberg), all three required [S]:** hepatocystic triangle cleared of fat and fibrous tissue; lower third of gallbladder off the cystic plate; two and only two structures entering the gallbladder (https://www.nature.com/articles/s41597-023-02073-7; https://www.facs.org/.../critical-view-of-safety-minimizes-risk-of-bile-duct-injury/).
- SAGES Safe Chole six strategies: CVS; anticipate aberrant anatomy; liberal cholangiography; intraoperative time-out before clipping; recognize the high-risk zone; get help (https://www.sages.org/safe-cholecystectomy-program/).
- Bile duct injury ~3/1,000 laparoscopic choles (SAGES page).
- [R] The single best decision point in the whole product is the **CVS time-out before clipping** (already in the repo spec).
- Intraoperative errors used as OR-transfer outcomes in Seymour 2002 (gallbladder dissection): lack of progress, gallbladder injury, liver injury, incorrect plane, burn non-target tissue, tearing tissue, **instrument out of view**, attending takeover. VR-trained residents made 1.19 vs 7.38 errors (P < .008) (https://pmc.ncbi.nlm.nih.gov/articles/PMC1422600/). [R] Reuse this list as Scalpal's error taxonomy.

### 1.3 Laparoscopic sigmoid colectomy (brief)
- 3–4 trocars; access; exploration/adhesiolysis; mobilize sigmoid and descending colon medial-to-lateral or lateral-to-medial; isolate and divide the inferior mesenteric artery (IMA) (stapler or vessel sealer); divide distal bowel; extract; anastomose; check (J&J MedTech operative steps, https://www.jnjmedtech.com/en-US/education/education-library/operative-steps/laparoscopic-sigmoid-colectomy/).
- **Key safety decision [S]:** identify the left ureter before dividing the IMA; the IMA lies over the ureter, and medial-to-lateral dissection exposes ureter, gonadal vessels and hypogastric nerves (Medscape, https://emedicine.medscape.com/article/1965584-technique; https://www.laparoscopyhospital.com/laparoscopic-sigmoidectomy-surgery.php).
- [R] Equivalent of CVS for this procedure: "Show me the ureter" time-out before firing on the IMA. Anastomosis (circular stapler) should be abstracted in a demo.

---

## 2. Core design question: which interaction metaphor

### 2.1 Evidence

| Finding | Source | Implication [I] |
| --- | --- | --- |
| Fulcrum effect: tip moves opposite to hand. Novices cutting under normal laparoscopic view performed significantly worse than with the image inverted to cancel the fulcrum; inversion accelerated novice learning. | Gallagher et al., *Endoscopy* 1998;30(7):617–620 (https://www.thieme-connect.de/products/ejournals/abstract/10.1055/s-1999-26; summarized https://pmc.ncbi.nlm.nih.gov/articles/PMC3741957) | A first-time judge on a monitor-only view will flail for the first minute. |
| Training first under direct vision through a transparent box cut total task time 27% vs standard box only; end-of-training motion/force metrics equal. 3D direct visualization first reduced time-to-proficiency 32%. | Transparent-top box trainer study (https://pmc.ncbi.nlm.nih.gov/articles/PMC5881314/); stereoscopic early-phase study (https://www.ncbi.nlm.nih.gov/pmc/articles/PMC5772130/) | Direct view is a legitimate learning scaffold, not just a demo cheat. |
| 3D faster than 2D for novices initially; difference vanishes after crossover. Novices lack monocular depth strategies for 2D. | 3D vs 2D-4K RCT (https://www.ncbi.nlm.nih.gov/pmc/articles/PMC11525257/) | Stereo direct view helps short sessions most. |
| Immersive VR laparoscopy raised cognitive load 66% over baseline and worsened performance vs conventional VR. | Frederiksen 2020 RCT (https://pubmed.ncbi.nlm.nih.gov/31172325) | Do not add a second hard thing (monitor-only mapping) on top of headset load. |
| HMD laparoscopy (LapSim output inside a 360° OR) matched standard LapSim performance; high presence, rare sickness. | Huber et al., *Surg Endosc* 2017 (https://link.springer.com/article/10.1007/s00464-017-5500-6) | A virtual monitor inside an HMD is viable and reads as "real lap surgery". |
| Quest 2 controllers with a mechanically constrained pivot reproduce the inverted motion-to-visual mapping; 6-DoF telemetry at 80–100 Hz separated experts from novices (AUC 0.90). | Montoto et al., *Sci Rep* 2026, SECMA (https://pmc.ncbi.nlm.nih.gov/articles/PMC13526842/) | Quest tracking is good enough for port-constrained kinematics and kinematic scoring. |
| Superimposed AR anatomy on skin gives misleading depth (looks like it floats on top); a "virtual window" cut in the skin restores occlusion and motion-parallax cues. | Bichlmeier & Navab, "Virtual Window for Improved Depth Perception in Medical AR" (https://www.semanticscholar.org/paper/Virtual-Window-for-Improved-Depth-Perception-in-AR-Bichlmeier-Navab/310fbf3c57e7f8c5281cbeb1649bb240d60bc844); "Improving Depth Perception in Medical AR" (https://link.springer.com/chapter/10.1007/978-3-540-71091-2_44) | In AR passthrough, never draw organs as a flat overlay on the person; draw them inside a window with a rim and inner wall. |
| AR overlays of hidden anatomy and AR telestration improve spatial understanding, time and errors; telestration shortened time from instruction to target fixation. | JMIR scoping review 2025 (https://www.jmir.org/2025/1/e58108); telestration study (https://www.ncbi.nlm.nih.gov/pmc/articles/PMC10156835/) | Target highlights drawn in-situ are an evidence-backed cue. |
| No association between simulator fidelity and transfer across 24 studies. | Norman, Dore, Grierson, *Med Educ* 2012;46:636–647 (via https://pubmed.ncbi.nlm.nih.gov/23171265/ context) | Spend fidelity budget on readability of steps/decisions, not physics. |

### 2.2 Options compared

| | (a) Faithful laparoscopic (monitor only) | (b) Open "x-ray window" only | (c) Hybrid (recommended) |
| --- | --- | --- | --- |
| Reads as "laparoscopic" to a judge | Best | Weak: looks like open surgery | Strong: ports, pivoting shafts, scope monitor all present |
| First-minute usability for novices | Worst (fulcrum + 2D + camera) | Best | Near (b), because the window is primary |
| AR value (real body) | Low: learner stares at a floating monitor, the real person is irrelevant | High | High: ports placed on the real body, anatomy seen in the body |
| Teaches real skill | Fulcrum + monitor mapping | Anatomy/sequence only | Sequence + anatomy + real fulcrum kinematics; monitor-only test mode later |
| Build cost | Needs good camera control UX | Lowest | Moderate: one extra render texture camera |

### 2.3 Recommendation [R]

**Hybrid (c), concretely:**
1. **Ports are physical anchors.** Learner picks up a trocar and docks it on a glowing port marker on the abdomen (real person in AR via registration; virtual patient in VR). Snap radius 3 cm in body frame, alignment cone 30°. This is the most convincing AR moment: the virtual trocar sits in a real belly.
2. **Instruments are truly port-constrained, computed not haptic.** The controller is the handle. Shaft direction = (port → handle) reversed; tip = port + direction × (|handle − port| clamped insertion). This yields the real fulcrum inversion and pivot with zero tolerance tricks, and makes instrument switching "pull out of port, insert another" (instruments are bound to ports per `procedures.ts` `instrumentIds`). Handles must stay ≥ 10 cm above the skin in AR; clamp and warn, so no controller ever touches the participant. [R, safety]
3. **Primary view: anatomy window.** A rim-bounded opening in the abdominal wall around the operative field (Bichlmeier virtual window), showing peritoneal cavity, organs, and instrument tips in stereo. In AR it is the only place anatomy is drawn; outside the window the real skin is visible.
4. **Secondary view: scope monitor.** World-locked monitor above the patient's left shoulder, in the line of sight of the hands (the classic coaxial setup, [I]), fed by a laparoscope camera at the umbilical port with a 30° angle. **A virtual assistant holds the camera** and auto-aims at the current step's target with smoothing; this is faithful to real lap appy (the assistant drives the camera) and removes the third-hand problem on two controllers. The learner can say "Camera, closer" / "Camera, show the base" (voice → deterministic camera presets, not LLM-driven pose).
5. **Mode ladder:** Practice = window + monitor + target highlights. Test = window dims to 15% "ghost" or hides; monitor only; highlights only on stall. Demo uses Practice.
6. **Hand tracking:** supported for port placement and bag handling, but not recommended for jaw/trigger actions; pinch jitter and occlusion over a body make clip/staple commits unreliable [I]. Default controllers; trigger = jaw close, grip = hold instrument.

Why not (a): the evidence (Gallagher 1998, transparent-box 27%, Frederiksen +66% load) says the first minute of a monitor-only fulcrum task is a failure experience, and a 2–6 min demo is all first minute. Why not (b): loses what makes it laparoscopic and the CTA steps about ports and camera become meaningless. Hybrid keeps the real kinematics (the hard part) visible in direct stereo, where the transparent-box literature shows novices learn faster.

---

## 3. Per-action mechanics without haptics

### 3.1 General feedback toolkit [R, with sourced basis]
- **Target zones:** each step has authored zones (capsule/box colliders in body frame) on the correct structure; drawn as a soft rim only after a stall or in Practice. In-situ highlights mirror AR telestration (above).
- **Visual tissue response as force substitute:** tissue deformation, blanching and color-coded strain. Visual force feedback reduced applied forces in novices, and color-coded stress fields have been used to warn of excessive force in VR robotic suturing (https://pmc.ncbi.nlm.nih.gov/articles/PMC8849007; https://link.springer.com/article/10.1007/s11701-024-02150-y). [S]
- **Pseudo-haptics:** increase control-display ratio (tip lags hand) when pulling on tethered tissue so it reads as resistance; C/D manipulation changes perceived weight and stiffness without kinesthetic feedback (Lécuyer; Samad et al. CHI 2019, https://dl.acm.org/doi/fullHtml/10.1145/3290605.3300550; Weiss 2023 pseudo-stiffness, https://www.medien.ifi.lmu.de/pubdb/publications/pub/weiss2023usingpseudostiffness/weiss2023usingpseudostiffness.pdf). [S] Keep the lag ≤ ~30% so it does not feel like tracking loss [R].
- **Controller vibration:** Quest controllers do vibrate; use short pulses for jaw close, clip fire, staple fire, and a distinct pattern for errors (repo already specifies "haptic buzz").
- **Snapping:** use Meta Interaction SDK snap interactors/hand-grab poses for picking instruments and docking trocars (https://developers.meta.com/horizon/documentation/unity/unity-isdk-create-snap-interactions/). Snap commit actions (clip, staple jaws) only when the jaw is inside the target zone with correct orientation; never snap free dissection or retraction, because that is the skill.
- **Ghost guides:** a translucent "ghost instrument" demonstrates the motion once per new mechanic, then fades with proficiency and returns after a slip (adaptive ghost fading; guidance hypothesis warns of over-reliance) (https://arxiv.org/html/2603.06253). [S]
- **Slow motion:** not recommended during the case (breaks the deterministic time metrics); use slow-mo only in the recap replay [R].

### 3.2 Action table [R unless cited]

| Action | Mechanic | Readable success | Error detection |
| --- | --- | --- | --- |
| Place trocar | Grab trocar (snap pose), bring to port marker, push along the axis 3–5 cm; obturator auto-withdraws | Marker turns solid, cannula seats with a click sound; scope view shows trocar entering | Trocar tip inside bladder/bowel collider during push (`port_into_bladder`); wrong port (instrument mismatch with port list) |
| Grasp / retract | Trigger closes jaws; grasp acquires only on eligible handle colliders (existing XPBD cage grasp in `NativeTissueSimulation`) | Tissue deforms locally, elevates; target structure enters "exposed" pose | Grasp on forbidden structure (`appendix` body if inflamed → `grab_appendix`); stretch > authored displacement limit (18/22/8 mm presets) → blanch then red, LapSim-style "max stretch damage" |
| Dissect / window mesoappendix | Maryland dissector tip pushed through mesoappendix zone at base, then spread (trigger open while inside) | Window zone "opens" (pre-authored aperture blend shape grows with spread count); light shows through | Dissecting > 1 cm from base (zone miss); contact with appendicular artery while spreading → small bleed |
| Seal / divide mesoappendix | Vessel sealer: close jaws on artery zone, hold trigger 1.5 s (seal ring fills, sizzle + smoke), then divide button | Sealed band turns pale tan; divided ends retract | Releasing early → partial seal → bleed on divide; jaws touching cecum/ileum while active → "burn non-target tissue" (Seymour error) |
| Clip / ligate (chole, or appendix base option) | Clip applier jaws must straddle target duct/artery zone, perpendicular within ±25°, fully across (both jaw tips past far edge) → fire | Clip visibly crimps, count shown (e.g., 2 proximal, 1 distal) | Clip partially across (tips not past edge) → "incomplete clip"; clip on wrong structure; firing before CVS confirmed → decision error |
| Staple appendix base | Endo stapler through 12 mm port; open, slide jaws across base zone (a band ≤ 5 mm from cecal wall), close (pre-fire check shows green/amber/red band), fire | Staple line appears, appendix separates; stump length readout | Jaws > 5 mm from cecum → long stump (stump appendicitis); including cecum wall or terminal ileum (repo mistakes) |
| Bag and extract | Bag deploys from port (auto-open hoop); grasp appendix, drop into hoop (magnet within 4 cm), pull drawstring (trigger), withdraw bag through port | Bag closes, specimen visibly inside, out through umbilical port | Specimen dropped outside bag / touching abdominal wall → contamination flag (SSI teaching point) |
| Inspect / suction | Suction tip into fluid pool, hold trigger; pool volume drains (repo `VesselBleeding` pool/suction volumes) | Pool shrinks, staple line visible dry | Irrigating instead of suctioning in perforated case (WSES 1B) → decision feedback only |
| Instrument out of view | Tip outside the scope camera frustum while moving (> 0.5 s) | n/a | Counted and timed (Seymour; LAP Mentor "instrument out of view" metric). Show a small edge arrow on the monitor |

### 3.3 Force proxy without force sensors [R]
- **Speed:** tip speed near tissue > ~10 cm/s inside a 2 cm proximity shell = "rough handling" event. Tunable; not clinically calibrated.
- **Stretch:** grasped-node displacement vs the existing per-structure limits; > 100% = tear event (LapSim defines max stretch damage where 100% = vessel torn and bleeding, https://pmc.ncbi.nlm.nih.gov/articles/PMC3433540/).
- **Activation dwell:** energy active while touching non-target tissue = burn event.
All three are deterministic and logged with timestamps for the robot replay timeline.

---

## 4. Scoring and coaching

### 4.1 Metrics feasible on Quest [S basis, R selection]

| Metric | Precedent | Scalpal computation |
| --- | --- | --- |
| Time per step, total time | LapSim, LAP Mentor | Step start → success check |
| Path length per hand, angular path length | LapSim (incl. clip application) (https://pmc.ncbi.nlm.nih.gov/articles/PMC3433540/) | Integrate tip position/rotation while instrument is inserted |
| Tissue damage / wrong-target contacts | LapSim tissue damage = hits; Seymour error list | Contacts with non-allowlisted structures per step |
| Max stretch damage | LapSim | Grasp displacement / limit |
| Instrument out of view (s) | LAP Mentor; Seymour | Tip outside scope frustum while inserted |
| Burn non-target tissue | Seymour | Energy active on non-target |
| Step order, skipped/repeated steps | CTA step lists | State machine |
| Decision-point correctness | Touch Surgery MCQs at decision steps | 1–2 per procedure |
| Hints used | Osso VR test mode hints | Count |
| Kinematic smoothness (optional) | SECMA Quest study (velocity stats, AUC 0.90) | Jerk/velocity variance; recap only |

Caveat [S]: construct validity of time/path length is not universal (repo `ux-medical-sim.md` §4), so show them against a band and lead with errors and decisions.

### 4.2 Score composition [R]
- **Safety (50%)**: critical errors (wrong structure cut/stapled/clipped, long stump, bladder injury, firing before critical view) each cap the grade.
- **Decisions (20%)**: decision-point answers.
- **Economy (20%)**: time and path length vs band.
- **Tissue respect (10%)**: stretch, rough-handling, burn events.
Displayed as a proficiency band, not a raw number.

### 4.3 Coaching without overload [S basis from repo + R]
- Keep repo rules: one line < 12 words at step start; silent otherwise; speak on ~15 s stall or error; safety lines not interruptible.
- Escalating cues on stall (supported by escalating-feedback evidence, https://link.springer.com/article/10.1007/s00464-005-0847-5): 15 s → pulse target zone; 25 s → ghost instrument demo; 40 s → coach states exact action; 60 s → offer auto-complete (counted as assisted).
- Errors: one visual (tissue flash) + one vibration + at most one line; batch further critique for recap (terminal feedback transfers better: Walsh 2009, in repo §4).
- Monitor HUD: current step name + progress dots only. No numbers during the case.

---

## 5. Recommended appendectomy design

### 5.1 Scope for a hackathon judge [R]
- **Learner-performed steps: 8.** Auto: open entry + insufflation + camera port (animated by the assistant), camera driving, fascial closure. Optional cut: "Assess appendix" folds into "Expose" to keep a 3–5 min run.
- **One decision point** (critical view of the base before stapling), optional second (suction vs irrigation) only in the perforated variant.
- **What makes it feel real**, ranked by impact per hour [I/R]:
  1. Sound: insufflator hum, trocar click, sealer sizzle, stapler double-click "fire", suction slurp, monitor beeps. Audio raises presence significantly in MR medical training (https://doi.org/10.3390/info17050399) and audiohaptic cues improved simulated drilling performance (https://www.ncbi.nlm.nih.gov/pmc/articles/PMC7016775/).
  2. Port-constrained shafts visibly pivoting in the real belly (AR) — the signature visual.
  3. Smoke puff + slight lens fog on the scope monitor during sealing; small arterial bleed if seal is incomplete. Smoke and bleeding are surgeon cues used in skill assessment (Halic et al. 2010, https://onlinelibrary.wiley.com/doi/10.1002/rcs.353).
  4. Local tissue deformation on grasp (already implemented via XPBD cage) and wet specular shading.
  5. The scope monitor itself (30° view, slight vignette) — reads as "real lap surgery" for anyone who has seen one.
- Do not spend time on true cutting topology for the demo; preauthored separations are enough (Norman 2012 fidelity–transfer finding).

### 5.2 Step-by-step design table [R; clinical content [S] as cited in §1]

Ports (from `procedures.ts`): umbilical 12 mm (camera, stapler, bag), LLQ 5 mm (grasper, dissector, sealer, suction), suprapubic 5 mm (grasper). Left hand = suprapubic grasper (retraction), right hand = LLQ working instrument.

| # | Step | Instrument (port) | Learner action | Success check (deterministic) | Common mistake → feedback | Coach line (< 12 words) | Auto / abstracted |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0 | Entry + camera | Trocar 12 mm, laparoscope (umbilical) | Watch; optionally confirm umbilical marker | Auto | — | "Camera's in. Let's place your working ports." | Auto: open entry, insufflation to 12–15 mmHg shown on tower, camera port, assistant takes scope |
| 1 | Working ports | Trocar 5 mm ×2 (LLQ, suprapubic) | Dock each trocar on its marker and push in along the axis | Both trocars seated within 3 cm / 30°, tip visualized on monitor during push | Pushing deep toward bladder → bladder flashes red, vibration | "Two ports: left lower, then suprapubic. Watch them enter." | Marker positions shown; obturator removal auto |
| 2 | Position + expose | Atraumatic grasper ×2 | Say/press "tilt" (table tilts head-down, left-down), then sweep small bowel medially out of RLQ zone | Bowel-clear zone empty; cecum in view | Grasping small bowel hard (stretch > limit) → blanch, "gentle" pulse | "Head down, left tilt. Sweep the bowel out." | Table tilt is a one-button action; bowel is a few rigid loops with soft push |
| 3 | Find appendix | Grasper (suprapubic) | Follow taenia (glow trail on contact) to base; lift appendix by mesoappendix/tip to 10 o'clock | Appendix tip in "elevated" zone, taenia + ileum visible in scope frame | Grabbing inflamed appendix body → red flash, perforation warning (`grab_appendix`) | "Follow the taenia down. Lift by the mesoappendix." | Taenia trail highlight appears on stall only |
| 4 | **Decision: confirm base** | — | Single-best-answer on monitor: "Where is the true base?" (3 marked points: tip, mid, where taeniae converge) | Correct point selected | Wrong → camera swings to show taeniae convergence; logged | "Before we secure anything: where's the true base?" | Gaze/point to answer; no motor task |
| 5 | Window mesoappendix | Maryland dissector (LLQ) | Push tip through mesoappendix at the base zone, spread twice | Window aperture ≥ authored size, ≤ 1 cm from base | Window too far from base → amber zone; artery touched → small ooze | "Make a window right at the base." | Window is a pre-authored opening driven by spread count |
| 6 | Seal + divide mesoappendix | Vessel sealer (LLQ) | Close jaws across mesoappendix/artery zone, hold trigger until ring fills (~1.5 s), press divide | Artery zone sealed and divided; no active bleed | Early release → bleed on divide (pool grows); jaws on cecum while active → burn event | "Seal the mesoappendix with the artery. Hold until done." | Seal/divide are authored state changes; smoke + sizzle |
| 7 | Staple base | Endo stapler (umbilical; camera auto-moves to LLQ view) | Slide jaws across the base band, close, check pre-fire band color, fire | Jaws within base band (≤ 5 mm from cecum), no ileum in jaws; fired | Too far up (long stump) → amber "stump too long"; cecum/ileum in jaws → red, block fire once with coach warning | "Flush with the cecum. Check the jaws, then fire." | Camera port swap auto [C]; stapler articulation fixed |
| 8 | Bag + extract | Retrieval bag (umbilical), grasper | Grasp appendix, drop into open bag hoop, pull drawstring, withdraw through port | Specimen inside closed bag, removed through port | Specimen touches abdominal wall/port outside bag → contamination flag | "Into the bag. Out through the camera port." | Bag hoop auto-deploys; 4 cm magnet into bag |
| 9 | Suction + inspect | Suction-irrigator (LLQ) | Suction RLQ/pelvic pool, then hover on staple line and mesoappendix to "inspect" (dwell 1 s each) | Pool volume < threshold; both inspect points dwelt | Irrigating in perforated variant → decision feedback in recap (WSES 1B) | "Suction the fluid. Check the staple line is dry." | Fluid is the existing pool volume model |
| 10 | Close | Fascial closure | None (or one confirm) | Auto | — | "Ports out under vision. Nice work." | Auto: ports removed under vision, fascia closed, fade to robot replay |

Target run time: steps 1–9 ≈ 3–5 min for a first-timer with the window view; fast-path judge variant skips step 2 (pre-exposed) and step 9.

### 5.3 Flags against current repo content
- **[C] Stapler and camera share the umbilical port.** `procedures.ts` lists `laparoscope_30` and `endo_stapler` on the umbilical port. In practice the stapler goes through a 10/12 mm suprapubic port or the camera is moved to a 5 mm port (§1.1 port sources). Recommend: make the suprapubic port 12 mm and bind the stapler to it, or keep umbilical and add an explicit auto "camera moves to 5 mm port" beat. Pick one; the first is more faithful.
- **[C] Stump closure choice.** Catalog uses only the stapler. WSES 2020 finds no advantage of staplers over endoloops (1B) and prefers loops/clips for uncomplicated cases. For a demo the stapler is the most legible single action, which is fine; the coach or recap should say loops/clips are equally valid, and an endoloop variant (6-step technique: Lim, Dosis, Lim, *Ann R Coll Surg Engl*, https://pmc.ncbi.nlm.nih.gov/articles/PMC12043359/) can be a later mode.
- **[C] `find_appendix` and `assess` both use the grasper with similar `touch_target` checks;** the success checks above (elevated zone, frame contains taenia + ileum) are stronger than "touched it" and encode why the step matters (Rule 9).
- **Decision point missing in catalog:** add a base-confirmation decision before `staple_base`.

---

## 6. Open questions / uncertainty (fail loud)
- Thresholds (snap 3 cm, 30° cone, 10 cm/s rough-handling, 1.5 s seal, ±25° clip angle) are design guesses to tune on device, not sourced values.
- StatPearls pages returned 403; positioning/complication statements are from search-result summaries of StatPearls, not a direct read.
- The appendiceal critical-view clock description comes from a low-tier journal (Burke); widely taught but not guideline-level.
- PubMed CTA validation paper (*Ir J Med Sci* 2019) was not readable in full; only the 8-step/4-decision summary is used.
- No published study directly compares window view vs monitor view for HMD laparoscopy on a real body; the recommendation extrapolates from transparent-box, 3D-vs-2D, and fulcrum-inversion evidence.
- AR comfort/safety of controllers hovering over a real participant is untested; the ≥ 10 cm handle clamp is a precaution.
