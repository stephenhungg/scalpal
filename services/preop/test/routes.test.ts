import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { matchRoute } from "../src/route-patterns.js";
import { unitySafetyErrors } from "../src/unity-safe.js";
import type { Action } from "../src/types.js";
import { NOW, fixtureClient } from "./helpers.js";

const app = createApp({ client: fixtureClient({ sandbox: true }), now: () => NOW });

async function call(method: string, route: string, body?: unknown) {
  const res = await app.request(route, {
    method,
    headers: body ? { "Content-Type": "application/json" } : {},
    body: body ? JSON.stringify(body) : undefined,
  });
  return { status: res.status, json: (await res.json()) as Record<string, unknown> };
}

// A plausible body for each POST route a client can be told to call.
function bodyFor(route: string): unknown {
  if (route.endsWith("/preop-check")) return { selected: ["bleeding", "allergy"] };
  return undefined;
}

describe("route crawl", () => {
  it("every action reachable from / resolves, renders in Unity, and offers a way onward", async () => {
    const seen = new Set<string>();
    const queue: Action[] = [{ id: "root", label: "root", method: "GET", route: "/" }];
    let crawled = 0;

    while (queue.length) {
      const action = queue.shift()!;
      const key = `${action.method} ${action.route}`;
      if (seen.has(key)) continue;
      seen.add(key);
      crawled += 1;

      const { status, json } = await call(action.method, action.route, bodyFor(action.route));
      expect((json.error as { code?: string } | undefined)?.code, `${key} hit a missing route`).not.toBe("route_not_found");
      expect(status, `${key} returned ${status}: ${JSON.stringify(json.error)}`).toBeLessThan(500);
      expect(unitySafetyErrors(json), `${key} is not JsonUtility-safe`).toEqual([]);

      const actions = collectActions(json);
      expect(actions.length, `${key} is a dead end with no actions`).toBeGreaterThan(0);
      for (const a of actions) {
        expect(a.route.startsWith("/"), `${key} offers a non-local route ${a.route}`).toBe(true);
        expect(matchRoute(a.method, a.route), `${key} offers ${a.method} ${a.route}, which the Unity router cannot handle`).not.toBeNull();
        queue.push(a);
      }
    }
    expect(crawled).toBeGreaterThan(20);
  });
});

function collectActions(value: unknown, out: Action[] = []): Action[] {
  if (Array.isArray(value)) value.forEach((v) => collectActions(v, out));
  else if (value && typeof value === "object") {
    const obj = value as Record<string, unknown>;
    if (Array.isArray(obj.actions)) out.push(...(obj.actions as Action[]));
    for (const [k, v] of Object.entries(obj)) if (k !== "actions") collectActions(v, out);
  }
  return out;
}

describe("unavailable states still route", () => {
  it("revoked consent renders a blocked case pointing back to the patient list", async () => {
    const { status, json } = await call("GET", "/patients/patient-demo-consent-revoked/case");
    expect(status).toBe(200);
    expect(json.status).toBe("blocked");
    expect((json.actions as Action[]).map((a) => a.route)).toContain("/patients");
  });

  it("rate limiting renders a retry case with the wait time", async () => {
    const { json } = await call("GET", "/patients/rate-limited/case");
    expect(json).toMatchObject({ status: "retry", retryAfterSeconds: 1 });
  });

  it("unknown patients 404 with a way back", async () => {
    const { status, json } = await call("GET", "/patients/patient-demo-nobody/case");
    expect(status).toBe(404);
    expect((json.actions as Action[])[0]?.route).toBe("/patients");
  });

  it("rejects malformed ids before calling FinchNode", async () => {
    expect((await call("GET", "/patients/..%2Fadmin/case")).status).toBe(400);
  });

  it("failed health system connections explain themselves", async () => {
    const { json } = await call("POST", "/connect/connect-failed");
    expect(json).toMatchObject({ status: "failed", failureCode: "source_unavailable" });
    expect(String(json.say)).toMatch(/failed/);
  });

  it("unknown routes 404 with a way home", async () => {
    const { status, json } = await call("GET", "/nope");
    expect(status).toBe(404);
    expect((json.actions as Action[])[0]?.route).toBe("/");
  });
});

