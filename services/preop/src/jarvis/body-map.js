// Image-plane body map for the camera test rig. Pure geometry, unit tested in test/body-map.test.ts.
//
// The torso frame comes from four pose landmarks (shoulders and hips). A fingertip is mapped into
// normalized torso coordinates: u runs across the body from the patient's RIGHT side (0) to their LEFT
// (1); v runs from the shoulder line (0) to the hip line (1). The landmark identities, not the image
// direction, define left and right, so it works whether the camera looks down at a reclining patient or
// faces a seated person. This is a 2D surface approximation with no depth: good enough to tell the coach
// which region a hand is over, not a measurement of anatomy.

// Approximate surface projections of the catalog structures, smallest first so they win overlaps.
export const REGIONS = [
  { id: "appendicular_artery", u: [0.29, 0.33], v: [0.9, 0.94] },
  // Cholecystectomy: the cystic artery and duct run from the gallbladder neck toward the midline.
  { id: "cystic_artery", u: [0.34, 0.38], v: [0.44, 0.47] },
  { id: "cystic_duct", u: [0.34, 0.38], v: [0.47, 0.5] },
  // Sigmoid colectomy: vessels and ureter lie deep to the sigmoid; this 2D map gives each a strip.
  { id: "inferior_mesenteric_artery", u: [0.52, 0.56], v: [0.7, 0.78] },
  { id: "left_ureter", u: [0.6, 0.64], v: [0.74, 0.92] },
  { id: "sigmoid_mesocolon", u: [0.68, 0.78], v: [0.86, 0.91] },
  { id: "rectum", u: [0.58, 0.66], v: [0.97, 1.06] },
  { id: "gallbladder", u: [0.25, 0.37], v: [0.42, 0.5] },
  { id: "mesoappendix", u: [0.27, 0.35], v: [0.87, 0.96] },
  { id: "appendix", u: [0.19, 0.29], v: [0.9, 0.99] },
  { id: "umbilicus", u: [0.45, 0.55], v: [0.62, 0.7] },
  { id: "terminal_ileum", u: [0.3, 0.42], v: [0.79, 0.87] },
  { id: "cecum", u: [0.12, 0.3], v: [0.79, 0.9] },
  { id: "duodenum", u: [0.37, 0.5], v: [0.46, 0.55] },
  { id: "urinary_bladder", u: [0.4, 0.6], v: [0.95, 1.08] },
  { id: "sigmoid_colon", u: [0.64, 0.86], v: [0.82, 0.98] },
  { id: "stomach", u: [0.55, 0.85], v: [0.33, 0.5] },
  { id: "descending_colon", u: [0.82, 0.96], v: [0.55, 0.85] },
  { id: "transverse_colon", u: [0.15, 0.85], v: [0.51, 0.6] },
  { id: "liver", u: [0.02, 0.55], v: [0.3, 0.46] },
  { id: "small_bowel", u: [0.3, 0.7], v: [0.6, 0.86] },
];

export const ABDOMEN = { u: [-0.05, 1.05], v: [0.28, 1.1] };

// Torso-frame meters (x toward the patient's left, z toward the head, origin at the umbilicus) to (u, v).
const HALF_WIDTH_M = 0.17; // umbilicus to flank
const SHOULDER_TO_HIP_M = 0.45;
const UMBILICUS_V = 0.66;
export function portToUV(position) {
  return { u: 0.5 + position.x / (2 * HALF_WIDTH_M), v: UMBILICUS_V - position.z / SHOULDER_TO_HIP_M };
}

const lerp2 = (a, b, t) => ({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t });

// Point on the torso quad for (u, v). Corners: rightShoulder, leftShoulder, leftHip, rightHip.
export function uvToImage(torso, u, v) {
  const top = lerp2(torso.rightShoulder, torso.leftShoulder, u);
  const bottom = lerp2(torso.rightHip, torso.leftHip, u);
  return lerp2(top, bottom, v);
}

// Inverse bilinear map by Newton iteration; returns null when the quad is degenerate.
export function imageToUV(torso, p) {
  let u = 0.5;
  let v = 0.5;
  for (let i = 0; i < 20; i++) {
    const q = uvToImage(torso, u, v);
    const ex = q.x - p.x;
    const ey = q.y - p.y;
    if (Math.abs(ex) < 1e-7 && Math.abs(ey) < 1e-7) break;
    const h = 1e-4;
    const qu = uvToImage(torso, u + h, v);
    const qv = uvToImage(torso, u, v + h);
    const j11 = (qu.x - q.x) / h, j12 = (qv.x - q.x) / h;
    const j21 = (qu.y - q.y) / h, j22 = (qv.y - q.y) / h;
    const det = j11 * j22 - j12 * j21;
    if (Math.abs(det) < 1e-9) return null;
    u -= (j22 * ex - j12 * ey) / det;
    v -= (-j21 * ex + j11 * ey) / det;
  }
  return { u, v };
}

export function regionAt(uv, allowed) {
  if (!uv) return "";
  if (uv.u < ABDOMEN.u[0] || uv.u > ABDOMEN.u[1] || uv.v < ABDOMEN.v[0] || uv.v > ABDOMEN.v[1]) return "";
  for (const r of REGIONS) {
    if (allowed && !allowed.has(r.id)) continue;
    if (uv.u >= r.u[0] && uv.u <= r.u[1] && uv.v >= r.v[0] && uv.v <= r.v[1]) return r.id;
  }
  return "";
}

export function portAt(uv, ports, radius = 0.07) {
  if (!uv) return "";
  let best = "";
  let bestD = radius;
  for (const p of ports) {
    const c = portToUV(p.position);
    const d = Math.hypot(c.u - uv.u, c.v - uv.v);
    if (d < bestD) {
      bestD = d;
      best = p.id;
    }
  }
  return best;
}

// Pinch from hand landmarks (thumb tip 4, index tip 8, wrist 0, middle MCP 9), scale-invariant,
// with hysteresis so a held pinch does not flicker.
export function pinchState(hand, wasPinched) {
  const d = (a, b) => Math.hypot(hand[a].x - hand[b].x, hand[a].y - hand[b].y);
  const ratio = d(4, 8) / Math.max(d(0, 9), 1e-6);
  return wasPinched ? ratio < 0.5 : ratio < 0.3;
}

// Pose landmarks 11/12 (shoulders) and 23/24 (hips). Left/right are the person's own sides.
export function torsoFromPose(pose, minVisibility = 0.5) {
  const pick = (i) => pose?.[i];
  const ls = pick(11), rs = pick(12), lh = pick(23), rh = pick(24);
  if (![ls, rs, lh, rh].every((p) => p && (p.visibility ?? 1) >= minVisibility)) return null;
  return { leftShoulder: ls, rightShoulder: rs, leftHip: lh, rightHip: rh };
}
