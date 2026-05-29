package runtime

import (
	"fmt"
	"strconv"
	"strings"
)

// Expand evaluates an MSBuild expression template against the property bag
// p and item bag i. Supported constructs (Phase A subset):
//
//   - Literal text.
//   - $(PropertyName) — property scalar substitution. Unknown names yield "".
//   - @(ItemName) — item-list-to-string, emitting `;`-joined identities.
//   - @(ItemName, 'sep') — item-list-to-string with custom separator.
//   - %(MetadataName) and %(ItemName.MetadataName) — qualified/unqualified
//     batch metadata reference, looked up in the supplied batchMeta map.
//     Outside a batched context, batchMeta is nil and these resolve to "".
//
// Returns (expanded, ok). ok=false signals the template contains a construct
// the Phase A evaluator does not handle (property function, escape syntax,
// nested $($(...)), MSBuild::F() intrinsic, etc.) — callers should treat
// the target as unsupported and fall back to a stub. The expanded prefix
// is returned for diagnostic purposes; do not rely on it for execution.
//
// This deliberately does NOT mirror the full MSBuild expander; it is just
// enough to drive a meaningful Phase A subset of real target bodies. The
// full port lives in Phase B+.
func Expand(template string, p PropertyBag, i ItemBag, batchMeta map[string]string) (string, bool) {
	if template == "" {
		return "", true
	}
	var sb strings.Builder
	for j := 0; j < len(template); j++ {
		c := template[j]
		switch c {
		case '$':
			if j+1 >= len(template) || template[j+1] != '(' {
				sb.WriteByte('$')
				continue
			}
			end := findMatchingParenQuoteAware(template, j+1)
			if end < 0 {
				return sb.String(), false
			}
			inner := template[j+2 : end]
			if value, ok, handled := tryEvalIntrinsic(inner, p); handled {
				if !ok {
					return sb.String(), false
				}
				sb.WriteString(value)
				j = end
				continue
			}
			// Reject indexers (`[`) and namespace intrinsics (`::`) that are NOT
			// the supported `[MSBuild]::F(args)` shape. Phase C+ work.
			if strings.ContainsAny(inner, "[:") {
				return sb.String(), false
			}
			value, ok := evalPropertyExpression(inner, p)
			if !ok {
				return sb.String(), false
			}
			sb.WriteString(value)
			j = end
		case '@':
			if j+1 >= len(template) || template[j+1] != '(' {
				sb.WriteByte('@')
				continue
			}
			end := findMatchingParenQuoteAware(template, j+1)
			if end < 0 {
				return sb.String(), false
			}
			inner := template[j+2 : end]
			if arrow := strings.Index(inner, "->"); arrow >= 0 {
				name := strings.TrimSpace(inner[:arrow])
				rhs := strings.TrimSpace(inner[arrow+2:])
				// Item transform `@(Name->'template'[, 'sep'])`: per-item
				// template expansion with item metadata as batch context.
				if strings.HasPrefix(rhs, "'") || strings.HasPrefix(rhs, "\"") {
					if !isSimpleIdentifier(name) {
						return sb.String(), false
					}
					value, ok := evalItemTransform(name, rhs, i, p)
					if !ok {
						return sb.String(), false
					}
					sb.WriteString(value)
					j = end
					continue
				}
				// Otherwise: function call (Count, AnyHaveMetadataValue, etc.).
				value, ok := evalItemFunc(inner, i, p)
				if !ok {
					return sb.String(), false
				}
				sb.WriteString(value)
				j = end
				continue
			}
			name, sep, ok := parseItemRef(inner)
			if !ok {
				return sb.String(), false
			}
			items := i.Get(name)
			for k, it := range items {
				if k > 0 {
					sb.WriteString(sep)
				}
				sb.WriteString(it.Identity)
			}
			j = end
		case '%':
			if j+1 >= len(template) || template[j+1] != '(' {
				sb.WriteByte('%')
				continue
			}
			end := findMatchingParen(template, j+1)
			if end < 0 {
				return sb.String(), false
			}
			inner := strings.TrimSpace(template[j+2 : end])
			if batchMeta == nil {
				j = end
				continue
			}
			// Either "Meta" (unqualified) or "ItemName.Meta" (qualified).
			key := inner
			if dot := strings.IndexByte(inner, '.'); dot >= 0 {
				key = inner[dot+1:]
			}
			sb.WriteString(batchMeta[strings.ToLower(strings.TrimSpace(key))])
			j = end
		default:
			sb.WriteByte(c)
		}
	}
	return sb.String(), true
}

