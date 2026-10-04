import { bodyAction } from "../src/open-body.js";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { ReflexAudio } from "../src/reflex.js";
import { NOW, fixtureClient } from "./helpers.js";

function rig(extra: Parameters<typeof createApp>[0] = {}) {
  const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, toolAckWaitMs: 300, ...extra });
  const req = async (method: string, route: string, body?: unknown) => {
    const res = await app.request(route, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
    return { status: res.status, json: (await res.json()) as Record<string, any> };
  };
  return { app, req };
}

async function session(req: ReturnType<typeof rig>["req"]) {
  return (await req("POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma", mode: "virtual" })).json.sessionId as string;
}

describe("shared Scalpal tools", () => {
  it("answers every tool from the server, so any voice client gets the same coaching", async () => {
    const { req } = rig();
    const sid = await session(req);
    const tool = async (name: string, params: unknown = {}) => (await req("POST", `/coach/sessions/${sid}/tools/${name}`, params)).json.result as string;
    expect(await tool("get_surgery_state")).toMatch(/LIVE SURGERY STATE/);
    expect(await tool("explain_structure", { structure: "appendicular artery" })).toMatch(/ileocolic/);
    expect(await tool("explain_structure", { structure: "cystic duct" })).toMatch(/isn't part of/);
    expect(await tool("get_patient_brief")).toMatch(/Checklist option types/);
    expect(await tool("check_preop", { selected: ["airway", "pediatric"] })).toBeTruthy();
    expect(await tool("highlight_structure", { structure: "cecum" })).toMatch(/not confirmed/); // nobody acked within the wait
    expect((await req("POST", `/coach/sessions/${sid}/tools/launch_rocket`, {})).status).toBe(404);
  });

  it("reports a highlight as applied only after the headset acks it", async () => {
    const { req } = rig({ toolAckWaitMs: 2000 });
    const sid = await session(req);
    const pending = req("POST", `/coach/sessions/${sid}/tools/highlight_structure`, { structure: "the cecum" });
    let cmd: { commandId: string } | undefined;
    for (let i = 0; i < 20 && !cmd; i++) {
      await new Promise((r) => setTimeout(r, 20));
      cmd = (await req("GET", `/coach/sessions/${sid}/commands`)).json.commands[0];
    }
    await req("POST", `/coach/sessions/${sid}/commands/${cmd!.commandId}/ack`, { status: "applied" });
    expect((await pending).json.result).toMatch(/Highlighted the cecum/);
  });

  it("escalates hints through the tool and highlights the target", async () => {
    const { req } = rig();
    const sid = await session(req);
    await req("POST", `/coach/sessions/${sid}/simulate`, { kind: "complete_step" });
    await req("POST", `/coach/sessions/${sid}/simulate`, { kind: "complete_step" });
    const first = (await req("POST", `/coach/sessions/${sid}/tools/get_hint`, {})).json.result;
    const second = (await req("POST", `/coach/sessions/${sid}/tools/get_hint`, {})).json.result;
    expect(first).toMatch(/^Hint tier 1 of 4/);
    expect(second).toMatch(/^Hint tier 2 of 4: .*fascia/i);
  });
});

describe("alerts feed for clients without SSE", () => {
  it("returns new alerts with tier, sim-event text, and clip routes", async () => {
    const reflex = new ReflexAudio({ apiKey: "k", voiceId: "v", cacheDir: mkdtempSync(join(tmpdir(), "rx-")), fetchImpl: (async () => new Response(new Uint8Array([1]))) as typeof fetch });
    const { req } = rig({ reflex });
    const sid = await session(req);
    expect((await req("GET", `/coach/sessions/${sid}/alerts`)).json).toMatchObject({ alerts: [], latestSeq: 0 });
    await req("POST", `/coach/sessions/${sid}/simulate`, { kind: "complete_step" });
    await req("POST", `/coach/sessions/${sid}/events`, { event: { type: "surgery", evidence: bodyAction("cut", "skin", { lengthMm: 4, depthMm: 15 }) } });
    const feed = (await req("GET", `/coach/sessions/${sid}/alerts?after=0`)).json;
    const [step, warning] = feed.alerts;
    expect(step).toMatchObject({ kind: "step_complete", tier: "caution", reflexRoute: `/jarvis/reflex/${sid}/step.incise_skin` });
    expect(step.simEvent).toMatch(/^\[SIM EVENT v1 step 2\/10 "Incise skin"\] kind=step_complete/);
    expect(warning).toMatchObject({ kind: "mistake", tier: "warning", reflexRoute: `/jarvis/reflex/${sid}/mistake.deep_skin_cut` });
    expect((await req("GET", `/coach/sessions/${sid}/alerts?after=${feed.latestSeq}`)).json.alerts).toEqual([]);
  });
});

describe("context key", () => {
  it("stays put while only timers change and moves when the state does", async () => {
    let t = NOW.getTime();
    const { req } = rig({ now: () => new Date(t) });
    const sid = await session(req);
    const k1 = (await req("GET", `/coach/sessions/${sid}`)).json.contextKey;
    t += 5000;
    const later = (await req("GET", `/coach/sessions/${sid}`)).json;
    expect(later.contextKey).toBe(k1);
    await req("POST", `/coach/sessions/${sid}/simulate`, { kind: "correct_action" });
    expect((await req("GET", `/coach/sessions/${sid}`)).json.contextKey).not.toBe(k1);
  });
});
