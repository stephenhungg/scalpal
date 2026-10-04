// Shared between the landing and explore pages so the hands look continuous across the switch.
export const SHIFT_Y = 0.22; // hands scene offset down, as a fraction of screen height
export const DIM_LANDING = 0.55; // black over the hands once the landing has launched
export const DIM_EXPLORE = 0.8; // black over the hands on the explore page
export const EXIT_MS = 450; // content fade-out before navigating
export const EXIT = { duration: EXIT_MS / 1000, ease: [0.44, 0, 0.56, 1] as const };
