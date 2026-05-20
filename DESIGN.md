# bsharp — Statically Compiled MSBuild

> Compile a closed-world subset of MSBuild into specialized C# + ReadyToRun, so
> `dotnet build` on a known project shape becomes a single fast binary invocation.

**Status:** working POC for `fixtures/console-net11`. The original 3-phase plan below is largely intact in spirit but the implementation diverged in a few key places (see §0 below).

> **Scope note:** This is a research playground, not a production system.
> Decisions optimize for "shortest path to a real measurement" over
> robustness, edge-case handling, and ecosystem compatibility. Many things
> a production version would need are explicitly dropped here.

**Target:** Scenario-dependent on `dotnet new console`:

| Scenario                          | Target      | Measured                                                                    |
|-----------------------------------|-------------|-----------------------------------------------------------------------------|
| Warm no-op rebuild                | **<100ms**  | ~200ms for the generated host, ~320ms through the native launcher. ~3× vs ~900ms `dotnet build --no-restore` baseline. |
| Clean build (cold)                | best effort | ~30-35s for first R2R publish; generated build itself is ~2-3s cold.        |

MAUI is the long-term north star, not the POC.

---

## 0. Implementation status (added 2026-05-18)

The following sections describe the original v0.3 design draft. Below are the deltas that landed during the playground POC build-out:

### Architecture (deltas from §2)

- **Two-binary architecture**, not the 3-phase pipeline as originally framed:
  - `bsharp` (Native AOT launcher) — parses args, hashes project, manages cache, invokes the inner binary
  - `codegen.dll` (managed; references `Microsoft.Build.Evaluation`) — XML→C# transform; invoked only on cache miss
  - `.bsharp/build` (the per-project ReadyToRun binary that codegen produces and the launcher caches)
- **`-pp:capture.xml` is not used.** We go directly through `Microsoft.Build.Evaluation.Project.AllEvaluatedProperties/Items` for the IR. `-pp` doesn't evaluate conditions/properties/globs and was redundant once we committed to the evaluation API as the frontend.
- **Binlog is not used.** The target DAG is derived topologically from `DependsOnTargets`/`BeforeTargets`/`AfterTargets` at codegen time. Removed the binlog dependency to keep cold builds purely XML-driven.

### Target execution model (deltas from §3)

- **On-demand recursive model, not linear topological invocation:**
  - Each target is a `static void T_NNN_<name>()` method with `static bool _T_NNN_<name>_ran` execute-once guard.
  - Main calls `Targets.Build();` which is `=> T_236_Build();` — direct call, no string-switch dispatch.
  - Inside each target body: condition gate → `DependsOnTargets` calls → Before-companions → optional Inputs/Outputs gate → body children → After-companions. All wrapped in one try/catch attributed to the target.
- **Static expansion of `DependsOn=$(Prop)`:** when `Prop` is never mutated by any target body (including via task `<Output PropertyName=.../>`), `instance.GetPropertyValue(Prop)` is split at codegen time and emitted as direct method calls. For HelloConsole, **every target is fully static-expandable** → `Targets.Run(string)` dispatcher is never emitted. Trimmer-friendly: from `Targets.Build()` only the methods actually reachable in the call graph are kept.
- **Before/AfterTargets** are reverse-indexed at codegen time. Only LITERAL Before/After references are inlined; dynamic ones (`BeforeTargets="$(X)"`) fall back to the runtime `Run(string)` dispatcher (which is then emitted).

### State representation (deltas from §3)

- **Properties:** `static class P { public static string X = "<baked value>"; ... }`. ~1400 typed fields for HelloConsole, initializers baked from `project.GetPropertyValue` at codegen time. `MSBuild*` paths and `ProjectDir` remain `""` and are set in `InitialState.Populate(csprojPath)` at runtime.
- **Items:** `static class I { public static List<Item> KnownFrameworkReference = [new Item("X", new() { ["meta"] = "v" }), ...]; ... }`. Collection-literal initializers; new `Item(string, Dictionary<string,string>)` constructor. Items deduplicated by Identity+metadata so SDK overlapping TFM-conditional ItemGroups don't multiply entries.
- **Property registry**: `P.Set(string, string)` dispatcher is emitted for the runtime csproj XML PropertyGroup loop. `P.Get(string)` was dropped — every codegen-time read uses literal property names so it resolves to a typed field directly or `P.GetExtra("...")` for unknowns.
- **An earlier optimization that dropped unreferenced property fields was reverted** as unsound: hand-rolled task code and runtime XML reads can touch any property by name. We trust the C#/NativeAOT compiler to trim what it can safely prove dead.

