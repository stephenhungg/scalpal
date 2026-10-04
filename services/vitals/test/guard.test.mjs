// The quota guard, exercised through the real server with a fake live source (no Presage quota).
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

const server = new URL("../src/server.mjs", import.meta.url).pathname;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function run(env, port) {
  const child = spawn(process.execPath, [server], {
    env: { ...process.env, PRESAGE_API_KEY: "test-key", PRESAGE_FAKE_LIVE: "1", PORT: String(port), ...env },
    stdio: ["ignore", "pipe", "pipe"],
  });
  let log = "";
  child.stdout.on("data", (d) => (log += d));
  child.stderr.on("data", (d) => (log += d));
  return { child, log: () => log };
}
const get = async (port, path) => (await fetch(`http://localhost:${port}${path}`)).json();
async function ready(port) {
  for (let i = 0; i < 50; i++) {
    try { return await get(port, "/health"); } catch { await sleep(100); }
  }
  throw new Error("server did not start");
}

test("without PRESAGE_LIVE the key alone stays in demo mode", async () => {
  const dir = mkdtempSync(join(tmpdir(), "vitals-"));
  const usage = join(dir, "usage.json");
  const s = run({ PRESAGE_USAGE_FILE: usage }, 18901);
  try {
    assert.equal((await ready(18901)).mode, "demo");
    await sleep(1500);
    assert.throws(() => readFileSync(usage), "demo mode must not write the live ledger");
  } finally {
    s.child.kill("SIGTERM");
  }
});

test("live session auto-stops at the limit and logs the time", async () => {
  const dir = mkdtempSync(join(tmpdir(), "vitals-"));
  const usage = join(dir, "usage.json");
  const s = run({ PRESAGE_LIVE: "1", PRESAGE_LIVE_MINUTES: "0.1", PRESAGE_USAGE_FILE: usage }, 18902); // 6 s
  try {
    assert.equal((await ready(18902)).mode, "live");
    await sleep(1500);
    assert.ok((await get(18902, "/vitals")).pulse, "fake live source produces readings");
    await sleep(6000);
    const v = await get(18902, "/vitals");
    assert.match(v.status.reason, /Live session ended \(time limit\)/);
    assert.equal(v.pulse, null, "no readings after the session ends");
    const used = JSON.parse(readFileSync(usage, "utf8")).usedSeconds;
    assert.ok(used >= 5.5 && used <= 7.5, `logged ${used}s for a 6 s session`);
  } finally {
    s.child.kill("SIGTERM");
  }
});

test("stopping early still logs the time", async () => {
  const dir = mkdtempSync(join(tmpdir(), "vitals-"));
  const usage = join(dir, "usage.json");
  const s = run({ PRESAGE_LIVE: "1", PRESAGE_LIVE_MINUTES: "5", PRESAGE_USAGE_FILE: usage }, 18903);
  await ready(18903);
  await sleep(3000);
  const exited = new Promise((r) => s.child.once("exit", r));
  s.child.kill("SIGINT");
  await exited;
  const used = JSON.parse(readFileSync(usage, "utf8")).usedSeconds;
  assert.ok(used >= 2.5 && used <= 5, `logged ${used}s after a ~3 s session`);
});

test("live mode is refused once the budget is used up", async () => {
  const dir = mkdtempSync(join(tmpdir(), "vitals-"));
  const usage = join(dir, "usage.json");
  writeFileSync(usage, JSON.stringify({ usedSeconds: 50 * 60, sessions: [] }));
  const s = run({ PRESAGE_LIVE: "1", PRESAGE_USAGE_FILE: usage, PRESAGE_BUDGET_MINUTES: "50" }, 18904);
  try {
    assert.equal((await ready(18904)).mode, "demo");
    assert.match(s.log(), /budget used up/);
  } finally {
    s.child.kill("SIGTERM");
  }
});

test("a session never runs past the remaining budget", async () => {
  const dir = mkdtempSync(join(tmpdir(), "vitals-"));
  const usage = join(dir, "usage.json");
  writeFileSync(usage, JSON.stringify({ usedSeconds: 50 * 60 - 18, sessions: [] })); // 18 s left
  const s = run({ PRESAGE_LIVE: "1", PRESAGE_LIVE_MINUTES: "5", PRESAGE_USAGE_FILE: usage }, 18905);
  try {
    assert.equal((await ready(18905)).mode, "live");
    assert.match(s.log(), /stops after 0\.3 min/);
  } finally {
    s.child.kill("SIGTERM");
  }
});

test("a live session with no face in view ends itself", async () => {
  const dir = mkdtempSync(join(tmpdir(), "vitals-"));
  const usage = join(dir, "usage.json");
  const s = run({ PRESAGE_LIVE: "1", PRESAGE_FAKE_LIVE: "noface", PRESAGE_NO_FACE_SECONDS: "2", PRESAGE_USAGE_FILE: usage }, 18906);
  try {
    await ready(18906);
    await sleep(3500);
    assert.match((await get(18906, "/vitals")).status.reason, /Live session ended \(no face for 2 s\)/);
    const used = JSON.parse(readFileSync(usage, "utf8")).usedSeconds;
    assert.ok(used >= 1.5 && used <= 3.5, `logged ${used}s, stopped at ~2 s`);
  } finally {
    s.child.kill("SIGTERM");
  }
});

test("a camera that can't be opened fails cleanly without spending quota", async () => {
  const dir = mkdtempSync(join(tmpdir(), "vitals-"));
  const usage = join(dir, "usage.json");
  const s = run({ PRESAGE_LIVE: "1", PRESAGE_FAKE_LIVE: "fail", PRESAGE_USAGE_FILE: usage }, 18907);
  try {
    await ready(18907);
    const v = await get(18907, "/vitals");
    assert.match(v.status.reason, /could not start: SmartSpectra input is unavailable/);
    assert.equal(s.child.exitCode, null, "the server keeps running");
    const used = JSON.parse(readFileSync(usage, "utf8")).usedSeconds;
    assert.ok(used < 0.5, `logged ${used}s`);
  } finally {
    s.child.kill("SIGTERM");
  }
});

test("a reading below the stable threshold is held, with its age, then dropped", async () => {
  const dir = mkdtempSync(join(tmpdir(), "vitals-"));
  const s = run({ PRESAGE_LIVE: "1", PRESAGE_FAKE_LIVE: "dip", PRESAGE_HOLD_SECONDS: "2", PRESAGE_USAGE_FILE: join(dir, "usage.json") }, 18908);
  try {
    await ready(18908);
    await sleep(1800); // stable until 1 s, held after
    const held = await get(18908, "/vitals");
    assert.ok(held.pulse && held.pulse.heldMs > 0, `held: ${JSON.stringify(held.pulse)}`);
    await sleep(2500);
    assert.equal((await get(18908, "/vitals")).pulse, null, "dropped after the hold");
  } finally {
    s.child.kill("SIGTERM");
  }
});
