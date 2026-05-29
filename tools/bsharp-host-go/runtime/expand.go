package runtime

import (
	"fmt"
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
			end := findMatchingParen(template, j+1)
			if end < 0 {
				return sb.String(), false
			}
			inner := template[j+2 : end]
			// Reject anything with a "." (property fn), "(" (nested call),
			// or a space (Property::Func() syntax remnant) — Phase B/C land.
			if strings.ContainsAny(inner, ".([:") {
				return sb.String(), false
			}
			sb.WriteString(p.Get(strings.TrimSpace(inner)))
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
