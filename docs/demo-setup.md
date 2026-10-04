# Demo Setup (one laptop, local database)

Decision (Matthew, October 4, 2026): the demo runs with a **local** SpacetimeDB on the demo laptop (Stephen's, which has the Quest and Unity). The Quest and any phones join over the same wifi.

## What lives where

- **The repo has everything that is code or authored content:** services, the realtime module, the 8 patients' interview files and the docs. Pull `main`.
- **Keys** (`services/preop/.env`) are never committed. Get them from Matthew privately.
- **Live data** (sessions, logs, vitals) is created fresh by each run on the laptop running SpacetimeDB. Nothing needs to be shared ahead of time.
- **Scalpal's pre-rendered voice clips** are cached in `.cache/reflex` and regenerate on first use.

## Run it

```sh
git pull
cp ~/scalpal-keys/preop.env services/preop/.env   # from Matthew
scripts/demo-up.sh            # PRESAGE_LIVE=1 for real vitals (uses Presage minutes); SKIP_VISION=1 to skip OWLv2
```

The script:
- starts SpacetimeDB (listening on all addresses) and publishes the module from this checkout, which keeps existing data;
- starts Scalpal (`:8787`, no watch mode, so edits cannot wipe a live case), vitals (`:8791`), vision (`:8792`) and the dashboard (`:5173`, reachable on the LAN);
- creates a fresh demo session, binds Scalpal to it, and prints every role's invite code plus the LAN addresses for the Quest and phones.

Anything already running on its port is left alone. `scripts/demo-down.sh` stops what the script started. Logs are in `.demo/logs/`.

## On the Quest

Set the coach service to `http://<laptop LAN IP>:8787` and SpacetimeDB to `ws://<laptop LAN IP>:3000`, then join with the **headset** code. Judges open `http://<laptop LAN IP>:5173` and join with the **viewer** code. The demo patient is Jonah Okoye (`patient-demo-sparse`, open appendectomy).

`dashboard.scalpal.tech` reads the maincloud database, not this laptop, so use the LAN address above during the demo.
