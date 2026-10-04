// Coaching knowledge for Jarvis, keyed by the same anatomy, procedure, and step ids as the render catalogs.
// This stays server-side (it feeds the voice agent's prompt and live hints) so Unity DTOs are unchanged.
// Facts follow standard anatomy and laparoscopic teaching references. Illustrative, not clinical guidance.

export interface StructureFacts {
  what: string; // one-line identity
  where: string; // position and landmarks a learner can find in the view
  supply: string; // blood supply or drainage, "" when not useful for teaching
  why: string; // surgical significance
}

export const STRUCTURE_FACTS: Record<string, StructureFacts> = {
  abdominal_wall: {
    what: "Layers from outside in. In the midline: skin, fat, linea alba or rectus sheath and rectus muscle, transversalis fascia, preperitoneal fat, peritoneum. At McBurney's point, lateral to the rectus: skin, fat, external oblique aponeurosis, internal oblique, transversus abdominis, transversalis fascia, peritoneum.",
    where: "The front of the abdomen between the ribs and the pelvis. McBurney's point is one third of the way from the right anterior superior iliac spine to the umbilicus.",
    supply: "Superior and inferior epigastric arteries behind the rectus; the deep circumflex iliac and lower intercostal vessels laterally.",
    why: "In an open appendectomy the oblique and transversus muscles are split along their fibers, not cut, which keeps their nerves and strength. Trocars through the rectus can tear the inferior epigastric vessels.",
  },
  umbilicus: {
    what: "Scar of the umbilical cord, where all fascial layers fuse into the thinnest point of the abdominal wall.",
    where: "Midline, roughly at the L3 to L4 level.",
    supply: "",
    why: "The aorta divides into the iliac arteries close beneath it in thin patients, so an open (Hasson) entry is used to avoid a blind vascular injury.",
  },
  greater_omentum: {
    what: "Fatty apron hanging from the greater curvature of the stomach over the transverse colon and small bowel.",
    where: "First thing you see on entry, draped over the lower abdomen.",
    supply: "Right and left gastroepiploic arteries.",
    why: "It migrates to inflammation and is often stuck to an inflamed gallbladder or appendix. Sweep it away bluntly.",
  },
  liver: {
    what: "Largest solid organ, divided into eight functional segments.",
    where: "Right upper quadrant under the diaphragm. The gallbladder sits in its bed on segments IVb and V.",
    supply: "Dual supply: portal vein (about 75 percent) and hepatic artery (about 25 percent).",
    why: "Rouviere's sulcus on the underside of the right lobe marks the level of the common bile duct: dissect above it. Going too deep in the gallbladder bed bleeds from the liver.",
  },
  gallbladder: {
    what: "Pear-shaped bile reservoir: fundus, body, infundibulum (Hartmann's pouch), and neck.",
    where: "Under the right lobe of the liver, fundus peeking below the liver edge.",
    supply: "Cystic artery. Small veins drain straight into the liver bed.",
    why: "Fundus up and infundibulum lateral is the traction that opens the hepatocystic triangle.",
  },
  cystic_duct: {
    what: "Duct draining the gallbladder neck into the common hepatic duct, forming the common bile duct. Usually 2 to 4 cm, with spiral valves of Heister.",
    where: "Leaves the gallbladder neck heading medially and down toward the common duct, lower than the cystic artery.",
    supply: "",
    why: "Its course varies: it can run parallel to the common hepatic duct or join low. Never clip until the critical view of safety is established.",
  },
  cystic_artery: {
    what: "Artery of the gallbladder.",
    where: "Usually arises from the right hepatic artery inside the hepatocystic triangle and runs to the gallbladder neck, above and slightly behind the cystic duct, often near Calot's lymph node.",
    supply: "",
    why: "It can be double or arise from an unusual source. A large tortuous vessel near the neck may be the right hepatic artery itself.",
  },
  common_hepatic_duct: {
    what: "Duct formed by the right and left hepatic ducts at the liver hilum.",
    where: "Medial border of the hepatocystic triangle, running down to meet the cystic duct.",
    supply: "",
    why: "Clipping or cutting it is a major bile duct injury. It is not one of the two structures that should enter the gallbladder.",
  },
  common_bile_duct: {
    what: "Duct formed where the cystic duct joins the common hepatic duct, carrying bile to the duodenum.",
    where: "Free edge of the hepatoduodenal ligament, to the right of the hepatic artery and in front of the portal vein, passing behind the duodenum into the pancreatic head.",
    supply: "",
    why: "The classic bile duct injury is mistaking it for the cystic duct, often after medial traction lines the two up. Stay on the gallbladder side.",
  },
  right_hepatic_artery: {
    what: "Branch of the proper hepatic artery supplying the right liver.",
    where: "Usually crosses behind the common hepatic duct into the hepatocystic triangle. A replaced right hepatic artery from the superior mesenteric artery runs behind and to the right of the common bile duct.",
    supply: "",
    why: "It can loop close to the gallbladder neck (Moynihan's hump) and be mistaken for the cystic artery, then clipped or burned.",
  },
  stomach: {
    what: "Muscular foregut organ between the esophagus and duodenum.",
    where: "Left upper quadrant and epigastrium, beneath the left liver.",
    supply: "Left and right gastric, gastroepiploic, and short gastric arteries.",
    why: "A distended stomach blocks the upper abdominal view. A gastric tube decompresses it.",
  },
  duodenum: {
    what: "First part of the small bowel, a C-shaped loop around the pancreatic head.",
    where: "Just below and medial to the gallbladder neck.",
    supply: "Gastroduodenal and pancreaticoduodenal arteries.",
    why: "An inflamed gallbladder can stick to it. A hook cautery touch can cause an unseen burn that perforates days later.",
  },
  pancreas: {
    what: "Retroperitoneal gland. The common bile duct runs through its head.",
    where: "Behind the stomach, head inside the duodenal C-loop.",
    supply: "Pancreaticoduodenal arcades and splenic artery branches.",
    why: "Stones passing down the common bile duct can cause gallstone pancreatitis.",
  },
  transverse_colon: {
    what: "Intraperitoneal colon hanging on the transverse mesocolon.",
    where: "Below the liver and stomach, under the omentum. The hepatic flexure sits near the gallbladder.",
    supply: "Middle colic artery.",
    why: "The hepatic flexure may adhere to an inflamed gallbladder. Retract it downward, never burn near it.",
  },
  small_bowel: {
    what: "Jejunum and ileum, mobile on their mesentery.",
    where: "Fills the central and lower abdomen.",
    supply: "Superior mesenteric artery branches.",
    why: "Use table tilt and gravity to move it. Graspers on bowel cause tears and burns that are easy to miss.",
  },
  terminal_ileum: {
    what: "Last segment of the ileum, entering the cecum at the ileocecal valve.",
    where: "Right lower quadrant, joining the medial side of the cecum.",
    supply: "Ileocolic artery.",
    why: "If the appendix looks normal, run it to look for Meckel's diverticulum or Crohn's disease. Thermal injury here causes a delayed leak.",
  },
  cecum: {
    what: "Blind pouch at the start of the colon, below the ileocecal valve.",
    where: "Right lower quadrant.",
    supply: "Anterior and posterior cecal branches of the ileocolic artery.",
    why: "Its three taeniae coli converge on the appendix base, which is the reliable way to find the appendix.",
  },
  appendix: {
    what: "Narrow blind-ended tube arising from the posteromedial cecum about 2 cm below the ileocecal valve.",
    where: "Base fixed where the taeniae meet. The tip varies: retrocecal is most common, then pelvic, subcecal, and around the ileum.",
    supply: "Appendicular artery.",
    why: "Grasp the mesoappendix, not the inflamed appendix, which can rupture. Divide it flush at the base to avoid stump appendicitis.",
  },
  mesoappendix: {
    what: "Triangular fold of mesentery attaching the appendix to the terminal ileum mesentery.",
    where: "Hangs from the appendix toward the ileum.",
    supply: "Carries the appendicular artery along its free edge.",
    why: "Dividing it controls the blood supply. Window it right at the appendix base.",
  },
  appendicular_artery: {
    what: "End artery supplying the appendix.",
    where: "Branch of the ileocolic artery that passes behind the terminal ileum into the free edge of the mesoappendix.",
    supply: "",
    why: "As an end artery, its thrombosis in appendicitis causes gangrene and perforation. Seal it securely: it retracts and bleeds if missed.",
  },
  right_ureter: {
    what: "Muscular tube carrying urine from the right kidney to the bladder.",
    where: "Retroperitoneal, crossing the iliac artery at the pelvic brim, medial to the cecum.",
    supply: "",
    why: "It worms (peristalses) when touched. At risk during dissection of an inflamed retrocecal appendix.",
  },
  descending_colon: {
    what: "Retroperitoneal left colon.",
    where: "Left flank, fixed laterally along the white line of Toldt.",
    supply: "Left colic artery and the marginal artery.",
    why: "It becomes the proximal limb of the anastomosis and must reach the rectum without tension.",
  },
  sigmoid_colon: {
    what: "Mobile S-shaped colon from the pelvic brim to the rectum.",
    where: "Left lower quadrant dipping into the pelvis.",
    supply: "Sigmoid branches of the inferior mesenteric artery.",
    why: "The usual site of diverticulitis. The whole diseased segment comes out to reduce recurrence.",
  },
  sigmoid_mesocolon: {
    what: "Mesentery of the sigmoid colon, with an inverted V-shaped root.",
    where: "Fanning from the sigmoid toward the sacral promontory.",
    supply: "Carries the inferior mesenteric and superior rectal vessels.",
    why: "The left ureter lies under its root near the intersigmoid recess. Lifting it shows the plane for medial-to-lateral dissection.",
  },
  inferior_mesenteric_artery: {
    what: "Aortic branch supplying the left colon, sigmoid, and upper rectum.",
    where: "Arises from the front of the aorta around L3, 3 to 4 cm above the bifurcation, then gives the left colic and sigmoid branches and continues as the superior rectal artery.",
    supply: "",
    why: "Sympathetic hypogastric nerves run near its origin. Confirm the ureter is safe before sealing.",
  },
  left_ureter: {
    what: "Muscular tube carrying urine from the left kidney to the bladder.",
    where: "Retroperitoneal, crossing the left common iliac artery near its bifurcation, beneath the sigmoid mesocolon and medial to the gonadal vessels.",
    supply: "",
    why: "The structure most at risk in sigmoid colectomy. Positively identify it before dividing any vessel: it peristalses when touched.",
  },
  left_gonadal_vessels: {
    what: "Left ovarian or testicular artery and vein.",
    where: "Run parallel and lateral to the left ureter. The vein drains to the left renal vein.",
    supply: "",
    why: "Commonly mistaken for the ureter. They are thin-walled and do not peristalse.",
  },
  rectum: {
    what: "Final segment of large bowel, beginning around S3 where the taeniae splay into a continuous muscle layer.",
    where: "Pelvis, following the curve of the sacrum.",
    supply: "Superior rectal (from the inferior mesenteric), middle rectal, and inferior rectal arteries.",
    why: "Transect on soft rectum below the splayed taeniae so no sigmoid is left behind.",
  },
  urinary_bladder: {
    what: "Hollow muscular urine reservoir.",
    where: "Anterior pelvis behind the pubic bone. A full bladder rises into the lower abdomen.",
    supply: "Superior and inferior vesical arteries.",
    why: "Empty it before surgery. A suprapubic trocar can perforate a full bladder.",
  },
  right_kidney: {
    what: "Retroperitoneal organ filtering blood into urine.",
    where: "Behind the liver and hepatic flexure.",
    supply: "Right renal artery.",
    why: "Pneumoperitoneum lowers renal blood flow, which matters with reduced kidney function.",
  },
  left_kidney: {
    what: "Retroperitoneal organ filtering blood into urine.",
    where: "Behind the spleen and descending colon.",
    supply: "Left renal artery.",
    why: "Pneumoperitoneum lowers renal blood flow, which matters with reduced kidney function.",
  },
  heart: {
    what: "Four-chambered pump.",
    where: "Middle of the chest.",
    supply: "Coronary arteries.",
    why: "Insufflation raises afterload and cuts venous return. Head-up tilt drops preload further in heart disease.",
  },
  lungs: {
    what: "Paired organs of gas exchange.",
    where: "Either side of the chest above the diaphragm.",
    supply: "",
    why: "Pneumoperitoneum pushes the diaphragm up and raises airway pressure. Absorbed CO2 raises blood CO2. Asthma adds bronchospasm risk.",
  },
};

