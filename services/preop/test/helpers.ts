import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { FinchNodeError, type ConnectSession, type FinchNodeClient, type SandboxSession } from "../src/finchnode.js";
import type { HealthRecord, Scenario } from "../src/types.js";

const FIXTURES = join(import.meta.dirname, "fixtures");

export const NOW = new Date("2026-10-03T12:00:00Z");

export function fixture(subject: string): HealthRecord {
  return JSON.parse(readFileSync(join(FIXTURES, `${subject}.json`), "utf8")) as HealthRecord;
}

// Offline stand-in for the FinchNode demo API, built from recorded synthetic responses and the
// documented behavior scenarios (revoked consent 410, rate limit 429, unknown patient 404).
// With sandbox: true it also fakes Connect admissions: a session completes on its first poll and
// its subject ("u_test_<scenario>") reads the scenario's record with a consent receipt.
export function fixtureClient(options: { sandbox?: boolean } = {}): FinchNodeClient {
  const scenarios = (JSON.parse(readFileSync(join(FIXTURES, "scenarios.json"), "utf8")) as { data: Scenario[] }).data;
  const sandboxSubject = (scenario: string) => `u_test_${scenario.replace(/-/g, "_")}`;
  const sandboxRecord = (subject: string): HealthRecord | null => {
    const scenario = scenarios.find((s) => sandboxSubject(s.id) === subject);
    if (!scenario?.subject || !existsSync(join(FIXTURES, `${scenario.subject}.json`))) return null;
    // Mirrors a real api/v1 read: no environment field, and the source label carries the scenario title.
    const { environment: _demoOnly, ...record } = fixture(scenario.subject);
    return {
      ...record,
      id: subject,
      sources: [{ system: "synthetic-test@o_test", organization: `Northstar Health System (Synthetic) \u00b7 ${scenario.title}` }],
      consent: { status: "active", receiptIds: ["rcpt_test_0001"] },
    };
  };
  const session = (scenario: string, completed: boolean): SandboxSession => ({
    id: `cs_test${scenario.replace(/[^a-z0-9]/g, "")}`,
    url: `https://finchnode.com/connect/cs_test${scenario.replace(/[^a-z0-9]/g, "")}`,
    status: completed ? "completed" : "system-selected",
    subject: completed ? sandboxSubject(scenario) : null,
    organization: "Northstar Health System (Synthetic)",
    externalId: `scalpal-${scenario}`,
    simulation: { scenario, state: completed ? "completed" : "syncing" },
    sync: { status: completed ? "complete" : "syncing" },
  });
  const client: FinchNodeClient = {
    listScenarios: async () => scenarios,
    getRecord: async (subject) => {
      if (subject === "patient-demo-consent-revoked") {
        throw new FinchNodeError(410, "consent_inactive", "The patient revoked sharing for this application.");
      }
      if (subject === "patient-demo-rate-limited") {
        throw new FinchNodeError(429, "rate_limited", "Scenario rate-limited: retry after the indicated interval.", 1);
      }
      // One FinchNode account that connected two different scenario patients' sources.
      if (subject === "u_test_mixed" && options.sandbox) {
        return {
          ...fixture("patient-demo-001"),
          id: subject,
          environment: undefined,
          sources: [
            { system: "synthetic-test@o_a", organization: "Northstar Health System (Synthetic) \u00b7 Baseline adult, age 38" },
            { system: "synthetic-test@o_b", organization: "Quillhaven Medical Group (Synthetic) \u00b7 Two sources with overlapping records, age 40" },
          ],
          consent: { status: "active", receiptIds: ["rcpt_test_a", "rcpt_test_b"] },
        };
      }
      const sandbox = subject.startsWith("u_test_") ? sandboxRecord(subject) : null;
      if (sandbox) return sandbox;
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
  if (options.sandbox) {
    client.sandbox = {
      admit: async (scenario) => session(scenario, false),
      getSession: async (sessionId) => {
        const scenario = scenarios.find((s) => session(s.id, false).id === sessionId);
        if (!scenario) throw new FinchNodeError(404, "not_found", "No such session.");
        return session(scenario.id, true);
      },
      // One patient who consented through the hosted page outside any admission this server made.
      listUsers: async () => [
        { id: "u_test_messy_coding", sources: [{ system: "synthetic-test@o_test", organization: "Northstar Health System (Synthetic) \u00b7 Messy coding, age 63" }], consentedAt: "2026-10-03T20:34:39Z" },
      ],
    };
  }
  return client;
}