### Tasks (deltas from §3)

- **Hybrid task strategy:** structural tasks stay hand-rolled, while real SDK tasks run in one persistent CoreCLR task server. This replaced the earlier per-task sub-CLI experiment, which was correct but far too slow due to process startup cost.
- **Implemented natively:** `Message`, `MakeDir`, `WriteLinesToFile`, `Touch`, `Delete`, `Copy`, `Error`, `Warning`, `ConvertToAbsolutePath`, `RemoveDir`, `CreateProperty`, `CreateItem`, `FindUnderPath`, `ReadLinesFromFile`, `Exec`, `Csc`, `Hash`, plus small HelloConsole-oriented shims for restore/assets-file-dependent SDK tasks.
- **Task batching:** hand-rolled structural tasks support sequential grouping by one metadata dimension, including qualified/unqualified metadata, `%(Identity)`, per-batch conditions, multiple lists sharing the metadata, and pass-through lists. Target batching remains an explicit v1 unsupported shape.
- **In-proc task loading:** one `AssemblyLoadContext` per task assembly directory, with `Microsoft.Build.Framework`, `Microsoft.Build.Utilities.Core`, and `Microsoft.Build` unified with the host context so `IBuildEngine`/`ITaskItem` identity stays valid.
- **Csc warm no-op:** `CoreCompile` still executes because the SDK includes `$(NonExistentFile)` in target outputs, but the hand-rolled `Csc` task performs a task-level timestamp check and returns without launching Roslyn.

### Caching and config (deltas from §5)

- **Closed-world `-p:X=Y` is supported.** Sorted `(Key=Value)` pairs are folded into the shape hash. Cache miss → codegen runs with `ProjectCollection.GlobalProperties` → `project.GlobalProperties` are unconditionally re-applied in `InitialState.Populate` (overriding csproj defaults). Same project + same `-p:` set → cache hit.
- **Cache miss triggers:** csproj + static project references/imports + `Directory.Build.{props,targets}` + `Directory.Packages.props` + `NuGet.config` + `global.json` (walked to root per project) + `packages.lock.json` + `obj/project.assets.json` timestamp/size + `-p:X=Y` set. Mode (R2R/JIT) recorded in `shape.hash` second line.
- **Publish mode:** R2R+SelfContained by default, then framework-dependent JIT fallback. Native AOT is no longer the default for the generated host because lazy in-proc SDK task loading is central to the current approach.

### Codegen-time expression optimizations

- **Conditional empty-list checks:** `'@(X)' != ''` compiles to `(I.X.Count != 0)` instead of `string.IsNullOrEmpty(string.Join(";", I.X.Select(__it => __it.Identity)))` (two allocations per evaluation).
- **`!!` double negation eliminated** via a `NegateBoolExpr` helper at the target-condition wrap site.
- **Pure-literal Include/Remove:** single-literal-with-no-`$/@/%` Includes skip the `.Split(';')` loop.
- **Static field initializers** instead of conditional runtime `if (string.IsNullOrEmpty(P.X)) P.X = "...";` passes.

### Logger

- `static class Log` with `Verbosity` enum (Quiet/Minimal/Normal/Detailed/Diagnostic; default Minimal).
- `Log.TaskStarted(name)` emits `  [<elapsed-ms>] <TaskName>` at Normal+; the build summary line is at Minimal+.
- Configurable via `-v <level>` / `-v:<level>` / `--verbosity <level>` / `--verbosity:<level>` (case-insensitive). Launcher forwards via `Process.Start`.

### What didn't land yet

