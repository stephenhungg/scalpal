import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { buildCase } from "../src/case-builder.js";
import { ENCOUNTERS_BY_PLAN } from "../src/catalog/encounters.js";
import { EncounterSession } from "../src/encounter.js";
import { buildCarryoverItems } from "../src/encounter-carryover.js";
import { unitySafetyErrors } from "../src/unity-safe.js";
import { fixture, fixtureClient, NOW } from "./helpers.js";

const session = (id: string) => new EncounterSession("enc-carryover", buildCase(fixture(id), "", NOW), ENCOUNTERS_BY_PLAN.get(id)!, () => NOW);

describe("structured office carryover", () => {
  it("intersects chart flags with case considerations and logged office evidence", () => {
    const s = session("patient-demo-multi-source");
    const before = s.score().carryoverItems;
    expect(before.find((item) => item.type === "latex")?.status).toBe("missed");
    s.answer("onset");
    expect(s.score().carryoverItems).toEqual(before);
    s.answer("allergies");
    const after = s.score().carryoverItems;
    expect(after.find((item) => item.type === "latex")).toMatchObject({ status: "found", historyTopics: ["allergies"], testIds: [] });
    expect(after.find((item) => item.type === "anemia")?.status).toBe("missed");
    for (const item of after) {
      expect(s.kase.brief.flags.some((flag) => flag.id === item.flagId)).toBe(true);
      expect(item.stepIds).toEqual([...new Set(s.kase.considerations.filter((c) => c.flagId === item.flagId).map((c) => c.stepId))]);
    }
    const withoutConsiderations = { ...s.kase, considerations: [] };
    expect(buildCarryoverItems(withoutConsiderations, ["allergies"], [])).toEqual([]);
  });

  it("requires medication and renal coverage for the compound risk, with tests as evidence", () => {
    const s = session("patient-demo-polypharmacy");
    s.answer("medications");
    expect(s.score().carryoverItems.find((item) => item.type === "metformin_renal")?.status).toBe("missed");
    s.orderTest("bmp");
    expect(s.score().carryoverItems.find((item) => item.type === "metformin_renal")).toMatchObject({
      status: "found", historyTopics: ["medications"], testIds: ["bmp"],
    });
  });

  it("does not credit unavailable test results and never scores age as an office miss", () => {
    const s = session("patient-demo-polypharmacy");
    s.answer("medications");
    const withoutBmp = { ...s.encounter, tests: { ...s.encounter.tests, bmp: undefined } };
    const unavailable = new EncounterSession("enc-noresult", s.kase, withoutBmp, () => NOW);
    unavailable.answer("medications");
    unavailable.orderTest("bmp");
    expect(unavailable.score().carryoverItems.find((item) => item.type === "metformin_renal")?.status).toBe("missed");
    expect(session("patient-demo-pediatric-asthma").score().carryoverItems.find((item) => item.type === "pediatric")).toMatchObject({
      status: "chart_only", historyTopics: [], testIds: [],
    });
  });

  it("keeps JSON safe and the correct patient, procedure, urgency and risks through the scored HTTP route", async () => {
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
    const request = (path: string, body?: object) => app.request(path, {
      method: body ? "POST" : "GET",
      headers: { "Content-Type": "application/json" },
      body: body ? JSON.stringify(body) : undefined,
    });
    const created = await (await request("/encounters", { patientId: "patient-demo-multi-source" })).json();
    const path = `/encounters/${created.encounterId}`;
    expect((await request(`${path}/score`)).status).toBe(409);
    await request(`${path}/tools/answer`, { topic: "allergies" });
    await request(`${path}/attending`, {});
    await request(`${path}/tools/record_assessment`, {
      diagnosis: "kidney stone", differential: [], procedure: "ureteroscopy", urgency: "elective",
    });
    const scored = await (await request(`${path}/score`)).json();
    expect(unitySafetyErrors(scored)).toEqual([]);
    expect(scored.scorecard).toMatchObject({
      patientId: "patient-demo-multi-source", patientName: "Priya Ramaswamy",
      procedureId: "lap_appendectomy", procedureChosenCorrectly: false,
      urgency: "urgent", site: "Abdomen — appendix / right lower quadrant",
    });
    expect(scored.scorecard.carryoverItems.find((item: { type: string }) => item.type === "latex")).toMatchObject({ status: "found" });
    const coachResponse = await request("/coach/sessions", {
      patientId: scored.scorecard.patientId, mode: "virtual", encounterId: created.encounterId,
    });
    expect(coachResponse.status).toBe(201);
    const coach = await coachResponse.json();
    expect(coach.snapshot).toMatchObject({
      patientId: "patient-demo-multi-source", procedureId: "lap_appendectomy", mode: "virtual",
    });
    expect(coach.systemPrompt).toContain("FROM THE PRE-OP OFFICE");
    expect(coach.systemPrompt).toContain('proposed "ureteroscopy"');
    expect((await (await request(`${path}/score`)).json()).scorecard).toEqual(scored.scorecard);

    // Existing coach behavior permits a standalone case but rejects another patient's carryover.
    const otherResponse = await request("/coach/sessions", {
      patientId: "patient-demo-pediatric-asthma", mode: "virtual", encounterId: created.encounterId,
    });
    expect(otherResponse.status).toBe(201);
    const other = await otherResponse.json();
    expect(other.snapshot.patientId).toBe("patient-demo-pediatric-asthma");
    expect(other.systemPrompt).not.toContain("FROM THE PRE-OP OFFICE");
    expect(other.systemPrompt).not.toContain('proposed "ureteroscopy"');
  });
});
