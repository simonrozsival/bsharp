#!/usr/bin/env bash
# Exploratory mutation driver for the vendored dotnet/msbuild E2E corpus.
#
# This is intentionally not part of the deterministic test suite. It copies one
# corpus case to a temp directory, asks `copilot -p` for a small constrained
# mutation, and leaves the result for manual inspection/minimization.

set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "usage: scripts/msbuild-corpus-mutate.sh <case-id> [prompt]" >&2
  exit 2
fi

if ! command -v copilot >/dev/null 2>&1; then
  echo "error: copilot CLI was not found on PATH" >&2
  exit 3
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CASE_ID="$1"
EXTRA_PROMPT="${2:-}"
CORPUS="$ROOT/fixtures/msbuild-e2e-corpus"
MANIFEST="$CORPUS/corpus.json"
WORK_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/bsharp-corpus-mutate.XXXXXX")"

python3 - "$MANIFEST" "$CASE_ID" "$CORPUS" "$WORK_ROOT" <<'PY'
import json
import pathlib
import shutil
import sys

manifest = pathlib.Path(sys.argv[1])
case_id = sys.argv[2]
corpus = pathlib.Path(sys.argv[3])
work_root = pathlib.Path(sys.argv[4])

data = json.loads(manifest.read_text())
case = next((c for c in data["cases"] if c["id"] == case_id), None)
if case is None:
    raise SystemExit(f"unknown corpus case: {case_id}")

source = corpus / case["sourceRoot"]
target = work_root / case_id
shutil.copytree(source, target)
(work_root / "case.json").write_text(json.dumps(case, indent=2))
print(target)
PY

CASE_DIR="$WORK_ROOT/$CASE_ID"
cat <<EOF
Copied corpus case to:
  $CASE_DIR

After mutation, validate manually from that directory with:
  dotnet build <entry project>
  dotnet run --project <entry project>

If useful, minimize the diff and promote it to a deterministic corpus mutation
or fixture. Do not commit live LLM output without review.
EOF

copilot -p "You are mutating a temporary copy of a small MSBuild test asset for bsharp exploratory testing.

Constraints:
- Work only under: $CASE_DIR
- Make one small, reviewable mutation.
- Prefer source edits or simple project-shape edits.
- Do not add package references, network dependencies, secrets, or large files.
- Preserve the intent that dotnet build should succeed unless explicitly asked otherwise.
- After editing, briefly describe the mutation and suggested deterministic regression assertion.

Case id: $CASE_ID
$EXTRA_PROMPT"
