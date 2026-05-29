package runtime

import "testing"

type stubPB struct{ m map[string]string }

func (s *stubPB) Get(name string) string  { return s.m[name] }
func (s *stubPB) Set(name, value string)  { s.m[name] = value }

type stubIB struct{ m map[string][]*Item }

func (s *stubIB) Get(name string) []*Item              { return s.m[name] }
func (s *stubIB) AppendTo(name string, items []*Item)  { s.m[name] = append(s.m[name], items...) }

func TestExpandLiteral(t *testing.T) {
	got, ok := Expand("hello world", &stubPB{}, &stubIB{}, nil)
	if !ok || got != "hello world" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandProperty(t *testing.T) {
	p := &stubPB{m: map[string]string{"Configuration": "Release", "TargetFramework": "net11.0"}}
	got, ok := Expand("$(Configuration)/$(TargetFramework)/app", p, &stubIB{}, nil)
	if !ok || got != "Release/net11.0/app" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandUnknownProperty(t *testing.T) {
	got, ok := Expand("=$(Missing)=", &stubPB{m: map[string]string{}}, &stubIB{}, nil)
	if !ok || got != "==" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandItems(t *testing.T) {
	items := &stubIB{m: map[string][]*Item{
		"Compile": {NewItem("a.cs"), NewItem("b.cs"), NewItem("c.cs")},
	}}
	got, ok := Expand("@(Compile)", &stubPB{}, items, nil)
	if !ok || got != "a.cs;b.cs;c.cs" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandItemsCustomSep(t *testing.T) {
	items := &stubIB{m: map[string][]*Item{
		"Compile": {NewItem("a"), NewItem("b")},
	}}
	got, ok := Expand("[@(Compile, ' | ')]", &stubPB{}, items, nil)
	if !ok || got != "[a | b]" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandPropertyFunctionToLower(t *testing.T) {
	p := &stubPB{m: map[string]string{"Config": "Release"}}
	got, ok := Expand("$(Config.ToLower())", p, &stubIB{}, nil)
	if !ok || got != "release" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandItemTransformUnsupported(t *testing.T) {
	_, ok := Expand("@(Foo->'%(Identity).bak')", &stubPB{}, &stubIB{}, nil)
	if ok {
		t.Error("item transforms must be reported as unsupported")
	}
}

func TestExpandBatchMetadata(t *testing.T) {
	bm := map[string]string{"culture": "en-US"}
	got, ok := Expand("res.%(Culture).resx", &stubPB{}, &stubIB{}, bm)
	if !ok || got != "res.en-US.resx" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandBatchMetadataQualified(t *testing.T) {
	bm := map[string]string{"culture": "fr-FR"}
	got, ok := Expand("%(EmbeddedResource.Culture)", &stubPB{}, &stubIB{}, bm)
	if !ok || got != "fr-FR" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandBatchMetadataOutsideBatch(t *testing.T) {
	got, ok := Expand("res.%(Culture).resx", &stubPB{}, &stubIB{}, nil)
	if !ok || got != "res..resx" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandLiteralDollar(t *testing.T) {
	got, ok := Expand("price $5", &stubPB{}, &stubIB{}, nil)
	if !ok || got != "price $5" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestMustExpandPanicsOnUnsupported(t *testing.T) {
	defer func() {
		if recover() == nil {
			t.Error("expected panic on unsupported template")
		}
	}()
	_ = MustExpand("$(X[0])", &stubPB{}, &stubIB{}, nil)
}

func TestExpandPropertyFunctionsBasic(t *testing.T) {
	p := &stubPB{m: map[string]string{
		"S":   "Hello World",
		"V":   "v11.0",
		"Cfg": "Debug",
		"TFM": "net11.0",
	}}
	cases := []struct {
		tmpl string
		want string
	}{
		{"$(S.ToUpper())", "HELLO WORLD"},
		{"$(S.ToLowerInvariant())", "hello world"},
		{"$(V.TrimStart('vV'))", "11.0"},
		{"$(S.Replace(' ', '_'))", "Hello_World"},
		{"$(S.Substring(6))", "World"},
		{"$(S.Substring(0, 5))", "Hello"},
		{"$(S.StartsWith('Hello'))", "True"},
		{"$(S.EndsWith('World'))", "True"},
		{"$(S.Contains('lo Wo'))", "True"},
		{"$(S.Contains('xyz'))", "False"},
		{"$(S.IndexOf('World'))", "6"},
		{"$(S.Length)", "11"},
		{"$(Cfg.ToString())", "Debug"},
	}
	for _, tc := range cases {
		got, ok := Expand(tc.tmpl, p, &stubIB{}, nil)
		if !ok || got != tc.want {
			t.Errorf("Expand(%q): got %q ok=%v, want %q", tc.tmpl, got, ok, tc.want)
		}
	}
}

func TestExpandPropertyFunctionChain(t *testing.T) {
	p := &stubPB{m: map[string]string{"TFI": "net.coreapp"}}
	got, ok := Expand("$(TFI.Replace('.', '').ToUpperInvariant())", p, &stubIB{}, nil)
	if !ok || got != "NETCOREAPP" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandPropertyFunctionWithPropertyArg(t *testing.T) {
	p := &stubPB{m: map[string]string{"S": "abc.def", "Sep": "."}}
	got, ok := Expand("$(S.Replace($(Sep), '_'))", p, &stubIB{}, nil)
	if !ok || got != "abc_def" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandPropertyFunctionUnknownMethod(t *testing.T) {
	p := &stubPB{m: map[string]string{"X": "foo"}}
	_, ok := Expand("$(X.UnknownMethod())", p, &stubIB{}, nil)
	if ok {
		t.Error("expected unsupported method to fail")
	}
}

func TestExpandPropertyFunctionTrimStartIsCharSet(t *testing.T) {
	// MSBuild/.NET TrimStart('vV') removes leading 'v' or 'V' chars, not the
	// substring "vV". Verify the char-set semantics.
	p := &stubPB{m: map[string]string{"V": "vvV11.0"}}
	got, ok := Expand("$(V.TrimStart('vV'))", p, &stubIB{}, nil)
	if !ok || got != "11.0" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandPropertyFunctionRejectsNestedFunction(t *testing.T) {
	p := &stubPB{m: map[string]string{"X": "foo", "Y": "BAR"}}
	_, ok := Expand("$(X.Replace($(Y.ToLower()), 'z'))", p, &stubIB{}, nil)
	if ok {
		t.Error("nested property functions in args should be unsupported")
	}
}

func TestExpandPropertyFunctionRejectsIndexer(t *testing.T) {
	_, ok := Expand("$(X[0])", &stubPB{m: map[string]string{"X": "abc"}}, &stubIB{}, nil)
	if ok {
		t.Error("indexer syntax should be unsupported")
	}
}

