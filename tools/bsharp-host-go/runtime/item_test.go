package runtime

import (
	"path/filepath"
	"testing"
)

func TestItemWellKnownMetadata(t *testing.T) {
	wd, _ := filepath.Abs(".")
	it := NewItem("foo/bar.txt")
	cases := map[string]string{
		"Identity":  "foo/bar.txt",
		"FullPath":  filepath.Join(wd, "foo", "bar.txt"),
		"FileName":  "bar",
		"Extension": ".txt",
	}
	for k, want := range cases {
		if got := it.GetMetadata(k); got != want {
			t.Errorf("%s: got %q, want %q", k, got, want)
		}
	}
}

func TestItemUserMetadataCaseInsensitive(t *testing.T) {
	it := NewItem("a")
	it.SetMetadata("CopyToOutputDirectory", "PreserveNewest")
	for _, name := range []string{"CopyToOutputDirectory", "copytooutputdirectory", "COPYTOOUTPUTDIRECTORY"} {
		if got := it.GetMetadata(name); got != "PreserveNewest" {
			t.Errorf("%s: got %q", name, got)
		}
	}
}

func TestItemHasMetadata(t *testing.T) {
	it := NewItem("a")
	it.SetMetadata("Pack", "true")
	if !it.HasMetadata("pack", "TRUE") {
		t.Error("HasMetadata should be case-insensitive for value too")
	}
	if it.HasMetadata("pack", "false") {
		t.Error("HasMetadata returned wrong value")
	}
}

func TestItemClone(t *testing.T) {
	it := NewItem("a")
	it.SetMetadata("X", "1")
	c := it.Clone()
	c.SetMetadata("X", "2")
	if it.GetMetadata("X") != "1" {
		t.Error("Clone metadata should be independent")
	}
}
