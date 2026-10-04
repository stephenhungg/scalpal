# Patient Status: patient-demo-consent-partial

## Identity and demographics
The chart label reads "Unnamed patient (limited chart)." By consent, the chart shares no name, birth date or sex. In person, the patient is Sam Ortiz, a 61-year-old man and retired city bus driver who lives with his wife. Confirm his identity directly. He welcomes this.

## Presentation
Elective surgery clinic referral for gallbladder removal after recurrent biliary colic. He is pain-free today.

## Key history, exam and test findings
- **History:** 5 or 6 attacks over about 6 months, the last 2 weeks ago.
  - Steady, pressing right upper quadrant pain, 7/10, sometimes radiating to the right shoulder.
  - Triggered by fried food or large dinners. Resolves on its own in 1 to 2 hours.
  - Nausea without vomiting.
  - No fever, dark urine or pale stools.
- **Past medical:** hypertension and hyperlipidemia. Denies MI, lung disease and diabetes.
- **Past surgical:** open right inguinal hernia repair about 15 years ago, with uneventful anesthesia.
- **Social:** about 30 pack-years, quit 10 years ago.
- **Exam:**
  - T 36.8 C, HR 76, BP 138/84, RR 14, SpO2 97% on room air.
  - No icterus. Soft abdomen with minimal deep RUQ tenderness. Murphy sign negative.
  - Healed right inguinal incision, no recurrence.
- **Tests:**
  - Normal CBC, CRP, BMP (K 4.5, Cr 1.0), LFTs (TB 0.8, ALP 90), lipase and UA.
  - Ultrasound: multiple stones up to 15 mm, 3 mm wall, no pericholecystic fluid, CBD 4 mm.
  - Type A positive, antibody screen negative.

## Working diagnosis
Symptomatic cholelithiasis (biliary colic), without features of cholecystitis or a duct stone.

## Procedure and urgency
Laparoscopic cholecystectomy (`lap_cholecystectomy`), elective.

## Chart risk flags and operative impact
- **Incomplete chart (high):**
  - Conditions, labs and demographics are unknown, not absent.
  - At `access_umbilical`, the time out must confirm identity (two identifiers), allergies and medications directly with the patient before incision.
- **Penicillin allergy (moderate):**
  - The reaction was a remote rash in the Army, with no swelling or breathing trouble.
  - At `access_umbilical`, confirm the prophylactic antibiotic was chosen around the allergy. Cefazolin is appropriate.
- **Clinical context (not formal flags):**
  - Lisinopril is commonly held the morning of surgery.
  - His smoking history raises pulmonary and cardiac risk under anesthesia.
  - Prior right lower quadrant surgery is unlikely to affect umbilical access.

## Data gaps and chart discrepancies
- **Not on the chart:** problem list, labs, vitals and demographics. History is patient-reported. Do not attempt to retrieve records outside his consent.
- **Identity metadata conflict:** FinchNode scenario metadata names "Marcus Delacroix, born 1972," which conflicts with the authored Sam Ortiz, 61. This is unresolved, so do not surface the metadata name.
- **Medications:** there is no authored medications topic, so whether the list is complete relies on the chart lines.
- **ECG:** none is available, despite cardiac ischemia being on the differential.

## What the learner should have elicited
1. **Critical:**
   - Past medical history (heart, lung, kidney, diabetes) and in-person identity confirmation.
   - BMP, because he takes lisinopril and no labs were shared.
   - LFTs as a duct-stone screen.
   - Characterization of the penicillin reaction.
2. **Expected:**
   - Onset and pattern, triggers and relief, fever, urine color.
   - Whether the medication list is complete.
   - Smoking history and past surgery with anesthesia.
   - Vitals, abdominal palpation and Murphy sign.
   - Ultrasound and CBC.
