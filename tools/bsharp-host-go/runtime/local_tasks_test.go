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

func TestRemoveDuplicatesDedupesPreservingOrder(t *testing.T) {
	props := newFakeProps()
	items := newFakeItems()
	outputs := NewOutputList(
		Output{Key: "Filtered", ItemName: "_F"},
		Output{Key: "HadAnyDuplicates", PropertyName: "HadDup"},
	)
	plist := NewParamList(Param{Key: "Inputs", Value: "a;b;A;c;B"})
	if err := RemoveDuplicates(plist, outputs, items, props); err != nil {
		t.Fatalf("RemoveDuplicates returned %v", err)
	}
	got := items.Get("_F")
	if len(got) != 3 {
		t.Fatalf("expected 3 items after dedupe, got %d: %+v", len(got), got)
	}
	ids := []string{got[0].Identity, got[1].Identity, got[2].Identity}
	want := []string{"a", "b", "c"}
	for i := range want {
		if ids[i] != want[i] {
			t.Errorf("item[%d] = %q; want %q", i, ids[i], want[i])
		}
	}
	if props.Get("HadDup") != "True" {
		t.Errorf("HadAnyDuplicates = %q; want True", props.Get("HadDup"))
	}
}

func TestRemoveDuplicatesNoDuplicates(t *testing.T) {
	props := newFakeProps()
	items := newFakeItems()
	outputs := NewOutputList(
		Output{Key: "Filtered", ItemName: "_F"},
		Output{Key: "HadAnyDuplicates", PropertyName: "HadDup"},
	)
	plist := NewParamList(Param{Key: "Inputs", Value: "a;b;c"})
	if err := RemoveDuplicates(plist, outputs, items, props); err != nil {
		t.Fatalf("RemoveDuplicates returned %v", err)
	}
	if props.Get("HadDup") != "False" {
		t.Errorf("HadAnyDuplicates = %q; want False", props.Get("HadDup"))
	}
}

func TestAllowEmptyTelemetryIsNoop(t *testing.T) {
	plist := NewParamList(
		Param{Key: "EventName", Value: "BuildFinished"},
		Param{Key: "EventData", Value: "Key1=Value1;Key2=Value2"},
	)
	if err := AllowEmptyTelemetry(plist); err != nil {
		t.Fatalf("AllowEmptyTelemetry returned %v; want nil", err)
	}
}

func TestCheckForDuplicateNuGetItemsTaskNoDuplicates(t *testing.T) {
	props := newFakeProps()
	items := newFakeItems()
	outputs := NewOutputList(Output{Key: "DeduplicatedItems", ItemName: "_Dedup"})
	plist := NewParamList(Param{Key: "Items", Value: "Pkg.A;Pkg.B;Pkg.C"})
	if err := CheckForDuplicateNuGetItemsTask(plist, outputs, items, props); err != nil {
		t.Fatalf("returned %v", err)
	}
	// No duplicates ⇒ DeduplicatedItems left empty so downstream
	// Condition="'@(_Dedup)' != ''" skips.
	if got := items.Get("_Dedup"); len(got) != 0 {
		t.Errorf("expected empty output on no duplicates, got %d items", len(got))
	}
}

func TestCheckForDuplicateNuGetItemsTaskWithDuplicates(t *testing.T) {
	props := newFakeProps()
	items := newFakeItems()
	outputs := NewOutputList(Output{Key: "DeduplicatedItems", ItemName: "_Dedup"})
	plist := NewParamList(Param{Key: "Items", Value: "Pkg.A;Pkg.B;pkg.a;Pkg.C;PKG.B"})
	if err := CheckForDuplicateNuGetItemsTask(plist, outputs, items, props); err != nil {
		t.Fatalf("returned %v", err)
	}
	got := items.Get("_Dedup")
	if len(got) != 3 {
		t.Fatalf("expected 3 deduped items, got %d: %v", len(got), got)
	}
	wantIdents := []string{"Pkg.A", "Pkg.B", "Pkg.C"}
	for i, want := range wantIdents {
		if got[i].Identity != want {
			t.Errorf("got[%d].Identity = %q; want %q (first-wins order)", i, got[i].Identity, want)
		}
	}
}

func TestCheckForDuplicateNuGetItemsTaskEmptyInput(t *testing.T) {
	props := newFakeProps()
	items := newFakeItems()
	outputs := NewOutputList(Output{Key: "DeduplicatedItems", ItemName: "_Dedup"})
	plist := NewParamList(Param{Key: "Items", Value: ""})
	if err := CheckForDuplicateNuGetItemsTask(plist, outputs, items, props); err != nil {
		t.Fatalf("returned %v", err)
	}
	if got := items.Get("_Dedup"); len(got) != 0 {
		t.Errorf("expected empty output on empty input, got %d items", len(got))
	}
}
