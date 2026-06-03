# B# — compiling a closed-world subset of MSBuild into a specialized build host

> **TL;DR.** B# (`bsharp`) pre-evaluates a known `.csproj` *shape* with the
> MSBuild evaluation API and **code-generates a specialized, NativeAOT-compiled
> build host** for it. Real SDK `UsingTask`s are delegated to a persistent
> CoreCLR task server that is **bundled next to the host**, so the generated
> `.bsharp/build` is a **self-contained binary you run directly** — no launcher,
> no env vars. On a simple `net11.0` console app the warm inner loop is
> **~57 ms vs ~1.0 s** for `dotnet build --no-restore` (**~18×**), at the cost of
> a one-time ~30–80 s NativeAOT publish per project shape.
>
> B# is structured **like a compiler**: `bsharp <project>` *compiles the build*
> (run once per shape, like `./configure`); `.bsharp/build` *is the compiled
> build* (run every inner-loop iteration).
>
> This is a **research playground**, not an MSBuild replacement. It deliberately
> implements a documented closed-world subset and rejects (or shims) the rest.

---

## 1. Motivation

`dotnet build` on an unchanged project still does a surprising amount of work:

_Note: My understanding of how MSBuild is limitted, this might not be accurate._

- **Evaluation every time.** MSBuild re-parses the project + the entire SDK
  import closure, re-evaluates conditions, properties, item globs, and builds
  the target graph from scratch on every invocation. For a trivial console app
  the evaluated shape is already **~735 properties, ~400 item types, ~500
  targets, 199 `UsingTask` registrations across 14 task assemblies** — all
  recomputed cold each run.
- **Dynamic everything.** Properties live in `Dictionary<string,string>`, items
  are boxed metadata bags, tasks are loaded by reflection through `UsingTask`,
  the target DAG is topologically sorted at runtime.
- **Process + runtime startup.** A fresh CoreCLR MSBuild process (or a node
  reuse handshake) pays JIT/startup costs on every cold invocation.

But during active development the **project shape rarely changes** — you edit
`Program.cs` a hundred times before you touch the `.csproj`. That is a
**closed-world** situation: if we freeze the shape, almost all of that dynamic
work is redundant.

**The B# bet:** treat a fixed project shape as a *compile-time constant*.
Evaluate it once, emit specialized C# that hardcodes the target DAG, bakes
property/item state into typed fields, and statically dispatches tasks — then
NativeAOT-compile that into a per-project build binary. Pay a big one-time
compilation cost to make every subsequent warm build cheap.

This is the same idea as AOT/source-generation applied to the *build itself*.

---

## 2. Results (fresh, this machine)

macOS 26.5 (arm64), .NET SDK `11.0.100-preview.4.26230.115`,
`fixtures/console-net11`, ~20 runs/cell. Wall time is noisy on macOS, so values
are **median ms** (min in prose) — trust the **ratios**; absolutes carry ±20–30%.

`.bsharp/build` is run **directly** (no launcher). Baseline = `dotnet build` with
restore; the `restore` row baseline is `dotnet restore`.

| Scenario | `.bsharp/build` (best, median) | vs `dotnet build --no-restore` | vs `dotnet build` (restore on) |
|---|---:|---:|---:|
| **noop** | **57** (no-restore, fast-noop) | 1014 → **~18×** | 1534 → **~27×** |
| **incremental** | **138** (no-restore) | 994 → **~7×** | 1522 → **~11×** |
| **clean** | **342** (fast-restore) | — | 1558 → **~4.5×** |
| **restore** (`build restore`) | **245** | — | 1020† → **~4.2×** |

† restore baseline is `dotnet restore`. bsharp wins **every** cell (**~4–27×**),
plus a one-time **~30–80 s** NativeAOT host publish the first time a shape is seen
(cache-keyed; amortized thereafter).

**Dropping the launcher.** Earlier numbers carried a ~75–150 ms launcher→host
`Process.Start` hop. That hop is now **gone**: the generator bundles the task
server into `.bsharp/`, so the customer runs the self-contained `.bsharp/build`
directly. The warm no-op is now **~57 ms** — the host's actual work — instead of
~150 ms. (Direct vs launcher overhead measured at ~62–128 ms, median ~75 ms.)

