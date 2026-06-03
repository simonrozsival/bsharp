#!/usr/bin/env bash
# Full B# benchmark matrix with per-cell stats: min | avg | median | p95 | max
# All configs: bsharp {clean,noop,incremental} x {--no-restore,--no-fast-noop,both}
#              dotnet {clean,noop,incremental} x {--no-restore, restore-on}
set -euo pipefail
ROOT="/Users/simonrozsival/Projects/playground/bsharp"
BSHARP="$ROOT/tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp"
export BSHARP_CODEGEN="$ROOT/tools/codegen/bin/Debug/net11.0/Codegen"
DIR="$ROOT/fixtures/console-net11"; PROJ="$DIR/console-net11.csproj"
RUNS="${1:-20}"; cd "$DIR"
now(){ python3 -c 'import time;print(int(time.time()*1000))'; }
stats(){ python3 -c '
import sys,statistics as s
v=sorted(int(x) for x in sys.stdin.read().split())
n=len(v)
def p95(v):
    if not v: return 0
    k=0.95*(len(v)-1); f=int(k); c=min(f+1,len(v)-1)
    return round(v[f]+(v[c]-v[f])*(k-f))
print(f"{v[0]:>5} | {round(s.mean(v)):>5} | {round(s.median(v)):>5} | {p95(v):>5} | {v[-1]:>5}")
'; }
git checkout -- Program.cs 2>/dev/null || true
"$BSHARP" build --no-cache -v:quiet build >/dev/null 2>&1   # ensure host published + warm

prep(){ local sc="$1" ron="$2"
  case "$sc" in
    noop) : ;;
    incremental) touch Program.cs ;;
    clean) if [ "$ron" = 1 ]; then rm -rf bin obj; else rm -rf bin; fi ;;
  esac
}
run_bsharp(){ "$BSHARP" build "$@" -v:quiet build >/dev/null 2>&1 || true; }
run_dotnet(){ dotnet build "$PROJ" "$@" --nologo -v:q >/dev/null 2>&1 || true; }

bench(){ # $1 label  $2 tool  $3 scenario  $4 restore_on  $5... flags
  local label="$1" tool="$2" sc="$3" ron="$4"; shift 4
  local vals=()
  for i in $(seq 1 "$RUNS"); do
    prep "$sc" "$ron"
    local s e; s=$(now)
    if [ "$tool" = bsharp ]; then run_bsharp "$@"; else run_dotnet "$@"; fi
    e=$(now); vals+=("$((e-s))")
  done
  printf '| %-34s | %s |\n' "$label" "$(printf '%s\n' "${vals[@]}" | stats)"
}

echo "=== FULL MATRIX STATS (RUNS=$RUNS, ms) ==="
echo "| Configuration                      |   min |   avg |   med |   p95 |   max |"
echo "|------------------------------------|------:|------:|------:|------:|------:|"
for sc in clean noop incremental; do
  bench "bsharp $sc --no-restore"   bsharp "$sc" 0 --no-restore
  bench "bsharp $sc --no-fast-noop" bsharp "$sc" 1 --no-fast-noop
  bench "bsharp $sc both"           bsharp "$sc" 0 --no-restore --no-fast-noop
done
for sc in clean noop incremental; do
  bench "dotnet $sc --no-restore"   dotnet "$sc" 0 --no-restore
  bench "dotnet $sc restore-on"     dotnet "$sc" 1
done
git checkout -- Program.cs 2>/dev/null || true
"$BSHARP" build --no-restore -v:quiet build >/dev/null 2>&1 || true
echo "=== done (fixture reset + rewarmed) ==="
