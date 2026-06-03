# B# (bsharp) — Architecture Deep Dive

> A precise, code-grounded walkthrough of how the pre-built build program works:
> the three processes, what feeds the code generator, the shape of the generated
> C#, how a single target is emitted, the optimizations, and how "fast restore"
> and "fast no-op" actually decide to skip work. Written for deep .NET / MSBuild
> engineers.
>
> All line references are against the checked-in generated host for the console
> fixture (`fixtures/console-net11/.bsharp/Program.cs`, ~42k lines) and the two
> tool projects (`tools/codegen`, `tools/bsharp`, `tools/bsharp-taskd`).

---

## 1. High-level architecture: two phases (generator + self-contained build)

B# replaces `dotnet build <project>` for a *known project shape* with a compiled,
project-specialized binary. It works in **two phases, like a compiler**:

- **Phase 1 — "compile the build" (`bsharp <project>`).** A NativeAOT *generator*
  that you run **once per shape** (like `./configure`/`cmake`). It hashes the
  shape, manages the `.bsharp/` cache, and on a miss runs codegen + `dotnet
  publish` to produce the host **and bundles the task server next to it**.
- **Phase 2 — "run the compiled build" (`.bsharp/build`).** A **self-contained**
  binary you run on **every inner-loop iteration**. No launcher, no env vars, no
  subprocess hop.

```
PHASE 1 — compile the build (once per shape)
        ┌─────────────────────────────────────────────────────────────┐
        │ bsharp <project>  (NativeAOT generator — NOT on the hot path)│
        │   - parses `build|run|restore|audit` + -p:X=Y + -v           │
        │   - computes the SHAPE HASH (project + ancestor props/        │
        │     targets + Directory.Packages.props + global.json +        │
        │     sorted -p:X=Y)                                            │
        │   - manages the project-local `.bsharp/` cache                │
        │   - on cache MISS: runs codegen, `dotnet publish` the host    │
        │     (ReadyToRun/NativeAOT), and COPIES bsharp-taskd into the   │
        │     publish dir so `.bsharp/build` is self-contained          │
        └─────────────────────────────────────────────────────────────┘

PHASE 2 — run the compiled build (every inner loop)
        ┌─────────────────────────────────────────────────────────────┐
        │ .bsharp/build  (generated host, ReadyToRun, SELF-CONTAINED)  │
        │   - the XML→C# compiled MSBuild slice for THIS project shape  │
        │   - static P (properties), I (items), Targets, Tasks         │
        │   - fast-restore / fast-noop gates                           │
        │   - finds its bundled bsharp-taskd sibling automatically     │
        └───────────────┬─────────────────────────────────────────────┘
                        │ Unix domain socket, length-prefixed JSON
                        ▼
        ┌─────────────────────────────────────────────────────────────┐
        │ bsharp-taskd  (persistent CoreCLR task server, BUNDLED)      │
        │   - copied next to the host during Phase 1                   │
        │   - loads real task assemblies via AssemblyLoadContext       │
        │   - reflection-instantiates the MSBuild ITask, sets props,   │
        │     provides an IBuildEngine, calls task.Execute()           │
        │   - returns outputs; idles out after N minutes               │
        └─────────────────────────────────────────────────────────────┘
```

**Why this split?**

