# bsharp

> Research prototype: compile a closed-world subset of MSBuild into a specialized
> NativeAOT build host, with real SDK `UsingTask`s delegated to a persistent CoreCLR
> task server.

This is a playground, not a general MSBuild replacement. The included fixture is a
simple `net11.0` console app in `fixtures/console-net11/`.

## Requirements

- macOS arm64
- .NET SDK `11.0.100-preview.4.26230.115` or a compatible .NET 11 SDK
- `dotnet publish` support for NativeAOT on `osx-arm64`

## Quick start

Build the tools and run the default fixture:

```bash
./regenerate.sh
```

Or build and use the tools directly:

```bash
dotnet build tools/codegen/Codegen.csproj
dotnet publish tools/bsharp/Bsharp.csproj -c Release -r osx-arm64

export BSHARP="$PWD/tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp"
export BSHARP_CODEGEN="$PWD/tools/codegen/bin/Debug/net11.0/Codegen"

cd fixtures/console-net11
$BSHARP build --no-cache -v:quiet run
```

After the first successful build, the generated project-local binary can be run
directly:

```bash
./.bsharp/build --no-restore build
./.bsharp/build --no-restore run
```

`BSHARP_CODEGEN` may point to either the `Codegen` executable/apphost or
`Codegen.dll`. DLL paths are invoked through `dotnet`; executable paths are invoked
directly.

## Architecture

```text
bsharp
  NativeAOT launcher. Finds the project, computes a shape hash, regenerates on cache
  miss, publishes the generated host and task server, then execs .bsharp/build.

tools/codegen
  Managed .NET tool. Uses MSBuild evaluation and MetadataLoadContext to generate the
  closed-world build host source and task-server source.

project/.bsharp/
  shape.hash
  src/
    Program.cs
    TaskModel.cs
    BsharpGenerated.csproj
    task-server/
      Program.cs
      BsharpTaskServer.csproj
  build -> src/bin/Release/net11.0/osx-arm64/publish/BsharpGenerated
```

The published per-project runtime shape is:

| Component | Publish shape | Purpose |
|---|---|---|
| Generated host | NativeAOT, self-contained, single file | Fast startup and generated target/property/item logic |
| Task server | CoreCLR ReadyToRun, framework-dependent | Persistent execution of real SDK `UsingTask`s that need dynamic assembly loading |

The task server is CoreCLR rather than NativeAOT because it loads SDK task assemblies
dynamically. Keeping that dynamic code in a separate persistent process lets the
generated host stay NativeAOT.

## Measurements

Observed `fixtures/console-net11` warm no-op numbers:

| Scenario | Time | What it measures |
|---|---:|---|
| Generated host cache-hit build | 0.4-0.8 ms | `.bsharp/build` internal build summary: fast no-op path, no SDK tasks |
| `bsharp` launcher cache-hit wall time | ~115 ms | Shell `time $BSHARP`: launcher startup/hash check + `Process.Start(.bsharp/build)` |
| Cumulative tasks on warm no-op | 0.00 ms | Fast path returns before restore/build target execution |

The sub-millisecond number is the generated host's own work. The larger shell wall
time is dominated by launching `bsharp` and then launching the project-local
`.bsharp/build` process.

## Commands

| Command | Behavior |
|---|---|
| `bsharp build` | Ensure `.bsharp/build` is current, then run `build` |
| `bsharp run` | Ensure `.bsharp/build` is current, then run `run` |
| `bsharp build --no-cache` | Force regeneration and republish |
| `bsharp audit` | Evaluate the project and print a JSON subset/shape report without generating a build host |
| `bsharp build path/to/project.csproj` | Build an explicit project |
| `bsharp build -p:Configuration=Release` | Add a closed-world global property override to the cache key |
| `bsharp build -v quiet` | Set generated-host verbosity (`quiet`, `minimal`, `normal`, `detailed`, `diagnostic`) |

Unknown flags are forwarded to the generated per-project binary.

`bsharp audit` reports the evaluated shape that codegen would see: target/task counts,
outer-build detection, `CallTarget` and `<MSBuild>` task sites, dynamic imports,
batching expressions, property functions, and `UsingTask` resolution issues. It is the
bring-up tool for larger SDKs such as MAUI.

## Tests

Run the automated validation suite:

```bash
dotnet test tests/Bsharp.Tests/Bsharp.Tests.csproj --nologo
```

The default suite includes fast structural codegen/audit unit tests derived from
regular `dotnet/msbuild` unit-test scenarios, plus the console fixture cold
launcher build, warm cache-hit build, direct generated-host build/run, forced
regeneration, incremental source-edit rebuild, shape invalidation, audit shape
checks, and unsupported-shape audit checks.

The minimized regular-unit scenarios live under
`fixtures/msbuild-unit-scenarios/` with upstream `dotnet/msbuild` provenance in
`unit-scenarios.json`. These tests run `tools/codegen` directly and assert audit
JSON or generated-source structure; they intentionally avoid NativeAOT publish so
they stay suitable for the default developer loop.

The default codegen unit tests also cover the supported task-batching subset:
local structural tasks are invoked sequentially once per distinct metadata value,
with batched item lists filtered to the current metadata key and non-batching item
lists passed through unchanged. Target batching is intentionally rejected during
generation instead of being silently approximated.

