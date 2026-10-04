#!/usr/bin/env bash
# One command to run the whole Scalpal demo on this laptop (docs/demo-setup.md):
#   SpacetimeDB (local) + the realtime module from this checkout, the Scalpal coach service, the Presage
#   vitals service (demo mode unless PRESAGE_LIVE=1), the OWLv2 vision service (optional), and the
#   dashboard; then a fresh demo session with every role's invite code, with Scalpal bound to it.
# The Quest and phones join over the same wifi using the LAN address printed at the end.
# Anything already listening on its port is left alone. Stop everything with scripts/demo-down.sh.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STATE="$ROOT/.demo"
mkdir -p "$STATE/logs" "$STATE/pids"
LAN_IP="$(ipconfig getifaddr en0 2>/dev/null || hostname -I 2>/dev/null | awk '{print $1}' || echo 127.0.0.1)"
SPACETIME="$(command -v spacetime || echo "$HOME/.local/bin/spacetime")"

say() { printf '\033[1m%s\033[0m\n' "$*"; }
busy() { lsof -ti "tcp:$1" -sTCP:LISTEN >/dev/null 2>&1; }
start() { # name port dir command...
  local name=$1 port=$2 dir=$3; shift 3
  if busy "$port"; then say "  $name: already running on :$port"; return; fi
  (cd "$dir" && nohup "$@" >"$STATE/logs/$name.log" 2>&1 & echo $! >"$STATE/pids/$name.pid")
  say "  $name: starting on :$port (log .demo/logs/$name.log)"
}
wait_for() { # url seconds
  for _ in $(seq 1 "$2"); do curl -s -o /dev/null --max-time 2 "$1" && return 0; sleep 1; done
  echo "  timed out waiting for $1" >&2; return 1
}

say "Checking prerequisites"
[ -x "$SPACETIME" ] || { echo "SpacetimeDB CLI not found (install: curl -sSf https://install.spacetimedb.com | sh)"; exit 1; }
command -v node >/dev/null || { echo "Node.js not found"; exit 1; }
[ -f "$ROOT/services/preop/.env" ] || { echo "services/preop/.env is missing: get the keys from Matthew (never commit them)"; exit 1; }
for d in services/preop apps/companion services/vitals services/realtime; do
  [ -d "$ROOT/$d/node_modules" ] || (say "  npm install in $d" && cd "$ROOT/$d" && npm install --silent)
done

say "SpacetimeDB"
if ! busy 3000; then
  (nohup "$SPACETIME" start --listen-addr 0.0.0.0:3000 >"$STATE/logs/spacetimedb.log" 2>&1 & echo $! >"$STATE/pids/spacetimedb.pid")
  wait_for http://127.0.0.1:3000/v1/ping 30 || true
fi
say "  publishing the realtime module from this checkout (keeps existing data)"
(cd "$ROOT/services/realtime" && "$SPACETIME" publish --server local --yes scalpal >"$STATE/logs/publish.log" 2>&1) || { echo "publish failed: see .demo/logs/publish.log"; exit 1; }

say "Services"
start scalpal 8787 "$ROOT/services/preop" env SPACETIMEDB_URI=ws://127.0.0.1:3000 SPACETIMEDB_DB=scalpal npx tsx src/server.ts
if [ "${PRESAGE_LIVE:-0}" = "1" ]; then start vitals 8791 "$ROOT/services/vitals" npm run live; else start vitals 8791 "$ROOT/services/vitals" npm start; fi
if command -v uv >/dev/null && [ "${SKIP_VISION:-0}" != "1" ]; then start vision 8792 "$ROOT/services/vision" uv run scalpal-vision serve; else say "  vision: skipped (no uv, or SKIP_VISION=1)"; fi
start dashboard 5173 "$ROOT/apps/companion" env VITE_SPACETIMEDB_URI="ws://$LAN_IP:3000" VITE_VITALS_URL="http://$LAN_IP:8791" npm run dev -- --host 0.0.0.0 --port 5173 --strictPort
wait_for http://127.0.0.1:8787/health 60

say "Demo session"
(cd "$ROOT/services/preop" && SPACETIMEDB_URI=ws://127.0.0.1:3000 npx tsx scripts/demo-session.ts) | tee "$STATE/session.txt"

cat <<EOF

$(say "Ready")
  Dashboard        http://$LAN_IP:5173   (this laptop: http://localhost:5173)
  Scalpal page     http://localhost:8787/scalpal
  Quest settings   coach service http://$LAN_IP:8787, SpacetimeDB ws://$LAN_IP:3000 (codes above)
  Vitals           http://$LAN_IP:8791/health $( [ "${PRESAGE_LIVE:-0}" = "1" ] && echo "(LIVE: uses Presage minutes)" || echo "(demo mode)")
  Stop             scripts/demo-down.sh
EOF
