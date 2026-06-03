#!/usr/bin/env bash
# Full B# matrix with a launcher-vs-direct dimension.
#   via=L : through the NativeAOT launcher ($BSHARP)            -> includes launcher overhead
#   via=D : invoke the per-csproj host (.bsharp/build) directly -> skips the launcher
#   via=- : dotnet baseline
# CSV columns: tool,scenario,via,flags,restore_on,run,ms
# Restore semantics (post-fix):
#   restore_on=1 build  -> --no-fast-restore (force a real in-process restore);
#                          for noop that also needs --no-fast-noop (fast-noop runs first).
#   restore_on=0 build  -> --no-restore.
#   scenario=restore    -> the standalone `restore` command (head-to-head with dotnet restore).
# Each measured run is preceded by an UNTIMED prep that establishes its precondition
# (heal-build for noop/incremental, rm for clean/restore), so round-robin order is safe.
set -euo pipefail
ROOT="/Users/simonrozsival/Projects/playground/bsharp"
BSHARP="$ROOT/tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp"
HOST=".bsharp/build"   # symlink to the generated NativeAOT host (relative to project dir)
export BSHARP_CODEGEN="$ROOT/tools/codegen/bin/Debug/net11.0/Codegen"
export BSHARP_TASKD_PATH="$ROOT/tools/bsharp-taskd/bin/Release/net11.0/osx-arm64/publish/bsharp-taskd"
DIR="$ROOT/fixtures/console-net11"; PROJ="console-net11.csproj"
RUNS="${1:-20}"
SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="${2:-$SELF_DIR/raw2-$(date +%Y%m%d-%H%M%S).csv}"
cd "$DIR"
now(){ python3 -c 'import time;print(int(time.time()*1000))'; }

git checkout -- Program.cs 2>/dev/null || true
"$BSHARP" build --no-cache -v:quiet >/dev/null 2>&1   # ensure host published + warm

clean_full(){ for _ in 1 2 3 4 5; do rm -rf bin obj 2>/dev/null && [ ! -e obj ] && [ ! -e bin ] && return 0; sleep 0.3; done; rm -rf bin obj 2>/dev/null || true; }
heal(){ "$BSHARP" build "$PROJ" -v:quiet >/dev/null 2>&1 || true; }   # bring tree to restored+built

# prep <scenario> : untimed; establish precondition for the measured run
prep(){ case "$1" in
    noop)        heal ;;                                   # built+restored -> measured build is a true no-op
    incremental) heal; touch Program.cs ;;                 # built, then a source change
    clean)       clean_full ;;                             # measured build will restore+compile
    restore)     rm -rf obj 2>/dev/null || true ;;         # wipe restore outputs only
  esac; }

# run <tool> <via> <scenario> <flags...>
run(){ local tool="$1" via="$2" sc="$3"; shift 3
  if [ "$tool" = dotnet ]; then
    if [ "$sc" = restore ]; then dotnet restore "$PROJ" "$@" --nologo -v:q >/dev/null 2>&1 || true
    else dotnet build "$PROJ" "$@" --nologo -v:q >/dev/null 2>&1 || true; fi
    return
  fi
  local cmd=build; [ "$sc" = restore ] && cmd=restore
  if [ "$via" = D ]; then "$HOST" "$cmd" "$PROJ" "$@" -v:quiet >/dev/null 2>&1 || true
  else "$BSHARP" "$cmd" "$PROJ" "$@" -v:quiet >/dev/null 2>&1 || true; fi
}

# tool|via|scenario|restore_on|label|flags
CONFIGS=(
  # dotnet baselines
  "dotnet|-|clean|1|restore|"
  "dotnet|-|noop|1|restore|"
  "dotnet|-|noop|0|norestore|--no-restore"
  "dotnet|-|incremental|1|restore|"
  "dotnet|-|incremental|0|norestore|--no-restore"
  "dotnet|-|restore|1|restore|"
  # bsharp via launcher (L) and direct (D)
  "bsharp|L|clean|1|restore|"
  "bsharp|D|clean|1|restore|"
  "bsharp|L|noop|1|restore|--no-fast-restore --no-fast-noop"
  "bsharp|D|noop|1|restore|--no-fast-restore --no-fast-noop"
  "bsharp|L|noop|0|norestore+fastnoop|--no-restore"
  "bsharp|D|noop|0|norestore+fastnoop|--no-restore"
  "bsharp|L|noop|0|norestore+nofastnoop|--no-restore --no-fast-noop"
  "bsharp|D|noop|0|norestore+nofastnoop|--no-restore --no-fast-noop"
  "bsharp|L|incremental|1|restore|--no-fast-restore"
  "bsharp|D|incremental|1|restore|--no-fast-restore"
  "bsharp|L|incremental|0|norestore+fastnoop|--no-restore"
  "bsharp|D|incremental|0|norestore+fastnoop|--no-restore"
  "bsharp|L|incremental|0|norestore+nofastnoop|--no-restore --no-fast-noop"
  "bsharp|D|incremental|0|norestore+nofastnoop|--no-restore --no-fast-noop"
  "bsharp|L|restore|1|restore|"
  "bsharp|D|restore|1|restore|"
  "bsharp|L|restore|1|restore+nofastrestore|--no-fast-restore"
  "bsharp|D|restore|1|restore+nofastrestore|--no-fast-restore"
)

echo "tool,scenario,via,flags,restore_on,run,ms" > "$OUT"
echo "Collecting $RUNS interleaved runs x ${#CONFIGS[@]} configs -> $OUT" >&2
for r in $(seq 1 "$RUNS"); do
  echo "  iteration $r/$RUNS" >&2
  for cfg in "${CONFIGS[@]}"; do
    IFS='|' read -r tool via sc ron flab flags <<< "$cfg"
    prep "$sc"
    s=$(now)
    # shellcheck disable=SC2086
    run "$tool" "$via" "$sc" $flags
    e=$(now)
    echo "$tool,$sc,$via,$flab,$ron,$r,$((e-s))" >> "$OUT"
  done
done

git checkout -- Program.cs 2>/dev/null || true
heal
echo "DONE -> $OUT (fixture reset + rewarmed)" >&2
echo "$OUT"
