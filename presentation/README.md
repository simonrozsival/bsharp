# B# presentation & benchmarks

Materials for the ~15-minute talk on B# — a research experiment that compiles a
closed-world subset of MSBuild into a specialized, NativeAOT-compiled per-project
build host. **It is a proof-of-concept, not an MSBuild replacement.**

## Deck

- [`bsharp-deck.md`](bsharp-deck.md) — Marp source for the slides.
- [`bsharp-deck.html`](bsharp-deck.html) / [`bsharp-deck.pdf`](bsharp-deck.pdf) — rendered deck.
- [`arch-flow.mmd`](arch-flow.mmd) / [`arch-flow.svg`](arch-flow.svg) — the two-phase
  architecture diagram (Mermaid source + rendered SVG embedded in slide 4).

Regenerate the deck (Marp can't render Mermaid natively, so the diagram is
pre-rendered to SVG first):

```bash
npx --yes @mermaid-js/mermaid-cli -i arch-flow.mmd -o arch-flow.svg -b transparent
npx --yes @marp-team/marp-cli@latest bsharp-deck.md -o bsharp-deck.html --allow-local-files
npx --yes @marp-team/marp-cli@latest bsharp-deck.md -o bsharp-deck.pdf  --allow-local-files
```

## Supporting docs

- [`demo-runbook.md`](demo-runbook.md) — live-demo script: env/aliases, the two-phase
  "generate once, then run `.bsharp/build`" flow, and the commands to show.
- [`architecture-deep-dive.md`](architecture-deep-dive.md) — long-form write-up of how
  the generator, host, and task server work, plus what's working and what isn't.
- [`bsharp-gist.md`](bsharp-gist.md) — standalone gist-style summary (motivation,
  results, architecture, limitations, improvements).

## Benchmarks

See [`benchmarks/`](benchmarks/):

- Scripts: `bench.sh`, `matrix.sh`, `matrix-raw.sh`, `matrix-raw2.sh`, `matrix-stats.sh`.
- [`benchmarks/matrix2-analysis.md`](benchmarks/matrix2-analysis.md) — the authoritative
  analysis (per-scenario tables, launcher-vs-direct overhead, sanity checks).
- [`benchmarks/benchmarks.md`](benchmarks/benchmarks.md) — earlier write-up.
- `benchmarks/results/*.csv` — raw timing runs. The authoritative set is
  `raw2-20260603-144807.csv` (480 rows, ~20 iters/config, console-net11, macOS arm64,
  SDK 11.0.100-preview.4).

### Headline numbers (median ms, console-net11)

| Scenario | `dotnet build` (restore) | best B# | speedup |
|---|--:|--:|--:|
| Clean | 1558 | 342 | ~4.5× |
| Incremental | 1522 | 149 | ~10× |
| No-op | 1534 | 56 | ~27× |
| Restore (vs `dotnet restore` 1020) | — | 244 | ~4.2× |

The speedups come **entirely from doing less** — B# only handles this one fixed,
trivial shape and skips most of what MSBuild checks. Numbers are noisy on macOS;
trust the ratios, not the absolute milliseconds.
