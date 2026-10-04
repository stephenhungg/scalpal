# Vitals (Presage patient channel)

Owner: Silas. Implements the service in [docs/presage](../../docs/presage/README.md): real breathing and pulse of the reclining volunteer from a fixed camera (an iPhone on a stand via Continuity Camera), served to the headset, Jarvis's coach and the companion.

## Quota guard

The Presage key has a fixed budget of live measurement minutes. The guard works like this:
- **Demo by default.** The service runs in labelled demo mode (`"mode": "demo"`, `"DEMO · synthetic vitals"`) unless you explicitly start it with `npm run live`.
- **Live sessions stop themselves** after `PRESAGE_LIVE_MINUTES` (default 5).
- **No face, no spend.** A live session with no face in view for `PRESAGE_NO_FACE_SECONDS` (default 15) ends itself.
- **Failures are clean.** If the camera can't be opened (no permission, phone not connected), the session ends with the reason and the server keeps running.
- **Usage is logged.** Live minutes go to `.presage-usage.json` (gitignored). Live mode is refused once the total reaches `PRESAGE_BUDGET_MINUTES` (default 50).

Build and integrate against demo mode. Spend live minutes on the real setup and the judge demo.

```sh
cd services/vitals
npm install
npm run cameras        # lists cameras + PRESAGE_CAMERA_ID (pick the iPhone)
npm start              # demo mode on :8791, no quota
npm run preview        # one frame from the camera to check framing (no quota)
npm run live           # real Presage measurement (uses quota, auto-stops)
npm test               # physiology model tests
```

`.env` (never committed):
- `PRESAGE_API_KEY=`
- `PRESAGE_CAMERA_ID=` (the iPhone's ID)
- `PORT=8791` (8790 belongs to the body-pose service in services/registration)

## Endpoints

| | |
| --- | --- |
| `GET /vitals` | Latest snapshot: `mode`, `label`, `status` (Presage readiness reason), `pulse` / `breathing` (`{ bpm, confidence }` only when Presage marks them stable, otherwise `null` + `measuring`), short `traces` |
| `GET /vitals/stream` | The same snapshot as server-sent events, about 10 times a second |
| `POST /baseline/capture` | At Time-Out: freezes the baseline from the last ~12 s of confident readings (median). Falls back to an authored baseline, labelled as such |
| `GET /baseline` | Current baseline and its `source`: `measured`, `demo` or `authored` |
| `GET /monitor?bloodLostMl=&bleedMlPerMin=&weightKg=&critical=1` | OR monitor values: measured baseline plus a simulated hemorrhage delta (ATLS classes), with a label like `HR 117 · simulated from baseline 72 (measured)` |
| `GET /preview.jpg` | One frame from the configured camera, for checking framing before going live (ffmpeg, no quota). `409` while a live session holds the camera |
| `GET /health` | Mode and status |

Readings that dip below Presage's stable threshold keep showing for `PRESAGE_HOLD_SECONDS` (default 10), marked with `heldMs`, before falling back to `measuring`. The baseline only uses truly stable readings.

The physiology model (`src/physiology.mjs`) is a pure function of body state, so Unity and the coach can mirror it exactly. It's authored teaching content, not a validated clinical simulation. Real vitals never respond to the virtual surgery; only the monitor numbers carry the simulated delta.

## Camera setup

- **Mount:** phone on a stand, 0.5 to 1 m from the volunteer, landscape.
- **Frame:** face and upper chest, front lighting, no flicker.
- **Volunteer:** still and not talking, for breathing.
- **Processing:** on-device; Scalpal stores no footage.
