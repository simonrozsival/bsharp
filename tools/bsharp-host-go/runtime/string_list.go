package runtime

import "strings"

// StringList is the Go equivalent of the C# `StringList` static helper.
// It returns the `;`-separated, non-empty segments of value.
func StringList(value string) []string {
	return SplitSemicolon(value)
}

// StringSet is a set with both exact and glob membership, mirroring the C#
// `StringSet` class used by SDK condition checks. Patterns containing `*`
// or `?` are stored separately and matched via simple glob.
type StringSet struct {
	exact    map[string]struct{}
	patterns []string
}

// NewStringSetFromSemicolonList constructs a StringSet from a `;`-list.
func NewStringSetFromSemicolonList(value string) *StringSet {
	s := &StringSet{exact: make(map[string]struct{})}
	for _, p := range SplitSemicolon(value) {
		s.add(p)
	}
	return s
}

// NewStringSetFromItems constructs a StringSet from item identities.
func NewStringSetFromItems(items []*Item) *StringSet {
	s := &StringSet{exact: make(map[string]struct{})}
	for _, it := range items {
		s.add(it.Identity)
	}
	return s
}

func (s *StringSet) add(value string) {
	norm := strings.ReplaceAll(value, "\\", "/")
	if strings.ContainsAny(norm, "*?") {
		s.patterns = append(s.patterns, norm)
		if strings.Contains(norm, "/**/") {
			s.patterns = append(s.patterns, strings.ReplaceAll(norm, "/**/", "/"))
		}
		return
	}
	s.exact[strings.ToLower(norm)] = struct{}{}
}

// Contains reports whether value matches any exact entry or any pattern.
func (s *StringSet) Contains(value string) bool {
	norm := strings.ReplaceAll(value, "\\", "/")
	if _, ok := s.exact[strings.ToLower(norm)]; ok {
		return true
	}
	for _, pat := range s.patterns {
		if globMatch(pat, norm) {
			return true
		}
	}
	return false
}

// globMatch is a tiny case-insensitive `*`/`?` glob matcher matching the C#
// implementation. `**` collapses to `*` (single-level), and the alternate
// pattern handling above covers the multi-level wildcard.
func globMatch(pattern, value string) bool {
	return globMatchAt(pattern, value, 0, 0)
}

func globMatchAt(pattern, value string, p, v int) bool {
	for p < len(pattern) {
		c := pattern[p]
		if c == '*' {
			for p+1 < len(pattern) && pattern[p+1] == '*' {
				p++
			}
			if p+1 == len(pattern) {
				return true
			}
			for i := v; i <= len(value); i++ {
				if globMatchAt(pattern, value, p+1, i) {
					return true
				}
			}
			return false
		}
		if v >= len(value) {
			return false
		}
		if c == '?' {
			p++
			v++
			continue
		}
		if upper(c) != upper(value[v]) {
			return false
		}
		p++
		v++
	}
	return v == len(value)
}

func upper(b byte) byte {
	if b >= 'a' && b <= 'z' {
		return b - 32
	}
	return b
}
