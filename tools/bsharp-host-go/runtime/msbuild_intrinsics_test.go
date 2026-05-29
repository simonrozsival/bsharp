package runtime

import "testing"

type testProps struct{ m map[string]string }

func (p *testProps) Set(k, v string) { p.m[k] = v }
func (p *testProps) Get(k string) string {
	if v, ok := p.m[k]; ok {
		return v
	}
	return ""
}

type testItems struct{}

func (testItems) AppendTo(string, []*Item) {}
func (testItems) Get(string) []*Item       { return nil }

func newProps(kv ...string) *testProps {
	p := &testProps{m: map[string]string{}}
	for i := 0; i+1 < len(kv); i += 2 {
		p.m[kv[i]] = kv[i+1]
	}
	return p
}

func TestEvalMSBuildIntrinsicVersionCompare(t *testing.T) {
	p := newProps("V", "5.0")
	cases := []struct {
		name   string
		args   string
		want   string
		wantOk bool
	}{
		{"VersionEquals", "'5.0', '5.0'", "True", true},
		{"VersionEquals", "'5.0', '5.1'", "False", true},
		{"VersionGreaterThan", "'5.1', '5.0'", "True", true},
		{"VersionGreaterThan", "'5.0', '5.0'", "False", true},
		{"VersionGreaterThanOrEquals", "'5.0', '5.0'", "True", true},
		{"VersionGreaterThanOrEquals", "'$(V)', '4.9'", "True", true},
		{"VersionGreaterThanOrEquals", "$(V), 4.9", "True", true},
		{"VersionGreaterThanOrEquals", "'$(V)', '5.0.1'", "False", true},
		{"VersionLessThan", "'5.0', '6.0'", "True", true},
		{"VersionLessThanOrEquals", "'5.0', '5.0'", "True", true},
		{"versionequals", "'5.0', '5.0'", "True", true},                // case-insensitive name
		{"VersionGreaterThanOrEquals", "'not-a-version', '5.0'", "False", true}, // unparseable → False
	}
	for _, tc := range cases {
		got, ok := EvalMSBuildIntrinsic(tc.name, tc.args, p)
		if ok != tc.wantOk || got != tc.want {
			t.Errorf("%s(%s) = (%q,%v); want (%q,%v)", tc.name, tc.args, got, ok, tc.want, tc.wantOk)
		}
	}
}

func TestEvalMSBuildIntrinsicIsOSPlatform(t *testing.T) {
	p := newProps()
	// Whichever platform we're on, one of these names matches.
	cases := []struct {
		args string
	}{
		{"'OSX'"},
		{"'Linux'"},
		{"'Windows'"},
		{"'osx'"},
		{"'LINUX'"},
	}
	var sawTrue bool
	for _, tc := range cases {
		got, ok := EvalMSBuildIntrinsic("IsOSPlatform", tc.args, p)
		if !ok {
			t.Fatalf("IsOSPlatform(%s) ok=false", tc.args)
		}
		if got == "True" {
			sawTrue = true
		}
	}
	if !sawTrue {
		t.Errorf("expected one of OSX/Linux/Windows to be True for current platform")
	}
	if g, _ := EvalMSBuildIntrinsic("IsOSPlatform", "'NonExistentOS'", p); g != "False" {
		t.Errorf("IsOSPlatform('NonExistentOS') = %q; want False", g)
	}
}

func TestEvalMSBuildIntrinsicValueOrDefault(t *testing.T) {
	p := newProps("X", "hello")
	cases := []struct {
		args string
		want string
	}{
		{"'$(X)', 'fallback'", "hello"},
		{"'', 'fallback'", "fallback"},
		{"'$(Missing)', 'fallback'", "fallback"},
		{"'$(X)', '$(Missing)'", "hello"},
	}
	for _, tc := range cases {
		got, ok := EvalMSBuildIntrinsic("ValueOrDefault", tc.args, p)
		if !ok {
			t.Fatalf("ValueOrDefault(%s) ok=false", tc.args)
		}
		if got != tc.want {
			t.Errorf("ValueOrDefault(%s) = %q; want %q", tc.args, got, tc.want)
		}
	}
}

