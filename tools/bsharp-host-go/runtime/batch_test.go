package runtime

import "testing"

func TestBatchKeysSingleMeta(t *testing.T) {
	items := []*Item{NewItem("a"), NewItem("b"), NewItem("c")}
	items[0].SetMetadata("Culture", "en-US")
	items[1].SetMetadata("Culture", "de-DE")
	items[2].SetMetadata("Culture", "en-US")
	keys := BatchKeys("Culture", items)
	if len(keys) != 2 {
		t.Errorf("expected 2 distinct batches, got %d: %v", len(keys), keys)
	}
}

func TestItemsForBatch(t *testing.T) {
	items := []*Item{NewItem("a"), NewItem("b"), NewItem("c")}
	items[0].SetMetadata("Culture", "en-US")
	items[1].SetMetadata("Culture", "de-DE")
	items[2].SetMetadata("Culture", "en-US")
	got := ItemsForBatch(items, "Culture", "en-US")
	if len(got) != 2 || got[0].Identity != "a" || got[1].Identity != "c" {
		t.Errorf("got %#v", got)
	}
}

func TestHasBatchMetadata(t *testing.T) {
	items := []*Item{NewItem("a"), NewItem("b")}
	items[0].SetMetadata("Culture", "en")
	if !HasBatchMetadata(items, "Culture") {
		t.Error("expected true")
	}
	if HasBatchMetadata(items, "Missing") {
		t.Error("expected false")
	}
}

