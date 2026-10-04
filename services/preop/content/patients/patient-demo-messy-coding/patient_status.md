# Patient Status: Dolores Marchetti

## Identity and demographics
Dolores "Dee" Marchetti, 63 F, retired bakery owner. Lives with her husband. Ex-smoker (quit 20 years ago), occasional wine. Synthetic patient.

## Presentation
Seen in surgery clinic, quiescent, about three months after her last episode. She has had three episodes of sigmoid diverticulitis in 18 months, the June episode complicated by a small abscess treated with antibiotics and no drain. She wants resection and fears "a bag."

## Key history, exam and test findings
- **History:** Every episode was left lower quadrant with fever (39 C in June). Each was CT confirmed by her report. August colonoscopy showed no cancer and left-sided diverticula. No bleeding, no stool narrowing, no pneumaturia or UTIs. Father had colon cancer. Prior tubal ligation.
- **Exam:** T 36.9, HR 70, BP 144/82, SpO2 98%, 76 kg. Mild deep LLQ tenderness, no mass, guarding or rebound. Small healed umbilical scar, no hernia.
- **Tests:** CBC normal (WBC 7.8, Hb 13.0). CRP 6. BMP: K 3.4 (low, thiazide), Cr 0.9, eGFR >60, glucose 128. UA clean. CT: sigmoid diverticulosis, mild wall thickening, abscess resolved, no fistula or mass. Type O positive, antibody screen negative.

## Working diagnosis
Recurrent sigmoid diverticulitis, complicated by a resolved abscess.

## Procedure and urgency
Laparoscopic sigmoid colectomy (`lap_sigmoid_colectomy`). Elective.

## Chart risk flags and operative impact
- **Allergy (amoxicillin, hives):** At `access_umbilical`, confirm prophylaxis avoids aminopenicillins. Cefazolin plus metronidazole is appropriate.
- **Diabetes (metformin):** At `access_umbilical`, check glucose before incision and monitor throughout. Metformin is held on the day of surgery.
- **Incomplete chart:** At the time out before `access_umbilical`, confirm identity, allergies and medications directly. The "blood pressure pill" is hydrochlorothiazide 25 mg. Expect potassium repletion and volume attention after bowel prep.
- **Umbilical access:** Prior tubal ligation scar at the umbilicus, so expect possible adhesions at entry.
- **Ureter:** Prior abscess may distort the left ureter. Identify it deliberately at `identify_ureter`.

## Data gaps and chart discrepancies
- The chart creatinine was cancelled. Today's BMP replaces it.
- The chart's simvastatin entry has unknown status. She stopped it in spring for myalgias.
- The chart contains no diverticular history (no CT, admission or colonoscopy). Her surgical history rests on her report. Do not cite the chart as proof of three CT-confirmed episodes.
- No A1c is on file.
- Chart glucose is in mmol/L and the BMP is in mg/dL. Do not compare the raw numbers.
- Metoprolol frequency is undocumented. Continue it perioperatively.
- The case plan indication omits "complicated." The abscess is the stronger reason to operate.

## What the learner should have elicited
1. **Critical:** medication reconciliation (thiazide identified, statin stopped).
2. **Critical:** BMP for kidney function and potassium.
3. **Critical:** colonoscopy excluding cancer.
4. **Critical:** allergy reaction type (hives).
5. Episode pattern and abscess, CT confirmation, urinary fistula symptoms, bowel red flags, family history, absence of current fever.
6. Vitals and abdominal exam, CT, CBC, UA, and type and screen.
