// Types for arbiter.js, which ships to the browser as plain JavaScript.
export interface ArbiterAlert {
  id: string;
  kind: string;
  tier: "warning" | "caution" | "advisory";
  stepId: string;
  version: number;
  say: string;
  reflexKey: string;
  highlight: string[];
}

export interface ArbiterSnapshot {
  status: string;
  stuckLevel: number;
  step: { id: string };
}

export interface ArbiterOptions {
  now?: () => number;
  minGapMs?: number;
  quickGapMs?: number;
  userQuietMs?: number;
  idleSettleMs?: number;
  cooldownMs?: Record<string, number>;
}

export type OfferAction = "reflex" | "speak_now" | "queued" | "silent" | "dropped";

export interface Arbiter {
  stats: Record<string, number>;
  readonly queueSize: number;
  setSnapshot(snapshot: ArbiterSnapshot): void;
  setMode(mode: string): void;
  responseComplete(): void;
  userSpoke(): void;
  setReflexPlaying(playing: boolean): void;
  offer(alert: ArbiterAlert): { action: OfferAction; reason: string };
  next(): ArbiterAlert | null;
}

export const DEFAULTS: Required<Omit<ArbiterOptions, "now">>;
export function createArbiter(options?: ArbiterOptions): Arbiter;
export function coalesceKey(alert: ArbiterAlert): string;
export function semanticKey(snapshot: unknown): string;
export function percentile(values: number[], p: number): number;
