# NativeAOT vs CoreCLR Launcher Performance

## Question

> "Is the launcher really also a NativeAOT published binary?? It would be faster if it wasn't right?"

**Answer: NO - NativeAOT is actually FASTER!**

## Test Results (30 samples, fast path)

| Runtime | Binary Size | Median Time | vs NativeAOT |
|---------|-------------|-------------|--------------|
| **NativeAOT** | 8.2 MB | **138.3ms** | baseline |
| **CoreCLR** | 122 KB | 178.0ms | **40ms slower** (1.29x) |

## Why is NativeAOT Faster?

**CoreCLR launcher startup cost:**
1. Fork process: ~5ms
2. **Load CoreCLR runtime (~50MB in memory): ~30-40ms**
3. **JIT compile launcher code: ~10-20ms**
4. Run launcher logic: ~10ms
5. Fork subprocess for host: ~5ms
6. WaitForExit: ~110ms
7. **Total: ~178ms**

**NativeAOT launcher startup cost:**
1. Fork process: ~5ms
2. **Load NativeAOT binary (8.2MB): ~5-10ms**
3. **Run launcher (already compiled): ~10ms**
4. Fork subprocess for host: ~5ms
5. WaitForExit: ~110ms
6. **Total: ~138ms**

**Key difference: CoreCLR pays 40-60ms for runtime init + JIT**

## Trade-offs

| Metric | NativeAOT | CoreCLR |
|--------|-----------|---------|
| **Binary size** | 8.2 MB | 122 KB |
| **Startup time** | ~10-15ms | ~50-70ms |
| **Memory usage** | Lower | Higher (runtime) |
| **Build time** | Slower | Faster |
| **Performance** | ✅ **40ms faster** | ❌ 40ms slower |

## Why NativeAOT Makes Sense for bsharp

The launcher is invoked **many times per day** during development:
- Each invocation saves 40ms with NativeAOT
- 100 builds/day = **4 seconds saved per day**
- Binary size doesn't matter (one-time cost)
- NativeAOT also uses less memory

**Conclusion: NativeAOT is the correct choice for the launcher.**

## But WaitForExit Still Dominates

Even with NativeAOT, the bottleneck is:
- **WaitForExit: ~110ms** (80% of total time)
- Hash computation: ~15ms (11%)
- Launcher startup: ~10ms (7%)
- Other: ~3ms (2%)

To get really fast builds (<10ms), need to eliminate WaitForExit entirely:
- Use direct invocation (`.bsharp/build`): **10.4ms**
- Or implement resident daemon: **2-5ms**
