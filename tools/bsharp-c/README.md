# bsharp-c — plain-C launcher prototype

A second-generation launcher prototype after `bsharp-go`. Goal: how close to
the syscall floor can we get if the launcher is written in C with only libc +
CommonCrypto (Apple's SHA-256, already linked into every process via libSystem)?

## What it does

Same scope as `bsharp-go`: warm fast-path only. Handles arg parsing
(build/run, `-p:KEY=VAL`, `-v:LEVEL`, `--no-restore`, `--no-fast-noop`,
positional `.csproj`, `--project`), resolves `.bsharp/...` cache root (with
`-p:` variant SHA-256 hashing — byte-identical to the C# launcher — and
`-p:TargetFramework=...` inner directories), walks the static MSBuild graph
to check freshness (csproj + ancestor `Directory.Build.{props,targets}` +
`Directory.Packages.props` + `NuGet.config` + `global.json` +
`packages.lock.json` + `obj/project.assets.json` + recursive `<Import>` +
recursive `<ProjectReference>`), and `execve`s into `.bsharp/build`.

Anything off the fast path (cache miss, `--no-cache`, `audit`/`clean`/`test`,
`-t:Target`, `--background-codegen`, solutions, missing csproj) falls back to
the sibling C# launcher (`bsharp`) by forwarding `argv` verbatim.

## How to build

`build.sh` runs `cc -O3` automatically. Manually:

    cd tools/bsharp-c
    cc -O3 -Wall -Wextra -o bsharp-c bsharp-c.c

The binary is ~53 KB (vs 1.7 MB Go, 8.6 MB C# NativeAOT). Single
translation unit, single source file, links only libSystem.

## Numbers (2026-05-29, console-net11, warm, n=100, clock_gettime)

Pure launcher startup overhead (no host invocation; runs in empty dir →
fallback to `/usr/bin/true`; baseline 2.55 ms = `posix_spawn`+`waitpid`):

    bsharp-c    median  6.90 ms  (~4.4 ms of actual launcher work)
    bsharp-go   median  8.52 ms  (~6.0 ms of actual launcher work)
    bsharp-cs   median  9.48 ms  (~6.9 ms of actual launcher work)

Touched-source warm build (`--no-fast-noop` against the real host):

    bsharp-c    median 91.5 ms
    bsharp-go   median 84.9 ms
    bsharp-cs   median 88.4 ms

All three are within noise. The launcher itself is no longer the bottleneck;
the host process (NativeAOT startup + populate + IPC to bsharp-taskd +
compilation) dominates the warm wall time.

## Takeaways

- We can save ~3 ms by going from C# NativeAOT to plain C, but the savings
  don't show up in end-to-end warm builds because the launcher is already
  ~7-10 ms of a ~90 ms total.
- The XDocument optimization in the C# launcher (commit fdff311) was the
  biggest single launcher-side win we could make; further launcher-language
  changes are noise on the real workload.
- This is a research datapoint, not a recommendation to ship.

## Caveats

- macOS-only (`CommonCrypto/CommonDigest.h`, `_NSGetExecutablePath`,
  `st_mtimespec`). The freshness check uses `clock_gettime`-compatible
  mtime comparisons. For Linux, swap `CommonCrypto` for OpenSSL or
  hand-rolled SHA-256, and the executable-path lookup for
  `readlink("/proc/self/exe")`.
- Cache-root hash MUST stay byte-for-byte identical to C# (`HashGlobalPropertySet`)
  and Go (`resolveProjectCacheRoot`). Any drift means the C launcher fast-paths
  into the wrong cache directory.
- Freshness inputs MUST match the C# launcher exactly. Missing one means
  silent stale-build success.
