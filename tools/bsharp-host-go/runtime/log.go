package runtime

import (
	"fmt"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// Verbosity matches the C# Log.Verbosity enum.
type Verbosity int

const (
	VerbosityQuiet      Verbosity = 0
	VerbosityMinimal    Verbosity = 1
	VerbosityNormal     Verbosity = 2
	VerbosityDetailed   Verbosity = 3
	VerbosityDiagnostic Verbosity = 4
)

// ParseVerbosity is the Go equivalent of `Log.Parse`.
func ParseVerbosity(s string) Verbosity {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "q", "quiet":
		return VerbosityQuiet
	case "m", "minimal":
		return VerbosityMinimal
	case "n", "normal":
		return VerbosityNormal
	case "d", "detailed":
		return VerbosityDetailed
	case "diag", "diagnostic":
		return VerbosityDiagnostic
	}
	return VerbosityMinimal
}

// Log is the global build log. Singleton — matches the C# static `Log` class.
var Log = &Logger{
	Level:  VerbosityMinimal,
	stderr: os.Stderr,
	stdout: os.Stdout,
	start:  time.Now(),
}

// Logger is the runtime log surface. Output streams are configurable for tests.
type Logger struct {
	Level   Verbosity
	stdout  *os.File
	stderr  *os.File
	start   time.Time
	taskNs  int64
	tgtNs   int64
	current atomic.Pointer[string]
}

// SetStreams overrides the default os.Stdout / os.Stderr (used by tests).
func (l *Logger) SetStreams(stdout, stderr *os.File) {
	l.stdout = stdout
	l.stderr = stderr
}

// CumulativeTaskMs returns total time spent inside task bodies, milliseconds.
func (l *Logger) CumulativeTaskMs() float64 {
	return float64(atomic.LoadInt64(&l.taskNs)) / 1e6
}

// CumulativeTargetMs returns total time spent inside target bodies, milliseconds.
func (l *Logger) CumulativeTargetMs() float64 {
	return float64(atomic.LoadInt64(&l.tgtNs)) / 1e6
}

// ResetTaskTiming clears the task-time accumulator (for warm-build benchmarks).
func (l *Logger) ResetTaskTiming() { atomic.StoreInt64(&l.taskNs, 0) }

// prefix returns the "[xxx.xxms]" timestamp used in all log lines.
func (l *Logger) prefix() string {
	return fmt.Sprintf("[%8.2fms]", float64(time.Since(l.start).Nanoseconds())/1e6)
}

// TaskScope is the Go equivalent of C# `Log.Task(name)` IDisposable.
type TaskScope struct {
	name    string
	started time.Time
	log     *Logger
}

// Task begins a scoped task log. Call End() (typically via defer) when the
// task finishes.
func (l *Logger) Task(name string) *TaskScope {
	return &TaskScope{name: name, started: time.Now(), log: l}
}

// End records the elapsed time and emits the "Task: name (Xms)" line if
// verbosity allows.
func (s *TaskScope) End() {
	elapsed := time.Since(s.started)
	atomic.AddInt64(&s.log.taskNs, elapsed.Nanoseconds())
	if s.log.Level < VerbosityNormal {
		return
	}
	fmt.Fprintf(s.log.stdout, "%s Task: %s (%.2fms)\n", s.log.prefix(), s.name, float64(elapsed.Nanoseconds())/1e6)
}

// TaskStarted returns the start time to be passed to TaskFinished. Use when
// you don't have a defer-friendly call site.
func (l *Logger) TaskStarted(name string) time.Time { return time.Now() }

// TaskFinished records elapsed time and emits the "Task: name (Xms)" line.
func (l *Logger) TaskFinished(name string, started time.Time) {
	elapsed := time.Since(started)
	atomic.AddInt64(&l.taskNs, elapsed.Nanoseconds())
	if l.Level < VerbosityNormal {
		return
	}
	fmt.Fprintf(l.stdout, "%s Task: %s (%.2fms)\n", l.prefix(), name, float64(elapsed.Nanoseconds())/1e6)
}

// TargetStarted records the start time of a target and returns it (so the
// caller can pass it to TargetFinished).
func (l *Logger) TargetStarted(name string) time.Time {
	now := time.Now()
	nameCopy := name
	l.current.Store(&nameCopy)
	if l.Level >= VerbosityDetailed {
		fmt.Fprintf(l.stdout, "%s Target started:   %s\n", l.prefix(), name)
	}
	return now
}

// TargetFinished records elapsed time and clears the current-target slot.
func (l *Logger) TargetFinished(name string, started time.Time) {
	elapsed := time.Since(started)
	atomic.AddInt64(&l.tgtNs, elapsed.Nanoseconds())
	if l.Level >= VerbosityDetailed {
		fmt.Fprintf(l.stdout, "%s Target finished:  %s (%.2fms)\n", l.prefix(), name, float64(elapsed.Nanoseconds())/1e6)
	}
	cur := l.current.Load()
	if cur != nil && *cur == name {
		var empty string
		l.current.Store(&empty)
	}
}

// TargetSkipped emits the "Target skipped" diagnostic line.
func (l *Logger) TargetSkipped(name, reason string) {
	if l.Level >= VerbosityDetailed {
		fmt.Fprintf(l.stdout, "%s Target skipped:   %s (%s)\n", l.prefix(), name, reason)
	}
}

// TargetUpToDate emits the "Target up-to-date" diagnostic line.
func (l *Logger) TargetUpToDate(name string) {
	if l.Level >= VerbosityDetailed {
		fmt.Fprintf(l.stdout, "%s Target up-to-date: %s\n", l.prefix(), name)
	}
}

// MessageHigh / Normal / Low / Warning / Error mirror the LogMessage* and
// LogWarning / LogError methods on the C# Log class. They are used by local
// task implementations and by task-server result handlers when surfacing
// daemon-reported messages.

func (l *Logger) MessageHigh(text string) {
	if l.Level >= VerbosityMinimal {
		fmt.Fprintf(l.stdout, "%s   %s\n", l.prefix(), text)
	}
}

func (l *Logger) MessageNormal(text string) {
	if l.Level >= VerbosityNormal {
		fmt.Fprintf(l.stdout, "%s   %s\n", l.prefix(), text)
	}
}

func (l *Logger) MessageLow(text string) {
	if l.Level >= VerbosityDetailed {
		fmt.Fprintf(l.stdout, "%s   %s\n", l.prefix(), text)
	}
}

func (l *Logger) Warning(code, text string) {
	if l.Level < VerbosityMinimal {
		return
	}
	if code == "" {
		fmt.Fprintf(l.stdout, "%s warning: %s\n", l.prefix(), text)
	} else {
		fmt.Fprintf(l.stdout, "%s warning %s: %s\n", l.prefix(), code, text)
	}
}

func (l *Logger) Error(code, text string) {
	if code == "" {
		fmt.Fprintf(l.stderr, "%s error: %s\n", l.prefix(), text)
	} else {
		fmt.Fprintf(l.stderr, "%s error %s: %s\n", l.prefix(), code, text)
	}
}

// logMu protects rarely-used global side effects (stream replacement).
var logMu sync.Mutex //nolint:unused // reserved for future global mutations
