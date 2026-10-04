// Creates a shared SpacetimeDB session for a demo, prints every role's invite code, and joins this
// coach service (Scalpal) to it. Prereqs: SpacetimeDB running with the module published, and the coach
// service running with SPACETIMEDB_URI. Run: npm run demo:session
import { DbConnection, tables } from "../src/module_bindings/index.js";

const URI = process.env.SPACETIMEDB_URI ?? "ws://127.0.0.1:3000";
const DB = process.env.SPACETIMEDB_DB ?? "scalpal";
const PREOP = process.env.PREOP_URL ?? "http://localhost:8787";
const wait = (n: number) => new Promise((r) => setTimeout(r, n));

const conn = await new Promise<DbConnection>((resolve, reject) => {
  DbConnection.builder()
    .withUri(URI)
    .withDatabaseName(DB)
    .onConnect((c) => c.subscriptionBuilder().onApplied(() => resolve(c)).subscribe([tables.sessionInvites] as never))
    .onConnectError((_c, err) => reject(new Error(`SpacetimeDB at ${URI}: ${String(err)}`)))
    .build();
});

const sessionId = `ses_demo_${Date.now().toString(36)}`;
await conn.reducers.createSession({ sessionId, label: "Scalpal demo", exerciseId: "open_appendectomy", exerciseVersion: "1", displayName: "Demo setup" });
let invites: { role: string; code: string }[] = [];
for (let i = 0; i < 100 && invites.length < 4; i++) {
  invites = [...conn.db.sessionInvites.iter()].filter((x) => x.sessionId === sessionId).map((x) => ({ role: x.role, code: x.code }));
  await wait(30);
}
const code = (role: string) => invites.find((x) => x.role === role)?.code ?? "?";

const res = await fetch(`${PREOP}/realtime/join`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code: code("coach") }) });
const joined = (await res.json()) as { sessionId?: string; error?: { message: string } };

console.log(`
Session ${sessionId}
  operator (dashboard, you)  ${code("operator")}
  headset  (Quest)           ${code("headset")}
  viewer   (judges)          ${code("viewer")}
  coach    (Scalpal)          ${code("coach")}  ${joined.sessionId === sessionId ? "joined" : `NOT joined: ${joined.error?.message ?? res.status}`}
`);
conn.disconnect();
process.exit(joined.sessionId === sessionId ? 0 : 1);
