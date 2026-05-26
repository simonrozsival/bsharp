# Fast Iteration Guide

## The 10ms Build Pattern

For the fastest possible inner-loop builds, **invoke the generated host directly** instead of going through the launcher.

### Quick Start

```bash
# One-time: Generate the build host
bsharp build MyProject.csproj --no-restore

# Fast iteration: Direct invocation (~10ms)
cd my-project
./.bsharp/build build --no-restore
./.bsharp/build build --no-restore
./.bsharp/build build --no-restore
```

### Performance Comparison

| Method | Time | Use Case |
|--------|------|----------|
| **Direct host** | ~10ms | **Fast iteration** (repeated builds) |
| Via launcher | ~180ms | First build, project changes |
| Target | 1ms | Future: resident daemon |

### Why is Direct Invocation Faster?

**Launcher overhead:** The `bsharp` launcher validates the project cache on every invocation:
- Reads .csproj, Directory.Build.props/targets, global.json
- Computes SHA256 hash of all inputs
- Compares with cached hash
- **Cost:** ~170ms

**Direct invocation skips this** - the generated host just runs the build.

### When to Regenerate

The generated host is valid until the project shape changes:

**Regenerate when you:**
- ✅ Change .csproj (add/remove files, change settings)
- ✅ Modify Directory.Build.props/targets
- ✅ Update NuGet packages
- ✅ Change global properties (`-p:Configuration=Release`)

**No regeneration needed for:**
- ❌ Editing source code (.cs files)
- ❌ Changing build outputs
- ❌ Most day-to-day development

### Aliases for Convenience

Add to your shell profile:

```bash
# Bash/Zsh
alias bb='./.bsharp/build build --no-restore'
alias br='./.bsharp/build run --no-restore'

# Then just:
bb  # 10ms build
br  # 10ms build + run
```

### Watch Mode (Coming Soon)

For automatic builds on file changes, watch mode will provide the best UX while maintaining 10ms build times.

### Path to 1ms

To hit the 1ms target, we need a **resident daemon** that:
- Eliminates process startup (~10ms overhead)
- Keeps project state in memory
- Accepts builds over IPC

**Estimated effort:** 3-4 days  
**Benefit:** Sub-millisecond no-op builds  
**Trade-off:** Added complexity (daemon lifecycle management)

## FAQ

**Q: Why not optimize the launcher instead?**  
A: File I/O is the bottleneck. Even checking mtimes of 10-20 files takes 50-100ms. Can't hit 1ms without eliminating file access entirely.

**Q: Is direct invocation safe?**  
A: Yes! The launcher just generates the host. The host is the real build system.

**Q: What if I forget to regenerate?**  
A: The build will use stale project settings. We're exploring auto-regeneration detection in the host itself.

**Q: Can I commit `.bsharp/build`?**  
A: No - it's machine-specific (contains absolute paths). Keep it in `.gitignore`.
