import Anthropic from "@anthropic-ai/sdk";
import type { CoachSnapshot } from "./coach.js";

// Jarvis's eyes. A point-of-view frame (the laptop camera rig, or the Quest composite of passthrough plus
// virtual overlay) goes to a fast vision model together with labeled boxes the scene already knows and
// the live coach state. Two uses:
//   look:  the learner asks "what am I looking at / how do I approach this"; answer from the frame.
//   watch: every few seconds, a one-line summary of the view goes into Jarvis's context before he's asked.
// The labeled boxes come from the scene graph or a detector and are trusted over the model's own guesses:
// general vision models are weak at naming anatomy, so the model describes and reasons, it does not
// identify.

export interface FrameMark {
  label: string; // display name, e.g. "Cecum" or "scissors"
  id: string; // catalog id when known, else ""
  source: string; // "scene" (exact, from the virtual scene or body map) or "detector"
  box: { x: number; y: number; w: number; h: number }; // normalized 0..1, top-left origin
}

export interface Frame {
  jpegBase64: string;
  marks: FrameMark[];
  source: string; // "camera" | "quest"
  at: number; // ms epoch
}

export interface SceneVision {
  look(frame: Frame, snapshot: CoachSnapshot, question: string): Promise<string>;
  watch(frame: Frame, snapshot: CoachSnapshot): Promise<string>;
}

function where(box: FrameMark["box"]): string {
  const cx = box.x + box.w / 2;
  const cy = box.y + box.h / 2;
  const h = cx < 0.35 ? "left" : cx > 0.65 ? "right" : "center";
  const v = cy < 0.35 ? "top" : cy > 0.65 ? "bottom" : "middle";
  return v === "middle" && h === "center" ? "center" : `${v} ${h}`;
}

export function describeMarks(marks: FrameMark[]): string {
  if (!marks.length) return "No labeled objects were reported for this frame.";
  return marks
    .slice(0, 30)
    .map((m) => `- ${m.label}${m.id && m.id !== m.label ? ` [${m.id}]` : ""} at the ${where(m.box)} (${m.source === "scene" ? "exact" : "detected"}; box x=${m.box.x.toFixed(2)} y=${m.box.y.toFixed(2)} w=${m.box.w.toFixed(2)} h=${m.box.h.toFixed(2)})`)
    .join("\n");
}

function stateLine(s: CoachSnapshot): string {
  if (s.status === "completed") return `The ${s.procedureTitle} is complete.`;
  const st = s.step;
  return [
    `Procedure: ${s.procedureTitle}. Step ${s.stepNumber} of ${s.stepCount}: ${st.title}. ${st.instruction}`,
    `Instrument for this step: ${st.instrumentName}. Still needed: ${st.remaining.join(", ") || "nothing"}.`,
    st.dangers.length ? `Danger structures this step: ${st.dangers.map((d) => d.name).join(", ")}.` : "",
    s.focusStructure.id ? `The learner's finger or tool is over: ${s.focusStructure.name}.` : "",
    s.activeBleeds.length ? `Active bleeding from: ${s.activeBleeds.map((b) => b.structure.name).join(", ")}.` : "",
  ]
    .filter(Boolean)
    .join("\n");
}

const LOOK_SYSTEM = `You are the eyes of Jarvis, a surgical coach in a teaching simulator. The image is the learner's point of view: a camera view, possibly with virtual anatomy and instruments drawn over it. The labeled objects list comes from the simulator and an object detector; trust those labels and positions over your own identification, and never name an anatomical structure that is not in that list. Answer the learner's question in at most 50 words of plain spoken English, using left, right, top, bottom, and nearby landmarks so they can find things. If the image does not show what they need, say what to move or look at instead. If the image plainly does not show a body, a surgical field, or the overlay the labels describe, say you cannot see the field clearly and what to point the camera at; do not describe structures from the labels alone. No lists or markdown.`;

const WATCH_SYSTEM = `You summarize a surgical training simulator's point-of-view frame for a voice coach who cannot see it. Using the labeled objects list (trusted) and the image, write one sentence of at most 25 words: what is in view and any problem a coach should know (target out of frame, hand blocking the view, wrong area, instrument far from the target). Never name anatomy that is not in the list. No preamble.`;

export class ClaudeSceneVision implements SceneVision {
  constructor(
    private readonly client: Anthropic = new Anthropic({ timeout: 10_000, maxRetries: 1 }),
    private readonly model = "claude-haiku-4-5",
  ) {}

  private async ask(system: string, frame: Frame, text: string, maxTokens: number): Promise<string> {
    try {
      const response = await this.client.messages.create({
        model: this.model,
        max_tokens: maxTokens,
        system,
        messages: [
          {
            role: "user",
            content: [
              { type: "image", source: { type: "base64", media_type: "image/jpeg", data: frame.jpegBase64 } },
              { type: "text", text },
            ],
          },
        ],
      });
      const out = response.content.map((b) => (b.type === "text" ? b.text : "")).join(" ").trim();
      return out || "I can't make out the view right now.";
    } catch (error) {
      if (error instanceof Anthropic.AuthenticationError) return "My vision isn't configured yet.";
      if (error instanceof Anthropic.RateLimitError) return "I can't look right now; give me a moment.";
      if (error instanceof Anthropic.APIError) return "I couldn't get a clear look at that.";
      throw error;
    }
  }

  look(frame: Frame, snapshot: CoachSnapshot, question: string): Promise<string> {
    const text = `Labeled objects in this frame:\n${describeMarks(frame.marks)}\n\nSimulator state:\n${stateLine(snapshot)}\n\nThe learner asks: "${question || "What am I looking at, and how do I approach this?"}"`;
    return this.ask(LOOK_SYSTEM, frame, text, 300);
  }

  watch(frame: Frame, snapshot: CoachSnapshot): Promise<string> {
    const text = `Labeled objects in this frame:\n${describeMarks(frame.marks)}\n\nSimulator state:\n${stateLine(snapshot)}`;
    return this.ask(WATCH_SYSTEM, frame, text, 120);
  }
}

export function sceneVisionFromEnv(env: NodeJS.ProcessEnv): SceneVision | null {
  if (!env.ANTHROPIC_API_KEY) return null;
  return new ClaudeSceneVision(new Anthropic({ timeout: 10_000, maxRetries: 1 }), env.JARVIS_VISION_MODEL || "claude-haiku-4-5");
}