// MustExpand is a convenience wrapper that returns just the string and ignores
// unsupported markers — callers that have already classified the template as
// simple use this. Panics on the unsupported case so unclassified misuse is
// loud rather than silently wrong.
func MustExpand(template string, p PropertyBag, i ItemBag, batchMeta map[string]string) string {
	s, ok := Expand(template, p, i, batchMeta)
	if !ok {
		panic(fmt.Sprintf("MustExpand: unsupported template: %q", template))
	}
	return s
}

// findMatchingParen returns the index of the ')' matching the '(' at openIdx,
// or -1 if not found. Handles nested parens but does NOT track quoted strings
// — that's a Phase B/C job.
func findMatchingParen(s string, openIdx int) int {
	depth := 0
	for k := openIdx; k < len(s); k++ {
		switch s[k] {
		case '(':
			depth++
		case ')':
			depth--
			if depth == 0 {
				return k
			}
		}
	}
	return -1
}

// parseItemRef parses the inside of @(...). Supported forms:
//
//	"ItemName"                            → name=ItemName, sep=";"
//	"ItemName, 'sep'"                     → name=ItemName, sep=sep
//	"ItemName, \" sep \""                 → name=ItemName, sep=sep
//
// Anything else (transforms like @(Foo->'%(Identity)'), conditions, etc.)
// returns ok=false so the caller can fall back to stub.
func parseItemRef(inner string) (name, sep string, ok bool) {
	comma := strings.IndexByte(inner, ',')
	if comma < 0 {
		trimmed := strings.TrimSpace(inner)
		if !isSimpleIdentifier(trimmed) {
			return "", "", false
		}
		return trimmed, ";", true
	}
	left := strings.TrimSpace(inner[:comma])
	if !isSimpleIdentifier(left) {
		return "", "", false
	}
	right := strings.TrimSpace(inner[comma+1:])
	if len(right) >= 2 && ((right[0] == '\'' && right[len(right)-1] == '\'') ||
		(right[0] == '"' && right[len(right)-1] == '"')) {
		return left, right[1 : len(right)-1], true
	}
	return "", "", false
}

func isSimpleIdentifier(s string) bool {
	if s == "" {
		return false
	}
	for _, c := range s {
		switch {
		case c >= 'a' && c <= 'z':
		case c >= 'A' && c <= 'Z':
		case c >= '0' && c <= '9':
		case c == '_' || c == '-':
		default:
			return false
		}
	}
	return true
}

// evalItemFunc evaluates `ItemName -> FuncName(args)` inside an @(...)
// expansion. Currently supported functions:
//   - Count()                                       -> integer count as string
//   - AnyHaveMetadataValue('MetaName', 'Value')     -> "True"/"False"
//
// Returns ok=false for any other shape so unsupported transforms (e.g.
// @(X->'%(Identity)'), @(X->WithMetadataValue(...)), chained calls)
// panic loudly via MustExpand instead of silently returning the wrong
// value.
func evalItemFunc(inner string, items ItemBag, p PropertyBag) (string, bool) {
	arrow := strings.Index(inner, "->")
	if arrow < 0 {
		return "", false
	}
	name := strings.TrimSpace(inner[:arrow])
	rhs := strings.TrimSpace(inner[arrow+2:])
	if !isSimpleIdentifier(name) {
		return "", false
	}
	openIdx := strings.IndexByte(rhs, '(')
	if openIdx < 0 || !strings.HasSuffix(rhs, ")") {
		return "", false
	}
	fn := strings.TrimSpace(rhs[:openIdx])
	argsSrc := rhs[openIdx+1 : len(rhs)-1]
	switch strings.ToLower(fn) {
	case "count":
		if strings.TrimSpace(argsSrc) != "" {
			return "", false
		}
		return fmt.Sprintf("%d", len(items.Get(name))), true
	case "anyhavemetadatavalue":
		args, ok := parseItemFuncArgs(argsSrc, p, items)
		if !ok || len(args) != 2 {
			return "", false
		}
		metaName, metaValue := args[0], args[1]
		for _, it := range items.Get(name) {
			if strings.EqualFold(it.GetMetadata(metaName), metaValue) {
				return "True", true
			}
		}
		return "False", true
	}
	return "", false
}