- The **generator** does the expensive, infrequent work (evaluate MSBuild, emit
  ~42k lines of C#, NativeAOT publish). It is the analogue of a compiler front
  end; you invoke it when the *shape* (a compile-time constant) changes.
- The **compiled build** is the hot path. Because it is self-contained (the task
  server is bundled as a sibling), it needs no launcher and no environment setup
  — the customer runs `.bsharp/build` directly.
- The **task server** exists because real SDK tasks (Csc, ResolveAssemblyReferences,
  GenerateDepsFile, …) are CoreCLR assemblies that need JIT, `IBuildEngine`, and
  `AssemblyLoadContext` isolation. Paying that startup once and keeping the
  process resident is dramatically cheaper than reloading per build.

**Bundling (the enabling change).** During Phase 1, after the host publish
succeeds, the generator copies the framework-dependent `bsharp-taskd` publish
output (apphost + `.dll` + `.deps.json` + `.runtimeconfig.json`) into the host's
publish directory (`BundleTaskDaemon` in `tools/bsharp/Program.cs`). The host's
`ResolveDaemonExecutable()` (host `Program.cs:1154`) then finds it as a sibling
of the running binary — so `.bsharp/build` runs standalone with zero env vars.
`BSHARP_TASKD_PATH` is still honored as an override.

**The dropped launcher hop.** Earlier measurements showed the launcher→host
`Process.Start` + `WaitForExit` cost ~75–150 ms on macOS. By invoking
`.bsharp/build` directly we eliminate it entirely: a warm no-op drops from
~0.15 s to ~0.057 s — and that ~57 ms *is* the host's own work.

**The tradeoff.** The shape-hash freshness check lives in the **generator**.
Running `.bsharp/build` directly skips it, so a **shape change** (csproj,
`Directory.Build.*`, `global.json`, `-p` globals) is **not** auto-detected on the
hot path — you re-run the generator. This is consistent with the thesis: the
shape is a compile-time constant; when it changes, you recompile. Source edits
stay on the fast path (the host's own fast-noop / fast-restore gates handle them).

### Task server protocol

- One Unix domain socket per **SDK fingerprint** (`DaemonPaths.GetSocketPath`).
  Different SDKs ⇒ different daemons, so task binaries never mismatch.
- Frames are **length-prefixed** (4-byte little-endian length + UTF-8 JSON
  payload; `FrameProtocol` in taskd, `DaemonClient` in the host at
  `Program.cs:1033`).
- Handshake first (`HandshakeRequest`/`HandshakeResponse`), then a request loop:
  client sends `TaskInvocation`, daemon replies `TaskResult` (`Program.cs:261`
  loop in taskd).
- The host **prewarms** the connection: `TaskRunner.StartConnectionPrewarm()` is
  kicked off inside `InitialState.Populate` (`Program.cs:10170`) so daemon spawn
  (~50 ms cold) overlaps with project population, and the socket is ready before
  the first task fires.
- Execution is serialized in the daemon (`ExecutionLock`,
  `taskd/Program.cs:296`): Console out/err are redirected to null and cwd is set
  per-invocation then restored to a stable home dir (the client may delete its
  cwd between calls).

---

## 2. Input data for the generated C#

Codegen (`tools/codegen`) is the only managed-MSBuild part. It is invoked **only
on a cache miss**. Its IR is the **MSBuild evaluation model**, not a binlog and
not `-pp` preprocessing:

1. **Register MSBuild defaults**, create a
   `Microsoft.Build.Evaluation.ProjectCollection` with the launcher's sorted
   `-p:X=Y` as `GlobalProperties`, and load the project.
2. Create a `ProjectInstance` and read the **fully-evaluated** state:
   - `AllEvaluatedProperties` → every property with its final value (after all
     conditions, imports, and SDK targets props have been applied).
   - `AllEvaluatedItems` → every item with identity + metadata.
   - The **target graph**: each `ProjectTargetInstance` with its `Condition`,
     `DependsOnTargets`, `BeforeTargets`, `AfterTargets`, `Inputs`, `Outputs`,
     and ordered child tasks/property-groups/item-groups.
3. **Scan `UsingTask` registrations** to know which task name maps to which
   assembly + type. Task *metadata* (settable properties, `[Output]` properties,
   parameter types) is loaded with a **`MetadataLoadContext`** (reflection-only;
   never executes task code at codegen time).
4. Decide, per task, **hand-rolled vs. server-delegated** (see §5/known-tasks).

Why the evaluation API and not `-pp`/binlog:
- `-pp:capture.xml` does **not** evaluate conditions/properties/globs — redundant
  once we commit to the evaluation API.
- Binlog was dropped so cold builds are purely XML-driven; the target DAG is
  derived topologically from `DependsOn/Before/AfterTargets` at codegen time.

The **closed-world contract** lives here: the generated host is only valid for
the exact shape that was evaluated. Any input that can change evaluation
(project file, ancestor `Directory.Build.props/.targets`, `Directory.Packages.props`,
`global.json`, sorted `-p:X=Y`) is folded into the launcher's shape hash, so a
changed input forces a cache miss and re-codegen.

---

## 3. High-level structure of the generated code

The generated `Program.cs` is one big file of `static` classes (HelloConsole
sizes in parentheses):

| Class | Role |
|---|---|
| `P` (~2k lines) | **Properties.** One typed `static string` field per evaluated property, initialized to the baked value. `P.Set/GetExtra` back the runtime XML PropertyGroup loop and unknown names. |
| `I` (~7k lines) | **Items.** `static List<Item>` per item type with collection-literal initializers (`[new Item("x", new(){["m"]="v"}), …]`), deduped by identity+metadata. |
| `InitialState` (~10.1k) | `Populate(csprojPath)` — sets `MSBuild*` reserved props + `ProjectDir`, re-reads csproj PropertyGroups (runtime override), applies the default Compile glob, re-applies global `-p:` props, and prewarms the task daemon. |
| `Targets` (~10.2k) | One pair of methods per target (guard + `_Core`), plus `RunBuildTarget(string)` dispatcher when dynamic dispatch is needed, and a `TargetRuntime` for execute-once/await semantics. |
| `Tasks` (~9.6k) | Hand-rolled structural task implementations (Message, Copy, MakeDir, Csc shim, …). |
| `TaskRunner` (~1.8k) | Task-server client: builds `TaskInvocation`, manages the `DaemonClient`, serializes set/get of typed parameters and `[Output]`s. |
| `Log` | Verbosity-aware logger (`TargetStarted/Finished/Skipped`, `Task`, build summary). |
| `FastPathFileHelpers` | Source-file enumeration + timestamp helpers for the fast paths. |
| `GeneratedProjectInfo` | Shape inputs + paths used by fast-restore / fast-noop. |

Entry flow: `Main` → parse host args (`--no-fast-noop`, `--no-fast-restore`,
`--no-restore`, `-t:`, `-v`) → fast-noop-before-populate gate → restore gate →
`InitialState.Populate` → `Targets.Build(requestedTargets)` →
`RunTargetOrError("Build")`. `Targets.Build` resolves to the entry target method
(`T_237_Build` for HelloConsole).

> **Note on a doc/code delta:** `DESIGN.md §0` still describes targets as
> `static void T_NNN_<name>()` with a `_ran` bool guard. The *actual* generated
> code has since moved to **`async ValueTask`** target methods with a
> `TargetRuntime.TryEnter/MarkDone` state machine (so concurrent awaiters of the
> same target block on a `TaskCompletionSource` instead of re-running). The
> structure below reflects the real emitted code.

---

## 4. Anatomy of a generated target

Every target compiles to **two methods** plus two static state fields:

```csharp
static int    T_NNN_<name>State;          // 0=unstarted, running, done, skipped
static TaskCompletionSource? T_NNN_<name>Completion;

public static async ValueTask T_NNN_<name>() {           // the GUARD
    // 1. EXECUTE-ONCE gate. First caller "enters"; later callers await.
    if (!TargetRuntime.TryEnter("<name>", ref T_NNN_<name>State,
                                ref T_NNN_<name>Completion, out var waitTask)) {
        if (waitTask is not null)
            await TargetRuntime.WaitForCompletionAsync("<name>", waitTask);
        return;
    }
    var targetExecuted = false;
    try {
        try {
            // 2. CONDITION gate (target's Condition attribute).
            if (!(<condition>)) {
                var s = Log.TargetStarted("<name>");
                Log.TargetSkipped("<name>", "condition was false");
                Log.TargetFinished("<name>", s);
                // NB: DependsOn + Before/After companions still run even when
                // the body is skipped (MSBuild semantics).
                return;
            }
            // 3. BeforeTargets companions + DependsOnTargets (sequential awaits).
            await T_..._BeforeXyz();
            await T_..._SomeDependency();
            targetExecuted = true;
            var start = Log.TargetStarted("<name>");
            // 4. BODY (tasks / property-groups / item-groups of this target).
            await T_NNN_<name>_Core();
            Log.TargetFinished("<name>", start);
            TargetRuntime.MarkDone(...);
            // 5. AfterTargets companions.
            await T_..._AfterXyz();
        } catch (Exception ex) {
            AddError("<name>", ex.Message);          // 6. error attributed to target
        }
    } finally {
        // 7. publish completion so awaiters unblock (done vs skipped).
        if (targetExecuted) TargetRuntime.MarkDone(...);
        else                TargetRuntime.MarkSkipped(...);
    }
}

static async ValueTask T_NNN_<name>_Core() {
    // the actual task calls, conditions, batching, item/property mutation
}
```

Concrete examples from the console fixture:

- **A leaf target with a condition + one task** —
  `T_001__CheckBrowserWorkloadNeededButNotAvailable` (`Program.cs:11398`). The
  guard checks the target condition (a `RuntimeIdentifier == browser-wasm && …`
  chain compiled to `string.Equals(..., OrdinalIgnoreCase)` calls); the `_Core`
  (`:11428`) runs a `Warning` task gated on `@(NativeFileReference)` count.
- **The `Build` meta-target** — `T_237_Build` (`Program.cs:25133`). Its `_Core`
  is **empty** (`:25181`); all the work is in its `DependsOnTargets`
  (`BeforeBuild` → `CoreBuild` → `AfterBuild`) and `AfterTargets` companions
  (`_CheckContainersPackage`, `_PackAsBuildAfterTarget`) that the guard awaits in
  order. This is the on-demand recursive model: there is no linear topological
  loop — calling `Build` pulls in exactly the reachable subgraph.

**Why `DependsOnTargets` run sequentially (not parallel):** later targets can
consume property/item mutations made by earlier ones (including via task
`<Output PropertyName=…/>`). The generated code awaits them one at a time to
preserve those data dependencies. Parallelizing would be unsound for the general
case.

**Inputs/Outputs gate:** when a target declares `Inputs`/`Outputs`, an
incremental up-to-date check is emitted around the body so an up-to-date target
skips its work (standard MSBuild incremental-build semantics, compiled inline).

---

## 5. Optimizations in the generated C#

**Codegen-time (the compiler does the work once, not every build):**

- **Static expansion of `DependsOnTargets="$(Prop)"`.** If `Prop` is never
  mutated by any target body (no task `[Output]` writes it, no property group
  reassigns it), codegen splits the value at generation time and emits **direct
  method calls** instead of a runtime string lookup + dispatch. For HelloConsole
  *every* target is fully static-expandable, so the `Targets.Run(string)` /
  `RunBuildTarget` dispatcher is dead and the trimmer drops it — only methods
  reachable from `Targets.Build()` survive.
- **Before/AfterTargets reverse-indexed at codegen time.** Only *literal*
  Before/After references are inlined as companion awaits; a dynamic
  `BeforeTargets="$(X)"` falls back to the runtime dispatcher (which is then
  emitted).
- **Conditional empty-list checks.** `'@(X)' != ''` compiles to
  `(I.X.Count != 0)` instead of
  `!string.IsNullOrEmpty(string.Join(";", I.X.Select(i => i.Identity)))` —
  removing two allocations per evaluation.
- **`!!` double-negation elimination** via a `NegateBoolExpr` helper at the
  condition-wrap site (so a negated `!= ''` doesn't become `!(!...)`).
- **Pure-literal Include/Remove.** A single literal with no `$`/`@`/`%` skips the
  `.Split(';')` parsing loop and is added directly.
- **Static field initializers** instead of runtime
  `if (string.IsNullOrEmpty(P.X)) P.X = "…";` passes. Property/item defaults are
  baked straight into field initializers; items are deduped by identity+metadata
  so TFM-conditional overlapping `ItemGroup`s don't multiply.
- **Typed property fields, no dictionary.** Every codegen-time property read uses
  a literal name → it binds to a typed `static string` field directly (or
  `P.GetExtra("…")` for genuinely unknown names). No per-read hashtable lookup.

**Runtime (host) micro-optimizations:**

- **Daemon connection prewarm** overlapped with `Populate` (§1).
- **cwd cached once per build** in `TaskRunner` (`_cachedCwd`,
  `Program.cs:822`) — `Directory.GetCurrentDirectory()` is a syscall and would
  otherwise cost ~5–10 µs *per task*.
- **`async ValueTask` targets** avoid `Task` allocation for the common
  synchronous-completion path.
- **ReadyToRun publish** so the host image is pre-JITted for fast startup.

**Honest note:** for a *warm no-op*, the generated-host work itself is sub-ms;
the wall time is dominated by `Process.Start`. The optimizations above matter
most for incremental/clean builds where targets actually execute.

---

## 6. How "fast restore" works

Restore has **three levels**, all implemented natively in the host (the launcher
no longer shells out to `dotnet restore`):

| Level | Flag | Behavior |
|---|---|---|
| Default | *(none)* | **Fast restore**: skip restore if the cache is provably fresh; otherwise do an in-process restore. |
| Force | `--no-fast-restore` | Always run the in-process restore (bypass the freshness check). |
| Skip | `--no-restore` | Don't restore at all (same as `dotnet build --no-restore`). |

All three use bsharp's **in-process restore** via the CoreCLR task server, which
is ~3× faster than the `dotnet restore` subprocess it replaced (~407–551 ms vs
~1100+ ms).

**The freshness check (`ShouldSkipRestore`)** compares modification times:

- Skip restore **iff** `obj/project.nuget.cache` exists **and** *no*
  dependency-affecting input is newer than it.
- Dependency-affecting inputs = the project file **+** the `FastNoOpShapeInputs`
  set walked up to the repo root: `Directory.Build.props`,
  `Directory.Build.targets`, `Directory.Packages.props`, and `global.json`.
  These are precisely the files that can change which packages/versions resolve.
- The comparison is **errs-toward-restoring**: if anything is missing or newer,
  we restore. False "stale" is cheap (an extra fast restore); false "fresh"
  would be a correctness bug, so we never risk it.

**The `project.nuget.cache` marker wart and the fix.** NuGet only rewrites
`project.nuget.cache` when the **dgspec hash changes** (content-based), *not* on
every restore. So a no-change restore would leave the marker's mtime older than
inputs that were merely touched, causing a *perpetual* re-restore. Fix:
`RunRestore` calls `TouchNuGetRestoreMarker` to **bump the marker's mtime** after
a successful restore. Bumping mtime is safe because freshness is purely a
timestamp comparison; the dgspec-hash content is unchanged and still valid.

This replaced an earlier **24-hour age heuristic** (skip restore if the marker is
< 24h old), which was unsound: a dependency edit within the window would be
silently ignored. The timestamp-vs-inputs model has no such blind spot.

---

## 7. How "fast no-op" works

Fast no-op is the "nothing changed, the output is already up to date" shortcut.
It is a **two-phase** check because some inputs are known before project
evaluation and some only after:

**Phase A — `FastNoOpBuildBeforePopulate`** (runs *before* `InitialState.Populate`,
i.e. before we evaluate the project):
- Only attempted when the project has **no Analyzer / AdditionalFiles /
  EditorConfigFiles / ProjectReference** — otherwise it is hard-wired to `false`
  (those inputs can't be checked without evaluation).
- Uses a **coarse `*.cs` glob** of the project directory as the input set.
- If every input is older than `TargetPath` (the output `.dll`), the build is a
  no-op and we exit *without ever evaluating the project* — the cheapest
  possible path.

**Phase B — `FastNoOpBuild`** (runs *after* `Populate`):
- Uses the **precise** input set from the evaluated items (actual `Compile`
  items, references, etc.) rather than the coarse glob.
- Same comparison: all inputs older than the output `.dll` ⇒ no-op.

Both phases compare input mtimes against the `TargetPath` assembly mtime. Fast
no-op is **disabled** by `--no-fast-noop`, `--no-build`, an explicit `-t:<target>`
(you asked for specific targets, so honor them), or the `restore` command.

**Important ordering subtlety:** fast-noop is checked **before** restore. So
`--no-fast-restore` *alone* cannot force a restore on an unchanged tree — Phase A
short-circuits first. To force a restore on an unchanged tree you need
`--no-fast-noop --no-fast-restore` together. (This matters for benchmarking the
"restore = yes" no-op cell honestly.)

**Fast restore vs fast no-op in one line:** fast *restore* decides whether
package assets need refreshing (inputs vs `project.nuget.cache`); fast *no-op*
decides whether the compiled output is already current (inputs vs `TargetPath`
`.dll`). They guard different outputs and are independent gates.

---

## 8. What works nicely

- **The closed-world compile is the right idea.** Treating one project shape as a
  compilation unit and baking evaluated state into typed fields removes the bulk
  of MSBuild's per-build evaluation cost. Warm no-op host work is sub-ms.
- **Static `DependsOn` expansion → trimmer-friendly call graph.** Because the
  whole console fixture expands statically, the dynamic dispatcher is dead code
  and the binary only contains reachable targets.
- **Hybrid task strategy.** Hand-rolling cheap structural tasks (Message, Copy,
  MakeDir, …) while delegating real SDK tasks to a *resident* CoreCLR daemon is a
  good split: structural work stays in-process and zero-overhead, heavy tasks
  reuse a warm runtime.
- **Timestamp-based fast restore/no-op gates** give a clean, debuggable
  up-to-date model with an explicit safety bias (always err toward doing work).
- **Three-level restore** maps cleanly onto real workflows (default fast / forced
  / skip) and the in-process restore is genuinely ~3× faster than the subprocess.
- **A self-contained compiled build.** Bundling the task server into `.bsharp/`
  turns the generated host into a single artifact the customer runs directly,
  with no launcher and no environment setup — and removes the entire
  `Process.Start` hop from the hot path.

## 9. What seems not worth the effort

- **The launcher on the hot path.** The ~75–150 ms launcher→host `Process.Start`
  hop dominated warm no-op wall time and bought nothing for steady-state builds,
  so it was **dropped**: the generator now bundles the task server into `.bsharp/`
  and the customer runs the self-contained `.bsharp/build` directly (~0.057 s vs
  ~0.15 s). The launcher survives only as the one-time generator. The remaining
  open question is re-instating *shape-change detection* without a hot-path
  launcher (e.g. a small mtime guard baked into the host).
- **Trying to statically prove every property dead.** An earlier optimization
  that dropped unreferenced property fields was **reverted as unsound** —
  hand-rolled task code and the runtime XML re-read can touch any property by
  name. Better to keep all fields and let the C#/NativeAOT trimmer remove what it
  can *prove* dead.
- **Per-task sub-CLI processes.** Correct but far too slow (process startup per
  task); the resident task daemon replaced it entirely.
- **Generalizing beyond the closed-world subset prematurely.** Broad MSBuild
  compatibility fallbacks hide incorrect build behavior. Explicit audit
  diagnostics / narrow unsupported-task failures are more valuable for a research
  prototype than silently-wrong general support.
- **`-pp` preprocessing and binlog as IR.** Both were dropped — the evaluation
  API supersedes `-pp`, and the target DAG is cheaper to derive topologically
  than to parse from a binlog.

---

### Appendix: key code coordinates

| What | Where |
|---|---|
| Generator (hash, cache, publish) | `tools/bsharp/Program.cs` (`ComputeShapeHash`, `IsHashFileStillFresh`) |
| Task-server bundling (self-contained `.bsharp/`) | `tools/bsharp/Program.cs` (`BundleTaskDaemon`, `ResolveTaskDaemonSourceDir`) |
| Codegen frontend + emitter | `tools/codegen/Program.cs` |
| `ShouldSkipRestore` / `RunRestore` / `TouchNuGetRestoreMarker` | `tools/codegen/Program.cs` (emitted into host) |
| Fast no-op emit (`FastNoOpBuild`, `FastNoOpBuildBeforePopulate`, `FastNoOpShapeInputs`) | `tools/codegen/Program.cs` |
| Generated target (guard + `_Core`) | `fixtures/console-net11/.bsharp/Program.cs:11398` (`T_001…`), `:25133` (`T_237_Build`) |
| `TargetRuntime.TryEnter` / `RunBuildTarget` dispatcher | `fixtures/console-net11/.bsharp/Program.cs:10254`, `:10380` |
| `InitialState.Populate` | `fixtures/console-net11/.bsharp/Program.cs:10146` |
| Task-server client (`TaskRunner`, `DaemonClient`) | `fixtures/console-net11/.bsharp/Program.cs:783`, `:1033` |
| Task server | `tools/bsharp-taskd/Program.cs` (`HandleClientAsync` `:243`, `ExecuteSerializedAsync` `:296`), `TaskExecutor.cs:65` |
