import type { AnatomyStructure } from "../types.js";

// Every structure a procedure or flag can reference. Unity matches imported meshes by unityName,
// so the Blender export must name each separately addressable object "anat_<id>".
const structure = (id: string, displayName: string, system: string, region: string): AnatomyStructure => ({
  id,
  unityName: `anat_${id}`,
  displayName,
  system,
  region,
});

export const ANATOMY: AnatomyStructure[] = [
  structure("abdominal_wall", "Anterior abdominal wall", "musculoskeletal", "abdominal_wall"),
  structure("umbilicus", "Umbilicus", "integumentary", "abdominal_wall"),
  structure("greater_omentum", "Greater omentum", "digestive", "peritoneal"),
  structure("liver", "Liver", "digestive", "upper_abdomen"),
  structure("gallbladder", "Gallbladder", "digestive", "upper_abdomen"),
  structure("cystic_duct", "Cystic duct", "digestive", "upper_abdomen"),
  structure("cystic_artery", "Cystic artery", "cardiovascular", "upper_abdomen"),
  structure("common_hepatic_duct", "Common hepatic duct", "digestive", "upper_abdomen"),
  structure("common_bile_duct", "Common bile duct", "digestive", "upper_abdomen"),
  structure("right_hepatic_artery", "Right hepatic artery", "cardiovascular", "upper_abdomen"),
  structure("stomach", "Stomach", "digestive", "upper_abdomen"),
  structure("duodenum", "Duodenum", "digestive", "upper_abdomen"),
  structure("pancreas", "Pancreas", "digestive", "upper_abdomen"),
  structure("transverse_colon", "Transverse colon", "digestive", "mid_abdomen"),
  structure("small_bowel", "Small bowel", "digestive", "mid_abdomen"),
  structure("terminal_ileum", "Terminal ileum", "digestive", "right_lower_quadrant"),
  structure("cecum", "Cecum", "digestive", "right_lower_quadrant"),
  structure("appendix", "Vermiform appendix", "digestive", "right_lower_quadrant"),
  structure("mesoappendix", "Mesoappendix", "digestive", "right_lower_quadrant"),
  structure("appendicular_artery", "Appendicular artery", "cardiovascular", "right_lower_quadrant"),
  structure("right_ureter", "Right ureter", "urinary", "retroperitoneum"),
  structure("descending_colon", "Descending colon", "digestive", "left_abdomen"),
  structure("sigmoid_colon", "Sigmoid colon", "digestive", "left_lower_quadrant"),
  structure("sigmoid_mesocolon", "Sigmoid mesocolon", "digestive", "left_lower_quadrant"),
  structure("inferior_mesenteric_artery", "Inferior mesenteric artery", "cardiovascular", "retroperitoneum"),
  structure("left_ureter", "Left ureter", "urinary", "retroperitoneum"),
  structure("left_gonadal_vessels", "Left gonadal vessels", "cardiovascular", "retroperitoneum"),
  structure("rectum", "Rectum", "digestive", "pelvis"),
  structure("urinary_bladder", "Urinary bladder", "urinary", "pelvis"),
  structure("right_kidney", "Right kidney", "urinary", "retroperitoneum"),
  structure("left_kidney", "Left kidney", "urinary", "retroperitoneum"),
  structure("heart", "Heart", "cardiovascular", "thorax"),
  structure("lungs", "Lungs", "respiratory", "thorax"),
];

export const ANATOMY_BY_ID = new Map(ANATOMY.map((s) => [s.id, s]));
