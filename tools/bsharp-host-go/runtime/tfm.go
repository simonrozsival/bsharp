package runtime

import (
	"strconv"
	"strings"
	"unicode"
)

// IsTargetFrameworkCompatible is the Go equivalent of
// `TargetFrameworkHelpers.IsCompatible`. It implements MSBuild's
// `$([MSBuild]::IsTargetFrameworkCompatible(candidate, requirement))` with
// the same approximation: parse "netN.M" / "netcoreappN.M" / "netstandardN.M",
// loose identifier-then-version match, with a special case for net5+ being
// considered compatible with netcoreapp and netstandard.
func IsTargetFrameworkCompatible(candidate, requirement string) bool {
	cID, cVer, cOK := parseTfm(candidate)
	rID, rVer, rOK := parseTfm(requirement)
	if !cOK || !rOK {
		return false
	}
	if cID == rID {
		return compareVersion(cVer, rVer) >= 0
	}
	if cID == "net" && cVer.major >= 5 && (rID == "netcoreapp" || rID == "netstandard") {
		return true
	}
	if cID == "netcoreapp" && rID == "netstandard" {
		return cVer.major >= 1
	}
	return false
}

func parseTfm(s string) (string, version, bool) {
	s = strings.TrimSpace(s)
	if s == "" {
		return "", version{}, false
	}
	i := 0
	for i < len(s) && unicode.IsLetter(rune(s[i])) {
		i++
	}
	id := strings.ToLower(s[:i])
	rest := s[i:]
	if !strings.ContainsRune(rest, '.') {
		rest = rest + ".0"
	}
	parts := strings.Split(rest, ".")
	var v version
	if len(parts) >= 1 {
		if n, err := strconv.ParseInt(parts[0], 10, 64); err == nil {
			v.major = n
		}
	}
	if len(parts) >= 2 {
		if n, err := strconv.ParseInt(parts[1], 10, 64); err == nil {
			v.minor = n
		}
	}
	return id, v, true
}
