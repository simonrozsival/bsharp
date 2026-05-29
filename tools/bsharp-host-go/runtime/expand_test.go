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

func TestExpandPropertyFunctionUnsupported(t *testing.T) {
	_, ok := Expand("$(Configuration.ToLower())", &stubPB{}, &stubIB{}, nil)
	if ok {
		t.Error("property functions must be reported as unsupported")
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
	_ = MustExpand("$(X.ToLower())", &stubPB{}, &stubIB{}, nil)
}
