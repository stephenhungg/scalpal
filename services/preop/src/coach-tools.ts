import { ANATOMY_BY_ID } from "./catalog/anatomy.js";
import { STRUCTURE_FACTS } from "./catalog/coach-knowledge.js";
import { scorePreopCheck } from "./case-builder.js";
import type { CoachSession } from "./coach.js";
import type { RealtimeSink } from "./realtime-bridge.js";
import type { SurgicalCase } from "./types.js";

// Jarvis's client tools, implemented once on the server. Any voice client (the laptop page, the native
// Quest voice transport) forwards a tool call to POST /coach/sessions/:id/tools/:name and speaks the
// returned text, so every client gets the same hints, highlights, and anatomy facts.

export const TOOL_NAMES = ["get_surgery_state", "get_hint", "explain_structure", "highlight_structure", "get_patient_brief", "check_preop"] as const;
export type ToolName = (typeof TOOL_NAMES)[number];

type Resolution = { kind: "case"; id: string } | { kind: "other_case"; id: string } | { kind: "none" };

export interface ToolDeps {
  renderContext: (s: CoachSession) => string;
  resolveStructure: (query: string, kase: SurgicalCase) => Resolution;
  ackWaitMs?: number;
  sleep?: (ms: number) => Promise<void>;
  realtime?: RealtimeSink;
}

export function explainStructure(s: CoachSession, query: string, resolve: ToolDeps["resolveStructure"]): { found: boolean; structureId: string; say: string } {
  const match = resolve(query, s.kase);
  if (match.kind === "none") return { found: false, structureId: "", say: `I don't have a structure called "${query}" in this case.` };
  if (match.kind === "other_case") {
    return { found: false, structureId: match.id, say: `The ${ANATOMY_BY_ID.get(match.id)?.displayName.toLowerCase()} isn't part of the ${s.kase.procedure.title.toLowerCase()}.` };
  }
  const a = s.kase.anatomy.find((x) => x.id === match.id)!;
  const f = STRUCTURE_FACTS[a.id];
  return { found: true, structureId: a.id, say: f ? `${a.displayName}: ${f.what} ${f.where} ${f.why}` : a.displayName };
}

async function highlight(s: CoachSession, structure: string, deps: ToolDeps): Promise<string> {
  const match = deps.resolveStructure(structure, s.kase);
  if (match.kind !== "case") return `${structure} is not part of this case's anatomy.`;
  const name = s.kase.anatomy.find((a) => a.id === match.id)?.displayName.toLowerCase() ?? structure;
  // Wired session: the command goes through SpacetimeDB and the headset resolves it there.
  const shared = await deps.realtime?.highlight(match.id, deps.ackWaitMs);
  if (shared) {
    if (shared.status === "applied") return `Highlighted the ${name} in the headset.`;
    if (shared.status === "pending") return `Highlight requested for ${structure}; the headset has not confirmed it yet.`;
    return `The headset could not highlight it: ${shared.reason || shared.status}.`;
  }
  const command = s.requestCommand("highlight", match.id);
  if ("error" in command) return command.error;
  // The headset acknowledges asynchronously; only claim what it actually applied.
  const sleep = deps.sleep ?? ((ms: number) => new Promise<void>((r) => setTimeout(r, ms)));
  const deadline = deps.ackWaitMs ?? 2000;
  for (let waited = 0; waited < deadline; waited += 100) {
    const c = s.command(command.commandId);
    if (c?.status === "applied") return `Highlighted the ${s.kase.anatomy.find((a) => a.id === match.id)?.displayName.toLowerCase() ?? structure} in the headset.`;
    if (c?.status === "rejected") return `The headset could not highlight it: ${c.reason || "no reason given"}.`;
    await sleep(100);
  }
  return `Highlight requested for ${structure}; the headset has not confirmed it yet.`;
}

export async function runTool(s: CoachSession, name: string, params: Record<string, unknown>, deps: ToolDeps): Promise<string | null> {
  const text = (k: string) => (typeof params[k] === "string" ? (params[k] as string) : "");
  switch (name as ToolName) {
    case "get_surgery_state": {
      // The live card carries only salient facts; the tool returns the full measured table too.
      const facts = s.snapshot().bodyFacts;
      return facts.length ? `${deps.renderContext(s)}\nAll body facts: ${facts.map((f) => `${f.key}=${f.value}`).join("; ")}.` : deps.renderContext(s);
    }
    case "get_hint": {
      const hint = s.requestHint();
      const lit = hint.highlight[0] ? ` ${await highlight(s, hint.highlight[0], deps)}` : "";
      return `Hint tier ${hint.tier} of ${s.snapshot().openBody ? 4 : 3}: ${hint.say}${lit}`;
    }
    case "explain_structure":
      return explainStructure(s, text("structure"), deps.resolveStructure).say;
    case "highlight_structure":
      return highlight(s, text("structure"), deps);
    case "get_patient_brief": {
      const b = s.kase.brief;
      const flags = b.flags.map((f) => `${f.title}: ${f.detail}`).join(" ");
      const options = s.kase.checklistOptions.map((o) => `${o.type} (${o.label})`).join(", ");
      return `${b.say} Flags: ${flags || "none"}. Gaps: ${b.dataGaps.map((g) => g.message).join(" ") || "none"}. Checklist option types: ${options}.`;
    }
    case "check_preop": {
      const raw = params.selected;
      const selected = Array.isArray(raw) ? raw.filter((x): x is string => typeof x === "string") : String(raw ?? "").split(",").map((x) => x.trim()).filter(Boolean);
      return scorePreopCheck(s.kase, selected).say;
    }
    default:
      return null;
  }
}
