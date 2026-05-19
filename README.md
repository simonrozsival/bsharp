# bsharp

> Compile MSBuild target XML into specialized C# and publish a cached ReadyToRun build host.

This is a research playground. See [`DESIGN.md`](./DESIGN.md) for the original architecture brief and the implementation notes section at the top of it.

## Quick start

Requirements: macOS arm64, .NET SDK 11.0.100-preview.4.26230.115 (or compatible .NET 11 SDK).

```bash
./regenerate.sh                                  # default: fixtures/console-net11/console-net11.csproj
./regenerate.sh path/to/YourProject.csproj
```

Once built once, the workflow is:

```bash
cd path/to/your-project/
BSHARP_CODEGEN=.../tools/codegen/bin/Debug/net11.0/Codegen \
  .../tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp build
```

Set `BSHARP_CODEGEN` to point to either the `Codegen` executable or `Codegen.dll`.
DLL paths are run through `dotnet`; executable paths are invoked directly.

## Two-binary architecture

```
bsharp                    # the launcher (Native AOT, ~2.2 MB).
                          # parses args, hashes project, checks .bsharp/ cache,
                          # codegens+publishes on miss, execs .bsharp/build.

codegen.dll               # the codegen tool (managed; uses MSBuild.Evaluation).
                          # Invoked by the launcher only on cache miss.

project_dir/.bsharp/      # per-project cache (gitignore-able)
├── shape.hash            # SHA-256 of csproj + Directory.Build.* + global.json + -p flags + mode
├── src/                  # generated C# (debuggable)
│   ├── Program.cs        # ~9000 lines for HelloConsole
│   └── BsharpGenerated.csproj
└── build                 # symlink to the published binary
```

## Commands and flags

| Command                          | Behavior                                                       |
|----------------------------------|----------------------------------------------------------------|
| `bsharp build`                   | Ensure binary is current; exec `.bsharp/build build`           |
| `bsharp run`                     | Same, then `.bsharp/build run`                                 |
| `bsharp build --no-cache`        | Force regen + republish                                        |
| `bsharp build path/to/X.csproj`  | Explicit project                                               |
| `bsharp build -p:Configuration=Release`  | Closed-world property override; folds into cache key |
| `bsharp build -v normal`         | Verbosity: `quiet`/`minimal`/`normal`/`detailed`/`diagnostic`  |
| Any other flag                   | Forwarded verbatim to the per-project binary                   |

Verbosity is gated at the generated binary level. `quiet` suppresses the summary line; `normal` and above print task starts as `[<elapsed-ms>] <TaskName>`.

## Cache invalidation

The launcher recomputes the shape hash on every invocation. Cache miss triggers when any of these change:

- the target `.csproj`
- `Directory.Build.props` / `Directory.Build.targets` (any ancestor)
- `Directory.Packages.props` (any ancestor)
- `global.json` (any ancestor)
- the set of `-p:X=Y` global properties passed on the command line

NOT currently tracked (known gaps):

- `project.assets.json` (transitive package versions)
- `ProjectReference` recursive (multi-project solutions)

## Publish mode

On cache miss, the launcher publishes the generated host as NativeAOT and publishes one
persistent CoreCLR task server for real SDK tasks:

| Component | Publish shape | Purpose |
|-----------|---------------|---------|
| Generated host | `PublishAot=true`, single-file, self-contained | Fast startup and generated target/property/item logic |
| Task server | CoreCLR ReadyToRun, framework-dependent | Persistent execution of real SDK `UsingTask` tasks |

The older per-task sub-CLI experiment has been removed; all non-native `UsingTask`
execution goes through the persistent task server.

## Generated code shape

For each MSBuild target, codegen produces a static method:

```csharp
static bool _T_236_Build_ran;
public static void T_236_Build() {
    if (_T_236_Build_ran) return;
    _T_236_Build_ran = true;
    try {
        if (!cond) return;
        T_040_BeforeBuild();                     // $(BuildDependsOn) statically expanded
        T_231_CoreBuild();
        T_232_AfterBuild();
        T_039__CheckForInvalidConfigurationAndPlatform();    // literal Before-companions
        // ...
        T_259__CheckContainersPackage();                      // literal After-companions
        T_266__PackAsBuildAfterTarget();
    } catch (Exception ex) {
        Errors.Add(("Build", ex.Message));
    }
}
```

Key invariants:

- **Execute-once via `static bool _T_NNN_<name>_ran`** — no central scheduler.
- **`DependsOn=$(Prop)` is statically expanded** at codegen time if `Prop` is never mutated by any target body (incl. task `<Output PropertyName=.../>`). For HelloConsole all 267 targets are fully static → the `Targets.Run(string)` switch dispatcher is never emitted, the entry is `Targets.Build() => T_236_Build();`.
- **Before/AfterTargets** are reverse-indexed at codegen time into the parent target's prologue/epilogue.
- **Per-target try/catch** around the whole method body — task errors and codegen-time-error stubs all attribute to the right target.
- **Properties** baked as static field initializers (`public static string Configuration = "Debug";`).
- **Items** baked as collection-literal initializers (`public static List<Item> KnownFrameworkReference = [new Item("X", new() { ["meta"] = "v" }), ...];`). Deduplicated by Identity+metadata so SDK overlapping conditional ItemGroups don't multiply entries.

## Performance

| Scenario                                 | Wall-clock | Internal     | Notes                                       |
|------------------------------------------|------------|--------------|---------------------------------------------|
| `dotnet build --no-restore` warm no-op   | ~900 ms    | —            | After `dotnet restore`                      |
| `.bsharp/build` warm no-op               | ~200 ms    | ~170-230ms   | Generated R2R host, no launcher             |
| `bsharp build` cache hit                 | ~320 ms    | ~170-230ms   | Native launcher + generated host            |
| `bsharp build` first run (cache miss)    | ~30-35s    | —            | dominated by `dotnet publish` R2R           |

The warm no-op path now enters `CoreCompile` but the hand-rolled `Csc` task performs its own timestamp check and returns without launching Roslyn. That avoids the previous ~1.6s warm-build cost.

Future: drop the double-spawn via `exec()` on Unix to get under 50ms.

## What works / doesn't

`bsharp build` produces `Hello, World!` end-to-end on the `fixtures/console-net11` fixture targeting `net11.0`. The generated build host runs the full emitted DAG and lazily loads real SDK task assemblies in-process through `AssemblyLoadContext`.

Currently 0 `// codegen failed:` markers on HelloConsole. The prototype still contains HelloConsole-specific shortcuts for restore/assets-file-dependent SDK tasks and is not a general MSBuild replacement.

Tasks implemented natively in `Tasks` class include `Message`, `MakeDir`, `WriteLinesToFile`, `Touch`, `Delete`, `Copy`, `Error`, `Warning`, `ConvertToAbsolutePath`, `RemoveDir`, `CreateProperty`, `CreateItem`, `FindUnderPath`, `ReadLinesFromFile`, `Exec`, `Csc`, `Hash`, and a few minimal SDK-specific shims. Other SDK tasks are invoked in-process from their SDK assemblies.

Csc finds `csc.dll` via the codegen-time-baked `P.RoslynTargetsPath` (no `Assembly.Location`).

## Repo layout

```
DESIGN.md
README.md
regenerate.sh                    # dev convenience: build everything + invoke bsharp
fixtures/console-net11/          # POC project
tools/
├── bsharp/                      # launcher (AOT)
└── codegen/                     # MSBuild XML → C#
```
