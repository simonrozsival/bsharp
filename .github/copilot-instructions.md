# Copilot instructions for bsharp

## Commands

This repository targets macOS arm64 with a .NET 11 preview SDK. The tool projects each have a `global.json` pinned to `11.0.100-preview.4.26230.115` with latest-patch roll-forward.

Build the development toolchain and run the default console fixture:

```bash
./build.sh
./build.sh fixtures/console-net11/console-net11.csproj
```

Build the two tools directly:

```bash
dotnet build tools/codegen/Codegen.csproj -c Debug
dotnet publish tools/bsharp/Bsharp.csproj -c Release -r osx-arm64
```

Run the console fixture with freshly built tools:

```bash
export BSHARP_CODEGEN="$PWD/tools/codegen/bin/Debug/net11.0/Codegen"
tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp build --no-cache -v:quiet fixtures/console-net11/console-net11.csproj
```

Run a single smoke path after the generated fixture binary exists:

```bash
cd fixtures/console-net11
./.bsharp/build --no-restore -v:quiet build
./.bsharp/build --no-restore -v:quiet run
```

Audit one project shape without generating a host:

```bash
dotnet build tools/codegen/Codegen.csproj -c Debug
tools/codegen/bin/Debug/net11.0/Codegen --audit --project fixtures/maui-net11/MauiNet11.csproj -p TargetFramework=net11.0-android
```

## Architecture

`tools/bsharp` is the NativeAOT launcher. It parses `build`, `run`, and `audit`, resolves the target `.csproj`, sorts `-p:X=Y` global properties for stable hashing, computes the shape hash, manages the project-local `.bsharp/` cache, invokes codegen on cache misses, publishes the generated NativeAOT host plus CoreCLR task server, and then runs `.bsharp/build`. For multi-target outer builds, it creates per-TargetFramework inner hosts under `.bsharp/inner/<tfm>/` and writes an outer dispatcher.

`tools/codegen` is the managed MSBuild frontend and emitter. It registers MSBuild defaults, loads the project with `Microsoft.Build.Evaluation.ProjectCollection`, creates a `ProjectInstance`, computes the reachable target sequence, scans `UsingTask` registrations, loads task metadata with `MetadataLoadContext`, and emits `.bsharp/src/Program.cs`, `TaskModel.cs`, `BsharpGenerated.csproj`, and `task-server/`.

The generated host keeps structural MSBuild behavior in-process and delegates real SDK tasks to a persistent CoreCLR task server over length-prefixed JSON using the shared `TaskModel`. The host emits typed static property/item state (`P.*`, `I.*`), async target methods with execute-once guards, a `Targets.Run(string)` dispatcher only when dynamic target execution is needed, and fast no-op checks before falling into full target execution.

`fixtures/console-net11` is the validated smoke fixture. `fixtures/maui-net11` is the long-term stress target; its `bsharp-baseline.json` records useful audit/build commands and current blockers.

## Repository conventions

Use `README.md` and the top "Implementation status" section of `DESIGN.md` as the current design summary. Later sections of `DESIGN.md` preserve the original draft and can lag the implementation.

Generated outputs are ignored (`.bsharp/`, `bin/`, `obj/`, `artifacts/`). Do not patch `.bsharp/src` directly; change `tools/codegen/Program.cs` so regenerated hosts get the fix.

This is a research playground for a closed-world MSBuild subset, not a general MSBuild replacement. Prefer explicit audit diagnostics or narrow unsupported-task failures over broad compatibility fallbacks that hide incorrect build behavior.

Global properties are closed-world inputs. Any new `-p` behavior must flow through the launcher hash, codegen `ProjectCollection` globals, and generated `InitialState.Populate` overrides so cache hits remain shape-correct.

When adding project-shaping inputs, update cache invalidation in both the launcher (`ComputeShapeHash`) and generated fast no-op shape inputs. Current shape inputs include the project file, ancestor `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, and sorted `-p:X=Y` values.

Task support has two paths: hand-rolled structural tasks in the emitted `Tasks` helper, and real SDK tasks via metadata-driven task-server invocation. If a task is implemented locally, keep `KnownTasks`, `_keepHandrolled`/`ForceLocalTaskImplementation`, parameter/output handling, and the emitted helper body consistent.

Target dependency execution is intentionally conservative: literal `DependsOnTargets` run sequentially because later targets can consume property/item mutations from earlier ones. Dynamic target names and `CallTarget` require the generated `Targets.Run(string)` dispatcher, which roots all emitted target methods.