MAUI audit regression coverage is gated because it depends on local workload
availability:

```bash
BSHARP_RUN_MAUI_AUDIT_TESTS=1 dotnet test tests/Bsharp.Tests/Bsharp.Tests.csproj --nologo --filter TestCategory=Maui
```

MSBuild corpus head-to-head coverage is also gated because it publishes generated
hosts for vendored `dotnet/msbuild` E2E assets:

```bash
BSHARP_RUN_MSBUILD_CORPUS_TESTS=1 dotnet test tests/Bsharp.Tests/Bsharp.Tests.csproj --nologo --filter TestCategory=MSBuildCorpus
```

Corpus results are written to `artifacts/msbuild-corpus-results/*.json`. The
vendored corpus lives under `fixtures/msbuild-e2e-corpus/` with upstream commit
and license metadata. For exploratory mutations, use:

```bash
scripts/msbuild-corpus-mutate.sh single-project "try a small source edit"
```

## Cache invalidation

The launcher recomputes a shape hash on every invocation. A cache miss occurs when
any of these change:

- target `.csproj`
- ancestor `Directory.Build.props`
- ancestor `Directory.Build.targets`
- ancestor `Directory.Packages.props`
- ancestor `global.json`
- `-p:X=Y` global properties passed to `bsharp`

Known gaps:

- `project.assets.json` / transitive package drift is not yet hashed.
- `ProjectReference` recursion is not yet modeled.
- NuGet packages added by the final project are assumed not to introduce new targets.

## Generated target shape

Each MSBuild target becomes an async method with execute-once state:

```csharp
static int T_123_CoreCompileState;
static TaskCompletionSource? T_123_CoreCompileCompletion;

public static async ValueTask T_123_CoreCompile() {
    if (!TargetRuntime.TryEnter(
            ref T_123_CoreCompileState,
            ref T_123_CoreCompileCompletion,
            out var waitTask)) {
        if (waitTask is not null)
            await waitTask;
        return;
    }

    try {
        await Task.WhenAll(
            Task.Run(static async () => await T_100_PrepareForBuild()),
            Task.Run(static async () => await T_101_ResolveReferences())
        );

        var targetStart = Log.TargetStarted("CoreCompile");
        // target body...
        Log.TargetFinished("CoreCompile", targetStart);
    } catch (Exception ex) {
        AddError("CoreCompile", ex.Message);
    } finally {
        TargetRuntime.MarkDone(
            ref T_123_CoreCompileState,
            ref T_123_CoreCompileCompletion);
    }
}
```

Target scheduling is deliberately conservative:

- `DependsOnTargets` and literal `BeforeTargets` prerequisites are expanded at codegen
  time. Consecutive static prerequisites are started on the ThreadPool and awaited as
  `Task.WhenAll(...)` batches.
- Dynamic `CallTarget` or mutated dependency properties fall back to the generated
  `Targets.Run(string)` dispatcher.
- If multiple prerequisite paths reach the same target concurrently, the first caller
  runs it and the others asynchronously await its completion task.
- `AfterTargets` companions run after the target body/up-to-date check.

## Generated state

- Properties are emitted as typed static fields on `P`.
- Item types are emitted as typed `List<Item>` properties on `I`.
- Empty item groups are lazy to avoid allocating hundreds of empty lists at startup.
- Task helper methods read generated global state (`P.*`, `I.*`) directly rather than
  receiving long `p0`, `p1`, ... argument lists.
- Generated locals use descriptive names.

## Task execution

Structural/simple tasks are hand-rolled in the generated host, including:

`Message`, `MakeDir`, `WriteLinesToFile`, `Touch`, `Delete`, `Copy`, `Error`,
`Warning`, `ConvertToAbsolutePath`, `RemoveDir`, `CreateProperty`, `CreateItem`,
`FindUnderPath`, `ReadLinesFromFile`, `Exec`, and `Hash`.

For hand-rolled structural tasks, codegen supports a narrow MSBuild task-batching
subset: a single batching metadata dimension, qualified or inferable unqualified
metadata references, `%(Identity)`, task `Condition` evaluation per batch, multiple
item lists sharing the same metadata, and pass-through item lists without that
metadata. Runtime batch execution remains sequential to preserve MSBuild ordering
and mutation semantics.

Real SDK tasks are represented as `TaskInvocation` objects and sent to the persistent
task server over a length-prefixed JSON stream. The task server:

- loads SDK task assemblies on CoreCLR,
- creates task instances with reflection,
- sets `BuildEngine` and .NET 11 `TaskEnvironment`,
- maps inputs/outputs through `TaskModel.cs`,
- stays alive across many task invocations.

This keeps the generated NativeAOT host small and avoids dynamic assembly loading in
the NativeAOT process.

## What works

Validated path:

```bash
cd fixtures/console-net11
$BSHARP build --no-cache -v:quiet run
./.bsharp/build --no-restore -v:quiet run
```

Both commands print `Hello, World!`.

The prototype still contains fixture-oriented shortcuts around restore/assets-file
dependent SDK tasks and is not a complete MSBuild implementation.

## Repo layout

```text
DESIGN.md
README.md
regenerate.sh
fixtures/
  console-net11/
tools/
  bsharp/
  codegen/
```