// evalItemTransform evaluates `@(Name->'template'[, 'sep'])`. For each item
// in the named list, expands `template` with the item's metadata as the
// batch context, then joins with `sep` (default `;`). Matches MSBuild's
// item-transform expansion. Returns ok=false if the transform shape is
// malformed.
func evalItemTransform(name, rhs string, items ItemBag, p PropertyBag) (string, bool) {
	template, sep, ok := parseTransformRhs(rhs)
	if !ok {
		return "", false
	}
	list := items.Get(name)
	parts := make([]string, 0, len(list))
	for _, it := range list {
		meta := itemBatchMeta(it)
		v, ok := Expand(template, p, items, meta)
		if !ok {
			return "", false
		}
		parts = append(parts, v)
	}
	return strings.Join(parts, sep), true
}

// parseTransformRhs parses the post-`->` RHS of an @() item transform.
// Forms: `'template'` (default sep `;`) and `'template', 'sep'`. Returns
// template, sep, ok. Quotes may be single or double (MSBuild allows both).
func parseTransformRhs(rhs string) (template, sep string, ok bool) {
	rhs = strings.TrimSpace(rhs)
	if len(rhs) < 2 {
		return "", "", false
	}
	q := rhs[0]
	if q != '\'' && q != '"' {
		return "", "", false
	}
	end := strings.IndexByte(rhs[1:], q)
	if end < 0 {
		return "", "", false
	}
	template = rhs[1 : 1+end]
	rest := strings.TrimSpace(rhs[1+end+1:])
	if rest == "" {
		return template, ";", true
	}
	if rest[0] != ',' {
		return "", "", false
	}
	rest = strings.TrimSpace(rest[1:])
	if len(rest) < 2 || (rest[0] != '\'' && rest[0] != '"') {
		return "", "", false
	}
	q2 := rest[0]
	endSep := strings.IndexByte(rest[1:], q2)
	if endSep < 0 || endSep+2 != len(rest) {
		return "", "", false
	}
	return template, rest[1 : 1+endSep], true
}

// itemBatchMeta materializes an item's well-known and custom metadata into
// a lowercase-keyed map suitable for passing as `batchMeta` to Expand. The
// transform template's `%(Key)` and `%(Item.Key)` references are resolved
// against this map (the qualifier is stripped by Expand's `%` handler).
func itemBatchMeta(it *Item) map[string]string {
	m := make(map[string]string, 8)
	m["identity"] = it.Identity
	m["fullpath"] = it.GetMetadata("FullPath")
	m["filename"] = it.GetMetadata("Filename")
	m["extension"] = it.GetMetadata("Extension")
	m["directory"] = it.GetMetadata("Directory")
	m["relativedir"] = it.GetMetadata("RelativeDir")
	m["rootdir"] = it.GetMetadata("RootDir")
	if it.meta != nil {
		for k, v := range it.meta {
			m[k] = v
		}
	}
	return m
}

