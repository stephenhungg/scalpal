// Authored patient encounters: the 1-on-1 interview before surgery. Keyed by the same plan subject as
// CASE_PLANS, so a sandbox patient gets the encounter of the demo patient its scenario mirrors.
//
// The patient agent never holds these facts in its prompt. It must call answer/examine/order_test, which
// return one fact at a time and log what the learner actually elicited. That keeps the patient from
// inventing history and makes scoring deterministic. Medications, allergies, and problems come from the
// FinchNode chart unless an encounter overrides them (a patient can tell you what an empty chart cannot).
// Illustrative teaching content, not clinical guidance.

export const HISTORY_TOPICS = [
  "chief_complaint",
  "onset",
  "location",
  "migration",
  "character",
  "severity",
  "aggravating_relieving",
  "nausea_vomiting",
  "appetite",
  "fever",
  "bowel",
  "urinary",
  "menstrual_pregnancy",
  "last_meal",
  "past_medical",
  "past_surgical",
  "medications",
  "allergies",
  "social",
  "family",
  "recent_illness",
] as const;
export type HistoryTopic = (typeof HISTORY_TOPICS)[number];

export const EXAM_MANEUVERS = [
  "general_appearance",
  "vitals",
  "abdomen_inspection",
  "abdomen_palpation",
  "mcburney_point",
  "rebound",
  "guarding_rigidity",
  "rovsing",
  "psoas",
  "obturator",
  "murphy",
  "cva_tenderness",
  "chest_lungs",
  "genitourinary",
  "pelvic",
] as const;
export type ExamManeuver = (typeof EXAM_MANEUVERS)[number];

export const TESTS = [
  "cbc",
  "crp",
  "bmp",
  "lactate",
  "lipase",
  "urinalysis",
  "pregnancy_test",
  "ultrasound",
  "ct_abdomen_pelvis",
  "type_and_screen",
] as const;
export type TestId = (typeof TESTS)[number];

export type RubricItem =
  | { kind: "history"; id: HistoryTopic; why: string }
  | { kind: "exam"; id: ExamManeuver; why: string }
  | { kind: "test"; id: TestId; why: string };

export interface DifferentialItem {
  id: string;
  label: string;
  keywords: string[]; // any match counts
}

export interface Encounter {
  planSubject: string;
  persona: {
    speaker: "patient" | "parent";
    name: string; // who is talking
    patientName: string; // who is sick
    age: number;
    patientSex: "female" | "male"; // the sick patient's chart sex, checked against FinchNode demographics (the voice may be a parent's)
    voiceKey: "adult_female" | "adult_male" | "parent_female";
    demeanor: string; // how they talk, for the persona prompt
    opener: string; // first line, before any questions
  };
  history: Partial<Record<HistoryTopic, string>>; // first person, plain language; missing topics fall back to the chart or "not sure"
  exam: Partial<Record<ExamManeuver, { reaction: string; finding: string }>>;
  tests: Partial<Record<TestId, { result: string; abnormal: boolean }>>;
  diagnosis: { label: string; keywords: string[][]; partial?: { label: string; keywords: string[][] } }; // every group must match
  procedureKeywords: string[][];
  urgency: "elective" | "urgent" | "emergency";
  differential: DifferentialItem[];
  critical: RubricItem[]; // missing any of these is called out first
  expected: RubricItem[];
  testNotes?: Partial<Record<TestId, string>>; // teaching notes when a test is ordered (e.g. radiation in children)
}

const ddx = (id: string, label: string, ...keywords: string[]): DifferentialItem => ({ id, label, keywords });

const COMMON_APPY_EXPECTED: RubricItem[] = [
  { kind: "history", id: "onset", why: "Timing separates early appendicitis from perforation." },
  { kind: "history", id: "migration", why: "Pain that starts at the umbilicus and moves to the right lower quadrant is the classic appendicitis pattern." },
  { kind: "history", id: "nausea_vomiting", why: "Anorexia and nausea after the pain starts support appendicitis." },
  { kind: "history", id: "fever", why: "Fever points to inflammation and, when high, to perforation." },
  { kind: "history", id: "bowel", why: "Diarrhea or blood suggests gastroenteritis or colitis instead." },
  { kind: "history", id: "urinary", why: "Urinary symptoms point to infection or a stone." },
  { kind: "history", id: "past_surgical", why: "A prior appendectomy rules the diagnosis out." },
  { kind: "history", id: "medications", why: "Anticoagulants, steroids, and other drugs change the plan." },
  { kind: "exam", id: "vitals", why: "Tachycardia and fever set urgency and flag sepsis." },
  { kind: "exam", id: "abdomen_palpation", why: "Localized right lower quadrant tenderness is the key finding." },
  { kind: "exam", id: "rebound", why: "Rebound tenderness signals peritoneal irritation." },
  { kind: "test", id: "cbc", why: "Leukocytosis supports the diagnosis and the Alvarado score." },
  { kind: "test", id: "urinalysis", why: "Rules out urinary infection and stones." },
];

