// Production preop routes over recorded synthetic data, plus one explicit legacy HTTP429
// compatibility response. No provider/network calls or user database are used by this fixture.
import { createRequire } from "node:module";
import { createApp } from "../../../services/preop/src/app.js";
import { fixtureClient, NOW } from "../../../services/preop/test/helpers.js";

const require = createRequire(new URL("../../../services/preop/package.json", import.meta.url));
const { serve } = require("@hono/node-server");
globalThis.fetch = async () => { throw new Error("External fetch is disabled in the isolated shell fixture"); };
const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
const server = serve({
  fetch: (request: Request) => {
    if (new URL(request.url).pathname === "/patients/patient-validation-legacy429/case") {
      return Response.json({
        error: { code: "rate_limited", message: "Controlled legacy case endpoint rate limit." },
        actions: [{ id: "patients", label: "Choose a patient", method: "GET", route: "/patients" }],
      }, { status: 429, headers: { "Retry-After": "3" } });
    }
    return app.fetch(request);
  }, hostname: "127.0.0.1", port: 0,
}, (address: { port: number }) => console.log(`SCALPAL_SHELL_TEST_ENDPOINT=http://127.0.0.1:${address.port}`));
for (const signal of ["SIGTERM", "SIGINT"] as const) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
