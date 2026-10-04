// One still frame from the configured camera, to check framing before spending live minutes.
// Uses ffmpeg (brew install ffmpeg) directly, so no Presage session and no quota.
import { execFile, execFileSync } from "node:child_process";

const WARMUP_SECONDS = 2; // the first frames from a camera (an iPhone especially) are black

function cameraName(id) {
  const cams = JSON.parse(execFileSync("system_profiler", ["SPCameraDataType", "-json"], { encoding: "utf8" })).SPCameraDataType ?? [];
  if (!cams.length) throw new Error("No cameras found");
  if (!id) return cams[0]._name;
  const cam = cams.find((c) => c["spcamera_unique-id"] === id);
  if (!cam) throw new Error(`Camera ${id} not found (is the phone connected?)`);
  return cam._name;
}

let inFlight = null;

/** @returns {Promise<Buffer>} a JPEG */
export function grabFrame(cameraId) {
  inFlight ??= new Promise((resolve, reject) => {
    const name = cameraName(cameraId);
    const args = ["-hide_banner", "-loglevel", "error", "-f", "avfoundation", "-framerate", "30", "-video_size", "1280x720", "-i", name, "-ss", String(WARMUP_SECONDS), "-frames:v", "1", "-f", "image2", "-c:v", "mjpeg", "pipe:1"];
    execFile("ffmpeg", args, { encoding: "buffer", timeout: 15_000, maxBuffer: 20 * 1024 * 1024 }, (err, stdout, stderr) => {
      if (err?.code === "ENOENT") return reject(new Error("ffmpeg not installed (brew install ffmpeg)"));
      if (err || !stdout.length) return reject(new Error(`Could not open ${name}: ${String(stderr).trim().split("\n").pop() || err?.message}`));
      resolve(stdout);
    });
  }).finally(() => (inFlight = null));
  return inFlight;
}

// CLI: npm run preview -> preview.jpg, opened in Preview
if (import.meta.url === `file://${process.argv[1]}`) {
  const { writeFileSync } = await import("node:fs");
  try {
    const jpg = await grabFrame(process.env.PRESAGE_CAMERA_ID?.trim());
    writeFileSync("preview.jpg", jpg);
    console.log("Saved preview.jpg. Check the face and upper chest are in frame and lit.");
    execFile("open", ["preview.jpg"]);
  } catch (err) {
    console.error(err.message);
    process.exitCode = 1;
  }
}
