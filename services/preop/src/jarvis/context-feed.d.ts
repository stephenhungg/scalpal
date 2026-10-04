// Types for context-feed.js, which ships to the browser as plain JavaScript.
export const FULL_EVERY_MS: number;
export interface ContextFeed {
  next(snapshot: any, fullText: string, options?: { force?: boolean }): { kind: "full" | "delta"; text: string } | null;
}
export function createContextFeed(options?: { now?: () => number; fullEveryMs?: number }): ContextFeed;