// parseItemFuncArgs splits a comma-separated argument list (quote-aware),
// unquotes each element, and expands any `$()` references inside. Returns
// ok=false on any quote/format error.
func parseItemFuncArgs(src string, p PropertyBag, items ItemBag) ([]string, bool) {
	src = strings.TrimSpace(src)
	if src == "" {
		return nil, true
	}
	// Split by commas at depth=0 outside quotes.
	var parts []string
	start := 0
	depth := 0
	var quote byte = 0
	for i := 0; i < len(src); i++ {
		c := src[i]
		if quote != 0 {
			if c == quote {
				quote = 0
			}
			continue
		}
		switch c {
		case '\'', '"':
			quote = c
		case '(':
			depth++
		case ')':
			depth--
		case ',':
			if depth == 0 {
				parts = append(parts, src[start:i])
				start = i + 1
			}
		}
	}
	if quote != 0 || depth != 0 {
		return nil, false
	}
	parts = append(parts, src[start:])
	out := make([]string, 0, len(parts))
	for _, raw := range parts {
		s := strings.TrimSpace(raw)
		if len(s) >= 2 && (s[0] == '\'' || s[0] == '"') && s[len(s)-1] == s[0] {
			inner := s[1 : len(s)-1]
			expanded, ok := Expand(inner, p, items, nil)
			if !ok {
				return nil, false
			}
			out = append(out, expanded)
			continue
		}
		return nil, false
	}
	return out, true
}

