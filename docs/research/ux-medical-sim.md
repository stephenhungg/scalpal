# Scalpal: UX and learning-design research for the VR med-sim flow

Date: 2026-10-03. Scope: the full Scalpal loop on Quest 3S (pick a patient, VR clinic history and exam with orders, present to the "Scalpal" attending, laparoscopic OR with voice coaching, robot-hand replay, recap).

Labels used below:
- **[S]** means the point is directly supported by the cited source.
- **[I]** means it is my inference or design recommendation drawn from the sources. Treat these as judgment, not evidence.

Caveats:
- Some primary sources were paywalled (ACM DL returned 403), so I took their findings from abstracts and search snippets.
- Firecrawl was rate-limited partway through. I used WebSearch and WebFetch for the rest.

---

## 1. Virtual patient / OSCE encounter design

### What the products do
- **Oxford Medical Simulation (OMS)**
  - Scenarios run about 20 minutes. One study used a time limit of about 15 minutes. [S]
  - Every learner action is logged and compared against best practice. [S]
  - After a scenario, the learner first does a guided self-reflection. Only then are results shown, as automated feedback and performance metrics. [S]
  - OMS classifies actions into "clinical behaviors and competencies" rather than giving a single score. [S]
  - Library of 260+ scenarios. A 2025 study (n=16) found no significant performance difference between OMS VR and a high-fidelity manikin. [S]
  - Sources:
    - https://oxfordmedicalsimulation.com/virtual-reality-or-manikin-based-simulation/
    - https://academic-med-surg.scholasticahq.com/article/146315-virtual-reality-a-novel-teaching-tool-in-undergraduate-medicine-clinical-skills-training
    - https://oxfordmedicalsimulation.com/academic-institutions/learners/
- **Body Interact**
  - A real-time physiology engine covers 50+ therapeutic areas and 600+ drugs. [S]
  - Wrong or delayed actions cause visible deterioration (for example, fibrinolytic overdose leads to hemorrhagic transformation), so "students can watch the patient deteriorate in real time as a direct consequence of the error." [S]
  - Feedback is reported across communication, assessment and management. [S]
  - Sources:
    - https://bodyinteract.com/blog/real-time-physiology/
    - https://bodyinteract.com/
- **Full Code**
  - 300+ peer-reviewed cases. [S]
  - The debrief shows an overall score plus a breakdown by category (Physical Exam, Investigations, diagnosis, management). [S]
  - Each action is tagged **Critical / Reasonable / Neutral / Unnecessary** rather than right or wrong. This is the key pattern for scoring orders. [S]
  - Sources:
    - https://mwm.ai/apps/full-code-medical-simulation/1207424206
    - https://apps.apple.com/us/app/full-code-medical-simulation/id1207424206
- **i-Human Patients**
  - The case flow is fixed in this order: History → Physical exam → Problem list / problem statement → Ranked differential → Tests → Plan → Summary. [S]
  - Learners must record the "pivotal findings" that separate their top differentials. That captures reasoning, not just the final answer. [S]
  - HPI is structured with OLDCARTS. [S]
  - Sources:
    - https://ihumanhelp.com/2024/10/22/ihuman-step-by-step-instructions-history-physical-examination-and-diagnosis/
    - https://www.studocu.com/en-us/document/walden-university/synthesis-in-advanced-nursing-practice-of-patients-in-primary-care-settings/i-human-patients-case-player-student-manual/64643837
- **SimX**
  - Learners speak naturally to patient, family and team. [S]
  - Voice goes through "a tightly controlled AI layer that only triggers pre-authored, clinician-validated actions." It uses no menus, and the free speech maps to authored outcomes. [S]
  - This is the same hybrid as Scalpal's "LLM for talk, deterministic engine for results."
  - Sources:
    - https://www.simxvr.com/
    - https://www.simxvr.com/platform/autonomous-simulation/

