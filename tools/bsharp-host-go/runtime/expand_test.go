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

func TestExpandItemTransformBasic(t *testing.T) {
	// `@(X->'template')` — per-item template expansion with metadata as
	// batch context, joined with `;` by default.
	ib := &stubIB{m: map[string][]*Item{
		"Src": {
			NewItemWithMetadata("a.cs", map[string]string{"TargetPath": "out/a.cs"}),
			NewItemWithMetadata("b.cs", map[string]string{"TargetPath": "out/b.cs"}),
		},
	}}
	got, ok := Expand("@(Src->'%(TargetPath)')", &stubPB{}, ib, nil)
	if !ok || got != "out/a.cs;out/b.cs" {
		t.Errorf("got %q ok=%v; want 'out/a.cs;out/b.cs'", got, ok)
	}
}

func TestExpandItemTransformWithPropertyRef(t *testing.T) {
	// Common SDK pattern: `@(X->'$(OutDir)%(TargetPath)')`.
	pb := &stubPB{m: map[string]string{"OutDir": "bin/Debug/"}}
	ib := &stubIB{m: map[string][]*Item{
		"Src": {
			NewItemWithMetadata("a.dll", map[string]string{"TargetPath": "lib/a.dll"}),
		},
	}}
	got, ok := Expand("@(Src->'$(OutDir)%(TargetPath)')", pb, ib, nil)
	if !ok || got != "bin/Debug/lib/a.dll" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandItemTransformCustomSep(t *testing.T) {
	ib := &stubIB{m: map[string][]*Item{
		"Items": {NewItem("a"), NewItem("b"), NewItem("c")},
	}}
	got, ok := Expand("@(Items->'%(Identity).x', ', ')", &stubPB{}, ib, nil)
	if !ok || got != "a.x, b.x, c.x" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandItemTransformQualifiedMeta(t *testing.T) {
	// `%(ItemName.Meta)` qualified — handler strips the qualifier and looks
	// up `meta` against the per-item batch map.
	ib := &stubIB{m: map[string][]*Item{
		"Src": {NewItemWithMetadata("x", map[string]string{"Culture": "fr-FR"})},
	}}
	got, ok := Expand("@(Src->'%(Src.Culture)')", &stubPB{}, ib, nil)
	if !ok || got != "fr-FR" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandItemTransformWellKnownMetadata(t *testing.T) {
	// `%(Filename)`, `%(Extension)`, `%(Directory)` are derived from Identity.
	ib := &stubIB{m: map[string][]*Item{
		"Src": {NewItem("sub/foo.cs")},
	}}
	got, ok := Expand("@(Src->'%(Filename)%(Extension)')", &stubPB{}, ib, nil)
	if !ok || got != "foo.cs" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandItemTransformEmptyList(t *testing.T) {
	ib := &stubIB{m: map[string][]*Item{}}
	got, ok := Expand("@(Nothing->'%(Identity).x')", &stubPB{}, ib, nil)
	if !ok || got != "" {
		t.Errorf("got %q ok=%v; want empty", got, ok)
	}
}

func TestExpandItemTransformRejectsMalformedRhs(t *testing.T) {
	ib := &stubIB{m: map[string][]*Item{"Src": {NewItem("a")}}}
	// Missing closing quote on template.
	if _, ok := Expand("@(Src->'tpl)", &stubPB{}, ib, nil); ok {
		t.Error("unclosed transform template must be reported unsupported")
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


func TestExpandItemCountEmpty(t *testing.T) {
	items := &stubIB{m: map[string][]*Item{}}
	got, ok := Expand("@(Compile->Count())", &stubPB{}, items, nil)
	if !ok || got != "0" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandItemCountMultiple(t *testing.T) {
	items := &stubIB{m: map[string][]*Item{
		"PackageReference": {NewItem("Foo"), NewItem("Bar"), NewItem("Baz")},
	}}
	got, ok := Expand("@(PackageReference->Count())", &stubPB{}, items, nil)
	if !ok || got != "3" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandItemCountInQuotedExpr(t *testing.T) {
	items := &stubIB{m: map[string][]*Item{
		"Using": {NewItem("System"), NewItem("System.IO")},
	}}
	// As used by GenerateGlobalUsings: '@(Using->Count())' != '0'
	got, ok := Expand("'@(Using->Count())' != '0'", &stubPB{}, items, nil)
	if !ok || got != "'2' != '0'" {
		t.Errorf("got %q ok=%v", got, ok)
	}
}

func TestExpandItemFuncRejectsUnsupportedFunc(t *testing.T) {
	items := &stubIB{m: map[string][]*Item{"X": {NewItem("a")}}}
	_, ok := Expand("@(X->WithMetadataValue('Foo','bar'))", &stubPB{}, items, nil)
	if ok {
		t.Error("unsupported item function should be rejected (so MustExpand panics loudly)")
	}
}

func TestExpandItemFuncRejectsChainedCall(t *testing.T) {
	items := &stubIB{m: map[string][]*Item{"X": {NewItem("a")}}}
	_, ok := Expand("@(X->Count().ToLower())", &stubPB{}, items, nil)
	if ok {
		t.Error("chained item function should be rejected")
	}
}

func TestExpandItemAnyHaveMetadataValue(t *testing.T) {
	items := &stubIB{m: map[string][]*Item{
		"PackageReference": {
			NewItemWithMetadata("Foo", map[string]string{"Identity": "Foo", "Version": "1.0"}),
			NewItemWithMetadata("Bar", map[string]string{"Identity": "Bar", "Version": "2.0"}),
		},
		"Empty": nil,
	}}
	p := &stubPB{m: map[string]string{"Wanted": "Bar"}}
	cases := []struct {
		tmpl string
		want string
	}{
		// literal metadata value
		{`@(PackageReference->AnyHaveMetadataValue('Identity', 'Foo'))`, "True"},
		{`@(PackageReference->AnyHaveMetadataValue('Identity', 'Baz'))`, "False"},
		// case-insensitive value compare (matches MSBuild)
		{`@(PackageReference->AnyHaveMetadataValue('Identity', 'BAR'))`, "True"},
		// expansion in the value arg
		{`@(PackageReference->AnyHaveMetadataValue('Identity', '$(Wanted)'))`, "True"},
		// empty collection always False
		{`@(Empty->AnyHaveMetadataValue('Identity', 'Anything'))`, "False"},
	}
	for _, tc := range cases {
		got, ok := Expand(tc.tmpl, p, items, nil)
		if !ok {
			t.Errorf("%q: expected ok, got unsupported", tc.tmpl)
			continue
		}
		if got != tc.want {
			t.Errorf("%q: got %q, want %q", tc.tmpl, got, tc.want)
		}
	}
}
