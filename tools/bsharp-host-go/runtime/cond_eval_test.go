package runtime

import (
	"os"
	"path/filepath"
	"testing"
)

func evalOK(t *testing.T, cond string, p *stubPB, want bool) {
	t.Helper()
	got, ok := EvalCondition(cond, p, &stubIB{})
	if !ok {
		t.Errorf("%q: expected ok, got unsupported", cond)
		return
	}
	if got != want {
		t.Errorf("%q: got %v, want %v", cond, got, want)
	}
}

func evalUnsupported(t *testing.T, cond string, p *stubPB) {
	t.Helper()
	if _, ok := EvalCondition(cond, p, &stubIB{}); ok {
		t.Errorf("%q: expected unsupported, got ok", cond)
	}
}

func TestCondEmpty(t *testing.T) {
	evalOK(t, "", &stubPB{}, true)
	evalOK(t, "   ", &stubPB{}, true)
}

func TestCondBoolLiterals(t *testing.T) {
	evalOK(t, "true", &stubPB{}, true)
	evalOK(t, "false", &stubPB{}, false)
	evalOK(t, "True", &stubPB{}, true)
}

func TestCondEqualityLiterals(t *testing.T) {
	evalOK(t, "'a' == 'a'", &stubPB{}, true)
	evalOK(t, "'a' == 'b'", &stubPB{}, false)
	evalOK(t, "'a' != 'b'", &stubPB{}, true)
	evalOK(t, "'A' == 'a'", &stubPB{}, true) // case-insensitive (matches MSBuild)
}

func TestCondEqualityWithProperty(t *testing.T) {
	p := &stubPB{m: map[string]string{"Configuration": "Release"}}
	evalOK(t, "'$(Configuration)' == 'Release'", p, true)
	evalOK(t, "'$(Configuration)' == 'Debug'", p, false)
	evalOK(t, "'$(Configuration)' != 'Debug'", p, true)
}

func TestCondEmptyStringEquality(t *testing.T) {
	p := &stubPB{m: map[string]string{}}
	evalOK(t, "'$(MissingProp)' == ''", p, true)
	evalOK(t, "'$(MissingProp)' != ''", p, false)
}

func TestCondAnd(t *testing.T) {
	p := &stubPB{m: map[string]string{"Configuration": "Release", "Platform": "AnyCPU"}}
	evalOK(t, "'$(Configuration)' == 'Release' And '$(Platform)' == 'AnyCPU'", p, true)
	evalOK(t, "'$(Configuration)' == 'Debug'   And '$(Platform)' == 'AnyCPU'", p, false)
}

func TestCondOr(t *testing.T) {
	p := &stubPB{m: map[string]string{"Configuration": "Debug"}}
	evalOK(t, "'$(Configuration)' == 'Release' Or '$(Configuration)' == 'Debug'", p, true)
	evalOK(t, "'$(Configuration)' == 'Release' Or '$(Configuration)' == 'Foo'", p, false)
}

func TestCondNot(t *testing.T) {
	evalOK(t, "!('a' == 'b')", &stubPB{}, true)
	evalOK(t, "!('a' == 'a')", &stubPB{}, false)
}

func TestCondParens(t *testing.T) {
	p := &stubPB{m: map[string]string{"A": "1", "B": "1", "C": "0"}}
	evalOK(t, "('$(A)' == '1' Or '$(B)' == '0') And '$(C)' == '0'", p, true)
}

func TestCondExistsTrueAndFalse(t *testing.T) {
	dir := t.TempDir()
	prev := ProjectDir
	ProjectDir = dir
	defer func() { ProjectDir = prev }()

	if err := os.WriteFile(filepath.Join(dir, "exists.txt"), []byte("x"), 0o644); err != nil {
		t.Fatal(err)
	}

	evalOK(t, "Exists('exists.txt')", &stubPB{}, true)
	evalOK(t, "Exists('missing.txt')", &stubPB{}, false)
	evalOK(t, "Exists('')", &stubPB{}, false)
	// $(X) in arg expands and then resolves relative to ProjectDir.
	p := &stubPB{m: map[string]string{"F": "exists.txt"}}
	evalOK(t, "Exists('$(F)')", p, true)
	// absolute path
	abs := filepath.Join(dir, "exists.txt")
	evalOK(t, "Exists('"+abs+"')", &stubPB{}, true)
}