**About fast-noop (automatic; disable with `--no-fast-noop`).** With the launcher
gone, the host-level shortcut now shows up directly in wall time: a warm no-op is
**~57 ms** (fast-noop, `cumulative tasks: 0.00ms`) vs **~110 ms** with
`--no-fast-noop` (full target graph, ~112 ms of task work). Detection is purely
mtime-based and conservative: a bare `touch` triggers a full rebuild, and editing
source content is never wrongly skipped (verified). On clean/incremental builds
fast-noop never triggers, so it's irrelevant there.

**Three-level restore.** Default = **fast restore** (skip when
`obj/project.nuget.cache` is newer than every dependency-affecting input);
`--no-fast-restore` = force a full in-process restore; `--no-restore` = skip. All
use B#'s **in-process** restore through the bundled task server, which is
**~4× faster than `dotnet restore`** head-to-head (245 ms vs 1020 ms median).

**Reading the numbers honestly** (the part a build engineer should push on):

- The warm no-op is **~57 ms** of real host work, now that there is no subprocess
  hop. The remaining cost is CoreCLR startup of the host itself.
- The win is **broad, not cherry-picked**: every build type is faster once the
  host exists, restore included or excluded.
- The tradeoff of dropping the launcher: **shape-change detection** (the shape
  hash) lived in the launcher, so a csproj/`Directory.Build.*`/`global.json`/`-p`
  change now means **re-running the generator** (it's a compile-time constant).
  Source edits stay on the host's fast path.
- The price is a **one-time NativeAOT publish per project shape** (~30–80 s),
  cache-keyed and only re-paid when the shape actually changes.

---

## 3. How it works

Two phases, like a compiler — a generator you run once per shape, and a
self-contained compiled build you run every inner loop:

```
PHASE 1 — compile the build (once per shape)
bsharp <project> (NativeAOT generator — NOT on the hot path)
   │  parse args, find .csproj, compute shape hash, manage .bsharp/ cache
   │  on cache miss → invoke codegen, publish host, BUNDLE task server into .bsharp/
   ▼
tools/codegen (managed; references Microsoft.Build.Evaluation)
   │  evaluate project, derive target DAG, scan UsingTasks (MetadataLoadContext)
   │  emit Program.cs + TaskModel.cs + BsharpGenerated.csproj + task-server/

PHASE 2 — run the compiled build (every inner loop)
.bsharp/build (generated NativeAOT host, SELF-CONTAINED)  ── JSON/IPC ──▶  bsharp-taskd
   structural state + target methods + hand-rolled tasks   (CoreCLR task server,
   no launcher · no env vars · no subprocess hop            bundled right alongside)
```

### 3.1 The generator (`tools/bsharp`, NativeAOT)

Run **once per shape** (`bsharp <project>`). Parses `build`/`run`/`audit`,
resolves the target `.csproj`, computes a **shape hash**, and either reuses or
regenerates the project-local `.bsharp/` cache. On a miss it runs codegen,
`dotnet publish`es the host, and **copies `bsharp-taskd` into the host's publish
directory** so the resulting `.bsharp/build` is self-contained (`BundleTaskDaemon`).
It also supplies low-noise closed-world global-property defaults (e.g.
`EnableSourceControlManagerQueries=false`, `EnableSourceLink=false`,
`SuppressNETCoreSdkPreviewMessage=true`) unless you override them with `-p`.

Because the generator is not on the hot path, the customer runs `.bsharp/build`
directly thereafter. The flip side: the **shape hash lives here**, so a shape
change is detected only when you re-run the generator (the inner loop is source
edits, which the host handles via fast-noop / fast-restore).

**Shape hash inputs** (a miss on any of these regenerates the host): the
`.csproj`; statically discoverable `ProjectReference` projects and imported
`.props`/`.targets`; ancestor `Directory.Build.props` / `Directory.Build.targets`
/ `Directory.Packages.props` / `NuGet.config` / `global.json`;
`packages.lock.json`; `obj/project.assets.json`; sorted `-p:X=Y` globals; and an
internal `ShapeHashVersion` bumped when host semantics change. There is a `stat`
fast path that walks the same import/reference graph to validate freshness
without rehashing contents.

Global-property variants are isolated: bare builds use `.bsharp/build`,
non-`TargetFramework` globals use `.bsharp/variants/<hash>/`, and per-TFM inner
builds live under `inner/<tfm>/`.

### 3.2 Codegen (`tools/codegen`, managed)

Runs only on a cache miss. It registers MSBuild defaults, loads the project with
`Microsoft.Build.Evaluation.ProjectCollection`, materializes a `ProjectInstance`,
computes the reachable target sequence, scans `UsingTask` registrations, loads
task metadata with **`MetadataLoadContext`** (no execution), and emits:

- `Program.cs` — the build host
- `TaskModel.cs` — the typed input/output contract shared with the task server
- `BsharpGenerated.csproj` — host project (R2R/NativeAOT)
- `task-server/` — the CoreCLR sidecar project

Note it goes **directly through the evaluation API**, not `-pp` preprocessing or
binlog: those don't give a faithful evaluated model of conditions/globs and were
redundant once evaluation became the frontend.

### 3.3 Generated state & target model

- **Properties** become typed static fields on a `P` class, initializers baked
  from evaluation (`P.Configuration = "Debug"`, …). Runtime-dependent paths
  (`MSBuild*`, `ProjectDir`) are filled by `InitialState.Populate(csproj)`.
  `-p` globals are re-applied there, overriding csproj defaults so cache hits
  stay shape-correct.
- **Items** become typed `List<Item>` on an `I` class, with collection-literal
  initializers, deduped by Identity+metadata. Empty groups are lazy.
- **Each target** becomes an `async ValueTask T_NNN_<name>()` with an
  execute-once guard (`TargetRuntime.TryEnter/MarkDone`). Literal
  `DependsOnTargets`/`BeforeTargets` prerequisites are expanded at codegen time
  and started as `Task.WhenAll(...)` batches on the ThreadPool; `AfterTargets`
  companions run after the body/up-to-date check.
- **Static dependency expansion:** `DependsOnTargets="$(Prop)"` is resolved at
  codegen time when `Prop` is never mutated by any target body. For the console
  fixture *every* target is fully static-expandable, so the dynamic
  `Targets.Run(string)` string dispatcher is **never even emitted** — only the
  reachable methods survive, which the trimmer/NativeAOT compiler then prunes.
- A **fast no-op path** does a timestamp check up front and returns before
  restore/build target execution when outputs are already current.

### 3.4 Tasks — the hybrid

Two execution paths:

1. **Hand-rolled structural tasks** compiled straight into the host:
   `Message`, `MakeDir`, `WriteLinesToFile`, `Touch`, `Delete`, `Copy`,
   `Error`, `Warning`, `ConvertToAbsolutePath`, `RemoveDir`, `CreateProperty`,
   `CreateItem`, `FindUnderPath`, `ReadLinesFromFile`, `Exec`, `Csc`, `Hash`.
   These support a narrow MSBuild **task-batching** subset (one metadata
   dimension, qualified/unqualified metadata, `%(Identity)`, per-batch
   conditions, shared/pass-through lists), executed sequentially to preserve
   MSBuild ordering/mutation semantics.
2. **Real SDK `UsingTask`s** (e.g. `Csc`, `ResolvePackageAssets`,
   `GenerateDepsFile`, `CreateAppHost`) are serialized as `TaskInvocation`s and
   sent to a **persistent CoreCLR task server** (`bsharp-taskd`) over a
   length-prefixed JSON stream typed by `TaskModel.cs`. The server loads task
   assemblies into per-directory `AssemblyLoadContext`s (unifying
   `Microsoft.Build.Framework/.Utilities.Core/.Build` with the host so
   `IBuildEngine`/`ITaskItem` identity stays valid), sets `BuildEngine` +
   .NET 11 `TaskEnvironment`, and stays alive across invocations.

**Why split runtimes?** The generated host wants to be NativeAOT (fast startup,
no JIT). But real SDK tasks require dynamic assembly loading and reflection,
which NativeAOT can't do. Isolating the dynamic code in a separate persistent
CoreCLR process keeps the host AOT-clean while still running stock SDK tasks.
(Even `Csc` is optimized: on a warm no-op the hand-rolled wrapper does a
task-level timestamp check and returns without launching Roslyn, because the SDK
puts `$(NonExistentFile)` in the target outputs and would otherwise always run.)

---

## 4. `bsharp audit`

`bsharp audit` (or `Codegen --audit`) evaluates a project and prints a JSON
shape report *without* generating a host: target/task counts, outer-build
detection, `CallTarget`/`<MSBuild>` task sites, dynamic imports, batching
expressions, property functions, and `UsingTask` resolution issues. It's the
bring-up/triage tool for deciding whether a larger SDK (MAUI is the north star)
fits the supported subset yet.

---

## 5. Known limitations

- **Closed-world subset by design.** Anything outside the documented subset is
  rejected with a diagnostic or shimmed — not silently approximated. Validated
  shape today: **console apps and class libraries** on `net11.0`.
- **Not yet supported:** MAUI, Blazor/web, `watch`, target batching (explicitly
  rejected), custom inline `UsingTask TaskFactory`, cross-language projects,
  NuGet packages that contribute new targets/`UsingTask`s.
- **Restore is delegated.** SDK restore-graph recursion stays out of the
  compiled subset; static `ProjectReference` graphs are pre-restored with
  `dotnet restore`, then the host runs `--no-restore`. There are still
  fixture-oriented shortcuts around assets-file-dependent SDK tasks.
- **One-time cost is large.** ~30–80 s NativeAOT publish per shape; brutal if
  your project shape churns constantly.
- **No hot-path shape detection.** Dropping the launcher means a **shape change**
  (csproj / `Directory.Build.*` / `global.json` / `-p` globals) is only picked up
  when you re-run the generator — `.bsharp/build` itself trusts its baked shape.
- **Platform.** macOS arm64, .NET 11 preview only. Cache invalidation has known
  gaps (dynamic imports and property-expanded `ProjectReference` paths aren't
  statically hashed; final-project NuGet packages are assumed not to add
  targets).

---

## 6. Potential improvements

- ✅ **Killed the subprocess hop.** The generator bundles the task server into
  `.bsharp/`, so the customer runs the self-contained `.bsharp/build` directly —
  the warm no-op dropped from ~150 ms (launcher) to **~57 ms** (host's own work).
- **Re-instate shape detection without a hot-path launcher:** e.g. bake a small
  mtime guard into the host that prints "shape changed — re-run the generator"
  instead of silently trusting a stale shape.
- **Shrink the one-time cost:** R2R-only or partial-AOT host; cache/share the
  publish across shapes; background regeneration (already prototyped via
  `--background-codegen` / `BSHARP_BACKGROUND_CODEGEN=1`).
- **Broaden the supported subset:** more in-proc tasks, target parallelism
  within a project, multi-project/solution shapes, and ultimately the MAUI
  shape (the long-term stress target).
- **Correctness oracle:** widen the gated MSBuild E2E corpus / differential
  testing against stock MSBuild so subset claims are continuously verified.

---

## 7. Try it (macOS arm64, .NET 11 preview)

```bash
# 1. Build the toolchain (codegen + generator + task daemon)
./build.sh

# 2. Point at the tools. BSHARP_TASKD_PATH lets the GENERATOR find the task
#    server to bundle into .bsharp/ (the compiled build needs no env vars).
export BSHARP="$PWD/tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp"
export BSHARP_CODEGEN="$PWD/tools/codegen/bin/Debug/net11.0/Codegen"
export BSHARP_TASKD_PATH="$PWD/tools/bsharp-taskd/bin/Release/net11.0/osx-arm64/publish/bsharp-taskd"

# 3. Compile the build once (one-time, slow: generates + publishes + bundles taskd)
cd fixtures/console-net11
$BSHARP build console-net11.csproj

# 4. Run the compiled build DIRECTLY — no launcher, no env vars (~57 ms warm no-op)
./.bsharp/build run                 # prints program output
./.bsharp/build --no-restore build  # ~57 ms

# 5. Inspect the evaluated shape without generating a host
$BSHARP audit
```

---

*B# is an opinionated fast path for project shapes that opt in, built to find
out how much of a build can be turned into a compile-time constant. It is not,
and is not trying to be, a general MSBuild replacement.*