- `bsharp watch`, `bsharp clean`, `bsharp test`.
- `project.assets.json` hashing (transitive package drift).
- `ProjectReference` recursive shape hashing for multi-project solutions.
- `exec()` replacement on Unix to drop the ~80ms `Process.Start` overhead on macOS.
- Spectre.Console-based pretty logging (basic Console logging is in).
- Broader correctness oracle (§7) suite. Current MSTest coverage validates fast structural codegen/audit unit scenarios, task-batching semantics, console cold/warm/direct/incremental builds, forced regeneration, shape invalidation, audit shape checks, gated MAUI audit, and a gated vendored MSBuild E2E corpus.

---

## 1. Goal & Non-Goals

### Goal
For a *known project shape*, produce an AOT-compiled native binary that runs the
build with:

- Hardcoded target DAG — no runtime topological sort
- Direct struct-field property access — no `Dictionary<string,string>` lookups
- Typed item arrays — no boxed metadata bags
- Statically-linked tasks — no `UsingTask` reflection, no dynamic assembly load
- Minimal IO — in-memory pipelining between tasks where possible

CLI surface of the compiled artifact:

```
$ compiled-build-script              # default: build
$ compiled-build-script build
$ compiled-build-script watch
$ compiled-build-script test
$ compiled-build-script <target>     # JIT'd entrypoint for any compiled target
```

### Non-Goals (v1)
- Full MSBuild compatibility. Only a documented subset is supported; Phase 2
  rejects anything outside it with a precise diagnostic.
- NuGet packages introducing new `Target`s or `UsingTask`s. PackageReference
  contributes items and properties only.
- Custom inline tasks (`<UsingTask TaskFactory="...">`).
- Cross-language MSBuild scenarios (project types other than C#/Roslyn).

### Non-Goals (forever)
- Replacing MSBuild for arbitrary csproj files in the wild. This is an
  *opinionated* fast path for projects that opt in.

---

## 2. Architecture

Three phases, each consuming the previous phase's output.

### Phase 1 — Capture
Input: user csproj + SDK + nupkgs.
Output: three artifacts:

1. **`capture.xml`** — preprocessed XML via `dotnet msbuild -pp:capture.xml`.
   Useful for *static structure* (UsingTasks declared, Target shells, Import scope
   markers) but **not sufficient on its own**: `-pp` inlines imports but does not
   evaluate properties, resolve conditions, expand item globs, or apply
   `Update`/`Remove` semantics.

2. **`evaluated.json`** — the evaluated project model via MSBuild's
   `Microsoft.Build.Evaluation.Project` API loaded against the shape inputs.
   This gives us resolved properties, expanded item lists, resolved conditions,
   and the actual target dependency graph that MSBuild would execute. **This
   is the real compiler frontend**, not `-pp`. We use MSBuild's own evaluator;
   we do not reimplement it.

3. **`baseline.binlog`** — a binlog from a real `dotnet build` of the project.
   Source of truth for: which targets actually ran, in what order, with what
   skip decisions; which task instances executed with which fully-expanded
   parameters; which files were read/written. The POC target chain is derived
   from this binlog, not hand-authored.

We also record a **shape descriptor** (cache key, SHA-256 hashed):

- SDK identity + version
- TargetFramework(s)
- Set of `PackageReference` ids (not versions — those are runtime-validated, see §5)
- Whitelisted boolean feature flags actually set in the csproj
  (`PublishAot`, `UseMaui`, `EnableDefaultCompileItems`, …)
- Set of override-able properties that the user explicitly sets in the csproj
  (so we know which to bake vs. accept at runtime)

### Phase 2 — Analyze
Input: preprocessed XML.
Output: an Intermediate Representation (IR) of the build graph, or a structured
diagnostic explaining which subset rule was violated.

This phase is where most of the language work lives. It:

1. Parses the preprocessed XML into a typed model.
2. Resolves all `Condition=` expressions against the shape (feature-flag
   properties are *known* at this point, so most conditions collapse to true/false).
3. Builds the target DAG: resolves `DependsOnTargets`, `BeforeTargets`,
   `AfterTargets` topologically; emits a linear ordered list.
4. Type-infers properties (bool / string / path / int — best effort, defaults to string).
5. Computes the metadata key set per item type — the union of metadata keys
   *referenced* anywhere in conditions, transforms, or task parameters. Anything
   else is dropped.
6. Enforces subset rules (§4). Rejects with diagnostics if violated.
7. Emits IR.

The IR is a serializable, version-stable representation of "the program to
emit." Phase 3 only depends on the IR, not on raw XML.

### Phase 3 — Codegen + AOT
Input: IR.
Output: an AOT-compiled native binary.

1. Emit a C# project that references the SDK task assemblies and bsharp's
   runtime support library.
2. Emit `BuildState` struct, item structs, target methods, entrypoint.
3. `dotnet publish -c Release -p:PublishAot=true`.
4. Cache the binary keyed by shape hash.

---

## 3. Compilation Model

### Properties → struct fields

```csharp
struct BuildState {
    // Bool inference from usage:
    public bool PublishAot;
    public bool EnableDefaultCompileItems;
    // Path-typed properties:
    public string OutputPath;
    public string IntermediateOutputPath;
    public string AssemblyName;
    // Plus per-item arrays:
    public CompileItem[]   Compile;
    public ReferenceItem[] Reference;
}
```

Property name → C# field name uses a deterministic mangling for names that
aren't C#-legal (rare; mostly dotted names). The mapping is recorded in the IR
for debugging.

### Items → typed structs in arrays

```csharp
struct CompileItem {
    public string Identity;
    public string FullPath;
    public string Link;        // only because at least one target references %(Link)
    // %(NotReferencedAnywhere) is NOT a field
}
```

Per item type, the metadata set is closed-world: union of every `%(...)` access
across the preprocessed graph. Unreferenced metadata is erased.

### Targets → static methods, DAG → straight-line calls

```csharp
static void Target_Build(ref BuildState s, Logger log) {
    Target_BeforeBuild(ref s, log);
    Target_ResolveReferences(ref s, log);
    Target_CoreCompile(ref s, log);
    Target_CopyFilesToOutputDirectory(ref s, log);
    Target_AfterBuild(ref s, log);
}
```

- `BeforeTargets` / `AfterTargets` / `DependsOnTargets` are resolved at codegen
  time. The runtime calls a fixed sequence of methods. No sort.
- `Condition=` on a target → emit as `if (cond) { … }` around the body.
- `Inputs=`/`Outputs=` incrementality → emit explicit timestamp comparisons
  before the body. We must match MSBuild's semantics exactly here (see §7,
  correctness oracle).
