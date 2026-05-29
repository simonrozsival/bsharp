#!/usr/bin/env bash
# Development convenience script: build the bsharp tools (codegen + launcher),
# then run `bsharp build` on the target project.
#
# Usage:
#   ./build.sh                                              # uses fixtures/console-net11/console-net11.csproj
#   ./build.sh path/to/MyProject.csproj
#
# For end-user usage, just install bsharp and run `bsharp build` directly.

set -euo pipefail

BSHARP_ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJECT="${1:-$BSHARP_ROOT/fixtures/console-net11/console-net11.csproj}"
PROJECT="$(cd "$(dirname "$PROJECT")" && pwd)/$(basename "$PROJECT")"

echo "==> Build codegen tool (Debug)"
( cd "$BSHARP_ROOT/tools/codegen" && dotnet build -c Debug --nologo -v q > /dev/null )
CODEGEN_BIN="$BSHARP_ROOT/tools/codegen/bin/Debug/net11.0/Codegen"

echo "==> Publish universal task daemon (CoreCLR R2R)"
( cd "$BSHARP_ROOT/tools/bsharp-taskd" && dotnet publish -c Release -r osx-arm64 --no-self-contained -p:PublishReadyToRun=true --nologo -v q > /dev/null )
TASKD_BIN="$BSHARP_ROOT/tools/bsharp-taskd/bin/Release/net11.0/osx-arm64/publish/bsharp-taskd"

echo "==> AOT-publish the launcher"
( cd "$BSHARP_ROOT/tools/bsharp" && dotnet publish -c Release -r osx-arm64 --nologo -v q > /dev/null )
BSHARP_BIN="$BSHARP_ROOT/tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp"

echo "==> Build Go launcher (experiment)"
if command -v go > /dev/null 2>&1; then
    ( cd "$BSHARP_ROOT/tools/bsharp-go" && go build -ldflags="-s -w" -trimpath -o bsharp-go . )
    BSHARP_GO_BIN="$BSHARP_ROOT/tools/bsharp-go/bsharp-go"
else
    echo "    (go not installed; skipping bsharp-go)"
    BSHARP_GO_BIN=""
fi

echo "==> Build C launcher (experiment)"
( cd "$BSHARP_ROOT/tools/bsharp-c" && cc -O3 -Wall -Wextra -o bsharp-c bsharp-c.c )
BSHARP_C_BIN="$BSHARP_ROOT/tools/bsharp-c/bsharp-c"

echo "==> Stage daemon next to the launcher"
TASKD_PUBLISH_DIR="$BSHARP_ROOT/tools/bsharp-taskd/bin/Release/net11.0/osx-arm64/publish"
LAUNCHER_DIR="$(dirname "$BSHARP_BIN")"
cp -f "$TASKD_PUBLISH_DIR"/* "$LAUNCHER_DIR/" || true
if [ -n "$BSHARP_GO_BIN" ] && [ -f "$BSHARP_GO_BIN" ]; then
    cp -f "$BSHARP_GO_BIN" "$LAUNCHER_DIR/bsharp-go"
fi
if [ -f "$BSHARP_C_BIN" ]; then
    cp -f "$BSHARP_C_BIN" "$LAUNCHER_DIR/bsharp-c"
fi

echo "==> Done. Try:"
echo "    cd $(dirname "$PROJECT")"
echo "    BSHARP_CODEGEN=$CODEGEN_BIN $BSHARP_BIN build"
echo "    BSHARP_CODEGEN=$CODEGEN_BIN $BSHARP_BIN run --no-build"
echo
echo "Or run it now:"
echo
cd "$(dirname "$PROJECT")"
BSHARP_CODEGEN="$CODEGEN_BIN" "$BSHARP_BIN" build
