# B# fresh benchmarks — fixtures/console-net11

- Machine: macOS 26.5 (Darwin arm64), .NET SDK 11.0.100-preview.4.26230.115
- Tool: Release NativeAOT `bsharp` launcher + Debug `Codegen`
- Project: simple net11.0 console app (`fixtures/console-net11`)
- Methodology: 15 runs/cell, **median and min** reported. Launcher wall time is
  noisy on macOS (scheduler/`WaitForExit` jitter), so **min** ≈ least-contended,
  closest to true compute cost; ratios are more stable than absolutes.
- Reproduce: `files/matrix.sh 15`

## Full matrix — `{bsharp, dotnet}` × `{clean, noop, incremental}` × flags

Flag axis = two independent toggles:
- **`--no-restore`** → restore OFF (bsharp keeps fast-noop ON)
- **`--no-fast-noop`** → fast-noop OFF (restore ON)
- **both** → restore OFF + fast-noop OFF

`dotnet` has no fast-noop, so its `--no-fast-noop` column is a plain
`dotnet build` (restore ON) and its **both** column equals `--no-restore`.

Values are **min ms (median ms)**:

| Scenario | Tool | `--no-restore` | `--no-fast-noop` (restore on) | both |
|---|---|---:|---:|---:|
| **clean** | bsharp | **210 (244)** | 435 (478) | 192 (200) |
| | dotnet | 893 (1049) | 1427 (1784) | =`--no-restore` |
| **noop** | bsharp | **130 (132)** | 183 (189) | 184 (209) |
| | dotnet | 1062 (1114) | 1267 (1649) | =`--no-restore` |
| **incremental** | bsharp | **201 (228)** | 208 (222) | 206 (223) |
| | dotnet | 910 (958) | 1392 (1589) | =`--no-restore` |

(`clean` for `--no-restore`/both removes `bin` only; for `--no-fast-noop` /
restore-on it removes `bin`+`obj` so restore actually runs.)

### Speedups (min, comparing matching restore state)
| Scenario | restore OFF (`--no-restore`/both) | restore ON (`--no-fast-noop`) |
|---|---:|---:|
| clean | 192–210 vs 893 → **~4.3–4.7×** | 435 vs 1427 → **~3.3×** |
| noop | 130–184 vs 1062 → **~5.8–8.2×** | 183 vs 1267 → **~6.9×** |
| incremental | 201–206 vs 910 → **~4.4–4.5×** | 208 vs 1392 → **~6.7×** |

**bsharp wins every cell of the matrix (~3.3–8×).**

## What the matrix reveals about fast-noop

- **Correctness:** the detection is solid (see below). At the **host** level the
  shortcut is real — `cumulative tasks: 0.00ms` with fast-noop vs `~112ms` of
  task work with `--no-fast-noop`; the generated host's own no-op work is
  **~57ms** (fast) vs **~152ms** (full graph).
- **At the launcher level the benefit is small and noisy:** on a no-op,
  `--no-restore` (fast-noop ON) is only tens of ms faster than **both** (fast-noop
  OFF): 130 vs 184 min here (~15 ms in a quieter run). The ~120 ms
  `Process.Start`+`WaitForExit` floor dominates, so the host-level saving is
  largely hidden. This is the strongest argument for the `exec()`/resident-daemon
  improvement.
- **On clean/incremental builds fast-noop is irrelevant** (it never triggers
  because outputs are stale): `clean --no-restore` ≈ `clean both` (210 vs 192,
  within restore/scheduler noise).

## Fast no-op correctness (verified empirically)
Detection is purely mtime-based: returns up-to-date only if the output assembly
is newer than every tracked input (csproj, `Compile` items, analyzers,
`AdditionalFiles`/editorconfig, `ProjectReference` sources, and shape files
`Directory.Build.*`/`Directory.Packages.props`/`global.json`).
- Default no-op → `cumulative tasks: 0.00ms`; `--no-fast-noop` → `~112ms`. Both
  produce correct output.
- **Edit source content** then default build → correctly rebuilds and runs the
  new output (does *not* wrongly skip).
- **`touch` only** → conservatively runs a full build (mtime newer), correct
  output. No false no-ops observed.

## Cold build, host NOT yet built (one-time)
- First-ever codegen + NativeAOT publish of the per-project host: **~30–80 s**.
- Paid once per project *shape* (cache-keyed by shape hash); amortized across
  every later build. After that, all cells above apply.

## Honest framing for the talk
- The win is broad: **every** build type is ~3.3–7.5× faster once the host
  exists — not a single cherry-picked scenario.
- Fast-noop is correct but its *wall-time* payoff is throttled by subprocess
  overhead; the host itself is sub-60 ms. `exec()`/daemon is the obvious next
  win.
- Restore is delegated to `dotnet restore` and isn't B#'s work; `--no-restore`
  columns are the apples-to-apples build-work comparison.
- Absolute ms carry ±20–30% machine variance; trust the ratios.
