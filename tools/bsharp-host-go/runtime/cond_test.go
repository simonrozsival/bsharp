package runtime

import "testing"

func TestSplitSemicolonTrimAndDrop(t *testing.T) {
	in := "  a ; b ;;  c  ;"
	got := SplitSemicolon(in)
	want := []string{"a", "b", "c"}
	if len(got) != len(want) {
		t.Fatalf("got %v, want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Errorf("[%d] got %q, want %q", i, got[i], want[i])
		}
	}
}

func TestSplitSemicolonEmpty(t *testing.T) {
	if got := SplitSemicolon(""); got != nil && len(got) != 0 {
		t.Errorf("empty input should return empty, got %v", got)
	}
}

func TestSplitSemicolonKeepEmpty(t *testing.T) {
	got := SplitSemicolonKeepEmpty("a;;b")
	if len(got) != 3 || got[0] != "a" || got[1] != "" || got[2] != "b" {
		t.Errorf("got %v", got)
	}
}

func TestNumericCompareIntVersionString(t *testing.T) {
	cases := []struct {
		a, b string
		want int
	}{
		{"1", "2", -1},
		{"2", "1", 1},
		{"2", "2", 0},
		{"6.0", "5.9", 1},
		{"6.0", "6.0.0", 0},
		{"abc", "abd", -1},
	}
	for _, c := range cases {
		got := NumericCompare(c.a, c.b)
		if (got < 0) != (c.want < 0) || (got > 0) != (c.want > 0) || (got == 0) != (c.want == 0) {
			t.Errorf("Compare(%q,%q)=%d want sign of %d", c.a, c.b, got, c.want)
		}
	}
}

func TestIsAny(t *testing.T) {
	if !IsAny("foo", "bar", "foo", "baz") {
		t.Error("expected match")
	}
	if IsAny("FOO", "bar", "baz") {
		t.Error("IsAny is case-sensitive (matches C# String.Equals)")
	}
}

func TestIsTargetFrameworkCompatible(t *testing.T) {
	cases := []struct {
		current, target string
		want            bool
	}{
		{"net11.0", "net11.0", true},
		{"net11.0", "net6.0", true},
		{"net11.0", "netstandard2.0", true},
		{"net11.0", "netcoreapp3.1", true},
		{"net5.0", "netcoreapp3.1", true},
		{"net5.0", "netstandard2.0", true},
		{"netstandard2.0", "netstandard2.1", false},
		{"net6.0", "net11.0", false},
	}
	for _, c := range cases {
		if got := IsTargetFrameworkCompatible(c.current, c.target); got != c.want {
			t.Errorf("IsTargetFrameworkCompatible(%q,%q)=%v want %v", c.current, c.target, got, c.want)
		}
	}
}
