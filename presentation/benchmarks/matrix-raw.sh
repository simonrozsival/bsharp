#!/usr/bin/env bash
# Collect RAW per-run timings for the full B# matrix into a CSV.
# Columns: tool,scenario,flags,restore_on,run,ms
# Configs:
#   bsharp {clean,noop,incremental} x {--no-restore, --no-fast-noop, both}
#   dotnet {clean,noop,incremental} x {--no-restore, restore-on}
# Clean semantics (THE FIX): clean ALWAYS restores (a clean build without restore
#   is artificial). clean -> rm -rf bin obj; build with restore ON for both tools.
#   bsharp auto-restores missing assets via its cached host; fast-noop never
#   triggers on a clean tree, so there is a single clean config per tool.
# Runs are INTERLEAVED (round-robin across configs each iteration) to spread load.
set -euo pipefail
ROOT="/Users/simonrozsival/Projects/playground/bsharp"
BSHARP="$ROOT/tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp"
export BSHARP_CODEGEN="$ROOT/tools/codegen/bin/Debug/net11.0/Codegen"
DIR="$ROOT/fixtures/console-net11"; PROJ="$DIR/console-net11.csproj"
RUNS="${1:-20}"
SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="${2:-$SELF_DIR/raw-$(date +%Y%m%d-%H%M%S).csv}"
cd "$DIR"
now(){ python3 -c 'import time;print(int(time.time()*1000))'; }

git checkout -- Program.cs 2>/dev/null || true
"$BSHARP" build --no-cache -v:quiet build >/dev/null 2>&1   # ensure host published + warm

clean_full(){ # robust against a transient daemon write race on macOS
  for _ in 1 2 3 4 5; do
    rm -rf bin obj 2>/dev/null && [ ! -e obj ] && [ ! -e bin ] && return 0
    sleep 0.3
  done
  rm -rf bin obj 2>/dev/null || true
}

# prep <scenario> <restore_on 0|1>
prep(){ local sc="$1" ron="$2"
  case "$sc" in
    noop) : ;;
    incremental) touch Program.cs ;;
    clean) clean_full ;;
    restore) rm -rf obj 2>/dev/null || true ;;   # wipe restore outputs only
  esac
}
# scenario "restore" uses the restore command; everything else uses build.
run_bsharp(){ local sc="$1"; shift
  if [ "$sc" = restore ]; then "$BSHARP" restore "$@" -v:quiet >/dev/null 2>&1 || true
  else "$BSHARP" build "$@" -v:quiet build >/dev/null 2>&1 || true; fi
}
run_dotnet(){ local sc="$1"; shift
  if [ "$sc" = restore ]; then dotnet restore "$PROJ" "$@" --nologo -v:q >/dev/null 2>&1 || true
  else dotnet build "$PROJ" "$@" --nologo -v:q >/dev/null 2>&1 || true; fi
}

# config table: tool|scenario|restore_on|flagslabel|flags...
# clean always restores (no --no-restore); noop/incremental vary restore & fast-noop.
CONFIGS=(
  "bsharp|clean|1|restore|--no-fast-restore"
  "dotnet|clean|1|restore|"
  "bsharp|noop|1|restore+fastnoop|--no-fast-restore"
  "bsharp|noop|1|restore+nofastnoop|--no-fast-restore --no-fast-noop"
  "bsharp|noop|0|norestore+fastnoop|--no-restore"
  "bsharp|noop|0|norestore+nofastnoop|--no-restore --no-fast-noop"
  "dotnet|noop|0|--no-restore|--no-restore"
  "dotnet|noop|1|restore|"
  "bsharp|incremental|1|restore+fastnoop|--no-fast-restore"
  "bsharp|incremental|1|restore+nofastnoop|--no-fast-restore --no-fast-noop"
  "bsharp|incremental|0|norestore+fastnoop|--no-restore"
  "bsharp|incremental|0|norestore+nofastnoop|--no-restore --no-fast-noop"
  "dotnet|incremental|0|--no-restore|--no-restore"
  "dotnet|incremental|1|restore|"
  "bsharp|restore|1|restore|"
  "dotnet|restore|1|restore|"
)

echo "tool,scenario,flags,restore_on,run,ms" > "$OUT"
echo "Collecting $RUNS interleaved runs x ${#CONFIGS[@]} configs -> $OUT" >&2
for r in $(seq 1 "$RUNS"); do
  echo "  iteration $r/$RUNS" >&2
  for cfg in "${CONFIGS[@]}"; do
    IFS='|' read -r tool sc ron flab flags <<< "$cfg"
    prep "$sc" "$ron"
    s=$(now)
    # shellcheck disable=SC2086
    if [ "$tool" = bsharp ]; then run_bsharp "$sc" $flags; else run_dotnet "$sc" $flags; fi
    e=$(now)
    echo "$tool,$sc,$flab,$ron,$r,$((e-s))" >> "$OUT"
  done
done

git checkout -- Program.cs 2>/dev/null || true
"$BSHARP" build --no-restore -v:quiet build >/dev/null 2>&1 || true
echo "DONE -> $OUT (fixture reset + rewarmed)" >&2
echo "$OUT"