func TestEvalMSBuildIntrinsicEscape(t *testing.T) {
	p := newProps()
	got, ok := EvalMSBuildIntrinsic("Escape", "'a;b%c'", p)
	if !ok {
		t.Fatal("Escape ok=false")
	}
	if got != "a%3Bb%25c" {
		t.Errorf("Escape = %q; want a%%3Bb%%25c", got)
	}
}

func TestEvalMSBuildIntrinsicUnsupportedReturnsFalse(t *testing.T) {
	p := newProps()
	// GetAssemblyIdentityFromConfigFile etc. — anything we haven't ported.
	if _, ok := EvalMSBuildIntrinsic("GetCurrentToolsDirectory", "", p); ok {
		t.Error("Unknown intrinsic should be rejected")
	}
	if _, ok := EvalMSBuildIntrinsic("Unknown", "'a'", p); ok {
		t.Error("Unknown intrinsic should be rejected")
	}
}

func TestExpandWithMSBuildIntrinsics(t *testing.T) {
	p := newProps("TargetFrameworkVersion", "5.0", "Custom", "hello")
	cases := []struct {
		template string
		want     string
		wantOk   bool
	}{
		{"$([MSBuild]::VersionGreaterThanOrEquals('$(TargetFrameworkVersion)', '5.0'))", "True", true},
		{"$([MSBuild]::VersionGreaterThanOrEquals($(TargetFrameworkVersion), 4.9))", "True", true},
		{"$([MSBuild]::VersionLessThan('$(TargetFrameworkVersion)', '4.0'))", "False", true},
		{"$([MSBuild]::ValueOrDefault('$(Custom)', 'fallback'))", "hello", true},
		{"prefix-$([MSBuild]::Escape('a;b'))-suffix", "prefix-a%3Bb-suffix", true},
		// Unsupported intrinsics still propagate as ok=false (the runtime
		// hasn't ported GetCurrentToolsDirectory etc.).
		{"$([MSBuild]::GetCurrentToolsDirectory())", "", false},
		// System.IO.Path::* is now supported (Phase H batch 2).
		{"$([System.IO.Path]::Combine('a','b'))", "a/b", true},
		// Unrelated System.* intrinsics remain rejected.
		{"$([System.String]::IsNullOrEmpty('x'))", "", false},
		// Lowercase `[msbuild]` should also work.
		{"$([msbuild]::VersionEquals('1.0', '1.0'))", "True", true},
	}
	for _, tc := range cases {
		got, ok := Expand(tc.template, p, testItems{}, nil)
		if ok != tc.wantOk {
			t.Errorf("Expand(%q) ok=%v; want %v (got=%q)", tc.template, ok, tc.wantOk, got)
			continue
		}
		if ok && got != tc.want {
			t.Errorf("Expand(%q) = %q; want %q", tc.template, got, tc.want)
		}
	}
}

func TestEvalConditionNumericComparison(t *testing.T) {
	p := newProps("Ver", "5.0", "Plain", "3")
	cases := []struct {
		cond string
		want bool
	}{
		{"'$(Ver)' >= '5.0'", true},
		{"'$(Ver)' >= '6.0'", false},
		{"'$(Ver)' > '4.9'", true},
		{"'$(Ver)' < '4.9'", false},
		{"'$(Ver)' <= '5.0'", true},
		{"'$(Plain)' < '5'", true},
		{"'$(Plain)' >= '3'", true},
		{"'$(Plain)' != '3'", false},
		// Combined with And/Or/Not still works.
		{"'$(Ver)' >= '5.0' And '$(Plain)' == '3'", true},
		{"!('$(Ver)' >= '6.0')", true},
	}
	for _, tc := range cases {
		got, ok := EvalCondition(tc.cond, p, testItems{})
		if !ok {
			t.Errorf("EvalCondition(%q) ok=false", tc.cond)
			continue
		}
		if got != tc.want {
			t.Errorf("EvalCondition(%q) = %v; want %v", tc.cond, got, tc.want)
		}
	}
}

