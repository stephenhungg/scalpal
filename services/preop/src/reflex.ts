import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

// Pre-rendered warning audio in Scalpal's voice. Safety warnings bypass the LLM: the page plays the clip
// immediately (tens of ms) instead of waiting seconds for a generated turn. Clips are rendered once per
// (voice, model, text) with ElevenLabs TTS and cached on disk, so restarts and repeat demos cost nothing.

export interface ReflexOptions {
  apiKey: string;
  voiceId: string;
  modelId?: string;
  cacheDir?: string;
  fetchImpl?: typeof fetch;
}

export class ReflexAudio {
  private readonly modelId: string;
  private readonly cacheDir: string;
  private readonly fetchImpl: typeof fetch;
  private readonly inflight = new Map<string, Promise<Buffer>>();

  constructor(private readonly options: ReflexOptions) {
    this.modelId = options.modelId ?? "eleven_flash_v2_5";
    this.cacheDir = options.cacheDir ?? join(process.cwd(), ".cache", "reflex");
    this.fetchImpl = options.fetchImpl ?? fetch;
  }

  get configured() {
    return Boolean(this.options.apiKey && this.options.voiceId);
  }

  private file(text: string) {
    const hash = createHash("sha1").update(`${this.options.voiceId}|${this.modelId}|${text}`).digest("hex").slice(0, 20);
    return join(this.cacheDir, `${hash}.mp3`);
  }

  async render(text: string): Promise<Buffer> {
    const file = this.file(text);
    if (existsSync(file)) return readFileSync(file);
    const pending = this.inflight.get(file);
    if (pending) return pending;
    const job = (async () => {
      const res = await this.fetchImpl(`https://api.elevenlabs.io/v1/text-to-speech/${encodeURIComponent(this.options.voiceId)}?output_format=mp3_44100_128`, {
        method: "POST",
        headers: { "xi-api-key": this.options.apiKey, "Content-Type": "application/json", Accept: "audio/mpeg" },
        body: JSON.stringify({ text, model_id: this.modelId }),
      });
      if (!res.ok) throw new Error(`ElevenLabs TTS ${res.status}: ${(await res.text()).slice(0, 200)}`);
      const audio = Buffer.from(await res.arrayBuffer());
      mkdirSync(this.cacheDir, { recursive: true });
      writeFileSync(file, audio);
      return audio;
    })().finally(() => this.inflight.delete(file));
    this.inflight.set(file, job);
    return job;
  }
}