### Academic evidence: LLM virtual and standardized patients
- **Holderried et al. 2024 (JMIR Med Educ), GPT-4 simulated patient**
  - 99.3% of answers matched the question and 99.3% were plausible (n=410 Q/A pairs). [S]
  - 86.3% of student questions were covered by the authored illness script. About 14% fell outside it, so the model had to improvise. [S]
  - GPT-4 feedback agreed with humans at κ=0.832 overall. Eight categories were below κ 0.6, mainly items that were fuzzy or overlapping. [S]
  - Lessons: make the prompt as specific as possible, use a separate prompt for feedback, and keep feedback items clearly separated. [S]
  - Source: https://pmc.ncbi.nlm.nih.gov/articles/PMC11364946/
- **Brügge et al. 2024 (BMC Med Educ, double-blind RCT)**
  - The group that got AI patient conversations **plus** structured AI feedback beat the conversation-only group on clinical decision-making. [S]
  - Feedback is the active ingredient, not the chat alone.
  - Source: https://link.springer.com/article/10.1186/s12909-024-06399-7
- **Systematic review of 32 LLM virtual-patient studies (2025)**
  - Strong realism and accuracy overall. [S]
  - Multimorbidity is rarely represented. [S]
  - Source: https://pmc.ncbi.nlm.nih.gov/articles/PMC12811743/
- **AIPatient (Nature Comms Med 2025)**
  - Grounding answers in the EHR gave 94.15% QA accuracy. [S]
  - Responses read at a median Flesch grade of 6.4, which is patient-like plain language. [S]
  - This supports grounding the patient in FinchNode chart data.
  - Source: https://www.nature.com/articles/s43856-025-01283-x
- **Huwendiek's 10 virtual-patient design principles.** A VP should: [S]
  - be relevant;
  - have an appropriate difficulty;
  - be highly interactive;
  - give specific feedback;
  - make optimal use of media;
  - focus on relevant learning points;
  - recap key learning points;
  - have an authentic interface and tasks;
  - include questions and explanations tailored to clinical reasoning.
  - Source: https://pmc.ncbi.nlm.nih.gov/articles/PMC6737267/
- **VAPS (VR + LLM embodied agents, 2025)**
  - In interviews, students asked for three things: realistic scenarios, **straightforward interaction mechanisms**, and **unpredictable dialogue**. [S]
  - Source: https://arxiv.org/abs/2503.01767
- **OSCE timing**
  - History stations usually run 8–15 minutes. Many use 10 minutes with a warning buzzer at 8 minutes. [S]
  - Harden's original 1972 OSCE gave 4.5 minutes per station. [S]
  - Source: https://brieflands.com/journals/jme/articles/142577

### Hints and cues (INACSL Simulation Design standard)
- Cues should be **linked to performance measures** and used to **refocus learners who stray** from the objectives. [S]
- Cues can be delivered:
  - verbally, through the patient or an embedded participant;
  - visually, through vitals;
  - as new data, such as a lab result. [S]
- Design **predetermined cues** for anticipated stalls, plus unplanned "lifesaver" cues. [S]
- Use **planned time frames** for progression. [S]
- Source: https://www.nursingsimulation.org/article/S1876-1399(21)00096-7/fulltext

### Realistic vs frustrating: recommendations for Scalpal
- **[I]** Realistic:
  - The patient answers only what is asked, in lay language (about grade 6), with emotion and pain behaviour that matches the acute presentation.
  - Orders return results after a short believable delay.
  - Vitals and patient demeanour drift if the learner stalls (Body Interact pattern).
- **[I]** Frustrating:
  - Hunting through menus for an exam maneuver.
  - The patient not understanding paraphrases.
  - Being graded on something you couldn't find how to do.
  - Unlimited test ordering that is never penalised (it teaches shotgunning).
- **[I]** Make "order everything" visible:
  - Tag every exam and order with Full Code's 4 tiers (Critical / Reasonable / Neutral / Unnecessary).
  - Show cost, radiation or time on the scorecard.
- **[I]** Time pressure: use a soft clock rather than a hard fail.
  - Clinic target is about 6–8 minutes, shorter than an OSCE because this is a demo.
  - At about 75% of the budget, deliver an in-world cue (the patient says "it's getting worse", or a nurse knocks).
  - At 100%, Scalpal prompts "ready to present?" Never hard-fail.
