package runtime

import "strings"

// ParamList is the Go equivalent of the C# `ParamList` struct: an ordered
// collection of (key, value) parameter pairs with case-insensitive key
// lookup. The C# version optimizes for 1–4 pairs with explicit fields; the
// Go version uses a slice unconditionally since per-allocation cost in Go
// is much lower than C# and the gain is negligible relative to the daemon
// RTT that dominates a real build.
type ParamList struct {
	items []paramPair
}

type paramPair struct {
	key, value string
}

// Param is a convenience builder for {key, value} literals at codegen time.
type Param struct {
	Key, Value string
}

// EmptyParams is an immutable empty ParamList. Useful for tasks with no
// parameters.
var EmptyParams = ParamList{}

// NewParamList constructs a ParamList from one or more Param pairs.
func NewParamList(pairs ...Param) ParamList {
	if len(pairs) == 0 {
		return EmptyParams
	}
	out := ParamList{items: make([]paramPair, len(pairs))}
	for i, p := range pairs {
		out.items[i] = paramPair{key: p.Key, value: p.Value}
	}
	return out
}

// GetValueOrDefault returns the value for key (case-insensitive), or "" if
// the key is not present.
func (p ParamList) GetValueOrDefault(key string) string {
	for _, it := range p.items {
		if strings.EqualFold(it.key, key) {
			return it.value
		}
	}
	return ""
}

// Has reports whether the key is present.
func (p ParamList) Has(key string) bool {
	for _, it := range p.items {
		if strings.EqualFold(it.key, key) {
			return true
		}
	}
	return false
}

// Len returns the number of (key, value) pairs.
func (p ParamList) Len() int { return len(p.items) }

// OutputSpec describes one task output: the item-list name to populate plus
// an optional metadata-name selector (the latter is non-nil iff the codegen
// used `<Output ItemName="..." MetadataName="..."/>` syntax).
type OutputSpec struct {
	ItemName     string
	MetadataName string
	HasMetadata  bool
}

// OutputList is the Go equivalent of the C# `OutputList` struct: a map of
// task output names (e.g. "AssignedFiles") to their target item lists.
type OutputList struct {
	items map[string]OutputSpec
}

// Output builds an OutputSpec for use in NewOutputList.
type Output struct {
	Key, ItemName, MetadataName string
	HasMetadata                 bool
}

// NewOutputList constructs an OutputList from one or more Output entries.
func NewOutputList(entries ...Output) *OutputList {
	if len(entries) == 0 {
		return nil
	}
	out := &OutputList{items: make(map[string]OutputSpec, len(entries))}
	for _, e := range entries {
		out.items[strings.ToLower(e.Key)] = OutputSpec{
			ItemName:     e.ItemName,
			MetadataName: e.MetadataName,
			HasMetadata:  e.HasMetadata,
		}
	}
	return out
}

// TryGetValue returns the OutputSpec for the named output (case-insensitive)
// and whether it was found. Mirrors `OutputList.TryGetValue`.
func (o *OutputList) TryGetValue(key string) (OutputSpec, bool) {
	if o == nil {
		return OutputSpec{}, false
	}
	v, ok := o.items[strings.ToLower(key)]
	return v, ok
}