// evalPropertyExpression evaluates the inside of a `$(...)` expression. It
// supports two shapes:
//
//   - "VarName" — simple property lookup; matches the Phase A behavior.
//   - "VarName.Method1(arg1, arg2).Method2(...).Property" — Phase B property
//     function chain. See applyPropertyFunction for the supported methods.
//
// Args may be quoted strings ('a' or "a"), integer literals, or a single
// `$(SimpleProp)` reference (deeper nesting is rejected to keep the classifier
// and runtime in lock-step).
//
// Returns ok=false on any shape the runtime doesn't handle. Callers in `Expand`
// translate that into a top-level ok=false, and `MustExpand` panics.
func evalPropertyExpression(inner string, p PropertyBag) (string, bool) {
	inner = strings.TrimSpace(inner)
	if inner == "" {
		return "", true
	}
	// Locate the variable name (first '.' or end). The variable name must be
	// a simple identifier.
	dot := indexOfTopLevel(inner, '.')
	if dot < 0 {
		if !isSimpleIdentifier(inner) {
			return "", false
		}
		return p.Get(inner), true
	}
	varName := strings.TrimSpace(inner[:dot])
	if !isSimpleIdentifier(varName) {
		return "", false
	}
	value := p.Get(varName)
	rest := inner[dot+1:]
	for len(rest) > 0 {
		// Parse one chain element: IDENT or IDENT(args). It must start with a
		// simple identifier.
		nameEnd := 0
		for nameEnd < len(rest) {
			c := rest[nameEnd]
			isIdent := (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'
			if !isIdent {
				break
			}
			nameEnd++
		}
		if nameEnd == 0 {
			return "", false
		}
		method := rest[:nameEnd]
		rest = rest[nameEnd:]
		var args []string
		hasArgs := false
		if len(rest) > 0 && rest[0] == '(' {
			closeIdx := findMatchingParenQuoteAware(rest, 0)
			if closeIdx < 0 {
				return "", false
			}
			parsed, ok := splitFunctionArgs(rest[1:closeIdx], p)
			if !ok {
				return "", false
			}
			args = parsed
			hasArgs = true
			rest = rest[closeIdx+1:]
		}
		v, ok := applyPropertyFunction(value, method, args, hasArgs)
		if !ok {
			return "", false
		}
		value = v
		rest = strings.TrimSpace(rest)
		if len(rest) == 0 {
			break
		}
		if rest[0] != '.' {
			return "", false
		}
		rest = rest[1:]
	}
	return value, true
}

// indexOfTopLevel returns the index of the first occurrence of c in s that
// is not nested inside parentheses or quotes, or -1.
func indexOfTopLevel(s string, c byte) int {
	depth := 0
	var quote byte
	for i := 0; i < len(s); i++ {
		ch := s[i]
		if quote != 0 {
			if ch == quote {
				quote = 0
			}
			continue
		}
		switch ch {
		case '\'', '"':
			quote = ch
		case '(':
			depth++
		case ')':
			if depth > 0 {
				depth--
			}
		default:
			if depth == 0 && ch == c {
				return i
			}
		}
	}
	return -1
}

// findMatchingParenQuoteAware is the quote-aware version of findMatchingParen.
// It treats '('/')' inside quoted strings as literal characters.
func findMatchingParenQuoteAware(s string, openIdx int) int {
	depth := 0
	var quote byte
	for k := openIdx; k < len(s); k++ {
		ch := s[k]
		if quote != 0 {
			if ch == quote {
				quote = 0
			}
			continue
		}
		switch ch {
		case '\'', '"':
			quote = ch
		case '(':
			depth++
		case ')':
			depth--
			if depth == 0 {
				return k
			}
		}
	}
	return -1
}

// splitFunctionArgs parses a comma-separated argument list (already stripped
// of its surrounding parens). Each argument must be either:
//
//   - a quoted string ('foo' or "foo") with no expansion inside;
//   - an integer literal (e.g. 0, 12);
//   - a `$(SimpleProp)` reference (single-level only).
//
// Returns the resolved argument values. Empty argument list returns nil.
// Anything more complex (transforms, nested calls, batching, item refs) → ok=false.
func splitFunctionArgs(s string, p PropertyBag) ([]string, bool) {
	if strings.TrimSpace(s) == "" {
		return nil, true
	}
	var parts []string
	depth := 0
	var quote byte
	start := 0
	for i := 0; i < len(s); i++ {
		ch := s[i]
		if quote != 0 {
			if ch == quote {
				quote = 0
			}
			continue
		}
		switch ch {
		case '\'', '"':
			quote = ch
		case '(':
			depth++
		case ')':
			depth--
		case ',':
			if depth == 0 {
				parts = append(parts, s[start:i])
				start = i + 1
			}
		}
	}
	if quote != 0 || depth != 0 {
		return nil, false
	}
	parts = append(parts, s[start:])
	out := make([]string, 0, len(parts))
	for _, raw := range parts {
		v, ok := resolveFunctionArg(strings.TrimSpace(raw), p)
		if !ok {
			return nil, false
		}
		out = append(out, v)
	}
	return out, true
}

func resolveFunctionArg(arg string, p PropertyBag) (string, bool) {
	if arg == "" {
		return "", true
	}
	if (arg[0] == '\'' || arg[0] == '"') && len(arg) >= 2 && arg[len(arg)-1] == arg[0] {
		return arg[1 : len(arg)-1], true
	}
	// $(...) — single bare expansion. The classic Phase B form requires the
	// inner to be a simple identifier; we additionally allow the Phase G+
	// shape `$(X.M(args).M2(args)...)` by routing the inner through
	// evalPropertyExpression (which is the same evaluator the top-level
	// `$` case in Expand uses). Nested property functions are common in
	// SDK conditions like
	// `$(RuntimeIdentifier.ToUpperInvariant().Contains($(PlatformTarget.ToUpperInvariant())))`.
	if strings.HasPrefix(arg, "$(") && strings.HasSuffix(arg, ")") {
		inner := strings.TrimSpace(arg[2 : len(arg)-1])
		if isSimpleIdentifier(inner) {
			return p.Get(inner), true
		}
		// `[TypeName]::Member(args)` intrinsic call wrapped in $(...).
		if len(inner) > 0 && inner[0] == '[' {
			if v, ok, handled := tryEvalIntrinsic(inner, p); handled {
				return v, ok
			}
			return "", false
		}
		// Property method chain — let evalPropertyExpression do the work.
		if v, ok := evalPropertyExpression(inner, p); ok {
			return v, true
		}
		return "", false
	}
	// Integer literal.
	if isIntegerLiteral(arg) {
		return arg, true
	}
	return "", false
}

func isIntegerLiteral(s string) bool {
	if s == "" {
		return false
	}
	start := 0
	if s[0] == '-' || s[0] == '+' {
		if len(s) == 1 {
			return false
		}
		start = 1
	}
	for i := start; i < len(s); i++ {
		if s[i] < '0' || s[i] > '9' {
			return false
		}
	}
	return true
}

// applyPropertyFunction applies a single chain element to value. method name is
// case-insensitive. hasArgs distinguishes property access (no parens) from a
// zero-arg method call.
func applyPropertyFunction(value, method string, args []string, hasArgs bool) (string, bool) {
	switch strings.ToLower(method) {
	case "tolower", "tolowerinvariant":
		if hasArgs && len(args) > 0 {
			return "", false
		}
		return strings.ToLower(value), true
	case "toupper", "toupperinvariant":
		if hasArgs && len(args) > 0 {
			return "", false
		}
		return strings.ToUpper(value), true
	case "trim":
		if !hasArgs || len(args) == 0 {
			return strings.TrimSpace(value), true
		}
		if len(args) != 1 {
			return "", false
		}
		return strings.Trim(value, args[0]), true
	case "trimstart":
		if !hasArgs || len(args) == 0 {
			return strings.TrimLeft(value, " \t\n\r"), true
		}
		if len(args) != 1 {
			return "", false
		}
		return strings.TrimLeft(value, args[0]), true
	case "trimend":
		if !hasArgs || len(args) == 0 {
			return strings.TrimRight(value, " \t\n\r"), true
		}
		if len(args) != 1 {
			return "", false
		}
		return strings.TrimRight(value, args[0]), true
	case "replace":
		if len(args) != 2 {
			return "", false
		}
		return strings.ReplaceAll(value, args[0], args[1]), true
	case "substring":
		if len(args) < 1 || len(args) > 2 {
			return "", false
		}
		start, err := strconv.Atoi(args[0])
		if err != nil || start < 0 || start > len(value) {
			return "", false
		}
		if len(args) == 1 {
			return value[start:], true
		}
		length, err := strconv.Atoi(args[1])
		if err != nil || length < 0 || start+length > len(value) {
			return "", false
		}
		return value[start : start+length], true
	case "startswith":
		if len(args) != 1 {
			return "", false
		}
		return boolToMSBuild(strings.HasPrefix(value, args[0])), true
	case "endswith":
		if len(args) != 1 {
			return "", false
		}
		return boolToMSBuild(strings.HasSuffix(value, args[0])), true
	case "contains":
		if len(args) != 1 {
			return "", false
		}
		return boolToMSBuild(strings.Contains(value, args[0])), true
	case "indexof":
		if len(args) != 1 {
			return "", false
		}
		return strconv.Itoa(strings.Index(value, args[0])), true
	case "length":
		if hasArgs && len(args) > 0 {
			return "", false
		}
		return strconv.Itoa(len(value)), true
	case "split":
		// MSBuild's String.Split(separator) returns a string[] which is
		// idiomatically consumed via SplitSemicolon downstream (e.g.
		// `<ItemGroup><X Include="$(Y.Split(';'))" /></ItemGroup>`). We
		// represent the array as a `;`-joined string so any caller that
		// runs the result through rt.SplitSemicolon sees the expected
		// individual entries. `%3B` literals in the arg are unescaped to
		// `;` first to match MSBuild's escape-aware separator handling.
		if len(args) != 1 {
			return "", false
		}
		sep := strings.ReplaceAll(args[0], "%3B", ";")
		if sep == "" {
			return "", false
		}
		parts := strings.Split(value, sep)
		return strings.Join(parts, ";"), true
	case "tostring":
		if hasArgs && len(args) > 0 {
			return "", false
		}
		return value, true
	}
	return "", false
}

func boolToMSBuild(b bool) string {
	if b {
		return "True"
	}
	return "False"
}
