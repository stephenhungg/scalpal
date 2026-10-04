import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { unitySafetyErrors } from "../src/unity-safe.js";
import { NOW, fixtureClient } from "./helpers.js";

describe("flythrough briefing narration", () => {
  it("has one line per briefing beat, keyed like the Unity briefing atlas, with intro and outro", async () => {
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
    const created = (await (await app.request("/coach/sessions", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ patientId: "patient-demo-sparse" }) })).json()) as any;
    const res = (await (await app.request(`/coach/sessions/${created.sessionId}/briefing`)).json()) as any;
    expect(unitySafetyErrors(res)).toEqual([]);
    const atlas = JSON.parse(readFileSync(new URL("../../../apps/quest/Assets/Scalpal/Briefing/Resources/briefing_parts.json", import.meta.url), "utf8"));
    const beats = atlas.steps.map((s: { id: string }) => s.id);
    expect(res.lines.filter((l: any) => l.stepId).map((l: any) => l.stepId)).toEqual(beats);
    expect(res.lines[0]).toMatchObject({ key: "brief.intro", stepId: "" });
    expect(res.lines[0].text).toMatch(/open appendectomy for Jonah/i);
    expect(res.lines.at(-1).key).toBe("brief.outro");
    for (const l of res.lines) expect(l.text.split(" ").length).toBeLessThan(40);
    const reflex = (await (await app.request(`/jarvis/reflex/${created.sessionId}`)).json()) as any;
    const keys = new Set((reflex.lines ?? []).map((l: any) => l.key));
    for (const l of res.lines) expect(keys.has(l.key)).toBe(true);
  });
});
