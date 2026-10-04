# Clinical authoring standards

## Contents
- Simulation design (INACSL)
- Case template mapping
- History-taking frameworks
- Multiple-choice rounds (NBME)
- Procedure decomposition
- WHO Surgical Safety Checklist (Time-Out)
- Hemorrhage classes (ATLS)
- Blood volume and vitals by age
- How Scalpal's monitor uses this
- Sources

Everything a case teaches is illustrative and must be reviewed by a clinician before it is presented as anything more. Cite a source for every management claim in `case.json` `notes`; flag unsourced claims for review.

## Simulation design (INACSL)

INACSL Healthcare Simulation Standards of Best Practice, Simulation Design (2021), 11 criteria. The ones that shape a Scalpal case:
- **Measurable objectives** (3 or 4 per scenario). Write them in `notes`.
- **A situation and backstory** revealed "if requested through adequate inquiry": that is the encounter history and the interview.
- **Scripted cues** tied to performance measures, delivered through the patient, the monitor or new results.
- **Critical actions**: evidence-based must-do moves. In Scalpal these are the encounter `critical` items and the safety content of the correct picks.
- **Conceptual fidelity**: "vital signs are consistent with the diagnosis". The chart, story, exam, labs and vitals must make sense together.
- **Debriefing**: the recap and scorecard feedback; write `feedback` lines as debrief points.
- **Pilot test** before calling a case done: one full playthrough, then fix.

## Case template mapping

| Sim template section (NLN style) | Scalpal field |
| --- | --- |
| Brief description of patient, PMH, allergies | FinchNode chart (not edited) |
| HPI, primary diagnosis | `plan.presentation`, `encounter.history`, `encounter.diagnosis` |
| Scenario progression and cues | interview rounds, findings, monitor |
| Expected interventions, critical actions | correct picks, `encounter.critical`, OR milestones and guardrails |
| Debriefing | `feedback`, recap |

## History-taking frameworks

- **SOCRATES**: site, onset, character, radiation, associated symptoms, timing, exacerbating and relieving factors, severity. Drives the pain story topics (`location`, `onset`, `character`, `migration`, `nausea_vomiting`, `aggravating_relieving`, `severity`).
- **SAMPLE / AMPLE**: signs and symptoms, allergies, medications, past history, last oral intake, events. Maps to `allergies`, `medications`, `past_medical`, `past_surgical`, `last_meal`. Last oral intake matters for anesthesia; make it explicit.

## Multiple-choice rounds (NBME)

NBME item-writing rules, applied to "what next" rounds:
1. Each round tests one important decision.
2. Test application to this patient, not recall.
3. The lead-in is closed and clear. Cover-the-options test: the answer is guessable from the situation alone.
4. Options are homogeneous (one kind) and plausible.
5. Remove technical flaws. Irrelevant difficulty: long options, vague terms, "none of the above", negatives ("except", "not"). Testwiseness cues: grammar, absolutes, the longest or most specific option being right, a word repeated between prompt and key, convergence.

Every distractor should be a mistake learners really make. Its `feedback` says why it is wrong for this patient.

## Procedure decomposition

Break procedures into phases, steps and tasks (hierarchical task analysis), and name each step's hazard and bleeding source.
- **Open appendectomy**: incision at McBurney's point (or Lanz), split external oblique along its fibers, split internal oblique and transversus, lift and open the peritoneum, follow a taenia to the base, deliver cecum and appendix, divide the mesoappendix between clamps and ligate, crush and ligate the base, divide, inspect, irrigate if contaminated, close in layers. Bleeding source: the appendicular artery in the mesoappendix.
- **Laparoscopic cholecystectomy**: pneumoperitoneum, ports, expose the hepatocystic triangle, achieve the **critical view of safety** (triangle cleared, lower third of the gallbladder off the cystic plate, two and only two structures entering the gallbladder), then clip and divide duct and artery, remove from the liver bed, hemostasis, extract, close. Bleeding sources: cystic artery, liver bed.

## WHO Surgical Safety Checklist (Time-Out)

Before skin incision: team introductions; confirm patient name, procedure and incision site; antibiotic prophylaxis within 60 minutes; anticipated critical events (surgeon: critical steps, duration, expected blood loss; anesthesia: patient-specific concerns; nursing: sterility, equipment); essential imaging displayed. Sign In flags known allergy, difficult airway or aspiration risk, and risk of blood loss over 500 ml (7 ml/kg in children).

Every Time-Out answer must be derivable from the package: name from the chart, site from the procedure, allergies and anticoagulants from the chart, expected blood loss against 500 ml or 7 ml/kg.

## Hemorrhage classes (ATLS)

| | Class I | Class II | Class III | Class IV |
| --- | --- | --- | --- | --- |
| Blood volume lost | < 15% | 15 to 30% | 31 to 40% | > 40% |
| Heart rate (9th ed. numbers) | < 100 | 100 to 120 | 120 to 140 | > 140 |
| Systolic BP | normal | normal | decreased | decreased |
| Pulse pressure | normal | decreased | decreased | decreased |
| Respiratory rate | 14 to 20 | 20 to 30 | 30 to 40 | > 35 |
| Base deficit (10th ed.) | 0 to -2 | -2 to -6 | -6 to -10 | -10 or less |
| Blood products | monitor | possible | yes | massive transfusion |

