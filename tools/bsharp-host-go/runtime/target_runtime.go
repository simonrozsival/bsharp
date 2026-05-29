package runtime

import (
	"strings"
	"sync"
)

// TargetState tracks the once-only execution state of one generated target.
// The C# host uses an int + TaskCompletionSource so multiple goroutines can
// race to be the executor. In this Go prototype, generated target methods are
// sequential, so a simple sync.Once-like guard is sufficient:
//
//   - the first caller transitions Pending → Running, runs the body, and
//     finishes either Done or Skipped (condition false).
//   - subsequent callers see Done and return without re-running, or see
//     Skipped and re-run (matching MSBuild's "skipped target can run again
//     later if condition becomes true").
//
// The Outcome field records the last completion so callers can implement the
// "condition was false → may try again" semantics without re-doing work.
type TargetState struct {
	mu      sync.Mutex
	outcome targetOutcome
}

type targetOutcome uint8

const (
	pending  targetOutcome = iota // never executed
	running                       // currently executing
	done                          // body ran to completion
	skipped                       // condition was false; may be retried later
)

// TryEnter returns true if the caller should run the target body. If false,
// the target has already completed; the caller must short-circuit.
//
// Note: this prototype is intentionally simpler than the C# TargetRuntime
// because Go generated targets are sequential within a single build. Cycle
// detection is not implemented.
func (t *TargetState) TryEnter() bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	switch t.outcome {
	case done:
		return false
	case running:
		// Sequential generator should never call a still-running target.
		// We defensively treat this as a "skip body".
		return false
	}
	t.outcome = running
	return true
}

// MarkDone records that the body ran successfully. Subsequent TryEnter calls
// will return false.
func (t *TargetState) MarkDone() {
	t.mu.Lock()
	t.outcome = done
	t.mu.Unlock()
}

// MarkSkipped records that the condition was false; subsequent TryEnter calls
// will still return true (matching MSBuild: a skipped target may execute later
// if the condition becomes true).
func (t *TargetState) MarkSkipped() {
	t.mu.Lock()
	t.outcome = pending
	t.mu.Unlock()
}

// IsDone returns true iff the target has completed successfully.
func (t *TargetState) IsDone() bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.outcome == done
}

// ErrorRecord is the Go equivalent of `Targets.Errors`: a (target, error)
// pair recorded when a target body throws. Build summary prints these at the
// end of the build.
type ErrorRecord struct {
	Target  string
	Message string
}

// ErrorList is the global collection of target errors. Append-only across
// goroutines; use Append to mutate.
type ErrorList struct {
	mu   sync.Mutex
	list []ErrorRecord
}

// Append records a target-attributed error.
func (e *ErrorList) Append(target, message string) {
	e.mu.Lock()
	e.list = append(e.list, ErrorRecord{Target: target, Message: message})
	e.mu.Unlock()
}

// Snapshot returns a copy of the current error list (safe for printing).
func (e *ErrorList) Snapshot() []ErrorRecord {
	e.mu.Lock()
	defer e.mu.Unlock()
	out := make([]ErrorRecord, len(e.list))
	copy(out, e.list)
	return out
}

// HasErrors returns true if any error has been recorded.
func (e *ErrorList) HasErrors() bool {
	e.mu.Lock()
	defer e.mu.Unlock()
	return len(e.list) > 0
}

// FormatSummary renders the error list one record per line, prefixed with the
// target name (for use in the build summary).
func (e *ErrorList) FormatSummary() string {
	snapshot := e.Snapshot()
	if len(snapshot) == 0 {
		return ""
	}
	var b strings.Builder
	for _, r := range snapshot {
		b.WriteString("error in ")
		b.WriteString(r.Target)
		b.WriteString(": ")
		b.WriteString(r.Message)
		b.WriteByte('\n')
	}
	return b.String()
}

// targetStateRegistry holds a *TargetState per target name. Emitted target
// functions call GetTargetState at the top of each invocation to get their
// per-build state; this matches the C# generated host where each target
// declares a static field that doubles as the once-only execution gate.
var targetStateRegistry sync.Map

// GetTargetState returns the shared *TargetState for the target identified by
// name. Concurrency-safe; the first caller per name wins, subsequent callers
// observe the same instance.
func GetTargetState(name string) *TargetState {
	if v, ok := targetStateRegistry.Load(name); ok {
		return v.(*TargetState)
	}
	v, _ := targetStateRegistry.LoadOrStore(name, &TargetState{})
	return v.(*TargetState)
}

// globalErrors is the singleton ErrorList consulted by all emitted target
// functions. Mirrors `Targets.Errors` in the C# host.
var globalErrors = &ErrorList{}

// GetErrorList returns the global build error list. Append errors via
// list.Append("TargetName", err.Error()).
func GetErrorList() *ErrorList { return globalErrors }

// Add is a convenience alias for Append that accepts an error directly,
// matching the pattern emitted by the Go codegen.
func (e *ErrorList) Add(target string, err error) {
	if err == nil {
		return
	}
	e.Append(target, err.Error())
}

