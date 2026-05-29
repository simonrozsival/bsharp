# bsharp-go — Go-language launcher prototype

This is a **prototype** to measure how much of the C# NativeAOT launcher's
warm-build overhead is intrinsic to the work it does vs. paid for the managed
runtime startup. The goal is informational: are we paying a meaningful cost
for the launcher being .NET, or is the launcher already near-optimal?

## What it does

`bsharp-go` is a drop-in replacement for the **warm fast-path** of the C#
launcher. It handles:

- argument parsing (`build`/`run`, `-p:`, `-v:`, `--no-restore`, `--no-fast-noop`,
  positional `.csproj`)
- locating the `.csproj` in the working directory
- resolving the cache root under `.bsharp/` (including `-p:` variant hashing
  and `-p:TargetFramework=...` inner directories)
- the `IsHashFileStillFresh` mtime walk over the static MSBuild graph
- `execve` into `.bsharp/build` with `BSHARP_TASKD_PATH`/`BSHARP_LAUNCHER_PATH`
  set

For everything else (cache miss, codegen, `--no-cache`, `audit`/`clean`/`test`,
`-t:Target`, solutions, multi-TFM outer build, `--background-codegen`,
`--no-csproj` errors), it shells out to the sibling C# launcher (`bsharp`).
This way the prototype is small and safe: anything it doesn't handle is
forwarded verbatim to the existing battle-tested launcher.

## How to build

`build.sh` invokes `go build` automatically if Go is installed. To do it
manually:

    cd tools/bsharp-go
    go build -ldflags="-s -w" -trimpath -o bsharp-go .

The binary is ~1.7 MB (vs ~8.6 MB for the C# NativeAOT launcher).

## How to benchmark

After `./build.sh`, the Go launcher is staged next to the C# one as
`tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp-go`. Pick a fixture
and run:

    PUBLISH=tools/bsharp/bin/Release/net11.0/osx-arm64/publish
    cd fixtures/console-net11
    for i in $(seq 1 50); do
        touch Program.cs
        /usr/bin/time -p "$PUBLISH/bsharp-go" build --no-restore --no-fast-noop -v:quiet >/dev/null
        /usr/bin/time -p "$PUBLISH/bsharp"    build --no-restore --no-fast-noop -v:quiet >/dev/null
    done 2>&1 | grep real | ...

## Current numbers (2026-05-29, console-net11, warm)

Touched-source build (`--no-fast-noop`):
- bsharp-go: mean 89 ms, median 90 ms, min 80 ms
- bsharp-cs: mean 107 ms, median 100 ms, min 90 ms
- Go saves ~18 ms (~17%)

Default fast-noop path (no source change):
- bsharp-go: mean 11 ms, median 10 ms
- bsharp-cs: mean 16 ms, median 20 ms
- Go saves ~5 ms (~31% relative)

## Caveats

- This is a **research prototype**, not a replacement. It punts to the C#
  launcher for anything non-trivial.
- The cache-root hash MUST stay byte-for-byte identical to the C# launcher
  (`HashGlobalPropertySet` + `ResolveProjectCacheRoot`). If you change one
  side, change the other or the fast path will look at the wrong directory
  and miss-route to a cold cache.
- The freshness check MUST cover the same inputs as the C# launcher's
  `IsHashFileStillFresh` (csproj + ancestor `Directory.Build.{props,targets}` +
  `Directory.Packages.props` + `NuGet.config` + `global.json` +
  `packages.lock.json` + `obj/project.assets.json` + recursive `<Import>` +
  recursive `<ProjectReference>`). Missing a shape input means stale builds
  silently succeed.
- The Go launcher does NOT recompute or write the shape hash; any cache
  invalidation falls back to the C# launcher.
