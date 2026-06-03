# B# benchmark matrix — full analysis

Source: `raw2-20260603-144807.csv` · 480 rows · ~20 iters/config · console-net11 · macOS arm64, SDK 11.0.100-preview.4. **Launcher rows dropped — all `bsharp` rows below run `.bsharp/build` directly.**

Sorted slowest→fastest by **median** (bold = fastest in table). All times in **ms**. `median vs. baseline` is signed (− = faster). High variance (±20–30%); trust ratios over absolutes.

## Clean build  (baseline: dotnet build w/ restore)

| Tool | Restore | Fast-noop | min | median | p95 | max | median vs. baseline |
|---|---|---|--:|--:|--:|--:|--:|
| dotnet | yes | – | 1400 | 1558 | 1661 | 1738 | baseline |
| bsharp | yes | – | 303 | **342** | 428 | 437 | **-78% (4.6× faster)** |

## Noop build  (baseline: dotnet build w/ restore)

| Tool | Restore | Fast-noop | min | median | p95 | max | median vs. baseline |
|---|---|---|--:|--:|--:|--:|--:|
| dotnet | yes | – | 1365 | 1534 | 1708 | 1883 | baseline |
| dotnet | no | – | 839 | 1014 | 1157 | 1238 | -34% (1.5× faster) |
| bsharp | yes | – | 261 | 296 | 309 | 330 | -81% (5.2× faster) |
| bsharp | no | no | 102 | 110 | 124 | 145 | -93% (13.9× faster) |
| bsharp | no | yes | 54 | **56** | 60 | 60 | **-96% (27.4× faster)** |

## Incremental build  (baseline: dotnet build w/ restore)

| Tool | Restore | Fast-noop | min | median | p95 | max | median vs. baseline |
|---|---|---|--:|--:|--:|--:|--:|
| dotnet | yes | – | 1376 | 1522 | 1674 | 1690 | baseline |
| dotnet | no | – | 907 | 994 | 1112 | 1191 | -35% (1.5× faster) |
| bsharp | yes | – | 309 | 322 | 347 | 350 | -79% (4.7× faster) |
| bsharp | no | yes | 135 | 149 | 166 | 190 | -90% (10.2× faster) |
| bsharp | no | no | 132 | **138** | 159 | 161 | **-91% (11.0× faster)** |

## Restore head-to-head  (baseline: dotnet restore)

*The third column is **Fast-restore** here (the restore-path freshness check), not Fast-noop.*

| Tool | Restore | Fast-restore | min | median | p95 | max | median vs. baseline |
|---|---|---|--:|--:|--:|--:|--:|
| dotnet | yes | – | 955 | 1020 | 1115 | 1144 | baseline |
| bsharp | yes | no | 227 | 246 | 254 | 289 | -76% (4.1× faster) |
| bsharp | yes | yes | 232 | **244** | 266 | 280 | **-76% (4.2× faster)** |

## Sanity-check notes

- **Within-tool gradient holds:** clean (342) > incremental no-restore (138–149) > noop no-restore (56–110). The fast-noop gradient is clearest in the no-restore rows.
- **fast-restore ≈ no-fast-restore in the restore head-to-head** because the prep step removes `obj/` before the timed `bsharp restore`, so the freshness check correctly cannot skip (assets absent) and both do a full in-process restore.
- **In-process restore is ~4.2× faster than `dotnet restore`** (244 vs 1020 ms median), confirming the restore-path rewrite.
- **Speedups come from doing less:** B# only handles this one fixed, trivial project shape and skips most of what MSBuild checks. Not a fair fight — by design.
