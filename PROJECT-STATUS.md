# bsharp Project Status

**Last Updated:** 2026-05-26

## What is bsharp?

bsharp is a **fast build tool for .NET** that generates a custom NativeAOT build host for your project. Instead of evaluating MSBuild XML at runtime, bsharp pre-evaluates your project structure and generates optimized C# code that executes your build.

## Performance Results

**Latest benchmarks (console-net11 fixture):**
- **Clean builds:** 1.6x faster (760ms → 463ms)
- **No-op builds:** 3.2x faster (669ms → 212ms)
- **Incremental builds:** 1.7x faster (735ms → 442ms)
- **True no-ops:** ~30ms with automatic detection

See [benchmark-results-2026-05-26.md](benchmark-results-2026-05-26.md) for detailed results.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ bsharp Launcher (NativeAOT)                                 │
│ - Parses commands (build, run, audit, clean, test)         │
│ - Computes project shape hash                               │
│ - Manages .bsharp/ cache                                    │
│ - Invokes code generator on cache miss                      │
│ - Runs generated build host                                 │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ Codegen (tools/codegen)                                     │
│ - Loads project with Microsoft.Build.Evaluation             │
│ - Analyzes target graph                                     │
│ - Scans UsingTask registrations                             │
│ - Emits Program.cs + TaskModel.cs + task-server/           │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ Generated Build Host (NativeAOT)                            │
│ - Static property/item state (P.*, I.*)                     │
│ - Async target methods with execute-once guards            │
│ - In-process tasks for simple operations                   │
│ - IPC to CoreCLR sidekick for complex SDK tasks            │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ CoreCLR Task Sidekick (ReadyToRun, direct references)      │
│ - Csc (Roslyn compiler)                                     │
│ - CreateAppHost, ResolvePackageAssets                       │
│ - GenerateDepsFile, etc.                                    │
│ - Persistent process, length-prefixed JSON IPC              │
└─────────────────────────────────────────────────────────────┘
```

## Capabilities

### ✅ Implemented and Working
- **Core build functionality:** Build .NET projects from .csproj files
- **Solution support:** Parse .sln files, build multiple projects with dependencies
- **Parallel builds:** Layer-based parallel execution for independent projects
- **Clean command:** Delete bin/, obj/, .bsharp/ directories
- **Test command:** Auto-detect test projects and run `dotnet test`
- **Audit mode:** Inspect project structure without generating host
- **Fast-noop detection:** Automatic input tracking (~30ms for true no-ops)
- **In-process tasks:** Copy, Delete, MakeDir, Touch, WriteLinesToFile, etc.
- **CoreCLR sidekick:** Persistent task server for SDK tasks
- **Cache management:** Shape-based host caching in .bsharp/variants/

### ⚠️ Limited Support
- **Project types:** Console apps and class libraries work well
- **Multi-targeting:** Outer builds supported, creates per-TFM inner hosts
- **ProjectReference:** Static references work with --no-restore

### ❌ Not Yet Supported
- **MAUI projects:** Complex project shapes not yet handled
- **Blazor projects:** Web-specific targets not implemented
- **Watch mode:** File watching and incremental rebuilds not implemented
- **NuGet restore:** Intentionally delegated to `dotnet restore`
- **Full MSBuild compatibility:** Subset of MSBuild functionality only

## Recent Work (May 2026)

### Performance Optimizations (Checkpoint 13: In-Process Tasks)
- Implemented 13 simple tasks in-process to avoid CoreCLR sidekick IPC overhead
- Added lazy target waiter allocation
- Added gated diagnostic tracking
- Implemented case-insensitive metadata dictionaries
- Added small-arity parameter packing
- Implemented boolean condition simplification
- Added conservative batch snapshot reduction
- Implemented mixed Task/ValueTask async batching

### Developer Experience Features
- **Solution support:** Parse .sln files and build multiple projects (SolutionParser.cs)
- **Dependency graph:** Analyze ProjectReference dependencies, topological sort (DependencyGraph.cs)
- **Parallel builds:** Layer-based parallel execution
- **Clean command:** Delete build outputs
- **Test command:** Auto-detect and run tests

## Testing

**Test suite:** 24 tests in tests/Bsharp.Tests/
- **Status:** 23/24 passing
- **Flaky:** ProjectReferenceConsoleBuildsAndRuns (intermittent failures)

**Run tests:**
```bash
dotnet test tests/Bsharp.Tests/Bsharp.Tests.csproj --nologo
```

## Usage

### Basic Commands
```bash
# Build a project
./tools/bsharp/bin/Debug/net11.0/Bsharp build MyProject.csproj

# Build without restoring
./tools/bsharp/bin/Debug/net11.0/Bsharp build MyProject.csproj --no-restore

# Build a solution
./tools/bsharp/bin/Debug/net11.0/Bsharp build MySolution.sln

# Clean build outputs
./tools/bsharp/bin/Debug/net11.0/Bsharp clean MyProject.csproj

# Run tests
./tools/bsharp/bin/Debug/net11.0/Bsharp test MySolution.sln

