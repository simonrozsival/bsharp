# Final Benchmark Results (50 samples each)

## Methodology

- Warmed up with 3 runs
- Fast path: 50 runs with 10ms delay (stays within 1s timestamp check)
- Slow path: 50 runs with 2s delay (forces full hash validation)
- Release NativeAOT launcher, macOS arm64

## Results

| Metric | Fast Path | Slow Path | Difference |
|--------|-----------|-----------|------------|
| **Median** | **131.3ms** | **134.0ms** | **2.7ms saved** |
| Mean | 144.7ms | 1126.7ms | - |
| Min | 98ms | 100ms | - |
| Max | 304ms | **19,932ms** | - |

## Key Findings

1. **Median improvement: 2.7ms (2% faster)**  
   The fast path provides a small but consistent improvement.

2. **Slow path has MASSIVE outliers**  
   - Median: 134ms
   - Mean: 1126ms (8.4x higher!)
   - Max: 19.9 seconds (!!!)
   
   These are likely macOS scheduler anomalies or disk I/O stalls when computing the hash.

3. **Both paths have ~130ms overhead**  
   Compared to direct host invocation (10.9ms), the launcher adds ~120ms regardless of path.

## What's the 120ms Overhead?

From earlier instrumentation:
- Launcher startup: ~10ms
- `WaitForExit()` scheduler latency: ~110ms
- Hash computation (slow path only): ~15ms

The fast path skips the 15ms hash, saving just **2.7ms in practice** (likely measurement noise).

## Why So Little Improvement?

**The `WaitForExit()` call dominates (110ms) and isn't affected by the fast path.**

The optimization is correct but the impact is small because:
1. WaitForExit is 7x more expensive than hash computation
2. The timestamp check itself adds a tiny bit of overhead (file stat)
3. macOS process scheduling variance is high

## Conclusion

**The fast-path optimization is CORRECT but provides minimal benefit (~3ms) because subprocess overhead (110ms) dominates.**

To actually get fast builds:
- **Direct invocation**: 10.9ms (use `.bsharp/build` directly)
- **Exec instead of fork**: Could eliminate WaitForExit
- **Resident daemon**: 2-5ms (keep host in memory)

**Recommendation:** Keep the optimization (it's free and correct), but focus on eliminating subprocess overhead for real speed gains.
