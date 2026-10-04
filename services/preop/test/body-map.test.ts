import { describe, expect, it } from "vitest";
import { ANATOMY_BY_ID } from "../src/catalog/anatomy.js";
import { PROCEDURES_BY_ID } from "../src/catalog/procedures.js";
import { ABDOMEN, REGIONS, imageToUV, pinchState, portAt, portToUV, regionAt, torsoFromPose, uvToImage, type Torso } from "../src/jarvis/body-map.js";

// Reclining patient seen from above, slightly rotated and foreshortened.
const above: Torso = {
  rightShoulder: { x: 0.3, y: 0.2 },
  leftShoulder: { x: 0.62, y: 0.24 },
  leftHip: { x: 0.6, y: 0.7 },
  rightHip: { x: 0.34, y: 0.68 },
};
// Person facing the camera: their right side appears on the image's left.
const facing: Torso = {
  rightShoulder: { x: 0.35, y: 0.3 },
  leftShoulder: { x: 0.65, y: 0.3 },
  leftHip: { x: 0.62, y: 0.75 },
  rightHip: { x: 0.38, y: 0.75 },
};

describe("torso frame", () => {
  it("round-trips points on a skewed quad", () => {
    for (const [u, v] of [[0.2, 0.9], [0.5, 0.66], [0.8, 0.4], [0.1, 1.05]] as const) {
      const uv = imageToUV(above, uvToImage(above, u, v))!;
      expect(uv.u).toBeCloseTo(u, 4);
      expect(uv.v).toBeCloseTo(v, 4);
    }
  });

  it("puts the patient's right lower quadrant on their right regardless of camera direction", () => {
    for (const torso of [above, facing]) {
      const nearRightHip = uvToImage(torso, 0.22, 0.94);
      expect(regionAt(imageToUV(torso, nearRightHip))).toBe("appendix");
    }
  });

  it("needs all four landmarks to be visible", () => {
    const pose = Array.from({ length: 33 }, () => ({ x: 0.5, y: 0.5, visibility: 0.9 }));
    expect(torsoFromPose(pose)).not.toBeNull();
    pose[23] = { x: 0.5, y: 0.5, visibility: 0.2 };
    expect(torsoFromPose(pose)).toBeNull();
  });
});

describe("regions", () => {
  it("only names structures that exist in the catalog", () => {
    for (const r of REGIONS) expect(ANATOMY_BY_ID.has(r.id), r.id).toBe(true);
  });

  it("covers every appendectomy target that is touched or identified", () => {
    const appy = PROCEDURES_BY_ID.get("lap_appendectomy")!;
    const touched = new Set(appy.steps.filter((s) => s.check.type !== "place_ports" && s.check.type !== "confirm").flatMap((s) => s.check.targets));
    const mapped = new Set(REGIONS.map((r) => r.id));
    for (const id of touched) expect(mapped.has(id), id).toBe(true);
  });

  // A step target the rig cannot point at stalls that session forever while the coach escalates hints.
  it("lets the camera reach every touched or identified target of every procedure", () => {
    for (const [procedureId, procedure] of PROCEDURES_BY_ID) {
      const allowed = new Set(procedure.structures);
      const touched = new Set(procedure.steps.filter((s) => s.check.type !== "place_ports" && s.check.type !== "confirm").flatMap((s) => s.check.targets));
      for (const id of touched) {
        let reachable = false;
        for (let u = 0; u <= 1 && !reachable; u += 0.005) {
          for (let v = 0.28; v <= 1.1 && !reachable; v += 0.005) reachable = regionAt({ u, v }, allowed) === id;
        }
        expect(reachable, `${procedureId}: ${id}`).toBe(true);
      }
    }
  });

  it("resolves the right lower quadrant finely and ignores the chest", () => {
    expect(regionAt({ u: 0.31, v: 0.92 })).toBe("appendicular_artery");
    expect(regionAt({ u: 0.2, v: 0.85 })).toBe("cecum");
    expect(regionAt({ u: 0.5, v: 0.66 })).toBe("umbilicus");
    expect(regionAt({ u: 0.5, v: 0.1 })).toBe("");
    expect(regionAt({ u: 0.31, v: 0.92 }, new Set(["appendix", "cecum"]))).toBe("");
  });
});

describe("ports and pinch", () => {
  it("places every appendectomy port inside the abdomen and finds it by position", () => {
    const appy = PROCEDURES_BY_ID.get("lap_appendectomy")!;
    expect(portToUV({ x: 0, y: 0, z: 0 })).toEqual({ u: 0.5, v: 0.66 });
    for (const p of appy.ports) {
      const uv = portToUV(p.position);
      expect(uv.u).toBeGreaterThan(ABDOMEN.u[0]);
      expect(uv.v).toBeLessThan(ABDOMEN.v[1]);
      expect(portAt(uv, appy.ports)).toBe(p.id);
    }
  });

  it("detects a pinch with hysteresis", () => {
    const hand = Array.from({ length: 21 }, () => ({ x: 0.5, y: 0.5 }));
    hand[0] = { x: 0.5, y: 0.8 };
    hand[9] = { x: 0.5, y: 0.6 }; // palm size 0.2
    hand[4] = { x: 0.45, y: 0.5 };
    hand[8] = { x: 0.5, y: 0.5 }; // gap 0.05: ratio 0.25
    expect(pinchState(hand, false)).toBe(true);
    hand[8] = { x: 0.53, y: 0.5 }; // gap 0.08: ratio 0.4
    expect(pinchState(hand, false)).toBe(false);
    expect(pinchState(hand, true)).toBe(true);
  });
});
