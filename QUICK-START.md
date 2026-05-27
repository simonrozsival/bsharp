# bsharp Quick Start Guide

## What is bsharp?

bsharp is a fast build tool for .NET projects. It:
1. Generates a custom NativeAOT build host for your project
2. Caches it for subsequent builds
3. Provides ~10ms incremental builds (vs ~700ms for `dotnet build`)

## Installation

The bsharp binary is located at:
```bash
tools/bsharp/bin/Release/net11.0/osx-arm64/publish/bsharp
```

A symlink has been created at `~/.local/bin/bsharp`.

**Add to PATH** (if not already):
```bash
echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.zshrc
source ~/.zshrc
```

## Basic Usage

### First Build (generates host)

```bash
cd your-project/
bsharp build YourProject.csproj
```

This will:
1. Compute a "shape hash" of your project
2. Generate a custom build host in `.bsharp/variants/<hash>/`
3. Compile it with NativeAOT + CoreCLR task sidekick
4. Run the build

**First build takes ~60s** (one-time codegen + NativeAOT compilation).

### Subsequent Builds (fast!)

```bash
bsharp build YourProject.csproj
```

**Takes ~130ms** through the launcher, or **~10ms** if you invoke the host directly.

### Direct Host Invocation (fastest)

For rapid iteration:

```bash
cd your-project/
./.bsharp/build build
```

**Takes ~10ms!** This skips the launcher entirely.

## Common Commands

### Build
```bash
bsharp build project.csproj              # Full build
bsharp build project.csproj --no-restore # Skip restore
```

### Clean
```bash
bsharp clean project.csproj
```

### Run
```bash
bsharp run project.csproj                # Build + run
bsharp run project.csproj -- arg1 arg2   # Pass arguments
```

### Audit
```bash
bsharp audit project.csproj              # Show what would be generated
```

### Cache Management
```bash
bsharp build --no-cache project.csproj   # Force regenerate host
rm -rf .bsharp/                          # Clear all cached hosts
```

## Fast Iteration Workflow

**Option 1: Use direct host invocation**

```bash
# Initial build (generates host)
bsharp build project.csproj

# Fast iteration (10ms builds)
./.bsharp/build build
./.bsharp/build build
./.bsharp/build build
```

Regenerate the host when you:
- Change project structure (add/remove files)
- Change .csproj properties
- Update dependencies

**Option 2: Use launcher with fast-path**

If you build within 1 second of the previous build, the launcher skips hash recomputation:

```bash
bsharp build project.csproj   # ~134ms (slow path)
bsharp build project.csproj   # ~131ms (fast path, within 1s)
bsharp build project.csproj   # ~131ms (fast path)
```

After 1+ second gap, it validates the hash again (~134ms).

## Performance Characteristics

| Method | Time | When to Use |
|--------|------|-------------|
| **Direct host** | **~10ms** | Fast iteration on stable project |
| Launcher (fast) | ~131ms | Rapid builds (within 1s) |
| Launcher (slow) | ~134ms | First build, or after 1s gap |
| First codegen | ~60s | One-time per project shape |
| `dotnet build` | ~670ms | Baseline comparison |

## Project Structure

After running bsharp, you'll have:

```
your-project/
├── .bsharp/
│   └── variants/
│       └── <hash>/              # One per project "shape"
│           ├── build            # The NativeAOT build host (symlink)
│           ├── shape.hash       # Cached hash value
│           ├── src/             # Generated C# source
│           │   ├── Program.cs   # Main entry point
│           │   ├── TaskModel.cs # Task definitions
│           │   └── ...
│           └── task-server/     # CoreCLR task sidekick
│               └── ...
├── YourProject.csproj
├── obj/
└── bin/
```

## Supported Projects

Currently tested:
- ✅ Console apps (net11.0)
- ⚠️  MAUI apps (experimental, some blockers)

Requirements:
- .NET 11 SDK (preview.4.26230.115 or later)
- macOS arm64 (for NativeAOT host)
- Static project structure (no dynamic imports/wildcards)

## Environment Variables

### BSHARP_CODEGEN
Path to the codegen tool. Auto-detected if not set.

```bash
export BSHARP_CODEGEN=/path/to/Codegen.dll
```

### BSHARP_BACKGROUND_CODEGEN
Enable background codegen (falls back to `dotnet build` while host generates):

```bash
export BSHARP_BACKGROUND_CODEGEN=1
bsharp build project.csproj  # Uses dotnet build, then regenerates host in background
```

## Troubleshooting

### "cannot find codegen tool"

Set the BSHARP_CODEGEN environment variable:

```bash
export BSHARP_CODEGEN="$(pwd)/tools/codegen/bin/Debug/net11.0/Codegen.dll"
```

### Build host is stale

If you change the project structure and the build fails:

```bash
bsharp build --no-cache project.csproj   # Force regenerate
```

Or just delete the cache:

```bash
rm -rf .bsharp/
```

### Slow builds

If builds are consistently slow (>200ms), check:

1. Are you using NativeAOT Release launcher?
   ```bash
   file ~/.local/bin/bsharp
   # Should show: Mach-O 64-bit executable arm64
   ```

2. Try direct host invocation for fastest builds:
   ```bash
   ./.bsharp/build build
   ```

## Examples

### Console App

```bash
cd /path/to/your/console-app
bsharp build ConsoleApp.csproj
bsharp run ConsoleApp.csproj
```

### With Multiple Target Frameworks

bsharp handles multi-target projects by creating inner hosts:

```bash
bsharp build -p:TargetFramework=net11.0 MyLib.csproj
bsharp build -p:TargetFramework=net8.0 MyLib.csproj
```

Each target framework gets its own host in `.bsharp/inner/<tfm>/`.

## Next Steps

- Try it on your project!
- Report issues at the bsharp repository
- For sub-10ms builds, use direct host invocation
- For sub-1ms builds, we'd need a resident daemon (future work)

## Learn More

- `README.md` - Project overview
- `DESIGN.md` - Architecture details
- `FINAL-BENCHMARK-50x.md` - Performance analysis
- `NATIVEAOT-VS-CORECLR-LAUNCHER.md` - Why NativeAOT is faster

## Setting Up Your Environment

For convenience, add these to your `~/.zshrc` or `~/.bashrc`:

```bash
# Add bsharp to PATH
export PATH="$HOME/.local/bin:$PATH"

# Set BSHARP_CODEGEN (adjust path as needed)
export BSHARP_CODEGEN="$HOME/Projects/playground/bsharp/tools/codegen/bin/Debug/net11.0/Codegen.dll"
```

Then reload your shell:
```bash
source ~/.zshrc
```

## Quick Test

```bash
# Clone or navigate to a test project
cd fixtures/console-net11

# First build (generates host, ~60s)
bsharp build console-net11.csproj

# Subsequent builds (~130ms via launcher)
bsharp build console-net11.csproj

# Or use direct invocation (~10ms)
./.bsharp/build build
```