export interface StepCoaching {
  why: string; // the reason behind the step, for the first-tier nudge
  lookHere: string; // where to look in the field, for the second tier
}

// procedureId -> stepId -> coaching. Step ids repeat across procedures, so they stay nested.
export const STEP_COACHING: Record<string, Record<string, StepCoaching>> = {
  open_appendectomy: {
    mark_incision: { why: "Find hip bone and belly button; mark a third across.", lookHere: "Follow the registered landmark line, five to eight centimeters." },
    incise_skin: { why: "One smooth stroke along your line. Skin only.", lookHere: "Follow your mark and watch the blade depth." },
    open_fascia: { why: "Open the aponeurosis along its fibers.", lookHere: "Follow the fascia fibers; avoid cutting across them." },
    split_muscle: { why: "Now split the muscle. Pull, don't cut.", lookHere: "Use both retractors; gently separate along the muscle fibers." },
    open_peritoneum: { why: "Lift the peritoneum first, then nick it.", lookHere: "Tent with forceps before the blade enters." },
    deliver_appendix: { why: "Follow the taenia; lift the appendix out gently.", lookHere: "Use the Babcock to deliver it above the wound." },
    divide_mesoappendix: { why: "Clamp twice, cut between, then tie.", lookHere: "Secure both sides and check for active bleeding." },
    ligate_base: { why: "Where is the true base? Identify it before tying.", lookHere: "Crush, tie within five millimeters, cut above your tie." },
    inspect_clean: { why: "Dry field? Check the stump and the vessels.", lookHere: "Suction the blood, then inspect both secured sites." },
    close: { why: "Close in layers. Nice work.", lookHere: "Confirm the field is dry before closing." },
  },
  lap_cholecystectomy: {
    access_umbilical: {
      why: "The camera has to go in first so every later port goes in under direct vision.",
      lookHere: "Look at the umbilicus, the center of the abdomen. That is the camera port site.",
    },
    working_ports: {
      why: "Ports spread around the right upper quadrant let the instruments triangulate on the gallbladder instead of clashing.",
      lookHere: "Look high in the midline below the breastbone for the epigastric port, then under the right ribs for the two 5 mm ports.",
    },
    retract_fundus: {
      why: "Pushing the fundus up over the liver lifts the liver edge and exposes the gallbladder neck.",
      lookHere: "Look under the right liver edge for the rounded tip of the gallbladder, the fundus.",
    },
    retract_infundibulum: {
      why: "Lateral and downward traction on the infundibulum opens the hepatocystic triangle and separates the cystic duct from the common bile duct.",
      lookHere: "Follow the gallbladder down from the fundus to its narrow lower part, the infundibulum, and pull it toward the patient's right side.",
    },
    dissect_triangle: {
      why: "Clearing fat and fibrous tissue from the triangle is the only way to see what really enters the gallbladder.",
      lookHere: "Work at the gallbladder neck, on the gallbladder side, front and back. Stay above Rouviere's sulcus and away from the common bile duct below.",
    },
    critical_view: {
      why: "The critical view of safety prevents bile duct injury: exactly two structures should enter the gallbladder.",
      lookHere: "Look at the two tubes going into the gallbladder neck. The upper thinner one is the cystic artery, the lower one is the cystic duct.",
    },
    clip_artery: {
      why: "Clipping before cutting keeps the artery from bleeding when it is divided.",
      lookHere: "Find the cystic artery at the gallbladder neck, above the duct. Two clips go on the patient side, one on the gallbladder side.",
    },
    clip_duct: {
      why: "Secure clips keep bile from leaking out of the stump.",
      lookHere: "Find the cystic duct where it leaves the gallbladder neck. Clip close to the gallbladder, far from the common bile duct.",
    },
    divide_structures: {
      why: "Cutting between the clips frees the gallbladder from its duct and artery.",
      lookHere: "Look for the gap between the clips on the cystic artery and on the cystic duct. Cut there.",
    },
    liver_bed: {
      why: "The gallbladder has to come off the cystic plate in the right plane: too deep bleeds, too shallow perforates.",
      lookHere: "Look at the line where the gallbladder wall meets the liver and stay right against the gallbladder.",
    },
    hemostasis: {
      why: "Bleeding or a bile leak missed now shows up after surgery.",
      lookHere: "Look at the empty gallbladder bed on the liver and the clip stumps.",
    },
    extract: {
      why: "A bag stops stones and bile from spilling into the abdomen or wound.",
      lookHere: "Look at the free gallbladder and the umbilical port it will come out through.",
    },
    close: {
      why: "Closing the fascia at 12 mm sites prevents port-site hernias.",
      lookHere: "Look at the umbilical and epigastric port sites.",
    },
  },
  lap_appendectomy: {
    access_umbilical: {
      why: "The camera goes in first so the working ports are placed under vision.",
      lookHere: "Look at the umbilicus in the center of the abdomen.",
    },
    working_ports: {
      why: "Ports on the left side and above the pubis point the instruments toward the right lower quadrant.",
      lookHere: "Look at the left lower abdomen and just above the pubic bone for the two 5 mm port sites.",
    },
    find_appendix: {
      why: "The appendix tip moves around, but its base never does: all three taeniae of the cecum converge on it.",
      lookHere: "Look in the right lower quadrant for the cecum, then follow its longitudinal bands down to where they meet. The appendix starts there.",
    },
    inspect: {
      why: "Perforation, abscess, or a mass changes the operation. If the appendix looks normal, the terminal ileum may be the real problem.",
      lookHere: "Look along the appendix to its tip, then at the terminal ileum entering the cecum.",
    },
    mesoappendix_window: {
      why: "A window at the base separates the blood supply from the appendix so each can be divided cleanly.",
      lookHere: "Look at the thin fold hanging from the appendix toward the ileum, right next to the appendix base.",
    },
    divide_mesoappendix: {
      why: "Sealing the mesoappendix controls the appendicular artery before the appendix is cut.",
      lookHere: "Look at the free edge of the mesoappendix. The appendicular artery runs along it.",
    },
    staple_base: {
      why: "Dividing flush with the cecum leaves no stump to get inflamed later.",
      lookHere: "Look at the junction of the appendix and the cecum, where the taeniae meet.",
    },
    extract: {
      why: "A bag keeps an infected appendix from touching the wound.",
      lookHere: "Look at the free appendix and the umbilical port.",
    },
    irrigate: {
      why: "Leftover pus or fluid becomes an abscess.",
      lookHere: "Look at the right lower quadrant around the cecum and down into the pelvis.",
    },
    close: {
      why: "Closing the umbilical fascia prevents a hernia.",
      lookHere: "Look at the umbilical port site.",
    },
  },
  lap_sigmoid_colectomy: {
    access_umbilical: {
      why: "The camera goes in first so the other ports are placed under vision.",
      lookHere: "Look at the umbilicus in the center of the abdomen.",
    },
    working_ports: {
      why: "Right-sided ports let the surgeon work across toward the left pelvis.",
      lookHere: "Look at the right lower and right upper abdomen, then the left lower abdomen.",
    },
    expose_pelvis: {
      why: "Gravity with head-down, right-side-down tilt moves the bowel out of the way so the sigmoid mesentery can be seen.",
      lookHere: "Look at the loops of small bowel in the pelvis and lift them up and to the right.",
    },
    score_mesentery: {
      why: "Opening the peritoneum under the mesentery starts the medial-to-lateral plane.",
      lookHere: "Lift the sigmoid mesentery toward the ceiling and look at its base over the sacral promontory, beneath the inferior mesenteric artery.",
    },
    identify_ureter: {
      why: "The left ureter must be seen and protected before any vessel is divided.",
      lookHere: "Look deep under the lifted mesentery where the iliac artery divides. The ureter lies medial to the gonadal vessels and worms when touched.",
    },
    divide_ima: {
      why: "Dividing the inferior mesenteric artery frees the sigmoid's blood supply for resection.",
      lookHere: "Look at the thick vessel running up the mesentery toward the aorta, with the ureter already seen and safe below it.",
    },
    mobilize_lateral: {
      why: "Releasing the lateral attachments lets the colon reach the pelvis without tension.",
      lookHere: "Look at the outer edge of the sigmoid and descending colon for the pale white line of Toldt.",
    },
    transect_rectum: {
      why: "Cutting on true rectum below the diseased sigmoid lowers the chance of diverticulitis coming back.",
      lookHere: "Look at the upper rectum below the sigmoid, where the bands on the colon spread out.",
    },
    extract: {
      why: "The specimen comes out through a protected incision, and the anvil goes into the remaining colon.",
      lookHere: "Look at the freed sigmoid colon.",
    },
    anastomosis: {
      why: "A tension-free, untwisted join with good blood supply is what keeps it from leaking.",
      lookHere: "Look at the descending colon end with the anvil and the stapled top of the rectum.",
    },
    leak_test: {
      why: "Air bubbles under saline reveal a leak while it can still be fixed.",
      lookHere: "Look at the anastomosis under the saline in the pelvis.",
    },
    close: {
      why: "Closing the extraction site and 12 mm fascia prevents hernias.",
      lookHere: "Look at the umbilical and right lower quadrant port sites.",
    },
  },
};