- **[I]** Hints, escalating in 3 tiers, and each one used is logged on the scorecard:
  - patient-voiced nudge;
  - Scalpal Socratic question;
  - explicit suggestion.

---

## 2. Voice conversational agents in VR

### Latency targets
- **Human turn-taking**
  - Median gap between turns is about 200 ms. 51–55% of transitions happen in under 200 ms. [S]
  - Typical spoken dialogue systems wait 700–1000 ms before taking the turn, which feels unnatural. [S]
  - Sources:
    - https://www.researchgate.net/publication/347821999_Turn-taking_in_Conversational_Systems_and_Human-Robot_Interaction_A_Review
    - https://sites.tufts.edu/hilab/files/2022/09/Threlkeld-Umair-de-Ruiter-2022.pdf
- **CHI 2026 "Quantifying Latencies" (LLM agents in social VR)**
  - Agents had a **median 4.1 s** response latency vs **1.2 s** for humans. [S]
  - The paper names two failure modes: **start-up latency** and **wind-down latency** (agents keep talking when interrupted). [S]
  - Users were frustrated because they didn't know when to speak. [S]
  - Source: https://dl.acm.org/doi/10.1145/3772318.3790947
- **Nielsen's limits** [S]
  - 0.1 s feels instant.
  - 1 s keeps flow.
  - 10 s keeps attention. Beyond that, show progress.
  - Source: https://www.nngroup.com/articles/response-times-3-important-limits/
- **Voice-industry guidance**
  - UX degrades beyond about 500 ms (users start talking over the bot). [S]
  - Turn detection plus network commonly adds 200–400 ms. [S]
  - Sources:
    - https://vapi.ai/blog/speech-latency
    - https://hamming.ai/resources/voice-ai-latency-whats-fast-whats-slow-how-to-fix-it
- **[I] Targets for Scalpal**
  - First audible patient response under 1.0 s after end of speech is good. Under 1.5 s is acceptable. Above 2.5 s, play a filler.
  - Scalpal can be slower (up to about 2 s) because an attending "thinking" is socially plausible.
  - Stream TTS sentence by sentence.

### Showing "thinking": use behaviour, not icons
- A VR study compared four ways of covering LLM delay: a behavioural filler (a conversational "hmm, let me think" plus a gesture), idle motion, a progress badge on the agent, and an external thought bubble. [S]
- The **behavioural filler won** on perceived response time, presence, humanlikeness and naturalness. [S]
- Only 12.5% and 4.2% of users preferred the two symbolic indicators. [S]
- Symbolic indicators made users look away from the agent's face. [S]
- Source: https://arxiv.org/abs/2508.11781
- **[I]** For the patient, play pre-recorded short fillers ("mm…", a wince, "uh, let me think") instantly while the LLM generates. Keep a small mic or listening glyph only on the learner's own HUD.

### Push-to-talk vs open mic
- **Push-to-talk (PTT)** [S]
  - Gives explicit control and is common in VR training.
  - In a clinical VR study, it was "counterintuitive or forgotten" by some participants.
- **Amplitude VAD with about 2 s silence cutoff** [S]
  - Felt natural.
  - Confused users because the system couldn't tell intentional speech apart.
- Sources:
  - https://doi.org/10.1145/3788851.3805005
  - https://www.sciencedirect.com/org/science/article/pii/S2291927922000708
- **LiveKit**
  - Supports semantic and acoustic turn detection, adaptive interruption (separates backchannels from real barge-ins), and resume after a false interruption. [S]
  - `turn_detection="manual"` gives you PTT. [S]
  - Source: https://docs.livekit.io/agents/logic/turns/
- **[I] Recommendation**
  - Make **hold-to-talk (trigger or grip) the default** for a noisy hackathon floor. Judges talking nearby will wreck open-mic VAD.
  - Offer open-mic as a toggle.
  - Show a persistent controller-anchored hint: "Hold [grip] to talk."
  - Haptic tick on press and release.
  - Earcon when the turn is committed.

