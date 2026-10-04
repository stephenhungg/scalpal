#!/usr/bin/env bash
# Stops what scripts/demo-up.sh started (only processes it started; anything already running is left).
set -uo pipefail
STATE="$(cd "$(dirname "$0")/.." && pwd)/.demo"
for f in "$STATE"/pids/*.pid; do
  [ -e "$f" ] || continue
  pid=$(cat "$f")
  if kill "$pid" 2>/dev/null; then echo "stopped $(basename "$f" .pid) ($pid)"; fi
  pkill -P "$pid" 2>/dev/null || true
  rm -f "$f"
done
