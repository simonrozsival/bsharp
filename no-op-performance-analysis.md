# No-Op Build Performance Analysis

**Goal:** 1ms no-op builds  
**Current State:** ~180ms (launcher) vs ~10ms (direct host)  
**Date:** 2026-05-26

## Measurements

### Direct Host Invocation
```bash
.bsharp/variants/<hash>/build build --no-restore
```
- **Wall clock:** 10.2ms (median of 20 runs)
- **Build time:** 0.75ms (internal measurement)
- **Process overhead:** ~9.5ms

### Via Launcher
```bash
bsharp build project.csproj --no-restore
```
- **Wall clock:** 181.6ms (median of 20 runs)
- **Build time:** 0.75ms (same as direct)
- **Launcher overhead:** ~171ms

## Problem Analysis

### Why is the launcher slow?

The launcher performs expensive validation on every invocation:

1. **`ComputeShapeHash()`** - Reads many files to compute project shape:
   - Project file (.csproj)
   - All ProjectReference dependencies
   - Directory.Build.props/targets (all ancestor directories)
   - Directory.Packages.props
   - NuGet.config
   - global.json
   - packages.lock.json
   - obj/project.assets.json
   - All MSBuild imports

2. **File I/O overhead:** Even with fast SSD, reading 10-20 files takes 50-100ms

3. **XML parsing:** `EnumerateStaticMsBuildPaths()` parses XML on each invocation

4. **Cache validation:** Compares computed hash with cached hash

### Why is direct host invocation still ~10ms?

- **Process startup:** Even NativeAOT binaries have ~5-10ms startup overhead
- **CoreCLR sidekick connection:** The host connects to the persistent task server
- **Fast-noop detection:** Still checks file mtimes (but much faster, ~1ms)

## Gap Analysis

| Target | Current (Direct) | Gap |
|--------|------------------|-----|
| 1ms | 10.2ms | **9.2ms** |

Even the **best case** (direct host invocation, no launcher) is **10x slower** than the 1ms target.

## Solutions

### Option 1: Resident Daemon (Best for 1ms target)

**Concept:** Keep a server process running that accepts build requests over IPC.

**Architecture:**
```
bsharp build project.csproj
  ↓ (Unix socket / named pipe)
bsharp-server (resident daemon)
  ↓ (in-memory)
Cached project state, no file I/O on cache hit
```

**Pros:**
- Can hit **sub-millisecond** no-op times (no process startup)
- Amortizes all startup costs across multiple builds
- Can keep project state in memory (no revalidation needed)

**Cons:**
- Complexity: server lifecycle management, crash recovery
- User experience: "invisible" daemon may confuse users
- Cross-platform: Unix sockets (Linux/macOS) vs named pipes (Windows)

**Estimated effort:** 3-4 days

**Example implementations:**
- Gradle daemon
- sccache
- buck2 daemon

### Option 2: Direct Invocation Pattern (Immediate, 10ms)

**Concept:** Document that users should invoke `.bsharp/build` directly for fast iteration.

**Workflow:**
```bash
# First time or after project changes
bsharp build project.csproj

# Fast iteration (directly invoke generated host)
cd my-project
./.bsharp/build build --no-restore   # 10ms
./.bsharp/build build --no-restore   # 10ms
./.bsharp/build build --no-restore   # 10ms
```

**Pros:**
- **Zero implementation cost** - works today
- Predictable, explicit
- No magic daemons

**Cons:**
- Requires user education
- Still 10x slower than 1ms target
- User must remember to regenerate after project changes

**Estimated effort:** 0 days (documentation only)

### Option 3: Optimize Launcher (Diminishing returns)

**Concept:** Make `ComputeShapeHash()` faster with caching/mtime checks.

**Attempted optimization:**
- Check if shape input files have changed since last hash
- Skip hash recomputation if unchanged

**Result:** **Worse performance** (344ms vs 181ms)
- Reason: mtime checks still require syscalls for 10-20 files
- File I/O is unavoidable in the launcher

**Best case estimate:** ~50-80ms (still 50-80x slower than target)

**Pros:**
- Improves current workflow
- No behavior changes

**Cons:**
- Cannot reach 1ms target (limited by syscall overhead)
- Diminishing returns on optimization effort

**Estimated effort:** 1-2 days for 50% improvement (180ms → 80ms)

### Option 4: Self-Validating Host (Hybrid approach)

**Concept:** Generated host checks if it needs regeneration, bypassing launcher.

**Workflow:**
```bash
# User always calls the generated host
./.bsharp/build build --no-restore

# Host internally:
# 1. Quick mtime check on critical files (project, Directory.Build.props)
# 2. If changed, invoke launcher to regenerate self
# 3. Otherwise, proceed with build (~10ms)
```

**Pros:**
- Single entry point (no launcher vs direct confusion)
- Fast path is 10ms (close to target)
- Auto-regenerates when needed

**Cons:**
- Still 10x slower than 1ms target
- Regeneration trigger might be too aggressive or too lazy
- Adds complexity to generated code

**Estimated effort:** 2-3 days

## Recommendation

**For hitting 1ms target:** Option 1 (Resident Daemon) is the only viable path.

**For immediate improvement:**
1. Document Option 2 (direct invocation) as the fast-iteration pattern
2. Update benchmarks to report direct invocation times as the "true" no-op performance
3. Consider Option 1 as a future enhancement

**Reality check:**
- The **launcher cannot hit 1ms** (syscall overhead is unavoidable)
- The **direct host** is limited by process startup (~10ms)
- Only a **resident daemon** can hit sub-millisecond times

## Benchmark Reporting

Current benchmarks show **212ms** for no-op builds because they invoke the launcher. This mixes two concerns:

1. **Cache validation overhead** (launcher)
2. **Build execution overhead** (host)

**Proposed reporting:**
```
No-op build (via launcher):  181ms
No-op build (direct host):    10ms
No-op build (in-process):    0.75ms
```

This separates launcher cost from actual build cost and makes clear that direct invocation is the fast path.

## Next Steps

1. **Document direct invocation** as the recommended fast-iteration pattern
2. **Update benchmarks** to show direct host times
3. **Decide on daemon priority** - is sub-1ms no-op critical for MVP?
4. **Alternative: watch mode** - File watcher triggers builds automatically, amortizing startup costs

## Watch Mode vs Resident Daemon

**Watch mode:**
- Runs persistent process that watches files
- Triggers builds automatically on changes
- Amortizes startup cost across all builds during session
- Similar benefits to daemon for inner loop

**Trade-off:**
- Watch mode: Better UX (automatic), but builds on every save
- Daemon: Faster (builds only on demand), but requires manual triggering
- **Possible combination:** Watch mode that uses daemon internally