- `Returns=` items become the method's return value (or a `BuildState` side-effect).

### Tasks — two-tier strategy

**Tier 1: reimplement (AOT-clean, fast).** ~10 structural tasks where the
implementation is small and reflection-free is worth the cost:

`Copy`, `MakeDir`, `RemoveDir`, `Delete`, `WriteLinesToFile`,
`WriteCodeFragment`, `CreateItem`, `Touch`, `FindUnderPath`, `ReadLinesFromFile`.

**Tier 2: wrap (correctness first).** Construct the existing SDK task type
directly and call `Execute()`:

```csharp
var t = new ResolveAssemblyReference {
    BuildEngine = engine,
    Assemblies  = MapItems(s.Reference),
    // ... bound from IR
};
t.Execute();
```

Trim warnings expected.

Implement enough of `IBuildEngine`/`IBuildEngine2..9` to keep wrapped tasks
happy — minimum is logging + `BuildProjectFile` (only if any wrapped task
actually calls it on our fixtures). Start with stubs that throw
`NotImplementedException` and fill in as wrapped tasks complain. For items
flowing into wrapped tasks, preserve the full metadata bag (don't erase
unreferenced metadata) since the task may inspect arbitrary keys via
`ITaskItem`.

**Tier 3: out-of-proc.** `Csc` stays as `csc.dll` invocation, OR in-proc via
`Microsoft.CodeAnalysis.CSharp` if we can make Roslyn AOT-clean enough.
Decision deferred to POC.

---

## 4. Subset Rules

These are enforced by Phase 2 and produce structured diagnostics on violation.

### Supported (v1)
- `<PropertyGroup>` with literal values, property refs, `Condition=` on shape
- `<ItemGroup>` with `Include=`, `Exclude=`, `Remove=`, literal metadata
- Conditions: `==`, `!=`, `And`, `Or`, `!`, `Exists()`, `HasTrailingSlash()`
- Property functions on the whitelist:
  - `$([MSBuild]::*)` — full set of ~50 intrinsics
  - `$([System.IO.Path]::*)` — full set
  - `$([System.String]::*)` — bounded subset (Format, Concat, Substring, …)
