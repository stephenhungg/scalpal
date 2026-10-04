import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { createEncounterFlow, type ApiResult, type FlowElement } from "../src/jarvis/encounter.js";
import { NOW, fixtureClient } from "./helpers.js";

// The laptop encounter flow against the real service, with a fake page and a fake voice SDK. A service
// hiccup must be shown to the learner with Retry, never silently skip the interview, and a voice agent
// must never be connected without the prompt the server built for its role (an empty prompt falls back
// to the agent's default, the surgery coach, with encounter tools attached).

type Fault = { method: string; path: RegExp; result?: ApiResult; throws?: boolean };

function harness() {
  const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, elevenLabs: { apiKey: "", agentId: "agent-jarvis", patientAgentId: "agent-patient" } });
  const faults: Fault[] = [];
  const api = async (method: string, path: string, body?: unknown): Promise<ApiResult> => {
    const i = faults.findIndex((f) => f.method === method && f.path.test(path));
    if (i !== -1) {
      const [fault] = faults.splice(i, 1);
      if (fault!.throws) throw new TypeError("Failed to fetch");
      return fault!.result!;
    }
    const res = await app.request(path, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
    return { ok: res.ok, status: res.status, json: (await res.json().catch(() => ({}))) as Record<string, unknown> };
  };
  const elements = new Map<string, FlowElement>();
  const doc = {
    getElementById: (id: string) => {
      if (!elements.has(id)) elements.set(id, { textContent: "", innerHTML: "", hidden: id === "flow-error", disabled: false, className: "", dataset: {} });
      return elements.get(id)!;
    },
    querySelectorAll: () => [],
  };
  const started: Record<string, any>[] = []; // startSession options as the SDK would receive them
  const Conversation = { startSession: async (options: Record<string, unknown>) => { started.push(options); return { endSession: async () => {} }; } };
  const logs: string[] = [];
  let scrubbed = 0;
  const flow = createEncounterFlow({
    api,
    log: (_kind, text) => logs.push(text),
    setActiveConvo: () => {},
    onStatus: () => {},
    onScrubIn: () => { scrubbed += 1; },
    Conversation,
    getMicrophone: async () => ({}),
    doc,
  });
  const promptOf = (i: number) => started[i]?.overrides.agent.prompt.prompt as string;
  return { flow, faults, started, logs, promptOf, el: doc.getElementById, scrubbed: () => scrubbed };
}

const serverError = (status: number, code: string): ApiResult => ({ ok: false, status, json: { error: { code, message: `${code} happened.` } } });

describe("encounter page flow", () => {
  it("connects the patient with the server-built patient prompt and voice", async () => {
    const h = harness();
    expect(await h.flow.start("multi-source-overlap")).toBe("encounter");
    expect(h.started).toHaveLength(1);
    expect(h.started[0]).toMatchObject({ agentId: "agent-patient", overrides: { tts: { voiceId: "EXAVITQu4vr4xnSDxMaL" } } });
    expect(h.promptOf(0)).toMatch(/You are Priya Ramaswamy/);
  });

  it("goes straight to surgery only when the patient has no authored interview", async () => {
    const h = harness();
    // Every buildable demo patient now has an interview, so the server's no_encounter answer is injected.
    h.faults.push({ method: "POST", path: /^\/encounters$/, result: serverError(404, "no_encounter") });
    expect(await h.flow.start("patient-demo-polypharmacy")).toBe("none");
    expect(h.started).toHaveLength(0);
    expect(h.el("flow-error")!.hidden).toBe(true);
  });

  it("shows a failed start with Retry instead of skipping to surgery", async () => {
    const h = harness();
    h.faults.push({ method: "POST", path: /^\/encounters$/, result: serverError(503, "busy") });
    expect(await h.flow.start("multi-source-overlap")).toBe("failed");
    expect(h.el("flow-error")!.hidden).toBe(false);
    expect(h.el("flow-error-text")!.textContent).toMatch(/busy happened/);
    expect(h.started).toHaveLength(0);
    expect(h.scrubbed()).toBe(0);
    expect(await h.flow.retry()).toBe("encounter");
    expect(h.el("flow-error")!.hidden).toBe(true);
    expect(h.promptOf(0)).toMatch(/You are Priya Ramaswamy/);
  });

  it("treats a network failure like any other failure", async () => {
    const h = harness();
    h.faults.push({ method: "POST", path: /^\/encounters$/, throws: true });
    expect(await h.flow.start("multi-source-overlap")).toBe("failed");
    expect(h.el("flow-error-text")!.textContent).toMatch(/unreachable/);
  });

  it("never connects Jarvis when the attending handoff fails", async () => {
    const h = harness();
    await h.flow.start("multi-source-overlap");
    h.faults.push({ method: "POST", path: /\/attending$/, result: serverError(409, "invalid_phase") });
    expect(await h.flow.presentToAttending()).toBe(false);
    expect(h.started).toHaveLength(1); // only the patient, earlier
    expect(h.el("flow-error")!.hidden).toBe(false);
    expect(await h.flow.retry()).toBe(true);
    expect(h.started).toHaveLength(2);
    expect(h.started[1]).toMatchObject({ agentId: "agent-jarvis" });
    expect(h.promptOf(1)).toMatch(/You are Jarvis, the attending surgeon/);
  });

  it("refuses to connect a voice when the server returns no prompt", async () => {
    const h = harness();
    h.faults.push({ method: "GET", path: /^\/jarvis\/connection/, result: { ok: true, status: 200, json: { mode: "public", agentId: "agent-patient", role: "patient" } } });
    expect(await h.flow.start("multi-source-overlap")).toBe("encounter");
    expect(h.started).toHaveLength(0);
    expect(h.el("flow-error")!.hidden).toBe(false);
    expect(await h.flow.retry()).toBe(true);
    expect(h.promptOf(0)).toMatch(/You are Priya Ramaswamy/);
  });
});
