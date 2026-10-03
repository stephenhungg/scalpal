# Coach relay lifecycle check

Run `dotnet run --project scripts/anatomy/relay-check` from the repository root.

This compiles the real CoachRelay and CaseRunner with controlled coroutine and HTTP doubles. It checks session replacement, fresh adoption, procedure mismatch, queued/stale messages, command acknowledgements, and non-idempotent event failure. It does not open sockets, execute Unity coroutines, or test a headset.

A live anatomy exercise must adopt a fresh Jarvis session for its exact patient and procedure. Adoption requires snapshot version zero and the first step, because the service currently does not expose enough partial progress to restore a local CaseRunner safely. Start a new Jarvis session immediately before joining from Unity. An event delivery failure pauses live forwarding and requires a new attempt; it must never blindly retry touches that could already have counted remotely.
