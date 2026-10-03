# Keeping a Realtime Voice Agent Caught Up With Live State

Research snapshot: October 3, 2026, for Jarvis (ElevenLabs Agents, Claude Sonnet 5.5, deterministic coach engine). **Doc** marks a claim from a primary source fetched for this note; **inference** marks our recommendation.

## What real deployments do

Every safety-relevant system we found keeps the alarm path deterministic and fast, and uses a generative model only to explain on top of structured state.

- **Medical devices.** GI Genius (FDA De Novo DEN200055) overlays a box and plays an audible alert within about 120 ms, with no language model in the loop. Doc: [FDA review](https://www.accessdata.fda.gov/cdrh_docs/reviews/DEN200055.pdf).
- **Live surgical commentary research.** SurgOnAir emits explicit state-transition tokens and speaks at transitions instead of narrating continuously. Doc: [arXiv 2605.21132](https://arxiv.org/abs/2605.21132).
- **Cockpits.** FAA AC 25.1322-1 defines warning, caution, and advisory tiers, presented most urgent first. Warnings and cautions get distinct aural tones and are acknowledged then suppressed; advisories get no sound. Doc: [AC 25.1322-1](https://www.faa.gov/documentLibrary/media/Advisory_Circular/AC_25.1322-1.pdf).
- **Games.** NVIDIA ACE turns game state into text for small models on sub-second cycles. Doc: [NVIDIA ACE](https://www.nvidia.com/en-us/geforce/news/nvidia-ace-autonomous-ai-companions-pubg-naraka-bladepoint/).
- **Ambient clinical AI** (Abridge, DAX) is asynchronous, not realtime coaching. Its relevant idea is grounding: each generated line links to source evidence. Doc: [Abridge Linked Evidence](https://support.abridge.com/hc/en-us/articles/30235128433811-Verify-a-Note-With-Linked-Evidence).

## Primitives in the voice stacks

| Stack | Silent state update | Make the agent speak | Context control |
| --- | --- | --- | --- |
| ElevenLabs Agents | `contextual_update` ("does not interrupt the current conversation flow") | No dedicated primitive; `user_message` triggers a normal turn | `context_usage` event per turn (SDK `onContextUsage`); full control only via a Custom LLM server |
| OpenAI Realtime | `conversation.item.create` without `response.create` | `response.create` with per-response `instructions`, or out-of-band with `conversation: "none"` | `conversation.item.delete`, truncation with `retention_ratio`, summarize-and-delete cookbook |
| Gemini Live | `send_client_content` with `turn_complete=False` | a turn with `turn_complete=True` | `contextWindowCompression` sliding window, session resumption |
| Pipecat | `LLMMessagesAppendFrame` with `run_llm=False` | `LLMRunFrame`; `TTSSpeakFrame` bypasses the LLM entirely | `LLMMessagesUpdateFrame` replaces context |
| LiveKit Agents | chat context update | `session.generate_reply(instructions=...)`; `session.say()` plays scripted audio | history auto-truncated to what the user heard |

Sources: [ElevenLabs client events](https://elevenlabs.io/docs/agents-platform/customization/events/client-to-server-events), [ElevenLabs server events](https://elevenlabs.io/docs/eleven-agents/customization/events/client-events), [ElevenLabs context usage changelog](https://elevenlabs.io/docs/changelog/2026/8/24), [ElevenLabs custom LLM](https://elevenlabs.io/docs/eleven-agents/customization/llm/custom-llm), [OpenAI Realtime conversations](https://developers.openai.com/api/docs/guides/realtime-conversations), [OpenAI context summarization](https://developers.openai.com/cookbook/examples/context_summarization_with_realtime_api), [Gemini Live sessions](https://ai.google.dev/gemini-api/docs/live-session), [Pipecat frames](https://docs.pipecat.ai/api-reference/server/frames/overview), [Pipecat context](https://docs.pipecat.ai/guides/learn/context-management), [LiveKit audio](https://docs.livekit.io/agents/build/audio/), [LiveKit turns](https://docs.livekit.io/agents/build/turns/).

The shared pattern: state changes mutate context silently; coach-initiated speech is an explicit trigger; urgent scripted lines bypass the LLM; context records only what was actually heard.

## What we built from this

Implemented in `services/preop/src/coach.ts`, `reflex.ts`, and `jarvis/arbiter.js`:

1. **Reflex warnings, no LLM.** High-severity mistakes and tracking loss are `warning` alerts with a `reflexKey`. Their lines are pre-rendered with ElevenLabs TTS in Jarvis's voice and cached on disk. The page ducks the agent, plays the clip, then sends `[JARVIS SAID ...]` as a contextual update so the agent knows what was said and does not repeat it. Measured in the browser on October 3: 4 ms from alert to clip playing (one sample), versus 1.4 to 3.2 s for an LLM text turn.
2. **Arbiter with cockpit tiers.** Warnings preempt and clear queued cautions. Cautions are coalesced by type, cooled down per type, spaced (6 s, 2.5 s for milestones and mistakes), wait for the agent to finish (`agent_response_complete` when present, otherwise a settle delay after speaking) and for the learner to be quiet (VAD score, transcripts). Before speaking, each caution is re-validated against the newest state and dropped if its step has passed. Advisories stay silent.
3. **Versioned, diff-gated context.** Context updates go out only when a meaningful field changes (step, progress, mistakes, focus, coaching level, tracking, commands), debounced 300 ms. Every `[SIM EVENT]` carries its state version and step, and the prompt tells Jarvis the newest `[LIVE SURGERY STATE vN]` wins and stale events are ignored.
4. **Latency trace.** The page shows p50/p95 for server-to-page, warning-to-clip, caution queue wait, LLM first text, and agent speaking start, plus arbiter counters and context usage.

## Not done yet (ranked)

- **Measure voice-to-ear latency** with room audio: a click at the event, then measure speech onset over 20 to 30 scripted events. Browser timestamps miss audio buffering. Inference.
- **Bound the context window.** Watch `onContextUsage`. If a 20-minute session grows past roughly 30 to 50k tokens, either restart the session at a step boundary with a summary, or move to an ElevenLabs Custom LLM proxy that keeps only the latest state plus the last few turns. Inference.
- **Grounding check.** Stream `agent_chat_response_part` text through a matcher against the snapshot's structures and steps, log anything asserted that the state does not support. Inference.
- **Unverified:** whether an ElevenLabs `user_message` sent while the agent speaks interrupts it, and whether dynamic-variable updates re-render the system prompt mid-session.
