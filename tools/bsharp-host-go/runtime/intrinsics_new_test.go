package runtime

import "testing"

func TestDoesTaskHostExistIntrinsic(t *testing.T) {
	cases := []string{
		`$([MSBuild]::DoesTaskHostExist('Current', 'x64'))`,
		`$([MSBuild]::DoesTaskHostExist(` + "`" + `$(X)` + "`" + `, ` + "`" + `x64` + "`" + `))`,
	}
	for _, c := range cases {
		got, ok := Expand(c, &stubPB{m: map[string]string{"X": "Current"}}, &stubIB{}, nil)
		if !ok || got != "True" {
			t.Errorf("Expand(%q) = %q ok=%v, want True", c, got, ok)
		}
	}
}

func TestIsTargetFrameworkCompatibleIntrinsic(t *testing.T) {
	cases := []struct {
		expr string
		want string
	}{
		{`$([MSBuild]::IsTargetFrameworkCompatible('net6.0', 'netstandard2.0'))`, "True"},
		{`$([MSBuild]::IsTargetFrameworkCompatible('netstandard2.0', 'net6.0'))`, "False"},
	}
	for _, c := range cases {
		got, ok := Expand(c.expr, &stubPB{}, &stubIB{}, nil)
		if !ok || got != c.want {
			t.Errorf("Expand(%q) = %q ok=%v, want %q", c.expr, got, ok, c.want)
		}
	}
}

func TestRegexMatchIntrinsic(t *testing.T) {
	cases := []struct {
		expr string
		want string
	}{
		{`$([System.Text.RegularExpressions.Regex]::Match('Xcode 15.2', '[1-9]\d*'))`, "15"},
		{`$([System.Text.RegularExpressions.Regex]::Match('no digits', '\d+'))`, ""},
	}
	for _, c := range cases {
		got, ok := Expand(c.expr, &stubPB{}, &stubIB{}, nil)
		if !ok || got != c.want {
			t.Errorf("Expand(%q) = %q ok=%v, want %q", c.expr, got, ok, c.want)
		}
	}
}
