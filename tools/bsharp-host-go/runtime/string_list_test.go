package runtime

import "testing"

func TestStringSetExact(t *testing.T) {
	s := NewStringSetFromSemicolonList("a;b;c")
	if !s.Contains("a") || !s.Contains("B") {
		t.Errorf("missing exact match")
	}
	if s.Contains("d") {
		t.Errorf("false positive")
	}
}

func TestStringSetGlob(t *testing.T) {
	s := NewStringSetFromSemicolonList("Microsoft.*;*.tests")
	if !s.Contains("Microsoft.Extensions.Logging") {
		t.Errorf("prefix glob failed")
	}
	if !s.Contains("Foo.Bar.tests") {
		t.Errorf("suffix glob failed")
	}
	if s.Contains("Newtonsoft.Json") {
		t.Errorf("false positive")
	}
}

func TestParamList(t *testing.T) {
	p := NewParamList(
		Param{Key: "Importance", Value: "high"},
		Param{Key: "Text", Value: "hello"},
	)
	if p.GetValueOrDefault("importance") != "high" {
		t.Errorf("case-insensitive lookup failed")
	}
	if !p.Has("Text") {
		t.Errorf("Has missed key")
	}
	if p.GetValueOrDefault("Unknown") != "" {
		t.Errorf("missing key should return empty")
	}
	if p.Len() != 2 {
		t.Errorf("Len=%d", p.Len())
	}
}

func TestOutputList(t *testing.T) {
	o := NewOutputList(
		Output{Key: "TouchedFiles", ItemName: "_Touched"},
	)
	spec, ok := o.TryGetValue("touchedfiles")
	if !ok || spec.ItemName != "_Touched" {
		t.Errorf("got ok=%v spec=%+v", ok, spec)
	}
	if _, ok := o.TryGetValue("Other"); ok {
		t.Errorf("unexpected match")
	}

	var nilList *OutputList
	if _, ok := nilList.TryGetValue("Anything"); ok {
		t.Errorf("nil OutputList should return ok=false")
	}
}