### Barge-in
- **[I]** Allow the learner to interrupt the patient and Scalpal.
  - On press, or on VAD speech of 300 ms or more, cut TTS within about 200 ms. This fixes the wind-down latency failure from the CHI paper.
  - Ignore backchannels ("ok", "mm-hm").
- **[I]** Do not allow barge-in on safety-critical OR coaching lines, or replay them after the interruption.

### Captions and transcripts
- Meta's guidance: [S]
  - Place captions at about 1 m (half the far-field distance) and let the user move them.
  - Lazy-follow (leash) them to the head to avoid nausea.
  - Keep them always visible and never occluded.
  - UI should sit at 0.5–1.5 m, at or slightly below eye level.
  - Text should subtend at least 2–3°.
  - Hit targets at least 22 mm (3° at 0.42 m).
  - The VRC requires subtitles for dialogue.
- Sources:
  - https://developers.meta.com/horizon/design/accessibility/
  - https://developers.meta.com/horizon/resources/vrc-quest-accessibility-2/
- **[I] Patterns**
  - Show the learner's ASR text immediately (partials, greyed), then the patient reply.
  - Label every caption with the speaker's name and colour (Patient / Scalpal / Coach).
  - Keep a scrollable transcript on a wrist or clipboard panel. It doubles as the source for the presentation and debrief.

### Recovering from misrecognition
- **[I] Show what was heard.** If ASR confidence is low or the transcript is under 2 words, have the *patient* respond in character ("Sorry, doctor, what was that?") rather than with a system error.
- **[I]** Offer one-tap repair: "Say again" plus 3–4 suggested question chips (for example "When did the pain start?"). This also acts as a hint for stuck novices.
- **[I]** Add a medical-vocabulary bias or keyword boost to ASR: the drug names in the chart plus symptoms such as "McBurney", "Murphy's", "rebound", "melena".

### Keeping the LLM patient grounded (no diagnosis leaks)
- Use identity framing plus explicit **negative prompts** ("you do not know your diagnosis", "do not use medical jargon", "do not volunteer information not asked") to curb over-helpfulness and hallucination. [S]
  - Sources:
    - https://link.springer.com/article/10.1007/s40596-026-02422-9
    - https://pubmed.ncbi.nlm.nih.gov/39980724/
- Make the prompt very specific and separate the role-play prompt from the feedback prompt. [S] (Holderried)
- **[I] Architecture**
  - Give the patient LLM only a **patient-knowledge view**: symptoms, timeline, PMH, meds, social history, feelings, and lay beliefs ("I think it's food poisoning").
  - Never include the diagnosis string, the lab values, or the exam findings. Exam findings and labs come only from the deterministic engine.
  - Run a cheap post-filter regex or classifier on output for the diagnosis term and its synonyms (appendicitis, cholecystitis, diverticulitis) and lab or imaging words. Regenerate if it hits.
  - Mark which history facts are "only if asked". Track asked facts in code (a deterministic checklist, not the LLM) for scoring.
  - About 14% of questions will fall outside the script (Holderried). Instruct the model to improvise consistent, clinically neutral details and to log them, so the engine and debrief never contradict them.

### Switching roles (patient / attending / coach)
- **[I]** One role per room or scene, if possible:
  - Patient exists only in the clinic.
  - Scalpal is a distinct avatar or voice in the clinic doorway or a "presentation" space.
  - Coach exists only in the OR.
- **[I]** Give each role its own voice timbre, caption colour, name tag and spatial audio position. Patient audio comes from the patient's mouth. Scalpal is diegetic, not a narrator voice in your head.
- **[I]** Make handoffs explicit with a scene transition ("Scalpal walks in: 'Okay, present your patient.'").
- **[I]** Address the active role only. The talk button routes to whoever is "in focus" (gaze or proximity), and the speaking role's name pulses.
- **[I]** Never let two LLM roles speak at once.

---

## 3. Diagnosis → treatment handoff: force the right procedure or let them be wrong?

### Evidence
- **Productive failure**
  - Sinha & Kapur 2021 meta-analysis: 53 studies, 166 comparisons.
  - Problem-solving *before* instruction beat instruction-first on conceptual knowledge and transfer: **d = 0.36**, and **up to 0.58** when designs stuck closely to productive-failure principles. Procedural knowledge was not compromised. [S]
  - Source: https://journals.sagepub.com/doi/pdf/10.3102/00346543211019105
