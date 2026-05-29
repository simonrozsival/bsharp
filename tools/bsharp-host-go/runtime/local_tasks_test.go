package runtime

import (
	"path/filepath"
	"strings"
	"testing"
)

// fakeProps + fakeItems are thin test doubles for PropertyBag / ItemBag —
// the runtime's PropertyBag/ItemBag interfaces (defined in local_tasks.go)
// are normally implemented by the generated *properties / *items types in
// emitted main.go.
type fakeProps struct{ m map[string]string }

func newFakeProps() *fakeProps        { return &fakeProps{m: make(map[string]string)} }
func (p *fakeProps) Set(k, v string)  { p.m[k] = v }
func (p *fakeProps) Get(k string) string { return p.m[k] }

type fakeItems struct{ m map[string][]*Item }

func newFakeItems() *fakeItems                    { return &fakeItems{m: make(map[string][]*Item)} }
func (f *fakeItems) AppendTo(k string, v []*Item) { f.m[k] = append(f.m[k], v...) }
func (f *fakeItems) Get(k string) []*Item         { return f.m[k] }

func TestHashWritesPropertyOutput(t *testing.T) {
	props := newFakeProps()
	items := newFakeItems()
	outputs := NewOutputList(Output{Key: "HashResult", PropertyName: "MyHash"})
	plist := NewParamList(Param{Key: "ItemsToHash", Value: "a;b;c"})
	if err := Hash(plist, outputs, items, props); err != nil {
		t.Fatalf("Hash returned %v", err)
	}
	got := props.Get("MyHash")
	if len(got) != 64 {
		t.Errorf("expected 64-char sha256 hex, got %d chars: %q", len(got), got)
	}
	// Stability — same input yields same digest.
	props2 := newFakeProps()
	_ = Hash(plist, outputs, items, props2)
	if got != props2.Get("MyHash") {
		t.Errorf("Hash is not deterministic across runs")
	}
}

func TestHashEmptyItemsToHashIsNoop(t *testing.T) {
	props := newFakeProps()
	items := newFakeItems()
	outputs := NewOutputList(Output{Key: "HashResult", PropertyName: "MyHash"})
	plist := NewParamList(Param{Key: "ItemsToHash", Value: ""})
	if err := Hash(plist, outputs, items, props); err != nil {
		t.Fatalf("Hash returned %v", err)
	}
	if got := props.Get("MyHash"); got != "" {
		t.Errorf("expected empty property on empty input, got %q", got)
	}
}

func TestConvertToAbsolutePathItemOutput(t *testing.T) {
	props := newFakeProps()
	items := newFakeItems()
	outputs := NewOutputList(Output{Key: "AbsolutePaths", ItemName: "_Abs"})
	plist := NewParamList(Param{Key: "Paths", Value: "rel1.txt;rel2.txt"})
	if err := ConvertToAbsolutePath(plist, outputs, items, props); err != nil {
		t.Fatalf("ConvertToAbsolutePath returned %v", err)
	}
	got := items.Get("_Abs")
	if len(got) != 2 {
		t.Fatalf("expected 2 items, got %d", len(got))
	}
	for _, it := range got {
		if !filepath.IsAbs(it.Identity) {
			t.Errorf("expected absolute path, got %q", it.Identity)
		}
	}
}

func TestConvertToAbsolutePathPropertyOutput(t *testing.T) {
	props := newFakeProps()
	items := newFakeItems()
	outputs := NewOutputList(Output{Key: "AbsolutePaths", PropertyName: "Abs"})
	plist := NewParamList(Param{Key: "Paths", Value: "rel1.txt;rel2.txt"})
	if err := ConvertToAbsolutePath(plist, outputs, items, props); err != nil {
		t.Fatalf("ConvertToAbsolutePath returned %v", err)
	}
	got := props.Get("Abs")
	parts := strings.Split(got, ";")
	if len(parts) != 2 {
		t.Fatalf("expected 2 semicolon parts, got %q", got)
	}
	for _, p := range parts {
		if !filepath.IsAbs(p) {
			t.Errorf("expected absolute path, got %q", p)
		}
	}
	// Items output not written when only PropertyName is bound.
	if len(items.Get("Abs")) != 0 {
		t.Errorf("unexpected items written: %d", len(items.Get("Abs")))
	}
}

func TestConvertToAbsolutePathSkipsEmpty(t *testing.T) {
	props := newFakeProps()
	items := newFakeItems()
	outputs := NewOutputList(Output{Key: "AbsolutePaths", ItemName: "_Abs"})
	plist := NewParamList(Param{Key: "Paths", Value: "a;;b"})
	if err := ConvertToAbsolutePath(plist, outputs, items, props); err != nil {
		t.Fatalf("ConvertToAbsolutePath returned %v", err)
	}
	if got := items.Get("_Abs"); len(got) != 2 {
		t.Errorf("expected empty entries to be dropped, got %d items", len(got))
	}
}

func TestWriteItemOutputIgnoresNilOutputs(t *testing.T) {
	items := newFakeItems()
	writeItemOutput(nil, items, "X", []*Item{NewItem("foo")})
	if len(items.Get("X")) != 0 {
		t.Errorf("nil OutputList should be no-op")
	}
}

func TestWriteStringOutputItemFallback(t *testing.T) {
	// If a task with a scalar string output is bound to ItemName instead of
	// PropertyName, the value should be promoted to a single-item list.
	props := newFakeProps()
	items := newFakeItems()
	outputs := NewOutputList(Output{Key: "Result", ItemName: "_R"})
	writeStringOutput(outputs, items, props, "Result", "hello")
	got := items.Get("_R")
	if len(got) != 1 || got[0].Identity != "hello" {
		t.Errorf("expected single 'hello' item, got %+v", got)
	}
	if props.Get("Result") != "" {
		t.Errorf("expected no property write, got %q", props.Get("Result"))
	}
}
