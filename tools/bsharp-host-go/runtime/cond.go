package runtime

import (
	"strconv"
	"strings"
)

// NumericCompare compares two strings using MSBuild's promotion rules:
// integer if both parse as integers, then SemVer-style version, then
// case-insensitive string. Returns -1, 0, or +1. Mirrors
// `CondHelpers.NumericCompare`.
func NumericCompare(a, b string) int {
	if x, errA := strconv.ParseInt(a, 10, 64); errA == nil {
		if y, errB := strconv.ParseInt(b, 10, 64); errB == nil {
			switch {
			case x < y:
				return -1
			case x > y:
				return 1
			default:
				return 0
			}
		}
	}
	if va, okA := parseVersion(a); okA {
		if vb, okB := parseVersion(b); okB {
			return compareVersion(va, vb)
		}
	}
	return strings.Compare(strings.ToLower(a), strings.ToLower(b))
}

// IsAny returns true iff value equals any candidate (case-insensitive).
// Mirrors `CondHelpers.IsAny`.
func IsAny(value string, candidates ...string) bool {
	for _, c := range candidates {
		if strings.EqualFold(value, c) {
			return true
		}
	}
	return false
}

// MSBuild Version semantics: up to 4 dotted integer components. Missing
// components compare as 0.
type version struct{ major, minor, build, revision int64 }

func parseVersion(s string) (version, bool) {
	s = strings.TrimSpace(s)
	if s == "" {
		return version{}, false
	}
	parts := strings.Split(s, ".")
	if len(parts) < 2 || len(parts) > 4 {
		return version{}, false
	}
	var v version
	for i, p := range parts {
		n, err := strconv.ParseInt(p, 10, 64)
		if err != nil || n < 0 {
			return version{}, false
		}
		switch i {
		case 0:
			v.major = n
		case 1:
			v.minor = n
		case 2:
			v.build = n
		case 3:
			v.revision = n
		}
	}
	return v, true
}

func compareVersion(a, b version) int {
	for _, pair := range [4][2]int64{
		{a.major, b.major},
		{a.minor, b.minor},
		{a.build, b.build},
		{a.revision, b.revision},
	} {
		switch {
		case pair[0] < pair[1]:
			return -1
		case pair[0] > pair[1]:
			return 1
		}
	}
	return 0
}