func TestEvalConditionWithMSBuildIntrinsic(t *testing.T) {
	p := newProps("TargetFrameworkVersion", "5.0", "TFI", ".NETCoreApp")
	cases := []struct {
		cond string
		want bool
	}{
		{"$([MSBuild]::VersionGreaterThanOrEquals('$(TargetFrameworkVersion)', '5.0'))", true},
		{"$([MSBuild]::VersionGreaterThanOrEquals($(TargetFrameworkVersion), 5.0))", true},
		{"!$([MSBuild]::VersionGreaterThanOrEquals($(TargetFrameworkVersion), 6.0))", true},
		{"'$(TFI)' == '.NETCoreApp' And $([MSBuild]::VersionGreaterThanOrEquals($(TargetFrameworkVersion), 5.0))", true},
		{"'$(TFI)' == '.NETCoreApp' And $([MSBuild]::VersionGreaterThanOrEquals($(TargetFrameworkVersion), 6.0))", false},
	}
	for _, tc := range cases {
		got, ok := EvalCondition(tc.cond, p, testItems{})
		if !ok {
			t.Errorf("EvalCondition(%q) ok=false", tc.cond)
			continue
		}
		if got != tc.want {
			t.Errorf("EvalCondition(%q) = %v; want %v", tc.cond, got, tc.want)
		}
	}
}

func TestPathIntrinsics(t *testing.T) {
	p := newProps("D", "/opt/work")
	cases := []struct {
		template string
		want     string
	}{
		{"$([System.IO.Path]::Combine('a','b','c'))", "a/b/c"},
		{"$([System.IO.Path]::Combine($(D),'src'))", "/opt/work/src"},
		{"$([System.IO.Path]::Combine('/x','/abs'))", "/abs"}, // .NET semantics: rooted segment discards prior
		{"$([System.IO.Path]::GetFileName('/foo/bar/baz.cs'))", "baz.cs"},
		{"$([System.IO.Path]::GetFileNameWithoutExtension('/foo/bar/baz.cs'))", "baz"},
		{"$([System.IO.Path]::GetExtension('/foo/bar/baz.cs'))", ".cs"},
		{"$([System.IO.Path]::GetDirectoryName('/foo/bar/baz.cs'))", "/foo/bar"},
		{"$([System.IO.Path]::GetDirectoryName('baz.cs'))", ""},
		{"$([System.IO.Path]::IsPathRooted('/abs'))", "True"},
		{"$([System.IO.Path]::IsPathRooted('rel'))", "False"},
		{"$([System.IO.Path]::ChangeExtension('foo.txt','.md'))", "foo.md"},
		{"$([System.IO.Path]::ChangeExtension('foo.txt','md'))", "foo.md"},
		{"$([System.IO.Path]::DirectorySeparatorChar)", "/"},
	}
	for _, tc := range cases {
		got, ok := Expand(tc.template, p, testItems{}, nil)
		if !ok {
			t.Errorf("Expand(%q) ok=false (got=%q)", tc.template, got)
			continue
		}
		if got != tc.want {
			t.Errorf("Expand(%q) = %q; want %q", tc.template, got, tc.want)
		}
	}
}

func TestIntrinsicArgsBareConcatWithExpansion(t *testing.T) {
	// MSBuild allows unquoted intrinsic args that concatenate literal text with
	// `$(...)` expansions, e.g. `$([System.IO.Path]::GetDirectoryName($(D)\sub))`.
	p := newProps("D", "/opt/work", "Sub", "extra")
	cases := []struct {
		template string
		want     string
	}{
		{`$([System.IO.Path]::GetDirectoryName($(D)\src\file.cs))`, "/opt/work/src"},
		{`$([System.IO.Path]::Combine($(D), $(Sub)))`, "/opt/work/extra"},
		{`$([MSBuild]::ValueOrDefault($(Missing), $(D)))`, "/opt/work"},
	}
	for _, tc := range cases {
		got, ok := Expand(tc.template, p, testItems{}, nil)
		if !ok {
			t.Errorf("Expand(%q) ok=false (got=%q)", tc.template, got)
			continue
		}
		if got != tc.want {
			t.Errorf("Expand(%q) = %q; want %q", tc.template, got, tc.want)
		}
	}
}

func TestPathIntrinsicsRejectUnsupported(t *testing.T) {
	p := newProps()
	// Unsupported Path::* members and unknown types remain ok=false.
	for _, tmpl := range []string{
		"$([System.IO.Path]::HasExtension('foo'))",   // not in whitelist
		"$([System.IO.Directory]::GetFiles('/tmp'))", // not supported
		"$([System.Guid]::NewGuid())",                // not supported
	} {
		if _, ok := Expand(tmpl, p, testItems{}, nil); ok {
			t.Errorf("Expand(%q) should be ok=false", tmpl)
		}
	}
}

