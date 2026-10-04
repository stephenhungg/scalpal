import Anthropic from "@anthropic-ai/sdk";
import type { ChoiceKey } from "./interview-types.js";

// Turns a spoken answer into one of the four interview choices. Saying the letter is matched directly;
// anything else ("I'd ask where it hurts now") goes to a fast model that must return one letter or none.
// Speech-to-text runs server side on ElevenLabs so the laptop page and the Quest share one path.

export interface AnswerClassifier {
  classify(heard: string, choices: { key: ChoiceKey; text: string }[]): Promise<ChoiceKey | null>;
}

export interface SpeechToText {
  transcribe(audio: Uint8Array, mimeType: string): Promise<string>;
}

const KEYS: ChoiceKey[] = ["A", "B", "C", "D"];
const ORDINALS: Record<string, ChoiceKey> = { first: "A", second: "B", third: "C", fourth: "D", last: "D" };

// "B", "option b", "answer C", "I'll go with d", "d as in dog", "the second one"; null when the words do
// not name exactly one letter (a hedge between two, a negation, or a correction goes to the model or a re-ask).
export function letterFrom(heard: string): ChoiceKey | null {
  const t = heard.trim().toLowerCase().replace(/[.!?,]/g, " ").replace(/\s+/g, " ").trim();
  const bare = t.match(/^(?:(?:option|answer|choice|letter|go with|i choose|i pick|pick|i'll go with|i'll pick)\s+)?([abcd])(?: as in [a-z]+)?(?: please)?$/);
  if (bare) return bare[1]!.toUpperCase() as ChoiceKey;
  if (/\b(?:not|no wait|wait|actually|or|and)\b/.test(t)) return null;
  const named = [...t.matchAll(/\b(?:option|answer|choice|letter)\s+([abcd])\b/g)].map((m) => m[1]!);
  if (new Set(named).size === 1) return named[0]!.toUpperCase() as ChoiceKey;
  if (named.length) return null;
  const ord = [...t.matchAll(/\bthe (first|second|third|fourth|last) (?:one|option|answer|choice)\b/g)].map((m) => m[1]!);
  if (new Set(ord).size === 1) return ORDINALS[ord[0]!] ?? null;
  return null;
}

const STOP = new Set("a an and are as ask at be but by can could did do does for from had has have he her his how i if in is it its me my of on or our she so that the their them they this to was we were what when where whether which who why will with would you your about any".split(" "));
const stem = (w: string) => w.replace(/(?:ing|ed|es|s)$/, "");
const words = (t: string) => new Set(t.toLowerCase().match(/[a-z]+/g)?.filter((w) => w.length > 2 && !STOP.has(w)).map(stem) ?? []);

// No-model fallback for a paraphrased move ("has your stomach pain moved?"): the choice sharing the most
// content words, only when it shares at least two and clearly beats every other choice.
export function wordMatch(heard: string, choices: { key: ChoiceKey; text: string }[]): ChoiceKey | null {
  const said = words(heard);
  const scored = choices.map((c) => ({ key: c.key, n: [...words(c.text)].filter((w) => said.has(w)).length })).sort((a, b) => b.n - a.n);
  const [best, next] = scored;
  return best && best.n >= 2 && best.n > (next?.n ?? 0) ? best.key : null;
}

export class ClaudeAnswerClassifier implements AnswerClassifier {
  constructor(
    private readonly client: Anthropic = new Anthropic({ timeout: 8000, maxRetries: 1 }),
    private readonly model = "claude-haiku-4-5",
  ) {}

  async classify(heard: string, choices: { key: ChoiceKey; text: string }[]): Promise<ChoiceKey | null> {
    const direct = letterFrom(heard);
    if (direct) return direct;
    if (!heard.trim()) return null;
    try {
      const res = await this.client.messages.create({
        model: this.model,
        max_tokens: 5,
        system:
          "A medical student is answering a multiple-choice question out loud. Reply with only the letter of the option their words match (A, B, C or D), or NONE if their words do not clearly match exactly one option. Do not judge whether the answer is medically right.",
        messages: [{ role: "user", content: `Options:\n${choices.map((c) => `${c.key}) ${c.text}`).join("\n")}\n\nThe student said: "${heard.slice(0, 500)}"` }],
      });
      const out = res.content.map((b) => (b.type === "text" ? b.text : "")).join("").trim().toUpperCase();
      const key = out.match(/^[ABCD]\b/)?.[0] as ChoiceKey | undefined;
      return key && KEYS.includes(key) ? key : null; // null: the model judged the answer unclear
    } catch (e) {
      // Unreachable or failing model: let the caller fall back instead of treating it as unclear.
      throw new Error(`answer classifier unavailable: ${(e as Error).message}`);
    }
  }
}

export class ElevenLabsSpeechToText implements SpeechToText {
  constructor(private readonly apiKey: string) {}

  async transcribe(audio: Uint8Array, mimeType: string): Promise<string> {
    const form = new FormData();
    form.append("model_id", "scribe_v1");
    form.append("file", new Blob([new Uint8Array(audio)], { type: mimeType || "audio/webm" }), "answer.webm");
    const res = await fetch("https://api.elevenlabs.io/v1/speech-to-text", {
      method: "POST",
      headers: { "xi-api-key": this.apiKey },
      body: form,
      signal: AbortSignal.timeout(15000),
    });
    if (!res.ok) throw new Error(`speech-to-text failed (${res.status})`);
    return ((await res.json()) as { text?: string }).text?.trim() ?? "";
  }
}