- **Nursing productive-failure simulation**
  - Letting learners make mistakes *before* instruction improved learning and satisfaction. [S]
  - Sources:
    - https://pubmed.ncbi.nlm.nih.gov/33773221/
    - https://opus.lib.uts.edu.au/handle/10453/168660
- **Productive struggle in simulation design (JOHS)**
  - Design slightly beyond mastery.
  - In the prebrief, say outright that struggle is expected.
  - Avoid rescuing too early.
  - In the debrief, ask "what were you thinking?" rather than correcting. [S]
  - Psychological safety is a prerequisite. [S]
  - Source: https://www.johs.org.uk/article/doi/10.54531/SSXC2439
- **Body Interact**: wrong management leads to visible deterioration and complications in real time. [S]

### Recommendation for Scalpal [I]
Let the learner commit to their own diagnosis and plan. Do not silently correct it. Then branch with consequences, but always end in the correct OR so the demo loop finishes.

1. Scalpal records exactly what the learner said: diagnosis, differential, procedure, urgency. The scorecard grades it then and there; correct gets green, wrong gets red.
2. **If the plan is wrong**, Scalpal uses an advocacy-inquiry line, for example: "I heard you say gastroenteritis and outpatient follow-up. I'm worried because of the RLQ rebound and WBC of 15. What made you lean away from appendicitis?" The learner gets **one chance to revise**, which is logged as "revised after prompt".
3. **If the plan is still wrong**, show a short consequence beat: a time-skip card ("6 hours later… perforated, septic") with vitals worsening. Then: "The surgical team takes the case — you'll scrub in." The learner proceeds to the correct procedure with a "case escalated" flag. The debrief treats the miss as the main learning point.
4. Never send the learner into the *wrong* surgery. There's no authored content for it, and it teaches a false procedure. Spend the consequence on the patient outcome instead.
5. For urgency (for example, choosing elective colectomy for perforated diverticulitis), use the same pattern with a shorter consequence card.

This keeps productive failure (commit, feel the consequence, reflect) without a dead end.

---

## 4. Surgical simulation UX

### Products
- **Osso VR** [S]
  - Practice mode gives step-by-step support plus educational insights.
  - Test mode removes procedural guidance and evaluates independent performance, but hints are still available on the **Y button**.
  - A collaborative multi-user mode exists.
  - Users trained with Osso finished 25% faster with fewer step corrections than with guided instruction.
  - Sources:
    - https://www.ossovr.com/support-articles/training-modes-in-the-osso-vr-nursing-series
    - https://www.ossovr.com/post/research-continues-to-support-the-benefits-of-osso-vr-for-surgical-training
- **Touch Surgery** [S]
  - Procedures are decomposed by **cognitive task analysis** into steps and decision points.
  - **Learn mode**: tap or swipe through each step.
  - **Test mode**: perform the manual steps (for example, positioning, scope insertion) and answer **single-best-of-4 MCQs at critical decision steps**.
  - Validated for laparoscopic cholecystectomy.
  - Sources:
    - https://www.researchgate.net/publication/314455179_Validation_of_the_mobile_serious_game_application_Touch_Surgery_for_cognitive_training_and_assessment_of_laparoscopic_cholecystectomy
    - https://en.wikipedia.org/wiki/Touch_Surgery
- **FundamentalVR** [S]
  - Metrics: economy of movement, 3D spatial awareness, surgical gaze, tissue preservation, and handling adverse events.
  - Uses haptics.
  - Sources:
    - https://venturebeat.com/games/fundamentalvr-unveils-virtual-reality-surgery-training-platform-with-haptic-feedback/
    - https://fundamentalsurgery.com/company-updates/validation-study-haptic-feedback/
