# B# benchmark matrix — full analysis

Source: `raw2-20260603-144807.csv` · 480 rows · ~20 iters/config · console-net11 · macOS arm64, SDK 11.0.100-preview.4.

Sorted slowest→fastest by **median**. `median vs baseline` is signed (− = faster). High variance (±20–30%); trust ratios over absolutes.


## Clean build  (baseline: dotnet build w/ restore)

| Configuration | min (ms) | median (ms) | median vs baseline |
|---|--:|--:|--:|
| dotnet build (restore) | 1400 | 1558 | baseline |
| bsharp build [launcher] (fast-restore) | 332 | 425 | -73% (3.7× faster) |
| bsharp build [direct] (fast-restore) | 303 | 342 | -78% (4.5× faster) |

## Noop build  (baseline: dotnet build w/ restore)

| Configuration | min (ms) | median (ms) | median vs baseline |
|---|--:|--:|--:|
| dotnet build (restore) | 1365 | 1534 | baseline |
| dotnet build (no-restore) | 839 | 1014 | -34% (1.5× faster) |
| bsharp build [launcher] (fast-restore) | 306 | 382 | -75% (4.0× faster) |
| bsharp build [direct] (fast-restore) | 261 | 296 | -81% (5.2× faster) |
| bsharp build [launcher] (no-restore, no-fast-noop) | 166 | 186 | -88% (8.2× faster) |
| bsharp build [launcher] (no-restore, fast-noop) | 109 | 131 | -91% (11.7× faster) |
| bsharp build [direct] (no-restore, no-fast-noop) | 102 | 110 | -93% (13.9× faster) |
| bsharp build [direct] (no-restore, fast-noop) | 54 | 56 | -96% (27.2× faster) |

## Incremental build  (baseline: dotnet build w/ restore)

| Configuration | min (ms) | median (ms) | median vs baseline |
|---|--:|--:|--:|
| dotnet build (restore) | 1376 | 1522 | baseline |
| dotnet build (no-restore) | 907 | 994 | -35% (1.5× faster) |
| bsharp build [launcher] (fast-restore) | 394 | 450 | -70% (3.4× faster) |
| bsharp build [direct] (fast-restore) | 309 | 322 | -79% (4.7× faster) |
| bsharp build [launcher] (no-restore, fast-noop) | 196 | 210 | -86% (7.2× faster) |
| bsharp build [launcher] (no-restore, no-fast-noop) | 201 | 208 | -86% (7.3× faster) |
| bsharp build [direct] (no-restore, fast-noop) | 135 | 149 | -90% (10.2× faster) |
| bsharp build [direct] (no-restore, no-fast-noop) | 132 | 138 | -91% (11.0× faster) |

## Restore head-to-head  (baseline: dotnet restore)

| Configuration | min (ms) | median (ms) | median vs baseline |
|---|--:|--:|--:|
| dotnet restore | 955 | 1020 | baseline |
| bsharp restore [launcher] (fast-restore) | 297 | 318 | -69% (3.2× faster) |
| bsharp restore [launcher] (no-fast-restore) | 293 | 316 | -69% (3.2× faster) |
| bsharp restore [direct] (no-fast-restore) | 227 | 246 | -76% (4.1× faster) |
| bsharp restore [direct] (fast-restore) | 232 | 244 | -76% (4.2× faster) |

## Launcher vs direct overhead (median ms)

Same host binary invoked via the `bsharp` launcher (L) vs executing `.bsharp/build` directly (D).

| Scenario / config | launcher | direct | overhead |
|---|--:|--:|--:|
| noop · no-restore · fast-noop | 131 | 56 | 74 |
| noop · no-restore · no-fast-noop | 186 | 110 | 76 |
| noop · fast-restore | 382 | 296 | 86 |
| incremental · no-restore · fast-noop | 210 | 149 | 62 |
| incremental · fast-restore | 450 | 322 | 128 |
| clean · fast-restore | 425 | 342 | 82 |
| restore cmd · fast-restore | 318 | 244 | 73 |
| restore cmd · no-fast-restore | 316 | 246 | 70 |

**Median launcher overhead: 75 ms** (range 62–128 ms). This is pure `Process.Start`+`WaitForExit`; an `exec()` replacement on Unix would reclaim it.

## Sanity-check notes

- **Within-tool gradient holds:** bsharp clean (425/342) > incremental (210/149 no-restore fast-noop) > noop (131/56). The textbook fast-noop gradient is clearest in *direct, no-restore* rows.
- **fast-restore ≈ no-fast-restore in the restore head-to-head** because the prep step removes `obj/` before the timed `bsharp restore`, so the freshness check correctly cannot skip (assets absent) and both do a full in-process restore. Exactly the expected behavior.
- **In-process restore is ~3.2× (launcher) / ~4.2× (direct) faster than `dotnet restore`**, confirming the restore-path rewrite.
