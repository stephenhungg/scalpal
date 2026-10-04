#!/bin/sh
# Regenerate client bindings for every consumer of the realtime module.
# Run from anywhere: sh services/realtime/scripts/generate-bindings.sh
set -eu
here="$(cd "$(dirname "$0")/.." && pwd)"
root="$(cd "$here/../.." && pwd)"

gen() {
  lang="$1"; out="$2"
  rm -rf "$out"
  mkdir -p "$out"
  spacetime generate --yes --lang "$lang" --out-dir "$out" --module-path "$here"
}

gen typescript "$root/apps/companion/src/module_bindings"
gen typescript "$root/services/api/src/module_bindings"
# Jarvis coach bridge (services/preop): transcript, status, encounters, commands.
gen typescript "$root/services/preop/src/module_bindings"
# Unity: Stephen copies or references this folder from apps/quest.
gen csharp "$here/bindings/csharp"
echo "bindings generated"
