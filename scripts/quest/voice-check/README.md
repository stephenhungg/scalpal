# Native Jarvis transport check

Run `python3 scripts/quest/voice-check/run.py` from the repository. It uses the installed Unity 6000.0.66f2 Mono compiler and engine assemblies without launching the Unity Editor. Set `SCALPAL_UNITY_CONTENTS` to another compatible Unity installation's `Unity.app/Contents` directory when necessary.

The check compiles the Android permission branch with warnings treated as errors, then exercises PCM16 little-endian encoding/decoding, stereo downmix, negotiated sample-rate validation, malformed samples and raw nested tool-parameter preservation. It does not open sockets, request microphone permission, call a provider, or prove IL2CPP/device playback.

Run `python3 scripts/quest/verify_session.py --suite voice` for actual Unity serialization and transport checks. `NativeVoiceToolsValidation` invokes production `BuildInitiation` rather than copying its DTOs: it checks patient/attending/surgery identities, escaped prompt/greeting/context, omission of empty optional fields, and an absent `tts` key for the default Jarvis voice. Its synthetic connection queue also exercises production interruption handling, late/idless audio rejection, half-duplex gating, filter reset, focus restoration without reconnect, and idempotent resource disposal. Actual native tool coroutines still exchange HTTP with the isolated service fixture; no live microphone or provider socket is started.

`QuestJarvisVoice` uses the existing service's `GET /jarvis/connection`; signed URLs are supported by the official ElevenLabs agent WebSocket protocol. Configure the endpoint, pass the existing `POST /coach/sessions` result's `systemPrompt`, `firstMessage` and `context` to `ConfigureConversation`, and call `Connect(sessionId)` only after an explicit voice action. Scene events update it through `SendContext` and `SendUserMessage`; it does not own progression or run another language model. Native client tools use the existing bounded coach HTTP routes. Other tools require an explicit scene handler and acknowledged `ResolveClientTool` result.

Primary references:

- [ElevenLabs Agent WebSocket protocol](https://elevenlabs.io/docs/eleven-agents/api-reference/eleven-agents/websocket)
- [Signed WebSocket authentication and contextual updates](https://elevenlabs.io/docs/eleven-agents/libraries/web-sockets)
- [Client events, interruptions and tool-call responses](https://elevenlabs.io/docs/eleven-agents/customization/events/client-events)

Limitations: only `pcm_*` audio formats are decoded; another negotiated encoding fails explicitly. Unity microphone capture does not configure acoustic echo cancellation. Physical headset authentication, speaker/microphone behavior, audio latency, interruptions, provider tool delivery and Android IL2CPP transport must be tested separately. No reflex-warning audio arbiter is implemented here: the scene can forward a coach alert as an agent message, but this does not provide the browser's deterministic pre-rendered warning latency guarantee. The laptop browser must not run a competing voice conversation or auto-ack headset effects during native use.

Microphone capture sends timed zero PCM while muted or while agent output is pending. A deliberate held-talk interruption clears playback before learner audio is sent. Provider interruption IDs reject older audio; local interruption suppresses arriving chunks until an identified provider interruption or finalized learner transcript establishes a turn boundary. Focus restoration does not reconnect automatically. These are half-duplex and protocol guards, not acoustic echo cancellation or measured headset latency.
