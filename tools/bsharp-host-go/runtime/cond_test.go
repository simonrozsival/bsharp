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

func TestEvalConditionWithMetaBatchedEquality(t *testing.T) {
	cases := []struct {
		cond string
		meta map[string]string
		want bool
	}{
		{`'%(Extension)' == '.pri'`, map[string]string{"extension": ".pri"}, true},
		{`'%(Extension)' == '.pri'`, map[string]string{"extension": ".dll"}, false},
		{`'%(FilesCopiedToPublishDir.Filename)%(FilesCopiedToPublishDir.Extension)' == 'foo.dll'`,
			map[string]string{"filename": "foo", "extension": ".dll"}, true},
		{`'%(ContentWithTargetPath.CopyToOutputDirectory)'=='Always' and '%(ContentWithTargetPath.CopyToPublishDirectory)' == ''`,
			map[string]string{"copytooutputdirectory": "Always", "copytopublishdirectory": ""}, true},
		{`'%(ContentWithTargetPath.CopyToOutputDirectory)'=='Always' and '%(ContentWithTargetPath.CopyToPublishDirectory)' == ''`,
			map[string]string{"copytooutputdirectory": "Never", "copytopublishdirectory": ""}, false},
		{`'%(Identity)' == 'Microsoft.NETCore.App'`,
			map[string]string{"identity": "Microsoft.NETCore.App"}, true},
	}
	for _, c := range cases {
		got, ok := EvalConditionWithMeta(c.cond, &stubPB{}, &stubIB{}, c.meta)
		if !ok || got != c.want {
			t.Errorf("EvalConditionWithMeta(%q, meta=%v) = %v ok=%v, want %v", c.cond, c.meta, got, ok, c.want)
		}
	}
}

func TestEvalConditionWithoutMetaRejectsBatch(t *testing.T) {
	// Sanity: without a meta bag, %()-bearing condition is still unsupported.
	_, ok := EvalCondition(`'%(Extension)' == '.pri'`, &stubPB{}, &stubIB{})
	if ok {
		t.Error("EvalCondition without batchMeta should reject %()-bearing operand")
	}
}

func TestEvalConditionWithMetaExistsCall(t *testing.T) {
	// `Exists('%(FullPath)')` should resolve through the batchMeta bag.
	// Just verify that a missing path returns False without panicking.
	got, ok := EvalConditionWithMeta(`Exists('%(FullPath)')`, &stubPB{}, &stubIB{},
		map[string]string{"fullpath": "/nonexistent/zzz-bogus-path-xyz"})
	if !ok || got {
		t.Errorf("Exists('%%(FullPath)' missing) = %v ok=%v, want False", got, ok)
	}
}