func TestMSBuildPathIntrinsics(t *testing.T) {
	p := newProps("Root", "/opt/work")
	cases := []struct {
		template string
		want     string
	}{
		{`$([MSBuild]::EnsureTrailingSlash('/foo'))`, "/foo/"},
		{`$([MSBuild]::EnsureTrailingSlash('/foo/'))`, "/foo/"},
		{`$([MSBuild]::EnsureTrailingSlash(''))`, ""},
		// MakeRelative when source has a trailing slash matches MSBuild's
		// "treat base as a directory" expectation.
		{`$([MSBuild]::MakeRelative('/a/b/', '/a/b/c/d'))`, "c/d"},
	}
	for _, tc := range cases {
		got, ok := Expand(tc.template, p, testItems{}, nil)
		if !ok {
			t.Errorf("Expand(%q) ok=false (got=%q)", tc.template, got)
			continue
		}
		if got != tc.want {
			t.Errorf("Expand(%q) = %q; want %q", tc.template, got, tc.want)
		}
	}
}

func TestMSBuildNormalizePath(t *testing.T) {
	p := newProps()
	// NormalizePath / NormalizeDirectory resolve to absolute paths, so we
	// only assert structural properties (rather than exact strings).
	abs, ok := Expand(`$([MSBuild]::NormalizePath('/a', 'b', '..', 'c'))`, p, testItems{}, nil)
	if !ok {
		t.Fatalf("NormalizePath ok=false")
	}
	if abs != "/a/c" {
		t.Errorf("NormalizePath = %q; want /a/c", abs)
	}
	dir, ok := Expand(`$([MSBuild]::NormalizeDirectory('/a', 'b'))`, p, testItems{}, nil)
	if !ok {
		t.Fatalf("NormalizeDirectory ok=false")
	}
	if dir != "/a/b/" {
		t.Errorf("NormalizeDirectory = %q; want /a/b/", dir)
	}
}

func TestRegexReplace(t *testing.T) {
	p := newProps("V", "8.0.0", "Pref", "latest")
	cases := []struct {
		in, want string
	}{
		// Strip everything after first `-` (the SDK uses this for analyzer prefixes).
		{`$([System.Text.RegularExpressions.Regex]::Replace($(Pref), '-(.)*', ''))`, "latest"},
		// Strip trailing `.0`s (the SDK uses this to compute analyzer versions).
		{`$([System.Text.RegularExpressions.Regex]::Replace($(V), '(\.0)*$', ''))`, "8"},
		// No-match passthrough.
		{`$([System.Text.RegularExpressions.Regex]::Replace($(Pref), 'xyz', 'q'))`, "latest"},
		// Replacement back-reference.
		{`$([System.Text.RegularExpressions.Regex]::Replace('abc-123', '([a-z]+)-(\d+)', '$2.$1'))`, "123.abc"},
	}
	for _, c := range cases {
		got, ok := Expand(c.in, p, testItems{}, nil)
		if !ok {
			t.Errorf("Expand(%q) ok=false", c.in)
			continue
		}
		if got != c.want {
			t.Errorf("Expand(%q) = %q; want %q", c.in, got, c.want)
		}
	}
}

func TestRegexIsMatch(t *testing.T) {
	p := newProps("X", "abc-123")
	for _, c := range []struct {
		in, want string
	}{
		{`$([System.Text.RegularExpressions.Regex]::IsMatch($(X), '^[a-z]+'))`, "True"},
		{`$([System.Text.RegularExpressions.Regex]::IsMatch($(X), '^[0-9]+'))`, "False"},
	} {
		got, ok := Expand(c.in, p, testItems{}, nil)
		if !ok {
			t.Errorf("Expand(%q) ok=false", c.in)
			continue
		}
		if got != c.want {
			t.Errorf("Expand(%q) = %q; want %q", c.in, got, c.want)
		}
	}
}

func TestRegexInvalidPatternIsRejected(t *testing.T) {
	p := newProps()
	// `(?<` is .NET named-group syntax that Go regexp doesn't accept.
	_, ok := Expand(`$([System.Text.RegularExpressions.Regex]::Replace('abc', '(?<name>x', 'q'))`, p, testItems{}, nil)
	if ok {
		t.Errorf("expected unsupported regex pattern to return ok=false")
	}
}
