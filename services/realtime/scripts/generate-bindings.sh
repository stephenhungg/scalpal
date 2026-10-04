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
gen csharp "$here/bindings/csharp"
# Unity: mirror the C# bindings into the Quest project, keeping existing .meta
# files (stable GUIDs) and dropping .meta files whose generated source is gone.
unity="$root/apps/quest/Assets/Scalpal/Realtime/Generated"
mkdir -p "$unity"
find "$unity" -name '*.cs' -type f -exec rm -f {} +
cp -R "$here/bindings/csharp/." "$unity/"
find "$unity" -name '*.cs.meta' -type f | while read -r meta; do
  [ -f "${meta%.meta}" ] || rm -f "$meta"
done
echo "bindings generated"
