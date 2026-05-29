package runtime

import (
	"path/filepath"
	"strings"
)

// EnsureTrailingSlash returns value with a trailing path separator if it
// doesn't already end in one. Empty input returns "".
func EnsureTrailingSlash(value string) string {
	if value == "" {
		return ""
	}
	last := value[len(value)-1]
	if last == '/' || last == '\\' {
		return value
	}
	return value + string(filepath.Separator)
}

// NormalizeSeparators converts '\' to the host directory separator on
// non-Windows hosts (no-op on Windows). Matches `PathUtil.NormalizeSeparators`.
func NormalizeSeparators(value string) string {
	if filepath.Separator == '/' {
		return strings.ReplaceAll(value, "\\", "/")
	}
	return value
}

// NormalizeSeparatorList splits a `;`-separated list, normalizes each entry,
// and re-joins. Empty entries are dropped.
func NormalizeSeparatorList(value string) string {
	if value == "" {
		return ""
	}
	parts := SplitSemicolon(value)
	for i := range parts {
		parts[i] = NormalizeSeparators(parts[i])
	}
	return strings.Join(parts, ";")
}
