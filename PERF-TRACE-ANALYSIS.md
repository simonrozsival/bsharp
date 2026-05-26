# Performance Trace Analysis (2026-05-26)

## Methodology

Used `Stopwatch` instrumentation in the Debug launcher (CoreCLR) to measure actual time spent in each phase. Collected 10 samples of each path.

## Results

### Slow Path (computes hash)

**Median times across 10 runs:**
- **Total launcher overhead: 30.5ms**
  - Pre-hash checks: ~14ms (restore checks, cache existence)
  - ComputeShapeHash: ~15ms (file I/O + XML parsing)
  - Cache check: <1ms (string comparison)
  
- **ExecBuildBinary (subprocess): 140ms median**  
  - Fork/exec: ~80ms
  - Generated host execution: ~1ms (actual build)
  - Wait + cleanup: ~60ms

**Total wall-clock time: ~170ms**

### Fast Path (skips hash)

**Median times across 10 runs:**
- **Total launcher overhead: 17ms**
  - Pre-hash checks: ~15ms
  - Timestamp check: <1ms
  - No hash computation! ✅

**Improvement: 13ms saved (43% faster launcher overhead)**

But... subprocess overhead is unchanged.

## The Real Bottleneck

```
ExecBuildBinary: 140ms  <-- 82% of total time
Launcher overhead: 30ms  <-- 18% of total time
```

**Fork/exec dominates everything!**

Even if we made the launcher overhead ZERO, we'd still have 140ms builds because of subprocess overhead.

## Why is ExecBuildBinary so slow?

Breakdown of 140ms:
1. **Fork process** (~40-50ms)
   - macOS process creation is expensive
   - NativeAOT binary is 8MB
   
2. **Exec + load binary** (~30-40ms)
   - Load 8MB NativeAOT executable
   - Initialize runtime
   - Connect to CoreCLR sidekick
   
3. **Wait for exit** (~40-60ms)
   - Process runs (~1ms actual build)
   - IPC drain
   - Process cleanup

## Comparison to Original Claims

| Claim | Reality |
|-------|---------|
| "125ms hash computation" | ❌ **15ms** (8x faster than thought) |
| "80ms subprocess overhead" | ❌ **140ms** (1.75x slower than thought) |
| "10ms process startup" | ❌ Part of 140ms total |
| "File I/O is the bottleneck" | ❌ **Subprocess is 9x worse** |

**I was completely wrong about what's slow!**

## Why My Previous Analysis Was Wrong

1. I measured wall-clock time and guessed at the breakdown
2. I didn't account for `wait()` overhead
3. I conflated "process startup" with "subprocess invocation"
4. I assumed file I/O was dominant (it's not)

## What the Fast Path Actually Buys You

**Before optimization:**
- Launcher: 30ms
- Subprocess: 140ms
- **Total: 170ms**

**After optimization (within 1s):**
- Launcher: 17ms
- Subprocess: 140ms
- **Total: 157ms**

**Savings: 13ms (7.6% faster)**

Not the 125ms I claimed! But still correct and useful.

## Path to Actually Fast Builds

**Current architecture is fundamentally limited by subprocess overhead.**

| Approach | Time | How |
|----------|------|-----|
| Current (fast path) | **157ms** | Status quo |
| In-process host | **20-30ms** | Link host into launcher, no fork |
| Resident daemon | **2-5ms** | Keep host running, IPC only |
| Direct invocation | **11ms** | User calls `.bsharp/build` directly |

**To hit 10ms:** Need in-process host or direct invocation  
**To hit 1ms:** Need resident daemon

## Conclusion

The fast-path optimization is **correct and useful** (saves 13ms), but subprocess overhead (140ms) is the real problem. 

**For sub-10ms builds, we must eliminate the subprocess.**
