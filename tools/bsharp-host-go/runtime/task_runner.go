package runtime

import (
	"encoding/json"
	"fmt"
	"sync"
	"sync/atomic"

	"github.com/simonrozsival/bsharp-host-go/taskd"
)

// TaskRunner is the Go equivalent of the C# `TaskRunner` static class: it
// owns the connection to bsharp-taskd, registers task descriptors at startup,
// and dispatches typed task invocations.
//
// In the C# host, task descriptors (assembly path, full type name, output
// names) are emitted as `Add(...)` calls in a `RegisterTasks` partial method
// inside the generated `Program.cs`. In the Go host, generated code calls
// `Tasks.Register(...)` once at init time to populate the same table.
type TaskRunner struct {
	mu       sync.Mutex
	tasks    map[string]TaskDescriptor
	client   *taskd.Client
	taskdExe string
	sdkFp    string
	cwd      string

	ipcNs int64 // cumulative IPC time, nanoseconds
}

// TaskDescriptor mirrors `TaskRunner.TaskDescriptor` in the C# host.
type TaskDescriptor struct {
	ShortName    string
	FullTypeName string
	AssemblyPath string
	OutputNames  []string
}

// NewTaskRunner returns an unconnected runner. Call Init then EnsureConnected.
func NewTaskRunner(sdkFingerprint string) *TaskRunner {
	return &TaskRunner{
		tasks: make(map[string]TaskDescriptor),
		sdkFp: sdkFingerprint,
	}
}

// Init sets the directory containing the bsharp-taskd binary (host expects
// it as a sibling of the build host). Cwd is captured for invocation payloads.
func (r *TaskRunner) Init(daemonDir, cwd string) {
	r.taskdExe = daemonDir + "/bsharp-taskd"
	r.cwd = cwd
}

// Register adds a task descriptor. Generated code calls this once per task
// in an init function before the build starts.
func (r *TaskRunner) Register(shortName, fullTypeName, assemblyPath string, outputNames []string) {
	r.mu.Lock()
	desc := TaskDescriptor{
		ShortName:    shortName,
		FullTypeName: fullTypeName,
		AssemblyPath: assemblyPath,
		OutputNames:  outputNames,
	}
	r.tasks[shortName] = desc
	if fullTypeName != "" && fullTypeName != shortName {
		r.tasks[fullTypeName] = desc
	}
	r.mu.Unlock()
}

// Has reports whether shortName has been registered.
func (r *TaskRunner) Has(shortName string) bool {
	r.mu.Lock()
	_, ok := r.tasks[shortName]
	r.mu.Unlock()
	return ok
}

// EnsureConnected connects to the daemon if not already connected.
func (r *TaskRunner) EnsureConnected() error {
	r.mu.Lock()
	if r.client != nil {
		r.mu.Unlock()
		return nil
	}
	r.mu.Unlock()
	client, err := taskd.Connect(r.taskdExe, r.sdkFp)
	if err != nil {
		return err
	}
	r.mu.Lock()
	r.client = client
	r.mu.Unlock()
	return nil
}

// Close shuts down the daemon connection.
func (r *TaskRunner) Close() error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.client == nil {
		return nil
	}
	err := r.client.Close()
	r.client = nil
	return err
}

// CumulativeIpcMs reports total time spent inside Invoke, milliseconds.
func (r *TaskRunner) CumulativeIpcMs() float64 {
	return float64(atomic.LoadInt64(&r.ipcNs)) / 1e6
}

// TaskValue is the discriminated payload type for typed task parameters and
// outputs. The C# host emits typed `TaskValue` enums; this Go version uses an
// interface-tagged wrapper that JSON-serializes to the same wire format.
type TaskValue struct {
	Kind   TaskValueKind
	Str    string
	Bool   bool
	Items  []ItemSpec
	Number float64
}

// TaskValueKind tags the payload variant.
type TaskValueKind int

const (
	TaskValueString TaskValueKind = iota
	TaskValueBool
	TaskValueItems
	TaskValueNumber
)

// MarshalJSON emits the wire format expected by bsharp-taskd. The daemon's
// dispatcher inspects the JSON shape to bind to the right CLR setter:
// scalar string/bool/number → primitive; array of ItemSpec → ITaskItem[].
func (v TaskValue) MarshalJSON() ([]byte, error) {
	switch v.Kind {
	case TaskValueString:
		return json.Marshal(v.Str)
	case TaskValueBool:
		return json.Marshal(v.Bool)
	case TaskValueNumber:
		return json.Marshal(v.Number)
	case TaskValueItems:
		return json.Marshal(v.Items)
	}
	return []byte("null"), nil
}

// TaskRequest is the in-progress build of a TaskInvocation. The generated
// host builds one of these per task call, fills properties, and ships it.
type TaskRequest struct {
	descriptor TaskDescriptor
	props      map[string]json.RawMessage
	outputs    []string
}

// BeginTask returns a TaskRequest for the named (registered) task.
func (r *TaskRunner) BeginTask(shortName string) (*TaskRequest, error) {
	r.mu.Lock()
	desc, ok := r.tasks[shortName]
	r.mu.Unlock()
	if !ok {
		return nil, fmt.Errorf("task '%s' is not registered", shortName)
	}
	return &TaskRequest{
		descriptor: desc,
		props:      make(map[string]json.RawMessage),
	}, nil
}

// SetString sets a string parameter.
func (t *TaskRequest) SetString(name, value string) error {
	b, err := json.Marshal(value)
	if err != nil {
		return err
	}
	t.props[name] = b
	return nil
}

// SetBool sets a bool parameter.
func (t *TaskRequest) SetBool(name string, value bool) error {
	b, err := json.Marshal(value)
	if err != nil {
		return err
	}
	t.props[name] = b
	return nil
}

// SetItems sets an ITaskItem[]-typed parameter.
func (t *TaskRequest) SetItems(name string, items []ItemSpec) error {
	b, err := json.Marshal(items)
	if err != nil {
		return err
	}
	t.props[name] = b
	return nil
}

// SetItem sets an ITaskItem-typed parameter.
func (t *TaskRequest) SetItem(name string, item ItemSpec) error {
	b, err := json.Marshal(item)
	if err != nil {
		return err
	}
	t.props[name] = b
	return nil
}

// SetStringSlice sets a string[]-typed parameter. The SDK side receives
// a JSON array of strings; the daemon coerces to whatever string[]-shape
// the destination property expects.
func (t *TaskRequest) SetStringSlice(name string, values []string) error {
	b, err := json.Marshal(values)
	if err != nil {
		return err
	}
	t.props[name] = b
	return nil
}

// SetRaw sets a parameter to a pre-marshaled JSON value.
func (t *TaskRequest) SetRaw(name string, raw json.RawMessage) {
	t.props[name] = raw
}

// ExpectOutput registers an output name on this request. The daemon's
// response will contain the bound value under the same name.
func (t *TaskRequest) ExpectOutput(name string) {
	t.outputs = append(t.outputs, name)
}

// Invoke ships the request to the daemon and returns the typed result.
func (r *TaskRunner) Invoke(req *TaskRequest, targetName string) (*taskd.TaskResult, error) {
	if err := r.EnsureConnected(); err != nil {
		return nil, err
	}
	inv := &taskd.TaskInvocation{
		TaskName:     req.descriptor.ShortName,
		TargetName:   targetName,
		AssemblyPath: req.descriptor.AssemblyPath,
		TypeName:     req.descriptor.FullTypeName,
		OutputNames:  req.outputs,
		Cwd:          r.cwd,
		Properties:   req.props,
	}
	return r.client.Invoke(inv)
}