export const ENCOUNTERS: Encounter[] = [
  {
    planSubject: "patient-demo-multi-source",
    persona: {
      speaker: "patient",
      name: "Priya Ramaswamy",
      patientName: "Priya Ramaswamy",
      age: 40,
      patientSex: "female",
      voiceKey: "adult_female",
      demeanor: "An accountant, articulate and a little anxious. Lies still because moving hurts. Answers directly, sometimes asks what a medical term means.",
      opener: "Hi, sorry, I'm trying not to move much. My stomach has been killing me since yesterday.",
    },
    history: {
      chief_complaint: "My stomach really hurts, down on the right side.",
      onset: "It started yesterday morning, around nine. It came on slowly, not all at once.",
      location: "Right now it's low on the right side, about here.",
      migration: "It actually started around my belly button, kind of dull. By the afternoon it moved down to the right and got sharper.",
      character: "It's constant and sharp now. It never fully goes away.",
      severity: "Maybe a seven out of ten. The car ride over was awful, every bump hurt.",
      aggravating_relieving: "Moving, coughing, and bumps make it worse. Lying still helps a little. I took ibuprofen last night and it barely touched it.",
      nausea_vomiting: "I've been nauseous since the pain started, but I haven't thrown up.",
      appetite: "I haven't wanted to eat anything since yesterday.",
      fever: "I felt hot and had chills last night. I didn't check my temperature.",
      bowel: "My last bowel movement was yesterday morning and it was normal. No diarrhea, no blood.",
      urinary: "No burning or blood when I pee. Maybe going a little more often, I'm not sure.",
      menstrual_pregnancy: "My last period was about two weeks ago, it was normal. I don't think I'm pregnant, but I'm not on any birth control right now.",
      last_meal: "I had a small dinner at around seven last night, just soup. Only water since then, last sip about two hours ago.",
      past_surgical: "No surgeries. I've never been operated on.",
      social: "I'm an accountant. I don't smoke, I have a glass of wine on weekends.",
      family: "Nothing like this in my family that I know of.",
      recent_illness: "No, I haven't been sick lately.",
    },
    exam: {
      general_appearance: { reaction: "", finding: "Uncomfortable, lying still with knees slightly bent, avoids moving." },
      vitals: { reaction: "", finding: "Temperature 37.9 C, heart rate 98, blood pressure 124/78, respiratory rate 16, oxygen saturation 99% on room air." },
      abdomen_inspection: { reaction: "", finding: "Flat abdomen, no scars, no distension." },
      abdomen_palpation: { reaction: "Ow! Yes, right there, that's the spot.", finding: "Tender in the right lower quadrant with voluntary guarding. Left side soft and nontender." },
      mcburney_point: { reaction: "Ah, yes, that's exactly where it hurts.", finding: "Maximal tenderness at McBurney's point." },
      rebound: { reaction: "Ow, letting go was worse than pushing!", finding: "Rebound tenderness in the right lower quadrant." },
      guarding_rigidity: { reaction: "I'm tensing up, sorry, I can't help it.", finding: "Voluntary guarding in the right lower quadrant, no generalized rigidity." },
      rovsing: { reaction: "Wait, pushing on the left made my right side hurt.", finding: "Positive Rovsing sign." },
      psoas: { reaction: "That pulls on the right side a bit.", finding: "Mildly positive right psoas sign." },
      obturator: { reaction: "That's fine, it doesn't really hurt.", finding: "Negative obturator sign." },
      murphy: { reaction: "No, that's okay up there.", finding: "Negative Murphy sign, no right upper quadrant tenderness." },
      cva_tenderness: { reaction: "No, my back is fine.", finding: "No costovertebral angle tenderness." },
      chest_lungs: { reaction: "", finding: "Lungs clear to auscultation." },
      pelvic: { reaction: "Okay, that's uncomfortable but not the bad pain.", finding: "No cervical motion tenderness, no adnexal mass or tenderness, no discharge." },
    },
    tests: {
      cbc: { result: "White count 13.1 with 82% neutrophils. Hemoglobin 11.6 (low, consistent with her known iron deficiency). Platelets 280.", abnormal: true },
      crp: { result: "CRP 48 mg/L (elevated).", abnormal: true },
      bmp: { result: "Sodium 138, potassium 4.1, creatinine 0.8, glucose 96. Normal kidney function.", abnormal: false },
      lactate: { result: "Lactate 1.2 (normal).", abnormal: false },
      lipase: { result: "Lipase 32 (normal).", abnormal: false },
      urinalysis: { result: "No blood, no leukocyte esterase, no nitrites. Normal.", abnormal: false },
      pregnancy_test: { result: "Serum beta-hCG negative.", abnormal: false },
      ultrasound: { result: "Appendix not clearly visualized. No free fluid. Ovaries normal with normal flow.", abnormal: false },
      ct_abdomen_pelvis: { result: "Dilated 11 mm appendix with periappendiceal fat stranding. No abscess, no free air.", abnormal: true },
      type_and_screen: { result: "Type O positive, antibody screen negative.", abnormal: false },
    },
    diagnosis: { label: "Acute appendicitis", keywords: [["appendicitis", "appendix"]] },
    procedureKeywords: [["appendectomy", "appendicectomy", "remove the appendix", "take out the appendix", "take the appendix out"]],
    urgency: "urgent",
    differential: [
      ddx("ectopic", "Ectopic pregnancy", "ectopic"),
      ddx("torsion", "Ovarian torsion", "torsion", "ovarian"),
      ddx("cyst", "Ruptured ovarian cyst", "cyst"),
      ddx("pid", "Pelvic inflammatory disease or tubo-ovarian abscess", "pid", "pelvic inflammatory", "tubo"),
      ddx("stone", "Ureteric stone", "stone", "kidney", "renal colic", "ureter"),
      ddx("uti", "Urinary tract infection or pyelonephritis", "uti", "urinary", "pyelo"),
      ddx("crohns", "Crohn's disease or terminal ileitis", "crohn", "ileitis", "ibd"),
      ddx("adenitis", "Mesenteric adenitis", "adenitis", "lymph"),
    ],
    critical: [
      { kind: "history", id: "allergies", why: "She has a high-severity latex allergy: the room, gloves, and catheters must be latex free." },
      { kind: "history", id: "menstrual_pregnancy", why: "A woman of reproductive age with right lower quadrant pain needs pregnancy excluded: ectopic pregnancy is the can't-miss diagnosis." },
      { kind: "test", id: "pregnancy_test", why: "History alone does not exclude pregnancy. Order a beta-hCG before imaging and surgery." },
      { kind: "history", id: "last_meal", why: "Last oral intake sets anesthesia timing and aspiration risk." },
    ],
    expected: [
      ...COMMON_APPY_EXPECTED,
      { kind: "history", id: "past_medical", why: "Her anemia lowers her tolerance for blood loss." },
      { kind: "test", id: "ct_abdomen_pelvis", why: "In an adult with an equivocal ultrasound, CT confirms the diagnosis." },
    ],
  },
  {
    planSubject: "patient-demo-pediatric-asthma",
    persona: {
      speaker: "parent",
      name: "Laura Abernathy",
      patientName: "Theo Abernathy",
      age: 9,
      patientSex: "male",
      voiceKey: "parent_female",
      demeanor: "Theo's mom. Worried but organized, knows his medical history well. Speaks for Theo and sometimes relays what he says ('he says it hurts more when he walks').",
      opener: "Hi, I'm Laura, Theo's mom. He's been miserable since last night and he won't let anyone touch his belly.",
    },
    history: {
      chief_complaint: "His tummy hurts, on the lower right side now.",
      onset: "It started last night around six, before dinner. Maybe eighteen hours ago.",
      location: "He points to the lower right side of his belly.",
      migration: "At first he said it was around his belly button. This morning he said it moved down to the right.",
      character: "He says it's a sharp, steady pain. It doesn't come and go.",
      severity: "He says eight. He's usually tough about this stuff.",
      aggravating_relieving: "He's walking hunched over. The car ride here made him cry, and he won't jump or run. Lying curled up helps a bit.",
      nausea_vomiting: "He threw up once this morning. He's been nauseous since last night.",
      appetite: "He didn't want his pizza last night, and he never refuses pizza.",
      fever: "He felt warm, I checked this morning and it was 38.0.",
      bowel: "His last poop was yesterday and it was normal. No diarrhea.",
      urinary: "No complaints about peeing.",
      last_meal: "A few crackers around seven last night. Just sips of water this morning, the last about an hour ago.",
      past_surgical: "He's never had surgery.",
      social: "He's in third grade. Nobody smokes at home.",
      family: "No one in the family has had problems with anesthesia that I know of.",
      recent_illness: "No colds or stomach bugs lately. Nobody at home is sick.",
      past_medical: "He has asthma, it's well controlled. He's never been hospitalized for it or needed a breathing tube. He has eczema too. His vaccines are up to date.",
      medications: "He uses a fluticasone inhaler every day, and albuterol when he needs it. Last time he needed the albuterol was two days ago.",
      allergies: "He's very allergic to peanuts. He had anaphylaxis once when he was five, so he carries an EpiPen. He's also allergic to dust mites. No medication allergies that we know of.",
    },
    exam: {
      general_appearance: { reaction: "", finding: "Quiet, lying still curled on his side, reluctant to move. Not toxic appearing." },
      vitals: { reaction: "", finding: "Temperature 38.0 C, heart rate 112, blood pressure 104/66, respiratory rate 20, oxygen saturation 98% on room air. Weight 30 kg." },
      abdomen_inspection: { reaction: "", finding: "Flat, not distended. He guards with his hand over the right lower quadrant." },
      abdomen_palpation: { reaction: "He's pulling away, he says that's where it hurts.", finding: "Right lower quadrant tenderness with guarding. Rest of the abdomen soft." },
      mcburney_point: { reaction: "He yelped, that's the spot.", finding: "Maximal tenderness at McBurney's point." },
      rebound: { reaction: "He's crying, he says letting go hurt more.", finding: "Rebound tenderness in the right lower quadrant." },
      guarding_rigidity: { reaction: "He's tensing his tummy.", finding: "Voluntary guarding in the right lower quadrant, no generalized rigidity." },
      rovsing: { reaction: "He says pushing on the left made the right side hurt.", finding: "Positive Rovsing sign." },
      psoas: { reaction: "He doesn't like lifting his right leg.", finding: "Positive right psoas sign." },
      obturator: { reaction: "He says that's okay.", finding: "Negative obturator sign." },
      murphy: { reaction: "He says that's fine up there.", finding: "No right upper quadrant tenderness." },
      cva_tenderness: { reaction: "", finding: "No costovertebral angle tenderness." },
      chest_lungs: { reaction: "", finding: "Clear breath sounds bilaterally, no wheeze. Normal work of breathing." },
      genitourinary: { reaction: "He's embarrassed but says it doesn't hurt there.", finding: "Normal testicular exam, no swelling or tenderness, normal cremasteric reflex." },
    },
    tests: {
      cbc: { result: "White count 14.2 with 85% neutrophils. Hemoglobin 13.1. Platelets 310.", abnormal: true },
      crp: { result: "CRP 32 mg/L (elevated).", abnormal: true },
      bmp: { result: "Sodium 137, potassium 4.0, creatinine 0.5, glucose 102.", abnormal: false },
      lipase: { result: "Lipase normal.", abnormal: false },
      urinalysis: { result: "Normal. No blood, no leukocytes, no nitrites.", abnormal: false },
      ultrasound: { result: "Noncompressible 9 mm appendix with periappendiceal fluid and hyperemia. No abscess.", abnormal: true },
      ct_abdomen_pelvis: { result: "Dilated 9 mm appendix with fat stranding, no abscess.", abnormal: true },
      type_and_screen: { result: "Type A positive, antibody screen negative.", abnormal: false },
    },
    testNotes: {
      ct_abdomen_pelvis: "In children, ultrasound comes first to avoid radiation. CT is for an equivocal ultrasound.",
    },
    diagnosis: { label: "Acute appendicitis", keywords: [["appendicitis", "appendix"]] },
    procedureKeywords: [["appendectomy", "appendicectomy", "remove the appendix", "take out the appendix", "take the appendix out"]],
    urgency: "urgent",
    differential: [
      ddx("adenitis", "Mesenteric adenitis", "adenitis", "lymph"),
      ddx("gastro", "Gastroenteritis", "gastroenteritis", "stomach bug", "viral"),
      ddx("constipation", "Constipation", "constipation"),
      ddx("intussusception", "Intussusception", "intussusception"),
      ddx("testicular", "Testicular torsion", "testicular", "torsion"),
      ddx("uti", "Urinary tract infection", "uti", "urinary"),
      ddx("pneumonia", "Right lower lobe pneumonia", "pneumonia"),
      ddx("meckel", "Meckel's diverticulitis", "meckel"),
    ],
    critical: [
      { kind: "history", id: "allergies", why: "Peanut anaphylaxis with an EpiPen: the team needs to know before induction, even though it is not a drug allergy." },
      { kind: "history", id: "past_medical", why: "Asthma changes anesthesia: ask about control, recent rescue inhaler use, and past intubations." },
      { kind: "history", id: "last_meal", why: "Last oral intake sets anesthesia timing and aspiration risk." },
      { kind: "exam", id: "genitourinary", why: "Every boy with lower abdominal pain needs a testicular exam: torsion is the can't-miss mimic." },
    ],
    expected: [
      ...COMMON_APPY_EXPECTED,
      { kind: "history", id: "medications", why: "His controller and rescue inhaler use tell you how stable his asthma is." },
      { kind: "exam", id: "chest_lungs", why: "Check for wheeze before anesthesia, and right lower lobe pneumonia can mimic appendicitis." },
      { kind: "test", id: "ultrasound", why: "Ultrasound is the first imaging test in children." },
    ],
  },
  {
    planSubject: "patient-demo-sparse",
    persona: {
      speaker: "patient",
      name: "Jonah Okoye",
      patientName: "Jonah Okoye",
      age: 30,
      patientSex: "male",
      voiceKey: "adult_male",
      demeanor: "A construction worker, normally stoic, now clearly sick: short sentences, sweaty, shivering, in a lot of pain. Wants it fixed.",
      opener: "Doc, I'm in bad shape. My whole stomach is on fire.",
    },
    history: {
      chief_complaint: "My whole stomach hurts. It's bad.",
      onset: "Started two days ago, Wednesday morning.",
      location: "It was on the right side low. Now it's everywhere.",
      migration: "First it was around my belly button, then it moved down to the right. Yesterday afternoon it actually got better for an hour or so, then it came back worse and spread everywhere.",
      character: "Constant. Like my whole gut is burning.",
      severity: "Nine. I don't go to doctors, so you know it's bad.",
      aggravating_relieving: "Any movement. Even breathing deep hurts. Nothing makes it better.",
      nausea_vomiting: "Threw up maybe four or five times since yesterday.",
      appetite: "Haven't eaten anything since yesterday morning.",
      fever: "Chills and sweats all night. I'm shaking.",
      bowel: "Haven't gone since Wednesday. No diarrhea.",
      urinary: "Peeing less than normal. No burning.",
      last_meal: "A breakfast sandwich yesterday morning, around seven. A little water about two hours ago.",
      past_medical: "Nothing. I'm never sick.",
      past_surgical: "Never had surgery.",
      medications: "I don't take anything. Some ibuprofen yesterday, didn't help.",
      allergies: "Penicillin. I broke out in hives when I was a kid.",
      social: "I work construction. I smoke about half a pack a day, and I drink on weekends.",
      family: "My dad has high blood pressure. That's all I know.",
      recent_illness: "No, I was fine before this.",
    },
    exam: {
      general_appearance: { reaction: "", finding: "Ill appearing, diaphoretic, lying completely still, shivering." },
      vitals: { reaction: "", finding: "Temperature 38.9 C, heart rate 118, blood pressure 102/64, respiratory rate 22, oxygen saturation 97% on room air." },
      abdomen_inspection: { reaction: "", finding: "Mildly distended, not moving with respiration." },
      abdomen_palpation: { reaction: "Agh! Everywhere hurts!", finding: "Diffuse tenderness, worst in the right lower quadrant." },
      mcburney_point: { reaction: "That's the worst spot!", finding: "Maximal tenderness in the right lower quadrant." },
      rebound: { reaction: "Don't do that again!", finding: "Diffuse rebound tenderness." },
      guarding_rigidity: { reaction: "I can't relax it, man.", finding: "Involuntary guarding and board-like rigidity. Generalized peritonitis." },
      rovsing: { reaction: "Everything hurts when you do that.", finding: "Positive Rovsing sign." },
      psoas: { reaction: "No, I can't lift my leg.", finding: "Positive right psoas sign." },
      obturator: { reaction: "Ah, that hurts too.", finding: "Positive obturator sign." },
      murphy: { reaction: "It's all bad.", finding: "Diffuse tenderness, not localized to the right upper quadrant." },
      cva_tenderness: { reaction: "My back's okay.", finding: "No costovertebral angle tenderness." },
      chest_lungs: { reaction: "", finding: "Clear, shallow breaths because of pain." },
      genitourinary: { reaction: "No, that's fine.", finding: "Normal testicular exam." },
    },
    tests: {
      cbc: { result: "White count 18.6 with 12% bands. Hemoglobin 15.1. Platelets 410.", abnormal: true },
      crp: { result: "CRP 180 mg/L (markedly elevated).", abnormal: true },
      bmp: { result: "Sodium 134, potassium 3.6, creatinine 1.3 (mildly elevated, likely dehydration), glucose 128.", abnormal: true },
      lactate: { result: "Lactate 2.8 (elevated).", abnormal: true },
      lipase: { result: "Lipase normal.", abnormal: false },
      urinalysis: { result: "Concentrated, specific gravity 1.030. No blood, no infection.", abnormal: true },
      ultrasound: { result: "Limited by bowel gas and guarding. Free fluid in the right lower quadrant.", abnormal: true },
      ct_abdomen_pelvis: { result: "Perforated appendicitis with periappendiceal phlegmon and free fluid. No drainable abscess. No free air under the diaphragm.", abnormal: true },
      type_and_screen: { result: "Type B positive, antibody screen negative.", abnormal: false },
    },
    diagnosis: {
      label: "Perforated appendicitis with peritonitis and sepsis",
      keywords: [["appendicitis", "appendix"], ["perforat", "ruptur", "burst"]],
      partial: { label: "Appendicitis (perforation not named)", keywords: [["appendicitis", "appendix"]] },
    },
    procedureKeywords: [["appendectomy", "appendicectomy", "remove the appendix", "take out the appendix", "take the appendix out", "washout"]],
    urgency: "emergency",
    differential: [
      ddx("ulcer", "Perforated peptic ulcer", "ulcer", "peptic"),
      ddx("diverticulitis", "Diverticulitis", "diverticul"),
      ddx("crohns", "Crohn's disease with perforation or abscess", "crohn", "ileitis", "ibd"),
      ddx("obstruction", "Small bowel obstruction", "obstruction"),
      ddx("stone", "Ureteric stone with infection", "stone", "kidney", "ureter"),
      ddx("ischemia", "Mesenteric ischemia", "ischemi"),
    ],
    critical: [
      { kind: "history", id: "allergies", why: "His chart is empty, but he is allergic to penicillin: that changes the antibiotic for an infected abdomen. Only asking him found it." },
      { kind: "history", id: "medications", why: "With no chart, medications have to come from the patient." },
      { kind: "exam", id: "vitals", why: "Fever, tachycardia, and soft blood pressure mean sepsis: fluids and antibiotics before the operating room." },
      { kind: "history", id: "last_meal", why: "Last oral intake sets anesthesia timing and aspiration risk." },
    ],
    expected: [
      ...COMMON_APPY_EXPECTED,
      { kind: "exam", id: "guarding_rigidity", why: "Rigidity means generalized peritonitis." },
      { kind: "test", id: "lactate", why: "Lactate grades sepsis severity." },
      { kind: "test", id: "ct_abdomen_pelvis", why: "CT defines perforation, phlegmon, or a drainable abscess." },
      { kind: "test", id: "bmp", why: "Vomiting and sepsis threaten the kidneys and electrolytes." },
    ],
  },
];

export const ENCOUNTERS_BY_PLAN = new Map(ENCOUNTERS.map((e) => [e.planSubject, e]));
