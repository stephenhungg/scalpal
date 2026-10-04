// Writes the offline bundle Unity loads from Resources when the service is unreachable.
// Run: npm run export:unity            (live FinchNode demo API)
//      npm run export:unity -- --offline   (recorded fixtures, no network)
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { createApp } from "../src/app.js";
import { unitySafetyErrors } from "../src/unity-safe.js";
import { fixtureClient } from "../test/helpers.js";
import { UNITY_EXERCISES_DIR } from "./gen-unity.js";
import { withOfflineAdvancedCase } from "./offline-advanced-case.js";
import type { SurgicalCase } from "../src/types.js";

const OUT = join(UNITY_EXERCISES_DIR, "Resources/scalpal_bundle.json");
const offline = process.argv.includes("--offline");

const app = createApp(offline ? { client: fixtureClient() } : {});
const res = await app.request("/unity/bundle");
if (!res.ok) throw new Error(`bundle request failed: HTTP ${res.status}`);
const bundle = withOfflineAdvancedCase((await res.json()) as { cases: SurgicalCase[] });

const errors = unitySafetyErrors(bundle);
if (errors.length) throw new Error(`bundle is not JsonUtility-safe:\n${errors.slice(0, 20).join("\n")}`);
const playable = bundle.cases.filter((c) => c.status === "ready" || c.status === "needs_review").length;
if (playable < 6) throw new Error(`only ${playable} playable cases; refusing to ship a thin bundle`);

mkdirSync(dirname(OUT), { recursive: true });
writeFileSync(OUT, `${JSON.stringify(bundle, null, 1)}\n`);
console.log(`wrote ${OUT} (${bundle.cases.length} cases, ${playable} playable, source: ${offline ? "fixtures" : "live FinchNode demo API"})`);
