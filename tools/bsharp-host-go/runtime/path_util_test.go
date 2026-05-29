package runtime

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestEnsureTrailingSlash(t *testing.T) {
	cases := map[string]string{
		"":    "",
		"a":   "a" + string(filepath.Separator),
		"a/":  "a/",
		"a\\": "a\\",
	}
	for in, want := range cases {
		if got := EnsureTrailingSlash(in); got != want {
			t.Errorf("EnsureTrailingSlash(%q)=%q want %q", in, got, want)
		}
	}
}

func TestNormalizeSeparators(t *testing.T) {
	in := "foo\\bar/baz"
	got := NormalizeSeparators(in)
	want := in
	if filepath.Separator == '/' {
		want = "foo/bar/baz"
	}
	if got != want {
		t.Errorf("got %q want %q", got, want)
	}
}

func TestFastPathFileHelpers(t *testing.T) {
	dir := t.TempDir()
	src := filepath.Join(dir, "a.cs")
	if err := os.WriteFile(src, []byte("//"), 0o644); err != nil {
		t.Fatal(err)
	}
	now := time.Now().UnixNano()
	// Source created at "now" — should NOT be newer than "now + 5s".
	if HasProjectSourceNewerThanOutput(dir, now+5*int64(time.Second)) {
		t.Error("source should not be newer than future outputTime")
	}
	// touch source into the future
	future := time.Now().Add(60 * time.Second)
	if err := os.Chtimes(src, future, future); err != nil {
		t.Fatal(err)
	}
	if !HasProjectSourceNewerThanOutput(dir, now) {
		t.Error("source should be newer after future touch")
	}
}

func TestEnumerateSkipsBinObj(t *testing.T) {
	dir := t.TempDir()
	if err := os.MkdirAll(filepath.Join(dir, "bin"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "a.cs"), nil, 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "bin", "skip.cs"), nil, 0o644); err != nil {
		t.Fatal(err)
	}
	got := EnumerateProjectSourceFiles(dir)
	if len(got) != 1 {
		t.Errorf("expected 1 file, got %d: %v", len(got), got)
	}
}