- `<Target>` with `DependsOnTargets`, `BeforeTargets`, `AfterTargets`,
  `Condition`, `Inputs`/`Outputs`, `Returns`, `KeepDuplicateOutputs`
- Whitelisted SDK tasks (curated list maintained in IR)
- `<Import>` resolved at preprocess time (Phase 1 inlines these)
- Private properties/items (prefix `_`) — fully specializable, may be erased
  if dead after analysis

### Banned (rejected by Phase 2)
- `$($(IndirectName))` — dynamic property reference
- `<Target BeforeTargets="$(X)">` — non-literal hook target
- `<UsingTask>` from user code or NuGets (SDK-shipped tasks only, by whitelist)
- `<UsingTask TaskFactory="...">` — inline tasks
- `<Import Project="$(X).targets">` where `X` isn't a shape-resolvable property
- `<MSBuild>` task except for P2P references with statically-known target shapes
- `<CallTarget Targets="$(X)">` with non-literal target name
- Property functions outside the whitelist
- Mutation of properties or items inside a target that other targets depend on
  via `BeforeTargets`/`AfterTargets` in ways that contradict the topo order
  computed at codegen time (must be diagnosable in Phase 2; details TBD)

### Batching is four separate concerns

Lumping all of these as "batching" hid the real picture. We make four
distinct decisions:

| Concern                                                             | v1 stance        | Why                                                                                            |
|---------------------------------------------------------------------|------------------|------------------------------------------------------------------------------------------------|
| **(a) Item transforms** `@(X->'%(Identity).bak')`                   | **support**      | Pervasive in mainline targets. Compile to LINQ projection / plain `foreach`.                   |
| **(b) Metadata expansion in task parameters** `%(FullPath)` in attrs | **support**      | Pervasive. Compile to inline expression evaluating against the item being passed.              |
| **(c) Task batching** (same task invoked once per distinct metadata) | **support local structural tasks** | Implemented as sequential grouped batches for the hand-rolled task path; real SDK task batching remains a narrower future slice. |
| **(d) Target batching** (target fan-out when `%(...)` in `Inputs`/`Outputs`)  | **ban (v1)**     | Rejected during generation for project-authored targets with a clean diagnostic; revisit when concrete need arises. |

### Deferred — decide after audit of evaluated graph
- **Property functions on arbitrary BCL static methods.** Probably ban; allow
  via opt-in attribute on the user csproj if we find we need them.
- **`<ProjectReference>`**. v1 either bans or treats as out-of-band shell-out
  to `dotnet build` on the referenced project. P2P-aware static compilation is
  v2.

---

## 5. Specialization & Caching Model

**Per-shape AOT artifact.** Cache key: SDK, TFM, set of PackageReference ids,
whitelisted feature-flag property values. Conditions on shape-properties are
pre-resolved at codegen.

**No `-p:Property=Value` override support in the playground.** Every property
is baked at codegen time. The compiled binary takes no CLI flags affecting
build behavior. If you want a different `Configuration`, recompile the bsharp
binary. We acknowledge this is unrealistic for production — see scope note.

**No runtime drift validation.** Same shape hash → same binary. We trust
the user to recompile when they change the SDK/packages/feature flags.
A future version can add `project.assets.json` hash checks and fallback to
MSBuild on mismatch; not in v1.

**Delivery: JIT-then-cache (later).** v1 is "manually invoke `bsharp codegen`
to produce a binary; then invoke that binary." Wiring it into a shell that
auto-codegens on first invocation is a follow-up.

**`dotnet build -t:X`.** Compiled binary exposes targets as subcommands.
Targets pruned as unreachable in Phase 2 are simply unavailable; if you need
them, recompile with the right entrypoint set.

---

## 6. IO Strategy

Wins worth chasing:

- **Memoized file reads** at the `BuildState` level (most config files are read
  3-5× during a build).
- **In-memory `WriteCodeFragment`** → keep the generated source as `SourceText`,
  hand to in-proc Roslyn, never touch disk. (Conditional on Tier 3 decision for Roslyn.)