ATLS itself calls the table imprecise; teach trends (rising heart rate, narrowing pulse pressure), not single thresholds. The 10th edition table was reconstructed from secondary sources; verify against the manual before quoting numbers to learners.

## Blood volume and vitals by age

Estimated blood volume: adult 65 to 70 ml/kg, child 70 to 75, infant 70 to 80, term neonate 80 to 90.

PALS normal ranges (awake heart rate, respiratory rate): infant 100 to 190, 30 to 53; toddler 98 to 140, 22 to 37; preschool 80 to 120, 20 to 28; school age 75 to 118, 18 to 25; adolescent 60 to 100, 12 to 20. Hypotension: SBP below 70 + 2 x age (1 to 10 years), below 90 over 10 years.

Children compensate: hypotension means more than 45% loss, so blood pressure should hold until late while heart rate climbs. Fever raises heart rate about 10 per degree C; do not confuse it with hemorrhage.

## How Scalpal's monitor uses this

`services/preop/src/physiology.ts` (mirrored in `services/vitals`): measured or charted baseline plus a simulated change from blood loss by ATLS class; 70 ml/kg adult and 80 ml/kg child in the patient model; an active bleed adds half a minute of look-ahead loss; a critical injury adds a spike. The VR baseline comes from the chart's latest vitals (or authored defaults, a child default under 13); in AR, Presage can supply a measured heart and breathing rate at Time-Out. Every displayed value is labeled simulated. Authors do not set vitals directly; keep the story consistent with the chart's baseline (a febrile appendicitis can run a heart rate near 100 before any bleeding).

## Sources

- INACSL Standards Committee (2021), Simulation Design. Clinical Simulation in Nursing 58:14-21. <https://doi.org/10.1016/j.ecns.2021.08.009>
- NLN-style Simulation Design Template (GVSU, 2023). <https://www.gvsu.edu/cms5/asset/9141cab0-7fe7-40e8-b832-7a0cd2df6b91/091fab78-16bf-4dcb-9377-60a2c6c78734/simulation-design-template-2023.docx>
- NBME, Constructing Written Test Questions for the Basic and Clinical Sciences, 4th ed. <https://health.uconn.edu/faculty-development/wp-content/uploads/sites/69/2017/06/constructing_written_test_questions.pdf>; current guide <https://www.nbme.org/educators/item-writing-guide>
- WHO Surgical Safety Checklist (2009). <https://cdn.who.int/media/docs/default-source/patient-safety/9789241598590-eng-checklist.pdf>
- Brunt LM et al. (2020), Safe Cholecystectomy Multi-Society Practice Guideline. Surg Endosc 34:2827-2855.
- Hashimoto DA et al. (2018), procedural map for laparoscopic cholecystectomy. <https://pmc.ncbi.nlm.nih.gov/articles/PMC6581213/>
- StatPearls, Appendectomy. <https://www.ncbi.nlm.nih.gov/books/NBK580514/>
- ATLS 10th edition update, ACS Bulletin (2018). <https://web.archive.org/web/2019/http://bulletin.facs.org/2018/06/atls-10th-edition-offers-new-insights-into-managing-trauma-patients/>
- Estimated blood volume by age: University of Iowa protocols. <https://iowaprotocols.medicine.uiowa.edu/node/649>
- Pediatric vital signs: <https://wikem.org/wiki/Pediatric_Vital_Signs>
- Appendicitis management (verify each before citing; listed so authors do not cite from memory):
  - Di Saverio S et al. (2020), Diagnosis and treatment of acute appendicitis: 2020 update of the WSES Jerusalem guidelines. World J Emerg Surg 15:27. doi:10.1186/s13017-020-00306-3
  - CODA Collaborative (2020), A randomized trial comparing antibiotics with appendectomy for appendicitis. N Engl J Med 383:1907-1919. doi:10.1056/NEJMoa2014320 (antibiotics-first is a legitimate option; say why a case does not offer it)
  - Bratzler DW et al. (2013), Clinical practice guidelines for antimicrobial prophylaxis in surgery. Am J Health Syst Pharm 70:195-283 (ASHP, IDSA, SIS, SHEA)
  - Khan DA et al. (2022), Drug allergy: a 2022 practice parameter update. J Allergy Clin Immunol 150:1333-1393 (penicillin label risk stratification; cefazolin with a low-risk history)
  - American Diabetes Association, Standards of Care, section 16: Diabetes care in the hospital (current year) (perioperative glucose, holding metformin)
  - ACR Manual on Contrast Media (current edition) (metformin and iodinated contrast by eGFR)
- HL7 `HTEST` test health data label. <https://terminology.hl7.org/CodeSystem-v3-ActReason.json>
