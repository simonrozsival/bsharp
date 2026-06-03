# B# live demo runbook — `fixtures/console-net11`

Audience: deep .NET/MSBuild engineers. Target time: ~5–6 min of demo woven into
the talk. **Everything below is copy-paste tested on this machine.**

> Requirements: macOS arm64, .NET SDK `11.0.100-preview.4.26230.115`.

## Mental model (say this once, up front)

B# has **two phases, like a compiler**:

- **`bsharp <project>` = "compile the build."** Evaluates the shape once, generates
  + NativeAOT-publishes a specialized host into `.bsharp/`, and **bundles its task
  server alongside it**. Run this when the *shape* changes (like `./configure`).
- **`.bsharp/build` = "the compiled build."** A self-contained binary you run
  every inner-loop iteration. **No launcher, no env vars, no subprocess hop.**

The inner loop is source edits -> run `.bsharp/build`. The "launcher" only exists
to produce the compiled build; it is **not on the hot path**.

---

## 0. One-time setup (do this BEFORE the talk)

```bash
cd /Users/simonrozsival/Projects/playground/bsharp

# Build the whole toolchain (codegen + generator + CoreCLR task daemon).
# Takes a couple of minutes; do it ahead of time.
./build.sh
```

### Env vars + aliases — paste into the demo shell

```bash
# --- B# demo environment ---
export BSHARP_ROOT="/Users/simonrozsival/Projects/playground/bsharp"
# The generator (used ONCE to compile the build). Not on the hot path.
export BSHARP="$BSHARP_ROOT/tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp"
export BSHARP_CODEGEN="$BSHARP_ROOT/tools/codegen/bin/Debug/net11.0/Codegen"
# Needed only so the GENERATOR can find the task server to bundle into .bsharp/.
export BSHARP_TASKD_PATH="$BSHARP_ROOT/tools/bsharp-taskd/bin/Release/net11.0/osx-arm64/publish/bsharp-taskd"

alias gen-bsharp="$BSHARP"            # the one-time "compile the build" step
alias demo-cd="cd $BSHARP_ROOT/fixtures/console-net11"
# The inner loop runs the compiled build directly — no env vars needed.
alias t-build='for i in 1 2 3; do /usr/bin/time -p ./.bsharp/build --no-restore -v:quiet build; done'
alias t-dotnet='for i in 1 2 3; do /usr/bin/time -p dotnet build --no-restore --nologo -v:q; done'
# --- end ---
```

> Note: `BSHARP_TASKD_PATH` is consumed **only by the generator**, so it can copy
> the task server into `.bsharp/`. Once generated, `.bsharp/build` is standalone.

### Compile the build once (avoids ~30–80 s of dead air on stage)

```bash
demo-cd
gen-bsharp build --no-cache -v:quiet build   # full regen + publish + bundle taskd NOW
ls .bsharp/bin/Release/net11.0/osx-arm64/publish/ | grep taskd   # taskd is bundled
./.bsharp/build run -v:quiet                  # confirm the compiled build runs standalone
```

After this, the self-contained compiled build lives at
`fixtures/console-net11/.bsharp/build` and every live step below runs it directly.

---

## 1. Show the project is trivial (10 s)

```bash
demo-cd
cat console-net11.csproj
cat Program.cs        # prints: Hello 9
```

**Say:** "Plain SDK-style console app. Nothing B#-specific in the project."

---

## 2. Show what got generated (45 s)

```bash
ls .bsharp/                                # generated host + BUNDLED task server
ls .bsharp/bin/Release/net11.0/osx-arm64/publish/ | grep bsharp-taskd
sed -n '1,40p' .bsharp/Program.cs          # baked P.* / I.* state + target methods
wc -l .bsharp/Program.cs                    # ~42k lines of generated host
head -10 .bsharp/tasks.report.txt           # UsingTask resolution report
```

**Say:** "Codegen evaluated the project once and emitted a ~42k-line specialized
host: ~735 typed properties, ~400 item types, ~500 targets as async methods, 199
`UsingTask`s triaged across 14 assemblies. The host is NativeAOT; real SDK tasks
go to a persistent CoreCLR task server **that's bundled right here** — so the
whole `.bsharp/` folder is self-contained."

---

## 3. The headline: warm no-op, compiled build vs dotnet (60 s)

```bash
echo "== dotnet =="; t-dotnet
echo "== .bsharp/build (the compiled build, run directly) =="; t-build
```

**Expect (median):** dotnet `--no-restore` ~1.0 s · **`.bsharp/build` ~57 ms**.
**Say:** "No launcher, no env vars — just the compiled build. ~18x on the warm
inner loop versus `dotnet build --no-restore`, ~27x versus a full restore-on
`dotnet build`."

