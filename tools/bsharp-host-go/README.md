# bsharp-host-go — Go build host experiment

This directory contains an experimental Go build host that talks to the
existing C# `bsharp-taskd` daemon. The goal was to answer one question:

> If we replaced the generated NativeAOT C# build host with a generated
> Go program, how much faster could a warm build be?

## Architecture

```
launcher (C# NativeAOT, unchanged)
   ↓ execve
generated Go host (this experiment)
   ↓ JSON-framed length-prefixed IPC over Unix socket
bsharp-taskd (C# CoreCLR R2R, unchanged)
   ↓ direct task instantiation
real SDK tasks (Csc, RAR, AssignTargetPath, …)
```

The Go host re-implements just one thing: the IPC client that today lives
in the generated `TaskRunner` class. The daemon (and the real MSBuild
tasks it loads) stay 100% unchanged.

## Components

- `taskd/` — minimal Go client for the bsharp-taskd JSON protocol.
  Mirrors `TaskModel.cs` types and `DaemonPaths.cs` discovery exactly.
- `cmd/replay/` — replays a `BSHARP_TASKD_TRACE=<path>` JSONL capture
  against the daemon. Used to measure the lower bound on host wall time.
- `cmd/justconnect/` — connects and handshakes once, exits. Measures
  pure Go-binary startup + Unix-socket handshake cost.

## Measurement methodology

1. Cold-spawn the daemon with `BSHARP_TASKD_TRACE=/tmp/warm.trace.jsonl`.
   Run two warm builds and discard the cold trace; keep only the second
   warm-build trace (42 task invocations for console-net11).
2. Kill the daemon and re-spawn it WITHOUT trace (so per-iteration
   replays don't pollute the trace file).
3. Touched-source warm builds: `clock_gettime` wrapper measures
   posix_spawn → exit, 30 iterations each.

## Results (console-net11, warm touched-source build)

| Scenario                                | min  | median | p90  | mean  |
|-----------------------------------------|------|--------|------|-------|
| C# NativeAOT host (current)             | 88.0 | 109.6  | 138.3| 110.3 |
| Go replay (just IPC, no MSBuild eval)   | 53.9 |  64.3  |  84.4|  66.3 |
| Go connect+handshake only               |  6.7 |   7.2  |   9.5|   7.9 |

All numbers are milliseconds.

## Interpretation

- **Pure daemon work is ~57 ms** (64 ms total replay − 7 ms Go startup).
  Both hosts pay this; it's the floor.
- **The C# host adds ~45 ms of overhead** on top of pure daemon time:
  NativeAOT startup, `InitialState.Populate`, target dispatch, expression
  evaluation, property/item state machinery.
- A real Go host (one that does MSBuild evaluation, not just trace
  replay) would land somewhere between 64 ms (replay floor) and 110 ms
  (current C#). A reasonable estimate is **75–90 ms**, i.e. a 20–30 ms
  / ~20–25 % improvement on warm builds.

## Status

This is an **architecture proof**, not a working codegen. It shows the
end-to-end wiring works and the perf headroom is real but modest.
A full Go codegen would need a `GoEmitter` in `tools/codegen/Program.cs`
that mirrors the existing C# emitter — comparable in scope to the
existing 8 000-line C# emitter.

The replay tool stays useful even without further Go-codegen work: it
gives a clean upper-bound for "how fast can a build go if we eliminate
ALL host overhead", and any future host implementation can be compared
against it.

## Build

```bash
cd tools/bsharp-host-go
go build -ldflags '-linkmode=external' -o ./bin/replay ./cmd/replay
codesign -s - --force ./bin/replay   # macOS requires LC_UUID + signature
```

## Re-running the benchmark

```bash
# 1. Kill any running daemon (look up pid in $TMPDIR/bsharp-501/*.pid).
# 2. Cold-spawn daemon WITH trace via a normal build:
BSHARP_TASKD_TRACE=/tmp/warm.trace.jsonl \
  BSHARP_TASKD_PATH=$PWD/tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp-taskd \
  fixtures/console-net11/.bsharp/build --no-restore --no-fast-noop build
# 3. Run a second warm build, discarding the cold trace and keeping the warm one:
> /tmp/warm.trace.jsonl
fixtures/console-net11/.bsharp/build --no-restore --no-fast-noop build
# 4. Kill daemon, re-spawn WITHOUT trace, benchmark:
./bin/replay -trace /tmp/warm.trace.jsonl -sdk <fingerprint> -quiet
```
