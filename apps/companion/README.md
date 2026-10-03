# Companion Website

Owner: Nathan. Follows [Nathan's implementation plan](../../docs/nathan-plan.md).

Browser companion for watching a Scalpal session live:

- **Live headset view:** the operator shares the Quest mirror window, and viewers receive it over WebRTC. Signaling goes through SpacetimeDB; video goes peer-to-peer or through TURN.
- **Session state panels:** exercise mode, step, organ, registration and recording; Jarvis transcript and status; app actions with their applied/rejected outcomes; learning result; event timeline.
- **Recording and processing:** upload test artifacts, request motion processing, follow job progress, read quality summaries, retry or cancel.
- **Robot replay:** a joint-target chart with invalid spans shaded, live joint bars, and the source clip on the same timeline. Playback can be synced from the operator. It is labelled as replay of recorded motion, not a learned policy.
- **Access control:** invite codes per role (viewer, coach, headset, operator), presence, rotate/revoke codes, end session.
- **Synthetic headset** (`/s/<id>/simulate`): imitates the Quest app for testing, including a scripted demo and a generated nonpersonal test clip. Everything it produces is labelled synthetic.

It is an observer interface. It does not run a second voice agent or simulate surgery.

Stack: Vite + React + TypeScript, SpacetimeDB TypeScript SDK 2.10.2.

## Run locally

Prerequisites: Node 20+, the `spacetime` CLI (`curl -sSf https://install.spacetimedb.com | sh`).

```sh
# 1. database
spacetime start                                    # terminal 1
cd services/realtime && npm install && npm run publish:local

# 2. gateway (storage URLs, ICE/voice credentials, worker API)
cd services/api && npm install && cp .env.example .env
npm run dev                                         # terminal 2; first run prints a command
spacetime call --server local scalpal add_service_identity '"0x<identity>"' '"gateway"'

# 3. synthetic motion worker (optional)
cd services/api && npm run worker:synthetic        # terminal 3

# 4. website
cd apps/companion && npm install && cp .env.example .env.local
npm run dev                                         # http://localhost:5173
```

Create a session, open the viewer invite link in another browser profile, press **Share Quest view**, then open **Synthetic headset** to drive the session.

## Configuration

| Variable | Meaning |
| --- | --- |
| `VITE_SPACETIMEDB_URI` | `ws://127.0.0.1:3000` locally, `wss://maincloud.spacetimedb.com` in production |
| `VITE_SPACETIMEDB_DB` | Database name the module was published under |

Screen sharing (`getDisplayMedia`) needs a secure context (HTTPS or localhost), a supporting desktop browser, and a click.

## Deploying

`npm run build` writes a static site to `dist/`. SPA fallbacks are included for Vercel (`vercel.json`) and Netlify / Cloudflare Pages (`public/_redirects`).

## Measured so far

These were checked locally with headless Chromium on Oct 3, not on the Quest. The capture used a canvas stream in place of `getDisplayMedia`:

- **Live video:** an operator and a viewer in separate browser contexts (separate identities) got video connected at 1280×720 and about 30 fps. Both were on one machine, so RTT was about 1 ms over a direct path.
- **Session flow:** the synthetic headset script, a headset-applied action, clip upload and verification, synthetic processing, and a replay the viewer followed all worked.
- **Edge states:** viewers cannot see invites or send actions; source stop is shown; the 390 px layout has no horizontal scroll.

Not yet measured: the real Quest mirror window, cross-network viewing through TURN, and end-to-end video delay.
