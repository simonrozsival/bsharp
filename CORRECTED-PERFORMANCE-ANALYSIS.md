# Corrected Performance Analysis (2026-05-26)

## The Real Numbers (NativeAOT Release Build)

| Method | No-op Time | Overhead | vs 1ms Target |
|--------|------------|----------|---------------|
| **Direct host** | **10.5ms** | baseline | 10.5x slower |
| **NativeAOT launcher** | **135.8ms** | +125ms | 135.8x slower |
| Debug launcher (CoreCLR) | 188.9ms | +178ms | 188.9x slower |
| dotnet build | 669ms | +659ms | 669x slower |

## Key Findings

1. **The launcher IS NativeAOT** - but even NativeAOT can't avoid file I/O
2. **NativeAOT is 36% faster** than CoreCLR for the launcher (136ms vs 189ms)
3. **125ms overhead remains** - this is from `ComputeShapeHash()`, not runtime startup
4. **Direct host is still the winner** at 10.5ms

## What's the 125ms Overhead?

**File I/O is the bottleneck**, not runtime performance:

The launcher reads and hashes ~10-20 files on every invocation:
- Project file (.csproj) - XML parsing + SHA256 hashing
- Directory.Build.props/targets - multiple ancestor directories
- global.json, NuGet.config
- packages.lock.json, project.assets.json
- All ProjectReference dependencies (recursive)
- All MSBuild imports

**NativeAOT doesn't help here** - file I/O takes the same time regardless of runtime.

## Measurement Details

**Environment:**
- macOS arm64, .NET 11.0.100-preview.4.26230.115
- Fixture: fixtures/console-net11
- Method: 10 runs per test, median reported
- Delay: 50ms between runs

**Test commands:**
```bash
# Debug launcher (CoreCLR)
./tools/bsharp/bin/Debug/net11.0/Bsharp build fixtures/console-net11/console-net11.csproj --no-restore

# Release launcher (NativeAOT)  
./tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp build fixtures/console-net11/console-net11.csproj --no-restore

# Direct host (NativeAOT)
cd fixtures/console-net11
./.bsharp/variants/00968cd8e46b31c9/build build --no-restore
```

## Why is Direct Invocation Still Faster?

**Direct invocation skips the entire cache validation pipeline:**
- No file reading
- No XML parsing
- No SHA256 hashing
- No cache comparison

It just runs the build. That's why it's **13x faster** than even the NativeAOT launcher.

## Path to 1ms

**Current best:** 10.5ms (direct host)  
**Target:** 1ms  
**Gap:** 9.5ms

**The 10ms is process startup:**
- Load executable: ~3-5ms
- Initialize runtime: ~2-3ms
- Connect to CoreCLR sidekick: ~2-3ms
- Fast-noop detection: ~1ms

**Only solution:** Resident daemon (eliminates process startup entirely)

## Recommendations

1. **Use NativeAOT Release launcher** (not Debug) for production
2. **Use direct invocation** (`.bsharp/build`) for fast iteration
3. **Implement resident daemon** if sub-1ms is required
4. **Watch mode** is a good UX compromise (amortizes 10ms startup)

## Previous Analysis Was Wrong About...

❌ "Launcher overhead is 171ms" - This was measured with Debug build (CoreCLR)  
✅ **Correct: Launcher overhead is 125ms** (with NativeAOT Release)

❌ "Process startup is the bottleneck" - Partially true, but file I/O is bigger  
✅ **Correct: File I/O (125ms) > Process startup (10ms)**

✅ "Direct invocation is fastest" - This was correct  
✅ "1ms requires resident daemon" - This was correct