- **Frozen lookup tables**: `ResolveAssemblyReference`'s SDK assembly list
  becomes a `FrozenDictionary<string,string>` constant baked into the binary at
  Phase 3. Same for ref-pack contents.
- **Skip the response-file dance** for `Csc` if we run in-proc.

Not worth it for v1:

- Reimplementing the assembly resolution algorithm (use SDK task verbatim).
- Hash-based incremental task outputs (orthogonal — could layer on later).

---

## 7. Correctness Oracle

Risk: silent incrementality bugs. Mitigated by a small scenario suite per fixture:

| Scenario           | Catches                                                       |
|--------------------|---------------------------------------------------------------|
| Clean build        | Baseline correctness; full target graph executes.             |
| No-op rebuild      | Incrementality: nothing should run, nothing rewritten.        |
| Single source edit | Partial rebuild: only affected targets run.                   |

Per scenario, compare:

- Exit code.
- Set of files written under `obj/`, `bin/`, `publish/`.
- File contents with per-extension normalizers stripping timestamps, PDB
  paths, GUIDs, MVIDs.

Mismatch = test failure. Run it on every commit while iterating.

A structured execution trace from the compiled binary is nice-to-have for
debugging (target started/skipped/completed events as JSONL) but not
required to ship the POC.

---

## 8. POC Scope

### 8.0 Prerequisite gate: Csc invocation bench

**Before any codegen work, we bench three Csc invocation strategies** on a
fixed source set:

1. Real `dotnet build` (MSBuild Csc task, with compiler server reuse).
2. bsharp-style out-of-proc `csc.dll` direct invocation.
3. bsharp-style in-proc Roslyn via `Microsoft.CodeAnalysis.CSharp`.

We measure: cold-start, warm/repeated, single-file edit. **If (3) is more than
~1.5× slower than (1) when warm, the in-proc story is dead and we plan around
out-of-proc Csc with explicit warm-up.** This gate determines whether the 5×
warm no-op target is reachable at all.

### 8.1 POC scope (after gate passes)

Smallest end-to-end vertical that proves the model works:

- **Project:** `dotnet new console` on `net11.0`, no PackageReferences.
- **Target chain:** **whatever MSBuild actually runs**, captured from binlog,
  not hand-authored. Expected to include `PrepareForBuild`,
  `GenerateAssemblyInfo`, `ResolveTargetingPackAssets`, `ResolvePackageAssets`,
  `GenerateMSBuildEditorConfigFile`, `GenerateGlobalUsings`, `CoreCompile`,
  `CopyFilesToOutputDirectory`, and any incrementality companions. The audit
  in Phase 2 enumerates the exact set.
- **Task strategy** (per task, after audit; this list is illustrative not exhaustive):
  - `Copy`, `MakeDir`, `WriteLinesToFile`, `WriteCodeFragment` — reimplement (Tier 1).
  - `ResolveAssemblyReference`, `ResolveTargetingPackAssets`,
    `ResolvePackageAssets`, `GenerateDepsFile`, `GenerateRuntimeConfigurationFiles`
    — wrap (Tier 2) with real `IBuildEngine`.
  - `Csc` — chosen by §8.0 gate.
- **CLI:** `bsharp build` only. No `watch`, no `test`, no JIT-cache, no fallback.
- **Bench harness:** scripted comparison vs. `dotnet build`, capturing
  wall-clock for the three scenarios in §1's matrix, plus first-build cost
  (Phase 1+2+3 ahead-of-time).

### 8.2 POC ships when:
- Oracle (§7) passes on the 3 scenarios for `dotnet new console`.
- Warm no-op rebuild is faster than `dotnet build` by some real, measured
  multiple — ideally 5×, accept 3× as a useful number, anything below 2× is
  a sign the model is wrong somewhere.

---

## 9. Open Questions

Tracked here, resolved as we hit them:

1. **Target batching ((d) in §4):** confirmed bannable after audit, or do we
   need to compile it? Resolved by §8 audit.
2. **Roslyn in-proc vs out-of-proc:** AOT-clean enough? Resolved by §8.0 gate.
3. **`Inputs`/`Outputs` semantics edge cases:** empty input sets, missing
   output files, items with metadata-derived paths. Need an exhaustive test
   matrix derived from the oracle's scenario list.
