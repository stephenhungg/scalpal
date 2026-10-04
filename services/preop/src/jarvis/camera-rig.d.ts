// Types for camera-rig.js, which ships to the browser as plain JavaScript.
export interface RigApiResult {
  ok: boolean;
  json: Record<string, any>; // JSON from the service; shapes vary by route
}
export interface RigCommand {
  commandId: string;
  action: string;
  targetId: string;
}
export interface RigSessionDeps {
  api: (method: string, path: string, body?: unknown) => Promise<RigApiResult>;
  actsAsHeadset: () => boolean;
  onAttach: (kase: Record<string, unknown>) => void;
  onSnapshot: (snapshot: Record<string, unknown>) => void;
  onCommand: (command: RigCommand) => void;
  feed: (text: string, cls?: string) => void;
}
export function createRigSession(deps: RigSessionDeps): {
  readonly sid: string;
  attach(): Promise<void>;
  send(event: Record<string, unknown>): Promise<RigApiResult | null>;
};
export function createFrameLoop(
  raf: (callback: () => void) => unknown,
  frame: () => void,
  onError?: (err: unknown) => void,
): { readonly running: boolean; start(): boolean; stop(): void };
