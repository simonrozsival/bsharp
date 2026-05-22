#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid="$(dotnet --info | awk -F': *' '/RID:/ { print $2; exit }')"

export BSHARP_CODEGEN="${BSHARP_CODEGEN:-$repo_root/tools/codegen/bin/Debug/net11.0/Codegen}"
BSHARP="${BSHARP:-$repo_root/tools/bsharp/bin/Release/net11.0/$rid/publish/bsharp}"

dotnet build "$repo_root/tools/codegen/Codegen.csproj" -c Debug --nologo -v:q
dotnet publish "$repo_root/tools/bsharp/Bsharp.csproj" -c Release -r "$rid" --nologo -v:q

fixture_root="$repo_root/artifacts/benchmarks/fixtures"
result_root="$repo_root/artifacts/benchmarks/results/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$fixture_root" "$result_root"

small_fixture="$fixture_root/console-net11"
rm -rf "$small_fixture"
mkdir -p "$small_fixture"
tar -C "$repo_root/fixtures/console-net11" \
  --exclude .bsharp --exclude bin --exclude obj \
  -cf - . | tar -C "$small_fixture" -xf -

"$repo_root/scripts/generate-console-10k-fixture.sh" "$fixture_root/console-10k" 10000

run_case() {
  local name="$1"
  local cwd="$2"
  shift 2
  local log="$result_root/$name.log"
  echo "== $name =="
  /usr/bin/time -p "$@" >"$log" 2>&1
  grep -E '^(real|user|sys)|build time:|cumulative tasks:|net overhead:|^[0-9]+$|Hello' "$log" || true
  echo
}

prepare_bsharp() {
  local cwd="$1"
  (cd "$cwd" && "$BSHARP" build --no-cache --no-restore -v:q >/dev/null)
}

generated_host_path() {
  local cwd="$1"
  find "$cwd/.bsharp" -name build -type f -o -name build -type l | sort | head -1
}

for fixture in console-net11 console-10k; do
  cwd="$fixture_root/$fixture"
  echo "## $fixture"
  (cd "$cwd" && dotnet restore --nologo -v:q)
  prepare_bsharp "$cwd"
  host="$(generated_host_path "$cwd")"

  run_case "$fixture-dotnet-build" "$cwd" \
    bash -lc "cd '$cwd' && dotnet build --no-restore --nologo -v:q"
  run_case "$fixture-bsharp-full-run" "$cwd" \
    bash -lc "cd '$cwd' && '$BSHARP' run --no-restore -v:n"
  run_case "$fixture-bsharp-fast-run" "$cwd" \
    bash -lc "cd '$cwd' && '$BSHARP' run --no-restore --fast-noop -v:n"
  run_case "$fixture-direct-host-run" "$cwd" \
    bash -lc "cd '$cwd' && '$host' run --no-restore -v:n"
done

echo "Logs written to $result_root"
