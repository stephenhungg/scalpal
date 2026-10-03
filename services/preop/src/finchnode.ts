import type { HealthRecord, Scenario } from "./types.js";

export const DEMO_BASE_URL = "https://api.finchnode.com/demo/v1";

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

export interface FinchNodeClient {
  getRecord(subject: string): Promise<HealthRecord>;
  listScenarios(): Promise<Scenario[]>;
  createConnectSession(scenario: string): Promise<ConnectSession>;
}

export interface ClientOptions {
  baseUrl?: string;
  fetchImpl?: typeof fetch;
  timeoutMs?: number;
  recordTtlMs?: number;
}

// Thin client over the public demo API. No key exists or is needed for synthetic data.
// Only standard headers are sent: the demo's CORS policy rejects custom ones.
export function createFinchNodeClient(options: ClientOptions = {}): FinchNodeClient {
  const baseUrl = (options.baseUrl ?? DEMO_BASE_URL).replace(/\/$/, "");
  const fetchImpl = options.fetchImpl ?? fetch;
  const timeoutMs = options.timeoutMs ?? 8000;
  const ttl = options.recordTtlMs ?? 60_000;
  const cache = new Map<string, { at: number; value: unknown }>();

  async function request<T>(path: string, init?: RequestInit, cacheKey?: string): Promise<T> {
    if (cacheKey) {
      const hit = cache.get(cacheKey);
      if (hit && Date.now() - hit.at < ttl) return hit.value as T;
    }
    let res: Response;
    try {
      res = await fetchImpl(`${baseUrl}${path}`, {
        ...init,
        headers: { Accept: "application/json", ...(init?.body ? { "Content-Type": "application/json" } : {}) },
        signal: AbortSignal.timeout(timeoutMs),
      });
    } catch (err) {
      throw new FinchNodeError(502, "upstream_unreachable", `FinchNode did not respond: ${(err as Error).message}`);
    }
    const body = (await res.json().catch(() => null)) as { error?: { code?: string; message?: string; requestId?: string } } | null;
    if (!res.ok) {
      throw new FinchNodeError(
        res.status,
        body?.error?.code ?? `http_${res.status}`,
        body?.error?.message ?? `FinchNode returned HTTP ${res.status}`,
        Number(res.headers.get("retry-after") ?? 0) || 0,
        body?.error?.requestId ?? res.headers.get("x-request-id") ?? "",
      );
    }
    if (body == null) throw new FinchNodeError(502, "invalid_response", "FinchNode returned a non-JSON body");
    if (cacheKey) cache.set(cacheKey, { at: Date.now(), value: body });
    return body as T;
  }

  return {
    getRecord: (subject) => request<HealthRecord>(`/users/${encodeURIComponent(subject)}/records`, undefined, `record:${subject}`),
    listScenarios: async () => (await request<{ data: Scenario[] }>("/scenarios", undefined, "scenarios")).data,
    createConnectSession: (scenario) =>
      request<ConnectSession>("/connect/sessions", { method: "POST", body: JSON.stringify({ scenario }) }),
  };
}
