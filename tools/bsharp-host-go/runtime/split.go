package runtime

import "strings"

// SplitSemicolon returns the `;`-separated, trimmed, non-empty segments of s.
// Mirrors `new SplitList(s)` / `new SemicolonSplit(s)` enumerator semantics
// (RemoveEmptyEntries + Trim).
func SplitSemicolon(s string) []string {
	if s == "" {
		return nil
	}
	var out []string
	start := 0
	for i := 0; i <= len(s); i++ {
		if i == len(s) || s[i] == ';' {
			seg := strings.TrimSpace(s[start:i])
			if seg != "" {
				out = append(out, seg)
			}
			start = i + 1
		}
	}
	return out
}

// SplitSemicolonKeepEmpty returns segments without trimming or dropping empty
// ones. Used in places where MSBuild preserves "" entries (rare).
func SplitSemicolonKeepEmpty(s string) []string {
	if s == "" {
		return nil
	}
	return strings.Split(s, ";")
}