# Audit project structure (no host generation)
./tools/bsharp/bin/Debug/net11.0/Bsharp audit MyProject.csproj
```

### Environment Setup
```bash
# Required: Path to code generator
export BSHARP_CODEGEN="$PWD/tools/codegen/bin/Debug/net11.0/Codegen.dll"

# Optional: Path to bsharp launcher (defaults to searching PATH)
export BSHARP="$PWD/tools/bsharp/bin/Debug/net11.0/Bsharp"
```

### Development Workflow
```bash
# Build the toolchain
./build.sh

# Build and run console fixture
./build.sh fixtures/console-net11/console-net11.csproj

# Run tests
dotnet test tests/Bsharp.Tests/Bsharp.Tests.csproj --nologo

# Run benchmarks
python3 benchmark-script.py  # See benchmark-results-2026-05-26.md
```

## Key Files

### Tools
- `tools/bsharp/Program.cs` - NativeAOT launcher
- `tools/codegen/Program.cs` - MSBuild → C# code generator
- `tools/bsharp/SolutionParser.cs` - Solution file parser
- `tools/bsharp/DependencyGraph.cs` - ProjectReference dependency analyzer

### Fixtures
- `fixtures/console-net11/` - Primary test fixture (simple console app)
- `fixtures/maui-net11/` - MAUI stress target (not yet working)

### Generated Outputs (not committed)
- `.bsharp/variants/<hash>/src/Program.cs` - Generated build host
- `.bsharp/variants/<hash>/src/TaskModel.cs` - Task parameter model
- `.bsharp/variants/<hash>/src/task-server/` - CoreCLR sidekick project
- `.bsharp/variants/<hash>/build` - Compiled NativeAOT host (symlinked to .bsharp/build)

### Documentation
- `README.md` - Project overview and usage
- `DESIGN.md` - Design document (original draft + status updates)
- `benchmark-results-2026-05-26.md` - Latest benchmark results
- `PROJECT-STATUS.md` - This file

## What to Work on Next

### High Priority
1. **Fix flaky test:** ProjectReferenceConsoleBuildsAndRuns
2. **Watch mode:** File watching with incremental rebuilds (estimated 2-3 days)
3. **MAUI support:** Handle complex project shapes

### Medium Priority
1. **Target parallelism:** Parallel execution of independent targets within a project
2. **More in-process tasks:** CreateAppHost (~30ms savings)
3. **Code size reduction:** Optimize generated host size
4. **Better error messages:** Improve diagnostic output

### Low Priority
1. **Package/publish commands:** `dotnet pack`, `dotnet publish` equivalents
2. **Plugin system:** Allow custom task implementations
3. **VS/VSCode integration:** Editor tooling

## Known Issues

1. **Flaky test:** ProjectReferenceConsoleBuildsAndRuns fails intermittently
2. **First build is slow:** NativeAOT host generation takes ~45-55 seconds (one-time cost)
3. **Limited project type support:** MAUI, Blazor not yet working
4. **No watch mode:** Must manually re-run builds

## Design Decisions

### Why NativeAOT + CoreCLR Hybrid?
- **NativeAOT host:** Fast startup, low overhead, good for orchestration
- **CoreCLR sidekick:** Needed for complex SDK tasks that require full .NET runtime
- **In-process tasks:** Simple operations avoid IPC overhead

### Why Generate Code?
- **Performance:** Pre-evaluated project structure, no runtime XML parsing
- **Type safety:** Static properties/items catch errors at compile time
- **Optimization:** Can specialize based on project shape

### Why Not Full MSBuild Compatibility?
- **Scope:** Closed-world subset for fast inner loop builds
- **Complexity:** Full MSBuild is massive, many features rarely used
- **Goals:** Fast and correct for common cases, not 100% compatible

## Repository Conventions

- Generated outputs are gitignored (`.bsharp/`, `bin/`, `obj/`, `artifacts/`)
- Don't patch `.bsharp/src` directly; change `tools/codegen/Program.cs`
- Global properties are closed-world inputs (shape hash includes `-p:X=Y`)
- Target dependency execution is conservative (literal deps run sequentially)

## Commands Reference

```bash
# Build toolchain
dotnet build tools/codegen/Codegen.csproj -c Debug
dotnet publish tools/bsharp/Bsharp.csproj -c Release -r osx-arm64

# Run console fixture with fresh tools
export BSHARP_CODEGEN="$PWD/tools/codegen/bin/Debug/net11.0/Codegen"
tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp build \
  --no-cache -v:quiet fixtures/console-net11/console-net11.csproj

# Run single smoke path
cd fixtures/console-net11
./.bsharp/build --no-restore -v:quiet build
./.bsharp/build --no-restore -v:quiet run

# Audit project shape without generating host
dotnet build tools/codegen/Codegen.csproj -c Debug
tools/codegen/bin/Debug/net11.0/Codegen --audit \
  --project fixtures/maui-net11/MauiNet11.csproj \
  -p TargetFramework=net11.0-android

# Run tests
dotnet test tests/Bsharp.Tests/Bsharp.Tests.csproj --nologo

# Run specific test
dotnet test tests/Bsharp.Tests/Bsharp.Tests.csproj --nologo \
  --filter FullyQualifiedName~CodegenUnitTests
```
