import { existsSync } from "node:fs";
import path from "node:path";

// Each asset resolves from an env URL first (for hosted files: a public GitHub release asset, R2, etc.),
// then from a file dropped into /public. Empty means "not ready yet" and the page says so.
function resolve(envUrl: string | undefined, publicFile: string) {
  if (envUrl) return envUrl;
  return existsSync(path.join(process.cwd(), "public", publicFile)) ? `/${publicFile}` : "";
}

export const APK_URL = resolve(process.env.NEXT_PUBLIC_APK_URL, "scalpal.apk");
export const DEMO_VIDEO_URL = resolve(process.env.NEXT_PUBLIC_DEMO_VIDEO_URL, "demo.mp4");

