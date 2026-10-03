import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { unitySafetyErrors } from "../src/unity-safe.js";

// Hits the real FinchNode demo API. Run with `npm run test:live`.
describe.runIf(process.env.LIVE === "1")("live FinchNode demo API", () => {
  const app = createApp();

  it("lists real patients and builds a Unity-safe case", async () => {
    const patients = (await (await app.request("/patients")).json()) as { patients: { patientId: string; status: string }[] };
    expect(patients.patients.filter((p) => p.status === "ready" || p.status === "needs_review").length).toBeGreaterThanOrEqual(6);

    const kase = (await (await app.request("/patients/polypharmacy-senior/case")).json()) as Record<string, unknown>;
    expect(kase.procedureId).toBe("lap_cholecystectomy");
    expect(unitySafetyErrors(kase)).toEqual([]);
  }, 30_000);

  it("maps the real revoked-consent scenario to a blocked case", async () => {
    const kase = (await (await app.request("/patients/consent-revoked/case")).json()) as Record<string, unknown>;
    expect(kase.status).toBe("blocked");
  }, 15_000);
});
