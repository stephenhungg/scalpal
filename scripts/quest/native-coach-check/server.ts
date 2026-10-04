// Isolated actual production routes with recorded synthetic health records, no provider configuration.
import { createRequire } from "node:module";
import { createApp } from "../../../services/preop/src/app.js";
import { fixtureClient, NOW } from "../../../services/preop/test/helpers.js";
import { legacyAppendectomyApp } from "../../../services/preop/test/legacy-appendectomy-fixture.js";

const require = createRequire(new URL("../../../services/preop/package.json", import.meta.url));
const { serve } = require("@hono/node-server");
// An accidental provider/network fetch should fail this fixture rather than touch a real account.
globalThis.fetch = async () => { throw new Error("External fetch is disabled in the isolated coach fixture"); };
// Deterministic spoken-answer stand-ins: the headset's WAV must be a valid mono PCM16 RIFF file; a silent
// recording "says" something that matches no choice (422 unclear_answer), a voiced one says "option b".
const speechToText = {
  async transcribe(audio: Uint8Array, mimeType: string): Promise<string> {
    const b = Buffer.from(audio);
    if (mimeType !== "audio/wav" || b.length < 46 || b.toString("ascii", 0, 4) !== "RIFF" || b.toString("ascii", 8, 12) !== "WAVE"
      || b.readUInt16LE(20) !== 1 || b.readUInt16LE(22) !== 1 || b.readUInt16LE(34) !== 16 || b.toString("ascii", 36, 40) !== "data"
      || b.readUInt32LE(40) !== b.length - 44 || b.readUInt32LE(4) !== b.length - 8) throw new Error("not a mono PCM16 WAV");
    let energy = 0;
    for (let i = 44; i + 1 < b.length; i += 2) energy += Math.abs(b.readInt16LE(i));
    return energy / ((b.length - 44) / 2) > 500 ? "option b" : "um I'm not sure";
  },
};
const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, speechToText, answerClassifier: { classify: async () => null } });
// Explicit advanced mechanics fixture; normal patient routes retain the open case.
app.route("/advanced", legacyAppendectomyApp());
const server = serve({ fetch: app.fetch, hostname: "127.0.0.1", port: 0 }, (address: { port: number }) => {
  console.log(`SCALPAL_COACH_TEST_ENDPOINT=http://127.0.0.1:${address.port}`);
});
for (const signal of ["SIGTERM", "SIGINT"] as const) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
