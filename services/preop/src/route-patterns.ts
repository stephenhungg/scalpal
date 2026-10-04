// Every route the service can hand a client in an `actions` list. The Unity router is generated from
// this list (npm run gen:unity), and the route crawl test fails if an action matches none of them.
export const ROUTE_PATTERNS = [
  { kind: "Index", method: "GET", pattern: "^/$" },
  { kind: "Health", method: "GET", pattern: "^/health$" },
  { kind: "Patients", method: "GET", pattern: "^/patients$" },
  { kind: "Case", method: "GET", pattern: "^/patients/[a-z0-9_-]+/case$" },
  { kind: "Brief", method: "GET", pattern: "^/patients/[a-z0-9_-]+/brief$" },
  { kind: "PreopCheck", method: "POST", pattern: "^/patients/[a-z0-9_-]+/preop-check$" },
  { kind: "Procedures", method: "GET", pattern: "^/procedures$" },
  { kind: "Procedure", method: "GET", pattern: "^/procedures/[a-z0-9_]+$" },
  { kind: "Anatomy", method: "GET", pattern: "^/anatomy$" },
  { kind: "Instruments", method: "GET", pattern: "^/instruments$" },
  { kind: "Bundle", method: "GET", pattern: "^/unity/bundle$" },
  { kind: "Connect", method: "POST", pattern: "^/connect/[a-z0-9-]+$" },
  { kind: "Admit", method: "POST", pattern: "^/admit/[a-z0-9-]+$" },
  { kind: "Admission", method: "GET", pattern: "^/admissions/cs_[a-z0-9]+$" },
] as const;

export type RouteKind = (typeof ROUTE_PATTERNS)[number]["kind"];

export function matchRoute(method: string, route: string): RouteKind | null {
  return ROUTE_PATTERNS.find((r) => r.method === method && new RegExp(r.pattern).test(route))?.kind ?? null;
}
