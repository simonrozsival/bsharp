// Package runtime is the hand-written runtime library that bsharp's generated
// Go build host depends on. It mirrors the runtime helpers the C# emitter
// inlines into every generated build host (Item, ItemSerde, CondHelpers,
// TargetRuntime, Log, PathUtil, FastPathFileHelpers, BatchRuntime, ParamList,
// OutputList, SplitList, StringList, StringSet, TargetFrameworkHelpers,
// TargetIncrementality, plus the local Task implementations).
//
// Design notes
//
//   - Sync, not async. The generated Go target methods are plain functions:
//     `func T_001_Foo() error`. Concurrent dependency batches are not
//     supported in this prototype. The C# host uses `async ValueTask` +
//     `TargetRuntime.TryEnter` + `TaskCompletionSource` to overlap
//     independent dependency chains; the Go equivalent uses sync.Once for
//     the execute-once guard and runs dependencies sequentially. See README
//     for the trade-off.
//
//   - Case-insensitive metadata. Item metadata keys are stored case-
//     preserving but compared case-insensitively, matching MSBuild and the
//     C# Item type. We achieve this by lower-casing the key on access.
//
//   - Path normalization. Identities and metadata values use the host
//     platform separator; PathUtil.NormalizeSeparators converts \ to / on
//     non-Windows, matching the C# behavior.
//
//   - Identity. This package keeps the Item type and the runtime helpers
//     intentionally small and dependency-free. Real SDK tasks delegate to
//     the `bsharp-taskd` daemon via the sibling `taskd` package; local task
//     fallbacks live in `tasks_local.go`.
package runtime
