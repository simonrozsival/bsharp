#!/bin/bash
PROJECT="$1"
RUNS="${2:-5}"
PROJECT_DIR=$(dirname "$PROJECT")

export BSHARP_CODEGEN="$PWD/tools/codegen/bin/Debug/net11.0/Codegen.dll"
BSHARP="$PWD/tools/bsharp/bin/Debug/net11.0/Bsharp"

echo "=== bsharp vs dotnet Benchmark ==="
echo "Project: $(basename $PROJECT)"
echo "Runs: $RUNS"
echo ""

measure() {
    local start=$(date +%s%3N)
    "$@" > /dev/null 2>&1
    local end=$(date +%s%3N)
    echo $((end - start))
}

# Clean builds
echo "=== Clean Builds ==="
for i in $(seq 1 $RUNS); do
    rm -rf "$PROJECT_DIR/bin" "$PROJECT_DIR/obj" "$PROJECT_DIR/.bsharp"
    btime=$(measure $BSHARP build "$PROJECT" --no-restore)
    
    rm -rf "$PROJECT_DIR/bin" "$PROJECT_DIR/obj" "$PROJECT_DIR/.bsharp"
    dtime=$(measure dotnet build "$PROJECT" --no-restore --nologo -v:q)
    
    echo "  Run $i: bsharp=${btime}ms dotnet=${dtime}ms"
done

# Noop builds
echo ""
echo "=== No-op Builds ==="
for i in $(seq 1 $RUNS); do
    btime=$(measure $BSHARP build "$PROJECT" --no-restore)
    dtime=$(measure dotnet build "$PROJECT" --no-restore --nologo -v:q)
    echo "  Run $i: bsharp=${btime}ms dotnet=${dtime}ms"
done

# Incremental builds
echo ""
echo "=== Incremental Builds ==="
for i in $(seq 1 $RUNS); do
    touch "$PROJECT_DIR/Program.cs"
    btime=$(measure $BSHARP build "$PROJECT" --no-restore)
    
    touch "$PROJECT_DIR/Program.cs"
    dtime=$(measure dotnet build "$PROJECT" --no-restore --nologo -v:q)
    
    echo "  Run $i: bsharp=${btime}ms dotnet=${dtime}ms"
done
