import type { FrameMark } from "./scene-vision.js";

// Real-camera object boxes from services/vision (OWLv2, about 1 frame/s on the laptop). These only name
// instruments and hands in a real camera image; virtual anatomy boxes still come from the scene.
export interface FrameDetector {
  detect(jpegBase64: string, labels: string[]): Promise<FrameMark[]>;
}

interface Detection {
  label: string;
  score: number;
  id: string | null;
  box: { x: number; y: number; w: number; h: number };
}

export class HttpFrameDetector implements FrameDetector {
  constructor(
    private readonly baseUrl: string,
    private readonly timeoutMs = 4000,
  ) {}

  async detect(jpegBase64: string, labels: string[]): Promise<FrameMark[]> {
    const res = await fetch(`${this.baseUrl.replace(/\/$/, "")}/detect`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ image: jpegBase64, labels, threshold: 0.25 }),
      signal: AbortSignal.timeout(this.timeoutMs),
    });
    if (!res.ok) throw new Error(`vision detect failed (${res.status})`);
    const { detections } = (await res.json()) as { detections: Detection[] };
    return detections.slice(0, 12).map((d) => ({ label: d.label, id: d.id ?? "", source: "detector", box: d.box }));
  }
}

export function frameDetectorFromEnv(env: NodeJS.ProcessEnv): FrameDetector | null {
  return env.VISION_DETECT_URL ? new HttpFrameDetector(env.VISION_DETECT_URL) : null;
}
