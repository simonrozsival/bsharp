#!/usr/bin/env bash
# Fresh B# benchmark for the presentation. Measures warm no-op + incremental
# (bsharp launcher vs dotnet build), plus the direct generated-host path.
# Cold build is measured separately (one-time ~tens of seconds for NativeAOT publish).
set -euo pipefail

ROOT="/Users/simonrozsival/Projects/playground/bsharp"
BSHARP="$ROOT/tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp"
export BSHARP_CODEGEN="$ROOT/tools/codegen/bin/Debug/net11.0/Codegen"
PROJ_DIR="$ROOT/fixtures/console-net11"
PROJ="$PROJ_DIR/console-net11.csproj"
RUNS="${1:-15}"

cd "$PROJ_DIR"

now_ms() { python3 -c 'import time;print(int(time.time()*1000))'; }
measure() { local s e; s=$(now_ms); "$@" >/dev/null 2>&1 || true; e=$(now_ms); echo $((e-s)); }
median() { python3 -c 'import sys;v=sorted(int(x) for x in sys.stdin.read().split());print(v[len(v)//2])'; }

echo "=== Environment ==="
echo "dotnet $(dotnet --version) | $(uname -sm) | runs=$RUNS"
echo

# Ensure warm host exists.
"$BSHARP" build --no-restore -v:quiet build >/dev/null 2>&1 || true

echo "=== Warm no-op build ==="
b=(); d=(); h=()
for i in $(seq 1 "$RUNS"); do
  b+=("$(measure "$BSHARP" build --no-restore -v:quiet build)")
  d+=("$(measure dotnet build "$PROJ" --no-restore --nologo -v:q)")
  h+=("$(measure ./.bsharp/build --no-restore -v:quiet build)")
done
echo "  bsharp launcher : $(printf '%s\n' "${b[@]}" | median) ms (median)"
echo "  dotnet build    : $(printf '%s\n' "${d[@]}" | median) ms (median)"
echo "  direct .bsharp  : $(printf '%s\n' "${h[@]}" | median) ms (median)"
echo "  bsharp raw: ${b[*]}"
echo "  dotnet raw: ${d[*]}"
echo "  host   raw: ${h[*]}"
echo

echo "=== Incremental build (touch Program.cs) ==="
b=(); d=()
for i in $(seq 1 "$RUNS"); do
  touch Program.cs; b+=("$(measure "$BSHARP" build --no-restore -v:quiet build)")
  touch Program.cs; d+=("$(measure dotnet build "$PROJ" --no-restore --nologo -v:q)")
done
echo "  bsharp launcher : $(printf '%s\n' "${b[@]}" | median) ms (median)"
echo "  dotnet build    : $(printf '%s\n' "${d[@]}" | median) ms (median)"
echo "  bsharp raw: ${b[*]}"
echo "  dotnet raw: ${d[*]}"