---

## 4. Incremental edit (45 s)

```bash
sed -i '' 's/Hello 9/Hello, B#!/' Program.cs
./.bsharp/build run -v:quiet               # prints: Hello, B#!
time ./.bsharp/build --no-restore -v:quiet build
time dotnet build --no-restore --nologo -v:q
```

**Expect:** `.bsharp/build` ~150 ms vs dotnet ~990 ms (~6.7x). **Say:** "Real
recompile — Roslyn runs in the bundled task server — still several x."

---

## 4b. Clean build-only, `--no-restore` (fair apples-to-apples, 30 s)

```bash
rm -rf bin                     # clean output, KEEP restored obj/
time ./.bsharp/build --no-restore -v:quiet build
rm -rf bin
time dotnet build console-net11.csproj --no-restore --nologo -v:q
```

**Expect:** `.bsharp/build` ~280 ms vs dotnet ~1,170 ms (~4x). **Say:** "Fairest
build-only comparison — restore excluded on both sides, pure build work from a
clean `bin`."

---

## 4c. Three-level restore (optional, 45 s)

```bash
# default: FAST restore (skips when obj/project.nuget.cache is newer than deps)
time ./.bsharp/build -v:quiet build
# force a full in-process restore
time ./.bsharp/build --no-fast-restore -v:quiet build
# skip restore entirely (like dotnet build --no-restore)
time ./.bsharp/build --no-restore -v:quiet build
# head-to-head restore vs dotnet
rm -rf obj; time ./.bsharp/build restore -v:quiet      # ~245 ms
rm -rf obj; time dotnet restore --nologo -v:q          # ~1,020 ms
```

**Say:** "Three levels — fast / forced / skip — all using B#'s in-process restore
through the bundled task server, ~4x faster than `dotnet restore`."

---

## 4d. Fast-noop on vs off + correctness (optional, 45 s)

```bash
./.bsharp/build --no-restore -v:n build                # cumulative tasks: 0.00ms (shortcut)
./.bsharp/build --no-restore --no-fast-noop -v:n build # cumulative tasks: ~112ms (full graph)

# correctness: editing source is never wrongly skipped
sed -i '' 's/Hello 9/Hello CHANGED/' Program.cs
./.bsharp/build run -v:quiet                            # prints Hello CHANGED
git checkout -- Program.cs
```

**Say:** "No-op detection is automatic and purely timestamp-based. Now that
there's no launcher hop, the host-level shortcut shows up directly in wall time:
~57 ms (fast-noop) vs ~110 ms (full graph). And it's conservative: a `touch`
rebuilds, a real source edit is never skipped."

---

## 5. Audit: how we reason about a shape (30 s)

```bash
gen-bsharp audit | head -40
```

**Say:** "`audit` evaluates the project and reports the shape — targets, tasks,
`CallTarget`/`<MSBuild>` sites, dynamic imports, `UsingTask` issues — without
generating a host. This is the bring-up tool for bigger SDKs like MAUI."

---

## 6. When do you re-compile the build? (shape vs source, 30 s)

```bash
# Source edits -> just re-run the compiled build (fast path):
sed -i '' 's/Hello 9/Hello again/' Program.cs && ./.bsharp/build run -v:quiet
git checkout -- Program.cs

# SHAPE edits (csproj, Directory.Build.*, global.json, -p globals) -> re-run the
# GENERATOR, exactly like re-running ./configure after changing build config:
touch console-net11.csproj
gen-bsharp build -v:quiet build    # generator detects the shape change -> regenerates
```

**Say:** "This is the one honest tradeoff of dropping the launcher: `.bsharp/build`
is compiled for a *fixed shape*, so when the shape changes you re-run the
generator. That's the whole thesis — the shape is a compile-time constant; when
the constant changes, you recompile. Source edits stay on the fast path."

---

## Reset / re-arm the demo

```bash
demo-cd
git checkout -- Program.cs
# nuke generated cache to demo a true cold gen (slow!)
rm -rf .bsharp bin obj
# ...or just re-compile the build without showing cold:
gen-bsharp build --no-cache -v:quiet build
```

## Fallback if something misbehaves on stage
- If a warm build looks slow, run it once more — first warm after a cold gen is
  ~150 ms before it settles to ~57 ms.
- If `.bsharp/build` can't find its task server, the generator didn't bundle it —
  re-run `gen-bsharp build --no-cache`, or set `BSHARP_TASKD_PATH` as a fallback
  (the host still honors it).
- Keep `benchmarks.md` / `matrix2-analysis.md` open in a tab as a numbers backup.
