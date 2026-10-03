import { randomUUID } from "node:crypto";
import type { HealthRecord, Scenario } from "./types.js";

export const DEMO_BASE_URL = "https://api.finchnode.com/demo/v1";
export const SANDBOX_BASE_URL = "https://api.finchnode.com/api/v1";

// Categories the briefing reads; the sandbox application allowlist must include them.
export const SANDBOX_CATEGORIES = ["demographics", "medications", "conditions", "labs", "vitals", "allergies"];

export class FinchNodeError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
    readonly retryAfterSeconds = 0,
    readonly requestId = "",
  ) {
    super(message);
    this.name = "FinchNodeError";
  }
}

export interface ConnectSession {
  id: string;
  scenario: string;
  status: string;
  patient_id: string | null;
  failure_code: string | null;
  failure_message: string | null;
}

// Authenticated sandbox Connect session (api/v1). Only the fields Scalpal reads.
export interface SandboxSession {
  id: string;
  status: string;
  subject: string | null;
  organization: string | null;
  externalId: string | null;
  simulation: { scenario: string; state: string } | null;
  sync: { status: string; warnings?: { code: string; message: string }[] } | null;
}

export interface FinchNodeClient {
  getRecord(subject: string): Promise<HealthRecord>;
  listScenarios(): Promise<Scenario[]>;
  createConnectSession(scenario: string): Promise<ConnectSession>;
  // Sandbox Connect: present only when a ck_test_ key is configured.
  sandbox?: {
    admit(scenario: string): Promise<SandboxSession>;
    getSession(sessionId: string): Promise<SandboxSession>;
  };
}

export interface ClientOptions {
  baseUrl?: string;
  sandboxBaseUrl?: string;
  apiKey?: string;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
  recordTtlMs?: number;
  maxRetryWaitSeconds?: number;
}

// Demo subjects ("patient-demo-...") read from the keyless demo API. Any other subject is an
// app-scoped sandbox subject from a completed Connect session and reads from api/v1 with the key.
export const isDemoSubject = (subject: string) => subject.startsWith("patient-demo-");

export function createFinchNodeClient(options: ClientOptions = {}): FinchNodeClient {
  const demoBase = (options.baseUrl ?? DEMO_BASE_URL).replace(/\/$/, "");
  const sandboxBase = (options.sandboxBaseUrl ?? SANDBOX_BASE_URL).replace(/\/$/, "");
  const apiKey = options.apiKey ?? "";
  const fetchImpl = options.fetchImpl ?? fetch;
  const timeoutMs = options.timeoutMs ?? 8000;
  const ttl = options.recordTtlMs ?? 60_000;
  const maxRetryWait = options.maxRetryWaitSeconds ?? 5;
  const cache = new Map<string, { at: number; value: unknown }>();
  const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

  async function once(url: string, init: RequestInit | undefined, auth: boolean): Promise<{ res: Response; body: unknown }> {
    // Only standard headers on demo calls: its CORS policy rejects custom ones.
    const headers: Record<string, string> = { Accept: "application/json" };
    if (init?.body) headers["Content-Type"] = "application/json";
    if (auth) headers.Authorization = `Bearer ${apiKey}`;
    Object.assign(headers, init?.headers ?? {});
    let res: Response;
    try {
      res = await fetchImpl(url, { ...init, headers, signal: AbortSignal.timeout(timeoutMs) });
    } catch (err) {
      throw new FinchNodeError(502, "upstream_unreachable", `FinchNode did not respond: ${(err as Error).message}`);
    }
    return { res, body: await res.json().catch(() => null) };
  }

  async function request<T>(url: string, init?: RequestInit, cacheKey?: string, auth = false): Promise<T> {
    if (cacheKey) {
      const hit = cache.get(cacheKey);
      if (hit && Date.now() - hit.at < ttl) return hit.value as T;
    }
    let { res, body } = await once(url, init, auth);
    // FinchNode's contract: retry the same request after Retry-After. Reads only, and only for short waits.
    const retryAfter = Number(res.headers.get("retry-after") ?? 0) || 0;
    if (res.status === 429 && (init?.method ?? "GET") === "GET" && retryAfter <= maxRetryWait) {
      await sleep(Math.max(retryAfter, 1) * 1000);
      ({ res, body } = await once(url, init, auth));
    }
    const err = body as { error?: { code?: string; message?: string; requestId?: string } } | null;
    if (!res.ok) {
      throw new FinchNodeError(
        res.status,
        err?.error?.code ?? `http_${res.status}`,
        err?.error?.message ?? `FinchNode returned HTTP ${res.status}`,
        Number(res.headers.get("retry-after") ?? 0) || 0,
        err?.error?.requestId ?? res.headers.get("x-request-id") ?? "",
      );
    }
    if (body == null) throw new FinchNodeError(502, "invalid_response", "FinchNode returned a non-JSON body");
    if (cacheKey) cache.set(cacheKey, { at: Date.now(), value: body });
    return body as T;
  }

  const client: FinchNodeClient = {
    getRecord: (subject) => {
      const path = `/users/${encodeURIComponent(subject)}/records`;
      if (isDemoSubject(subject) || !apiKey) return request<HealthRecord>(`${demoBase}${path}`, undefined, `record:${subject}`);
      // Consent is re-checked on every sandbox read, so revocation shows up within one cache window.
      return request<HealthRecord>(`${sandboxBase}${path}?categories=${SANDBOX_CATEGORIES.join(",")}`, undefined, `record:${subject}`, true);
    },
    listScenarios: async () => (await request<{ data: Scenario[] }>(`${demoBase}/scenarios`, undefined, "scenarios")).data,
    createConnectSession: (scenario) =>
      request<ConnectSession>(`${demoBase}/connect/sessions`, { method: "POST", body: JSON.stringify({ scenario }) }),
  };

  if (apiKey) {
    client.sandbox = {
      async admit(scenario) {
        const session = await request<SandboxSession>(
          `${sandboxBase}/connect/sessions`,
          {
            method: "POST",
            headers: { "Idempotency-Key": randomUUID() },
            body: JSON.stringify({
              externalId: `scalpal-${scenario}-${randomUUID().slice(0, 8)}`,
              categories: SANDBOX_CATEGORIES,
              syncMode: "one-time",
              returnUrl: "https://scalpal.invalid/connect-return",
            }),
          },
          undefined,
          true,
        );
        return request<SandboxSession>(
          `${sandboxBase}/connect/sessions/${encodeURIComponent(session.id)}/simulate`,
          { method: "POST", body: JSON.stringify({ scenario }) },
          undefined,
          true,
        );
      },
      getSession: (sessionId) => request<SandboxSession>(`${sandboxBase}/connect/sessions/${encodeURIComponent(sessionId)}`, undefined, undefined, true),
    };
  }
  return client;
}
