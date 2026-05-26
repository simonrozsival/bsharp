# No-Op Performance Findings (2026-05-26)

## TL;DR

**Target:** 1ms no-op builds  
**Current (via launcher):** 181ms  
**Current (direct host):** 10ms  
**Gap to target:** 9-180ms depending on invocation method

**To hit 1ms:** Need a resident daemon. Direct host invocation (~10ms) is the best we can do with the current architecture.

## Key Findings

1. **The generated host IS fast** - 0.75ms build time, 10ms wall clock
2. **The launcher is the bottleneck** - 171ms overhead from cache validation
3. **Process startup is unavoidable** - Even NativeAOT takes ~10ms to start

## What Works Today

**Fast iteration pattern** (10ms no-op):
```bash
# Generate host once
bsharp build project.csproj --no-restore

# Fast repeated builds
cd my-project
./.bsharp/build build --no-restore   # 10ms
./.bsharp/build build --no-restore   # 10ms
```

This is **18x faster** than going through the launcher and only **10x slower** than the 1ms target.

## What's Next

**Three paths forward:**

1. **Document direct invocation** (0 days) - Make the 10ms path the recommended workflow
2. **Implement watch mode** (2-3 days) - File watcher triggers builds automatically
3. **Implement resident daemon** (3-4 days) - Hit sub-1ms no-op times

**Recommendation:** Start with #1 (documentation), then #2 (watch mode). The daemon (#3) can wait unless sub-1ms is critical.

## Detailed Analysis

See `no-op-performance-analysis.md` for:
- Measurement methodology
- Problem breakdown
- Four solution options with trade-offs
- Implementation estimates
- Resident daemon architecture

---

## UPDATE (2026-05-26 23:30)

**Correction:** Previous measurements used Debug launcher (CoreCLR). The launcher IS NativeAOT when built as Release.

**Corrected measurements:**

| Method | No-op Time |
|--------|------------|
| **Direct host (NativeAOT)** | **10.5ms** ✅ |
| NativeAOT launcher (Release) | 135.8ms |
| CoreCLR launcher (Debug) | 188.9ms |
| dotnet build | 669ms |

**Key insight:** Even NativeAOT launcher has 125ms overhead from file I/O (`ComputeShapeHash()`). This overhead is unavoidable - file reads take the same time regardless of runtime.

**Recommendation stands:** Direct invocation (`.bsharp/build`) is the fast path at 10.5ms.