- **LapSim / proficiency-based VR training** [S]
  - Core metrics: total time, instrument path length (per hand), angular path length, tissue damage, and errors.
  - Proficiency-based VR training reduced errors in residents' first 10 lap choles: controls made about 3× more errors and took 58% longer (Ahlberg 2007).
  - Salpingectomy RCT: operating time halved, from 24 to 12 minutes.
  - Note that construct validity for path length and time is not universal.
  - Sources:
    - https://surgicalscience.com/validation-categories/lapsim/
    - https://www.bmj.com/content/338/bmj.b1802
    - https://cuaj.ca/index.php/journal/article/download/293/227/0

### Cognitive load and feedback timing
- **Immersive VR laparoscopy (Frederiksen 2020 RCT, n=31)** [S]
  - Cognitive load rose by **66%** over baseline in immersive VR, vs 58% in conventional VR.
  - A severe stressor added **+43%** in immersive VR vs +23% in conventional.
  - Performance was worse in immersive VR.
  - Source: https://pubmed.ncbi.nlm.nih.gov/31172325
  - **[I]** Headset novices are already overloaded, so coach sparingly.
- **Concurrent vs terminal feedback** [S]
  - Walsh 2009 (colonoscopy sim): terminal (after-trial) feedback gave better **transfer**. The concurrent-feedback group dropped on the transfer test.
  - Lumbar puncture study: concurrent feedback helped **novices on complex procedures**.
  - Sources:
    - https://pubmed.ncbi.nlm.nih.gov/19907387/
    - https://pubmed.ncbi.nlm.nih.gov/36931315
  - **[I]** For novices in a one-shot demo, use concurrent *step* guidance. Save *performance* critique (efficiency, errors) for after the case.

### Recommendations [I]
- **Onboarding (60–90 s max)**
  - A tutorial "touch the target with each instrument" in the OR before the case starts.
  - Teach grip, trigger, and how the trocar or camera works.
  - Skip it if the learner has done it before.
- **Step structure**
  - Use a CTA-style step list per procedure, 6–10 steps. Example for lap appendectomy: port placement → identify appendix → mesoappendix division → base ligation/stapling → specimen extraction → inspection.
  - Show the steps on a wall monitor (the diegetic OR screen) with the current step highlighted.
- **Coaching**
  - One short voice line per step: under 12 words, imperative, delivered at step start.
  - Speak again only on stall (no progress for about 15 s) or an error.
  - Highlight the target anatomy with a pulsing outline after the stall, not before (cues escalate, per INACSL).
- **Decision points**
  - At 1–2 key moments per procedure (for example, establishing the critical view of safety before clipping in a chole), pause and ask a single-best-answer question, in the Touch Surgery style.
  - These test reasoning without requiring fine motor fidelity on Quest controllers.
- **Errors**
  - Show immediate but non-blocking feedback: a red flash on tissue, haptic buzz, and the coach line "Careful — that's the cystic artery."
  - Log the error, let the learner continue, and save the critique for the recap.
- **Metrics to compute**
  - Time per step.
  - Path length per hand (from the recorded hand motion that already feeds the robot replay).
  - Number of wrong-target contacts.
  - Hints used.
  - Decision-point correctness.
  - Show them against a "proficiency" benchmark band, not raw numbers alone.

---

## 5. Debriefing and recap

### Models
- **PEARLS (Eppich & Cheng 2015)** has 4 phases: [S]
  1. **Reactions** ("How are you feeling?")
  2. **Description** (summarize the case and the main issues)
  3. **Analysis**, choosing between three strategies:
     - learner self-assessment (**plus/delta**: what went well and why, what would you change and why);
     - **focused facilitation** (for example, **advocacy-inquiry**: state an observation and your view, then ask about their reasoning);
     - **directive feedback and teaching** when there is a knowledge gap.
  4. **Summary**: the learner states 1–2 take-aways, or the educator summarizes.
  - Source: https://case.edu/nursing/sites/default/files/2018-05/Article-Eppich-PEARLS.pdf
