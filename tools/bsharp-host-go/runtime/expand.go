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
			end := findMatchingParen(template, j+1)
			if end < 0 {
				return sb.String(), false
			}
			inner := template[j+2 : end]
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
	// $(SimpleProp) — single-level expansion.
	if strings.HasPrefix(arg, "$(") && strings.HasSuffix(arg, ")") {
		inner := strings.TrimSpace(arg[2 : len(arg)-1])
		if isSimpleIdentifier(inner) {
			return p.Get(inner), true
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
