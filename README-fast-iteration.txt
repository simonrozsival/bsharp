
## ⚡ Fast Iteration (10ms builds)

For the fastest inner-loop builds, **invoke the generated host directly**:

```bash
# One-time: Generate host
bsharp build MyProject.csproj --no-restore

# Fast iteration: Direct invocation (~10ms)
cd my-project
./.bsharp/build build --no-restore
./.bsharp/build build --no-restore
```

**Performance:**
- Direct host: **10ms** no-op builds (67x faster than dotnet build)
- Via launcher: 212ms no-op builds (3.2x faster than dotnet build)

See [FAST-ITERATION-GUIDE.md](FAST-ITERATION-GUIDE.md) for details.
