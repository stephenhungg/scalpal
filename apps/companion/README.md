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

## Restyling the companion (Silas)

Silas owns the companion's look so it matches the main Scalpal website. Nathan owns the plumbing underneath. You do not need the database, gateway or any keys to work on the UI.

### Run against the live database

```sh
git checkout nathan/companion-realtime     # or main, once merged
git checkout -b silas/companion-ui
cd apps/companion
npm install
printf 'VITE_SPACETIMEDB_URI=wss://maincloud.spacetimedb.com\nVITE_SPACETIMEDB_DB=scalpal\n' > .env.local
npm run dev                                 # http://localhost:5173
```

To fill every panel with data:

1. Create a session on the home page.
2. Open **Synthetic headset** (under People and access) and press **Run demo script**.
3. Click **Record and upload a 5 s synthetic clip**, then **Process** it on the session page. The deployed worker turns it into a replay within a few seconds while a synthetic worker is running; ask Nathan if it stays queued.
4. Open the viewer invite link in a private window to see what a judge sees.

### Where the design lives

| What | Where | Notes |
| --- | --- | --- |
| Colors, fonts, radius, shadows | `src/styles.css`, the `:root` variables at the top | Dark is the default; the light-mode block follows it. Changing these reskins everything |
| Shared UI pieces | `src/components/ui.tsx` | Panel, Pill (status badges), buttons, toast, brand mark |
| Panels | `src/components/*.tsx` | LiveView, ExerciseState, Coach, Commands, Learning, Events, Motion, Replay, Members |
| Pages | `src/pages/*.tsx` | Home, Join, Session (panel layout), HeadsetSimulator |
| Page title, favicon, web fonts | `index.html`, `public/favicon.svg` | Add a Google Fonts `<link>` in `index.html` and set `--font` |
| Chart line colors | `PALETTE` in `src/components/Replay.tsx` | |

Suggested order: match the variables first (fastest, lowest risk), then layout and markup if time allows.

### Leave to Nathan

- `src/module_bindings/` (generated)
- `src/lib/` (WebRTC, uploads, grants, trajectory parsing, router)
- `src/data/live.tsx` (subscriptions)

If a design change needs different data or behavior, ask; don't edit these directly. Tell Nathan before reworking a component he is also changing.

### Keep these honest labels

- The replay is labelled **replay of recorded motion, not a learned policy**.
- Anything synthetic stays visibly marked synthetic.
- Video status (live, connecting, stopped) stays separate from database status.
- The "assessment paused" notice appears when the anatomy fit is uncertain.

### Shipping a change

Run `npm run build` (it must pass), then open a PR from `silas/companion-ui`. After it is merged, Nathan redeploys to https://scalpal-companion.vercel.app.

To link from the main site, use an invite link such as `https://scalpal-companion.vercel.app/join/ABC123`, which drops the visitor straight into a session. The home page works too.

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
| `VITE_API_URL` | Authenticated replay gateway; defaults to `http://127.0.0.1:8788` |

Screen sharing (`getDisplayMedia`) needs a secure context (HTTPS or localhost), a supporting desktop browser, and a click.

## Deploying

`npm run build` writes a static site to `dist/`. SPA fallbacks are included for Vercel (`vercel.json`) and Netlify / Cloudflare Pages (`public/_redirects`).

## Measured so far

These were checked locally with headless Chromium on Oct 3, not on the Quest. The capture used a canvas stream in place of `getDisplayMedia`:

- **Live video:** an operator and a viewer in separate browser contexts (separate identities) got video connected at 1280×720 and about 30 fps. Both were on one machine, so RTT was about 1 ms over a direct path.
- **Session flow:** the synthetic headset script, a headset-applied action, clip upload and verification, synthetic processing, and a replay the viewer followed all worked.
- **Edge states:** viewers cannot see invites or send actions; source stop is shown; the 390 px layout has no horizontal scroll.

Not yet measured: the real Quest mirror window, cross-network viewing through TURN, and end-to-end video delay.

## Judge recap

`/recap` starts with “No result for this run.” Select the sample demo explicitly
(`/recap?demo=sample`) to preview authored scorecards. The session page links to
`/s/<sessionId>/recap`, which starts empty and accepts only an imported
`scalpal.run_result.v1` JSON document for that session. Import is local to the tab;
the file is not uploaded or persisted. There is currently no live RunResult
publisher, and the viewer does not reconstruct a surgery grade from old attempt
counters. It displays the supplied encounter and surgery grades separately.

The two reflection prompts precede the scorecard reveal. Feedback selects at most
two strengths and two improvements from critical interview facts, milestones and
guardrail violations, matching the Unity contract. The companion does not run a
second Jarvis voice agent. Source/replay videos share playback and scrubbing;
error markers require learner provenance, verified clock alignment and clip bounds.
Queued, processing and failed states retain their real status when the operator
chooses the clearly labeled bundled synthetic fallback. Imported demo flags limit
the replay highlight to 20 seconds near the first clip-valid guardrail hit, or
first incision/ligation if no guardrail hit exists (three-second lead-in, bounded
by clip duration). Flags are frozen on import and demo-assisted grades are labeled.

RunResult stores durable artifact IDs. The view uses the existing session token
to resolve URLs through `GET /v1/sessions/:sessionId/replay/:jobId`; legacy exported
URLs are discarded. Polling backs off from three to ten seconds and stops on
ready/failed. Media identity is the artifact ID; expiry/error refresh preserves
the current time. Unknown capture provenance fails closed. A gateway connection
and session membership are needed for protected learner/rehearsal artifacts.

`npm test` (also `npm run test:recap`) checks the real
`EncounterSession.score()` producer against the parser, explicit unavailable
scorecards, durable replay identities, URL rotation, source/clock gates, highlight
selection, immutable flags, stale attempts and the shared Unity sample JSON.
`tests/fixtures/preop-scorecard.json` is generated from the same synthetic chart
and real producer used by `tests/preop-producer.ts`; it is shared with Unity
boundary checks. `npm ci && npm run build` builds this view.