4. **Binlog compatibility surface:** v1 ships structured JSONL trace, v2
   ships binlog-compatible writer. Confirm v1 trace is enough for the oracle
   differ; revisit if we lose debugging fidelity.
5. **Package buildTransitive assets:** banned in v1 for user-added packages.
   But the SDK itself ships `build`/`buildTransitive` props/targets through
   its own packaging system. Phase 2 must distinguish "SDK-shipped (allowed)"
   from "user PackageReference-contributed (banned)" — exact mechanism TBD,
   probably via `project.assets.json` provenance walking.
6. **Source generators and analyzers:** these are loaded via reflection from
   NuGet packages. They run inside `Csc` (Roslyn). Bsharp doesn't load them
   directly, but we need to thread the generator file list through to the
   compile invocation correctly. Out-of-scope for §8.0 bench, in scope for
   POC.
7. **`<ProjectReference>`:** still deferred. Single-project POC sidesteps this.

---

## 10. Decision Log

| Date       | Decision                                                                     | Rationale                                                                                                              |
|------------|------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------|
| 2026-05-18 | Closed-world subset, not full MSBuild                                        | Full dynamic semantics defeat AOT specialization. Subset enforced by Phase 2 with diagnostics.                         |
| 2026-05-18 | Per-shape AOT artifact (not per-project, not per-SDK)                        | Per-project AOT compile cost destroys savings; per-SDK leaves too much dynamic.                                        |
| 2026-05-18 | JIT-then-cache delivery                                                      | Avoids needing a static prover; first build pays interpreter cost, subsequent builds win.                              |
| 2026-05-18 | 5× wall-clock target on POC                                                  | Aggressive enough to justify the specialization effort; measure-and-iterate, not contract.                             |
| 2026-05-18 | Item batching: deferred                                                      | Decide after looking at real preprocessed XML from `dotnet new console`.                                               |
| 2026-05-18 | Tasks: 3-tier strategy (reimplement / wrap / out-of-proc)                    | Reimplement only where small and worth it; wrap for correctness; out-of-proc where AOT is hopeless.                    |
| 2026-05-18 | Correctness oracle in CI from day one                                        | Incrementality bugs are silent and severe; we need an external truth source.                                           |
| 2026-05-18 | POC: console app, `Build` chain, three tasks, no watch/test/cache            | Smallest vertical that exercises the model end-to-end.                                                                 |
| 2026-05-18 | Phase 1 captures evaluation API output + binlog, not just `-pp`              | `-pp` doesn't evaluate properties/conditions/globs. Use MSBuild's own evaluator as the frontend; binlog as ground truth for target sequence. |
| 2026-05-18 | Batching split into 4 concerns; (a)(b)(c) supported, (d) banned v1           | Lumping these together hid the picture. Transforms and metadata expansion are pervasive and unavoidable.               |
| 2026-05-18 | `-p:Property=Value` overrides have a 3-class taxonomy, in v1 scope           | `Configuration=Release` is canonical in CI; cannot be deferred. shape vs. runtime vs. unsupported.                     |
| 2026-05-18 | Perf target is a scenario matrix, not a single number                        | 5× plausible for warm no-op only; clean build is Roslyn-bound and won't hit 5×.                                        |
| 2026-05-18 | Shape hash + assets.json hash are separate concerns                          | Same shape can have different transitive closures across invocations. Validate assets at startup, fall back on mismatch. |
| 2026-05-18 | Csc invocation bench is a prerequisite gate before codegen work              | If in-proc Roslyn isn't fast enough, the whole 5× story collapses; need to know up front.                              |
| 2026-05-18 | Correctness oracle is scenario-based with normalized diffs, not raw bytes    | Byte-equality misses ordering bugs and over-constrains nondeterminism.                                                 |
| 2026-05-18 | Structured execution trace is v1, not deferred                               | Required for oracle differ and for any debugging story.                                                                |
| 2026-05-18 | `IBuildEngine` implementation is a first-class component, not a stub         | SDK tasks use 9 IBuildEngine interfaces; stubs will silently break wrapped tasks.                                      |
| 2026-05-18 | Metadata erasure only for bsharp-owned code paths                            | Wrapped SDK tasks may inspect arbitrary metadata via `ITaskItem` even when XML doesn't reference it.                   |