func TestCondHasTrailingSlash(t *testing.T) {
	p := &stubPB{m: map[string]string{
		"WithSlash":   "bin/Debug/",
		"WithBack":    "bin\\Debug\\",
		"WithoutSep":  "bin/Debug",
		"EmptyString": "",
	}}
	evalOK(t, "HasTrailingSlash('$(WithSlash)')", p, true)
	evalOK(t, "HasTrailingSlash('$(WithBack)')", p, true)
	evalOK(t, "HasTrailingSlash('$(WithoutSep)')", p, false)
	evalOK(t, "HasTrailingSlash('$(EmptyString)')", p, false)
	evalOK(t, "HasTrailingSlash('foo/')", &stubPB{}, true)
	evalOK(t, "!HasTrailingSlash('$(WithoutSep)')", p, true)
}

func TestCondCallRejectsBatchingMetadata(t *testing.T) {
	// %() is still batching and remains unsupported in condition calls.
	evalUnsupported(t, "Exists('%(Compile.Identity)')", &stubPB{})
}

func TestCondExistsWithItemList(t *testing.T) {
	// Build a temp dir with two files; reference them via an item list.
	dir := t.TempDir()
	pathA := filepath.Join(dir, "a.dll")
	pathB := filepath.Join(dir, "b.dll")
	pathMissing := filepath.Join(dir, "missing.dll")
	if err := os.WriteFile(pathA, []byte("a"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(pathB, []byte("b"), 0o644); err != nil {
		t.Fatal(err)
	}

	ib := &stubIB{m: map[string][]*Item{
		"IntermediateAssembly": {{Identity: pathA}},
		"MultiPresent":         {{Identity: pathA}, {Identity: pathB}},
		"MultiOneMissing":      {{Identity: pathMissing}, {Identity: pathA}},
		"AllMissing":           {{Identity: pathMissing}},
		"Empty":                nil,
	}}
	check := func(cond string, want bool) {
		t.Helper()
		got, ok := EvalCondition(cond, &stubPB{}, ib)
		if !ok {
			t.Errorf("%q: expected ok, got unsupported", cond)
			return
		}
		if got != want {
			t.Errorf("%q: got %v, want %v", cond, got, want)
		}
	}
	check("Exists('@(IntermediateAssembly)')", true)
	check("Exists('@(MultiPresent)')", true)
	// At least one exists -> True (matches MSBuild IntrinsicFunctions.Exists).
	check("Exists('@(MultiOneMissing)')", true)
	check("Exists('@(AllMissing)')", false)
	check("Exists('@(Empty)')", false)
	// Negation should still parse.
	check("!Exists('@(AllMissing)')", true)
}

func TestCondCallRejectsUnknownFunction(t *testing.T) {
	evalUnsupported(t, "Foo('x')", &stubPB{})
}

func TestCondUnsupportedPropertyFunc(t *testing.T) {
	// Property functions inside condition operands are still unsupported.
	// (Phase B adds them to Expand, but conditions evaluate them by going
	// through Expand which would reject the `.` inside `$(X.ToLower())` only
	// if Expand has not been extended. Now that Expand supports them, the
	// condition will accept the form too — verify behavior.)
	p := &stubPB{m: map[string]string{"X": "FOO"}}
	evalOK(t, "'$(X.ToLower())' == 'foo'", p, true)
}

func TestCondBareNumericLiterals(t *testing.T) {
	// Bare numeric literals on either side of a numeric comparator should be
	// accepted as decimal strings (matches SDK conditions like
	// `@(X->Count()) > 0`).
	evalOK(t, "5 > 3", &stubPB{}, true)
	evalOK(t, "3 > 5", &stubPB{}, false)
	evalOK(t, "5 == 5", &stubPB{}, true)
	evalOK(t, "5 != 3", &stubPB{}, true)
	evalOK(t, "5.5 >= 5", &stubPB{}, true)
}

func TestCondItemCount(t *testing.T) {
	// @(X->Count()) used in numeric and string comparisons (matches the
	// SDK's GenerateGlobalUsings and *NativeFileReference* tasks).
	ib := &stubIB{m: map[string][]*Item{
		"Using":            {{Identity: "System"}, {Identity: "System.IO"}},
		"NoItems":          nil,
		"NativeFileReference": {{Identity: "x"}, {Identity: "y"}, {Identity: "z"}},
	}}
	check := func(cond string, want bool) {
		t.Helper()
		got, ok := EvalCondition(cond, &stubPB{}, ib)
		if !ok {
			t.Errorf("%q: expected ok, got unsupported", cond)
			return
		}
		if got != want {
			t.Errorf("%q: got %v, want %v", cond, got, want)
		}
	}
	check("@(Using->Count()) != 0", true)
	check("@(NoItems->Count()) != 0", false)
	check("@(NativeFileReference->Count()) > 0", true)
	check("@(NoItems->Count()) > 0", false)
	check("'@(NativeFileReference->Count())' > 1", true)
}
