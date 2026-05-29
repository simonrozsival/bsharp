package runtime

import (
	"reflect"
	"testing"
)

func TestItemSpecsRoundTrip(t *testing.T) {
	a := NewItem("foo")
	a.SetMetadata("X", "1")
	a.SetMetadata("Y", "2")
	b := NewItem("bar")
	specs := ItemsToSpecs([]*Item{a, b})
	if len(specs) != 2 || specs[0].Identity != "foo" || specs[1].Identity != "bar" {
		t.Fatalf("unexpected specs: %#v", specs)
	}
	roundTripped := ItemsFromSpecs(specs)
	if len(roundTripped) != 2 {
		t.Fatalf("got %d items", len(roundTripped))
	}
	if roundTripped[0].GetMetadata("X") != "1" {
		t.Errorf("metadata lost")
	}
}

func TestItemSpecsDedupe(t *testing.T) {
	a := ItemSpec{Identity: "foo"}
	b := ItemSpec{Identity: "foo"}
	got := ItemsFromSpecs([]ItemSpec{a, b})
	if len(got) != 1 {
		t.Errorf("expected 1 deduped item, got %d", len(got))
	}
}

func TestSpecsFromScalar(t *testing.T) {
	got := SpecsFromScalar("a;b;;c")
	want := []ItemSpec{{Identity: "a"}, {Identity: "b"}, {Identity: "c"}}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("got %#v, want %#v", got, want)
	}
}

func TestConcatSpecs(t *testing.T) {
	a := []ItemSpec{{Identity: "a"}}
	b := []ItemSpec{{Identity: "b"}, {Identity: "c"}}
	got := ConcatSpecs(a, b)
	if len(got) != 3 {
		t.Errorf("got %d", len(got))
	}
}

func TestItemsToSpecsWithIdentity(t *testing.T) {
	items := []*Item{NewItem("foo/bar.cs"), NewItem("foo/baz.cs")}
	got := ItemsToSpecsWithIdentity(items, func(it *Item) string {
		return it.GetMetadata("FileName")
	})
	if len(got) != 2 || got[0].Identity != "bar" || got[1].Identity != "baz" {
		t.Errorf("got %#v", got)
	}
}
