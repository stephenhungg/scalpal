// Types for body-map.js, which ships to the browser as plain JavaScript.
export interface Point {
  x: number;
  y: number;
  visibility?: number;
}
export interface UV {
  u: number;
  v: number;
}
export interface Torso {
  leftShoulder: Point;
  rightShoulder: Point;
  leftHip: Point;
  rightHip: Point;
}
export interface Region {
  id: string;
  u: [number, number];
  v: [number, number];
}
export const REGIONS: Region[];
export const ABDOMEN: { u: [number, number]; v: [number, number] };
export function portToUV(position: { x: number; y: number; z: number }): UV;
export function uvToImage(torso: Torso, u: number, v: number): Point;
export function imageToUV(torso: Torso, p: Point): UV | null;
export function regionAt(uv: UV | null, allowed?: Set<string>): string;
export function portAt(uv: UV | null, ports: { id: string; position: { x: number; y: number; z: number } }[], radius?: number): string;
export function pinchState(hand: Point[], wasPinched: boolean): boolean;
export function torsoFromPose(pose: Point[] | undefined, minVisibility?: number): Torso | null;