- **INACSL Debriefing standard.** Debriefing should: [S]
  - be preceded by a prebrief;
  - be specific and based on observable behaviour;
  - be timely;
  - give both positive and constructive analysis;
  - be grounded in a theoretical framework;
  - foster transfer to practice.
  - It may be facilitated by a "technology-enhanced system".
  - The **prebrief** should set expectations, roles and psychological safety, and orient the learner to the modality.
  - Sources:
    - https://lewis.gsu.edu/files/2024/06/Healthcare-Simulation-Standards-of-Best-Practice.pdf
    - https://www.healthysimulation.com/healthcare-simulation-standards-of-best-practice/
- **OMS**: guided reflection comes *before* scores are shown. [S]
- **Brügge RCT**: the structured feedback is what drives the improvement. [S]

### Scorecard design [I]
- **Two separate scores**, never blended into one number:
  - **Clinical Reasoning** (clinic + Scalpal).
  - **Procedural Skill** (OR).
  - Wrong diagnosis followed by good surgery should read clearly as "great hands, missed diagnosis."
- **Clinical Reasoning section**
  - History coverage: critical items asked out of total (a deterministic checklist), with missed critical questions listed.
  - Exam: critical maneuvers done (for example, McBurney, Rovsing, Murphy's).
  - Orders: Full Code-style tiers, with counts of Critical / Reasonable / Unnecessary.
  - Diagnosis: correct or incorrect. Differential: whether it included the must-not-miss items.
  - Procedure choice and urgency.
  - Hints used, and time.
- **Procedural section**
  - Steps completed in order.
  - Time per step vs benchmark.
  - Path length or economy.
  - Errors (by type).
  - Decision-point answers.
- **Recap flow (PEARLS-lite, about 60–90 s)**
  1. Scalpal asks one Reaction question by voice ("How did that feel?"). Don't score it.
  2. One self-assessment question ("One thing you'd do differently?").
  3. Then reveal the scorecard.
  4. Scalpal gives at most 2 strengths and 2 deltas using advocacy-inquiry phrasing, generated by the LLM from the **deterministic** event log so it can't hallucinate events.
  5. End with "one take-away" and the robot replay highlight.
- **[I]** Use the robot-hand replay as the "description" phase for the OR: scrub to error timestamps, marked on the timeline.

---

## 6. Hackathon-demo pacing

### Sources
- Judges form opinions in the first 30 s. [S]
- Get the "wow" in the first minute. [S]
- Script the 90-second walkthrough. [S]
- Hard-code the demo path, mock external services, and keep fallback video or screenshots. [S]
- Rehearse the live demo 5 times. [S]
- Sources:
  - https://reskilll.com/blogs/hackathon-demo-presentation-tips-pitch-3-minutes-win-2026/
  - https://dev.to/pranjulrathour/the-hackathon-demo-that-works-live-a-technical-checklist-4k1
  - https://info.devpost.com/blog/6-tips-for-making-a-hackathon-demo-video

### Target timings [I]

| Stage | Full run | Judge fast path |
|---|---|---|
| Patient pick + prebrief card | 30 s | 10 s (preselect "Appendicitis — Maya, 24") |
| Clinic history (voice) | 3–4 min | 60–90 s (3–4 key questions; chips available) |
| Exam + orders | 1–2 min | 30 s (one-tap "focused abdominal exam" + "CBC, CT") |
| Present to Scalpal | 1 min | 30 s |
| Scorecard (reasoning) | 30 s | 15 s |
| OR onboarding | 60–90 s | 20 s (skip if done) |
| Surgery | 4–6 min | 90 s (2–3 key steps + 1 decision point; remaining steps auto-complete) |
| Robot replay | 30–60 s | 20 s highlight |
| Recap | 60–90 s | 30 s |
| **Total** | **~13–18 min** | **~5–6 min** |

### Fast-path mechanics [I]
- A "Demo mode" toggle that does the following:
  - preselects the best-tuned case;
  - shows suggested-question chips;
  - auto-completes non-key surgical steps with a time-lapse (play the recorded hand motion on fast-forward);
  - keeps every stage visible, so depth is *shown* even if it is *sampled*.
- Depth signals to show judges:
  - 12-patient selection wall, built on real synthetic chart data;
  - live voice with a patient who refuses to name the diagnosis;
  - the deterministic results engine (say "the LLM never invents labs");
  - the branching consequence for a wrong diagnosis (have a second demo run deliberately miss it);
  - the dual scorecard;
  - the robot replay.
- Fallbacks:
  - a prerecorded mixed-reality capture of the full run;
  - offline TTS fillers;
  - a canned patient response set if the LLM or network fails.
- Cast to a laptop or phone so judges see what the headset sees, with captions on.

---

## Source index
- OMS: https://oxfordmedicalsimulation.com/virtual-reality-or-manikin-based-simulation/ ; https://academic-med-surg.scholasticahq.com/article/146315-virtual-reality-a-novel-teaching-tool-in-undergraduate-medicine-clinical-skills-training
- Body Interact: https://bodyinteract.com/blog/real-time-physiology/ ; https://bodyinteract.com/blog/embracing-productive-failure/
- Full Code: https://mwm.ai/apps/full-code-medical-simulation/1207424206
- i-Human: https://ihumanhelp.com/2024/10/22/ihuman-step-by-step-instructions-history-physical-examination-and-diagnosis/
- SimX: https://www.simxvr.com/
- Holderried 2024: https://pmc.ncbi.nlm.nih.gov/articles/PMC11364946/
- Brügge 2024: https://link.springer.com/article/10.1186/s12909-024-06399-7
- LLM-VP systematic review: https://pmc.ncbi.nlm.nih.gov/articles/PMC12811743/
- AIPatient: https://www.nature.com/articles/s43856-025-01283-x
- Huwendiek: https://pmc.ncbi.nlm.nih.gov/articles/PMC6737267/
- VAPS: https://arxiv.org/abs/2503.01767 ; CLiVR (Quest 3 LLM patients): https://arxiv.org/abs/2510.19031
- OSCE timing: https://brieflands.com/journals/jme/articles/142577
- INACSL Simulation Design: https://www.nursingsimulation.org/article/S1876-1399(21)00096-7/fulltext
- INACSL standards (debrief/prebrief): https://lewis.gsu.edu/files/2024/06/Healthcare-Simulation-Standards-of-Best-Practice.pdf
- Turn-taking: https://www.researchgate.net/publication/347821999_Turn-taking_in_Conversational_Systems_and_Human-Robot_Interaction_A_Review
- CHI 2026 latencies: https://dl.acm.org/doi/10.1145/3772318.3790947
- Fillers in VR: https://arxiv.org/abs/2508.11781
- Onset strategies (PTT vs VAD): https://doi.org/10.1145/3788851.3805005
- LiveKit turns: https://docs.livekit.io/agents/logic/turns/
- Nielsen: https://www.nngroup.com/articles/response-times-3-important-limits/
- Meta accessibility: https://developers.meta.com/horizon/design/accessibility/
- Prompt guardrails: https://link.springer.com/article/10.1007/s40596-026-02422-9 ; https://pubmed.ncbi.nlm.nih.gov/39980724/
- Kapur meta-analysis: https://journals.sagepub.com/doi/pdf/10.3102/00346543211019105
- Productive struggle: https://www.johs.org.uk/article/doi/10.54531/SSXC2439
- Osso: https://www.ossovr.com/support-articles/training-modes-in-the-osso-vr-nursing-series
- Touch Surgery: https://www.researchgate.net/publication/314455179_Validation_of_the_mobile_serious_game_application_Touch_Surgery_for_cognitive_training_and_assessment_of_laparoscopic_cholecystectomy
- FundamentalVR: https://venturebeat.com/games/fundamentalvr-unveils-virtual-reality-surgery-training-platform-with-haptic-feedback/
- LapSim: https://surgicalscience.com/validation-categories/lapsim/
- Frederiksen 2020: https://pubmed.ncbi.nlm.nih.gov/31172325
- Walsh 2009: https://pubmed.ncbi.nlm.nih.gov/19907387/ ; LP concurrent: https://pubmed.ncbi.nlm.nih.gov/36931315
- PEARLS: https://case.edu/nursing/sites/default/files/2018-05/Article-Eppich-PEARLS.pdf
- Hackathon demo: https://dev.to/pranjulrathour/the-hackathon-demo-that-works-live-a-technical-checklist-4k1