describe("FinchNode Connect admissions", () => {
  it("admits a patient through a consented sandbox session and opens their case", async () => {
    const admit = await call("POST", "/admit/polypharmacy-senior");
    expect(admit.json).toMatchObject({ state: "connecting", scenarioId: "polypharmacy-senior" });
    const routesOffered = (admit.json.actions as Action[]).map((a) => a.route);
    expect(routesOffered).toContain("/patients/patient-demo-polypharmacy/case");

    const poll = await call("GET", `/admissions/${admit.json.sessionId}`);
    expect(poll.json).toMatchObject({ state: "completed", patientId: "u_test_polypharmacy_senior" });

    const kase = await call("GET", "/patients/u_test_polypharmacy_senior/case");
    expect(kase.json).toMatchObject({ procedureId: "lap_cholecystectomy", urgency: "urgent" });
    const brief = kase.json.brief as { dataSource: string; consentReceipts: string[]; chart: { section: string; text: string }[] };
    expect(brief.dataSource).toBe("sandbox");
    expect(brief.consentReceipts).toEqual(["rcpt_test_0001"]);
    expect(brief.chart.find((l) => l.section === "Consent")?.text).toContain("rcpt_test_0001");

    const list = await call("GET", "/patients");
    expect((list.json.patients as { patientId: string; kind: string }[]).some((p) => p.patientId === "u_test_polypharmacy_senior" && p.kind === "sandbox")).toBe(true);
  });

  it("admission returns FinchNode's hosted consent link", async () => {
    const admit = await call("POST", "/admit/baseline-adult");
    expect(String(admit.json.connectUrl)).toMatch(/^https:\/\/finchnode\.com\/connect\//);
    expect(String(admit.json.say)).toMatch(/approve sharing/);
  });

  it("lists patients who consented on the hosted page and maps them to their scenario's case", async () => {
    const list = await call("GET", "/patients");
    const console = (list.json.patients as { patientId: string; scenarioId: string; procedureId: string }[]).find((p) => p.patientId === "u_test_messy_coding");
    expect(console).toMatchObject({ scenarioId: "messy-coding", procedureId: "lap_sigmoid_colectomy" });
    const kase = await call("GET", "/patients/u_test_messy_coding/case");
    expect((kase.json.brief as { dataSource: string }).dataSource).toBe("sandbox");
  });

  it("only states chart facts the actual record supports", async () => {
    // The fake sandbox Priya has one source; the demo record has two.
    const sandbox = await call("GET", "/patients/u_test_multi_source_overlap/case");
    expect(String(sandbox.json.presentation)).not.toMatch(/two health systems/);
    const demo = await call("GET", "/patients/patient-demo-multi-source/case");
    expect(String(demo.json.presentation)).toMatch(/two health systems/);
  });

  it("flags a patient whose connected sources belong to different people", async () => {
    const { json } = await call("GET", "/patients/u_test_mixed/case");
    expect(json).toMatchObject({ scenarioId: "multi-source-overlap", procedureId: "lap_appendectomy" });
    const brief = json.brief as { dataGaps: { code: string }[]; flags: { type: string; severity: string }[] };
    expect(brief.dataGaps.map((g) => g.code)).toContain("identity_mismatch");
    expect(brief.flags.find((f) => f.type === "incomplete_chart")?.severity).toBe("high");
  });

  it("without a sandbox key, admission falls back to the demo record", async () => {
    const plain = createApp({ client: fixtureClient(), now: () => NOW });
    const res = await plain.request("/admit/baseline-adult", { method: "POST" });
    const json = (await res.json()) as { state: string; actions: Action[] };
    expect(json.state).toBe("unavailable");
    expect(json.actions[0]?.route).toBe("/patients/patient-demo-001/case");
  });

  it("unknown admissions 404 with a way back", async () => {
    const { status, json } = await call("GET", "/admissions/cs_nope123456");
    expect(status).toBe(404);
    expect((json.actions as Action[])[0]?.route).toBe("/patients");
  });
});

describe("scenario ids resolve to patients", () => {
  it("baseline-adult opens Morgan's case", async () => {
    const { json } = await call("GET", "/patients/baseline-adult/case");
    expect(json).toMatchObject({ patientId: "patient-demo-001", scenarioId: "baseline-adult" });
  });
});

describe("ElevenLabs tools", () => {
  it("list_patients speaks every connected patient", async () => {
    const { status, json } = await call("POST", "/tools/list_patients");
    expect(status).toBe(200);
    expect(String(json.say)).toContain("Harriet Lindqvist");
  });

  it("get_case returns a speakable case", async () => {
    const { json } = await call("POST", "/tools/get_case", { patientId: "polypharmacy-senior" });
    expect(json.procedure).toBe("Laparoscopic cholecystectomy");
    expect(String(json.say)).toMatch(/apixaban/);
  });

  it("get_case speaks revoked consent instead of erroring", async () => {
    const { status, json } = await call("POST", "/tools/get_case", { patientId: "patient-demo-consent-revoked" });
    expect(status).toBe(200);
    expect(String(json.say)).toMatch(/revoked consent/);
  });

  it("check_preop scores a learner's picks", async () => {
    const { json } = await call("POST", "/tools/check_preop", { patientId: "patient-demo-001", selected: ["allergy", "diabetes"] });
    expect(json).toMatchObject({ status: "passed", score: 2, total: 2 });
  });

  it("get_step walks the procedure with patient-specific notes", async () => {
    const { json } = await call("POST", "/tools/get_step", { patientId: "patient-demo-polypharmacy", stepId: "liver_bed" });
    expect(json.nextStepId).toBe("hemostasis");
    expect((json.patientNotes as string[]).join(" ")).toMatch(/Anticoagulant/);
    const first = await call("POST", "/tools/get_step", { patientId: "patient-demo-polypharmacy" });
    expect(first.json.stepId).toBe("access_umbilical");
  });
});
