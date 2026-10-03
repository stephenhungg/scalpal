// Isolated actual production routes with recorded synthetic health records, no provider configuration.
import { createRequire } from "node:module";
import { createApp } from "../../../services/preop/src/app.js";
import { fixtureClient, NOW } from "../../../services/preop/test/helpers.js";

const require = createRequire(new URL("../../../services/preop/package.json", import.meta.url));
const { serve } = require("@hono/node-server");
// An accidental provider/network fetch should fail this fixture rather than touch a real account.
globalThis.fetch = async () => { throw new Error("External fetch is disabled in the isolated coach fixture"); };
const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
const server = serve({ fetch: app.fetch, hostname: "127.0.0.1", port: 0 }, (address: { port: number }) => {
  console.log(`SCALPAL_COACH_TEST_ENDPOINT=http://127.0.0.1:${address.port}`);
});
for (const signal of ["SIGTERM", "SIGINT"] as const) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
