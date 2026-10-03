import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { FinchNodeError, type ConnectSession, type FinchNodeClient } from "../src/finchnode.js";
import type { HealthRecord, Scenario } from "../src/types.js";

const FIXTURES = join(import.meta.dirname, "fixtures");

export const NOW = new Date("2026-10-03T12:00:00Z");

export function fixture(subject: string): HealthRecord {
  return JSON.parse(readFileSync(join(FIXTURES, `${subject}.json`), "utf8")) as HealthRecord;
}

// Offline stand-in for the FinchNode demo API, built from recorded synthetic responses and the
// documented behavior scenarios (revoked consent 410, rate limit 429, unknown patient 404).
export function fixtureClient(): FinchNodeClient {
  const scenarios = (JSON.parse(readFileSync(join(FIXTURES, "scenarios.json"), "utf8")) as { data: Scenario[] }).data;
  return {
    listScenarios: async () => scenarios,
    getRecord: async (subject) => {
      if (subject === "patient-demo-consent-revoked") {
        throw new FinchNodeError(410, "consent_inactive", "The patient revoked sharing for this application.");
      }
      if (subject === "patient-demo-rate-limited") {
        throw new FinchNodeError(429, "rate_limited", "Scenario rate-limited: retry after the indicated interval.", 1);
      }
      const file = join(FIXTURES, `${subject}.json`);
      if (!existsSync(file)) throw new FinchNodeError(404, "patient_not_found", "Synthetic demo patient not found.");
      return fixture(subject);
    },
    createConnectSession: async (scenario): Promise<ConnectSession> => ({
      id: `demo_cs_${scenario}`,
      scenario,
      status: scenario === "connect-cancelled" ? "cancelled" : scenario === "connect-failed" ? "failed" : "completed",
      patient_id: null,
      failure_code: scenario === "connect-failed" ? "source_unavailable" : null,
      failure_message: scenario === "connect-failed" ? "The health system did not respond during authorization." : null,
    }),
  };
}
