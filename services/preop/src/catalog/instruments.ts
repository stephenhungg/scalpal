import type { Instrument } from "../types.js";

// Unity spawns instruments from prefabs named "inst_<id>".
const instrument = (id: string, displayName: string, kind: string): Instrument => ({
  id,
  unityPrefab: `inst_${id}`,
  displayName,
  kind,
});

export const INSTRUMENTS: Instrument[] = [
  instrument("scalpel", "Scalpel", "cutting"),
  instrument("trocar_5mm", "5 mm trocar", "access"),
  instrument("trocar_12mm", "12 mm trocar", "access"),
  instrument("laparoscope_30", "30 degree laparoscope", "visualization"),
  instrument("atraumatic_grasper", "Atraumatic grasper", "grasper"),
  instrument("maryland_dissector", "Maryland dissector", "dissector"),
  instrument("hook_cautery", "Monopolar hook", "energy"),
  instrument("vessel_sealer", "Bipolar vessel sealer", "energy"),
  instrument("clip_applier", "Clip applier", "ligation"),
  instrument("lap_scissors", "Laparoscopic scissors", "cutting"),
  instrument("endo_stapler", "Endoscopic linear stapler", "stapler"),
  instrument("circular_stapler", "Circular stapler", "stapler"),
  instrument("suction_irrigator", "Suction irrigator", "irrigation"),
  instrument("retrieval_bag", "Specimen retrieval bag", "retrieval"),
  instrument("fascial_closure", "Fascial closure device", "closure"),
];

export const INSTRUMENTS_BY_ID = new Map(INSTRUMENTS.map((i) => [i.id, i]));
