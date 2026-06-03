#!/usr/bin/env bash
# Full B# benchmark matrix: {bsharp,dotnet} x {clean,noop,incremental}
#   x {--no-restore, --no-fast-noop, both}
# One uniform pass, median of RUNS. dotnet has no fast-noop:
#   dotnet --no-restore == both (restore off); dotnet --no-fast-noop == plain build (restore on).
set -euo pipefail
ROOT="/Users/simonrozsival/Projects/playground/bsharp"
BSHARP="$ROOT/tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp"
export BSHARP_CODEGEN="$ROOT/tools/codegen/bin/Debug/net11.0/Codegen"
DIR="$ROOT/fixtures/console-net11"; PROJ="$DIR/console-net11.csproj"
RUNS="${1:-9}"; cd "$DIR"
now(){ python3 -c 'import time;print(int(time.time()*1000))'; }
med(){ python3 -c 'import sys;v=sorted(int(x) for x in sys.stdin.read().split());print(v[len(v)//2] if v else 0)'; }
mn(){ python3 -c 'import sys;v=sorted(int(x) for x in sys.stdin.read().split());print(v[0] if v else 0)'; }
git checkout -- Program.cs 2>/dev/null || true
"$BSHARP" build --no-cache -v:quiet build >/dev/null 2>&1   # ensure host published + warm

# prep <scenario> <restore_on 0|1>  — set up state before a measured run
prep(){ local sc="$1" ron="$2"
  case "$sc" in
    noop) : ;;                                   # leave up-to-date
    incremental) touch Program.cs ;;
    clean) if [ "$ron" = 1 ]; then rm -rf bin obj; else rm -rf bin; fi ;;
  esac
}
# run one measured invocation
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
  printf '%-46s med %5s  min %5s  ms\n' "$label" "$(printf '%s\n' "${vals[@]}" | med)" "$(printf '%s\n' "${vals[@]}" | mn)"
}

echo "=== FULL MATRIX (median of $RUNS ms) ==="
echo "--- bsharp ---"
for sc in clean noop incremental; do
  bench "bsharp $sc --no-restore"            bsharp "$sc" 0 --no-restore
  bench "bsharp $sc --no-fast-noop"          bsharp "$sc" 1 --no-fast-noop
  bench "bsharp $sc both"                     bsharp "$sc" 0 --no-restore --no-fast-noop
done
echo "--- dotnet (no fast-noop concept) ---"
for sc in clean noop incremental; do
  bench "dotnet $sc --no-restore"            dotnet "$sc" 0 --no-restore
  bench "dotnet $sc restore-on (=no-fast-noop col)" dotnet "$sc" 1
done
git checkout -- Program.cs 2>/dev/null || true
"$BSHARP" build --no-restore -v:quiet build >/dev/null 2>&1 || true
echo "=== done (fixture reset + rewarmed) ==="
