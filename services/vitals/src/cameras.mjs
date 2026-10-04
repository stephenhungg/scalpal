// Lists cameras macOS can see (including an iPhone via Continuity Camera) with the unique ID to
// put in PRESAGE_CAMERA_ID.
import { execFileSync } from "node:child_process";

const out = execFileSync("system_profiler", ["SPCameraDataType", "-json"], { encoding: "utf8" });
const cams = JSON.parse(out).SPCameraDataType ?? [];
if (!cams.length) console.log("No cameras found.");
for (const c of cams) console.log(`${c._name}\n  PRESAGE_CAMERA_ID=${c["spcamera_unique-id"] ?? "(no id)"}\n`);
