// Types for encounter.js, which ships to the browser as plain JavaScript.
export interface ApiResult {
  ok: boolean;
  status: number;
  json: Record<string, unknown>;
}
export interface VoiceConversation {
  endSession(): Promise<void>;
}
export interface FlowElement {
  textContent: string | null;
  innerHTML: string;
  hidden: boolean;
  disabled?: boolean;
  className: string;
  dataset?: Record<string, string | undefined>;
}
export interface EncounterFlowDeps {
  api: (method: string, path: string, body?: unknown) => Promise<ApiResult>;
  log: (kind: string, text: string, extra?: string) => void;
  setActiveConvo: (c: VoiceConversation | null) => void;
  onStatus: (text: string, cls: string) => void;
  onScrubIn: () => void;
  Conversation: { startSession(options: Record<string, unknown>): Promise<VoiceConversation> };
  getMicrophone?: () => Promise<unknown>;
  doc?: { getElementById(id: string): FlowElement | null; querySelectorAll(selector: string): Iterable<FlowElement> };
}
export function cleanTranscript(text: unknown): string;
export function createEncounterFlow(deps: EncounterFlowDeps): {
  start(patientId: string): Promise<"encounter" | "none" | "failed">;
  presentToAttending(): Promise<boolean>;
  retry(): Promise<unknown>;
  scrubIn(): Promise<void>;
  stop(): Promise<void>;
};
