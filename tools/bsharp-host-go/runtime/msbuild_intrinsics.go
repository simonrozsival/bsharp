package runtime

import (
	"runtime"
	"strings"
)

// EvalMSBuildIntrinsic evaluates `[MSBuild]::Name(args)` where the receiver
// has already been stripped. The argument string `argsStr` is the raw text
// between the outer parens (may contain commas, quoted strings, and `$(…)`
// expansions). Returns (value, ok). ok=false signals an unsupported intrinsic
// or unsupported argument shape; callers should bubble up the unsupported
// signal so the surrounding template/condition stubs out loudly rather than
// silently evaluating to "".
//
// Supported intrinsic names (case-insensitive):
//
//   - VersionEquals(a, b)
//   - VersionGreaterThan(a, b)
//   - VersionGreaterThanOrEquals(a, b)
//   - VersionLessThan(a, b)
//   - VersionLessThanOrEquals(a, b)
//   - IsOSPlatform(name)  / IsOsPlatform(name)
//   - ValueOrDefault(value, default)
//   - Escape(s)
//
// Other MSBuild intrinsics (MakeRelative, NormalizePath, EnsureTrailingSlash,
// IsTargetFrameworkCompatible, …) deliberately return ok=false so the closed-
// world classifier can stub them out and surface them for prioritization
// rather than producing wrong-but-quiet builds.
func EvalMSBuildIntrinsic(name, argsStr string, p PropertyBag) (string, bool) {
	args, ok := splitIntrinsicArgs(argsStr, p)
	if !ok {
		return "", false
	}
	switch strings.ToLower(name) {
	case "versionequals":
		if len(args) != 2 {
			return "", false
		}
		c, ok := compareVersionArgs(args[0], args[1])
		if !ok {
			return "False", true
		}
		return boolToMSBuild(c == 0), true
	case "versiongreaterthan":
		if len(args) != 2 {
			return "", false
		}
		c, ok := compareVersionArgs(args[0], args[1])
		if !ok {
			return "False", true
		}
		return boolToMSBuild(c > 0), true
	case "versiongreaterthanorequals":
		if len(args) != 2 {
			return "", false
		}
		c, ok := compareVersionArgs(args[0], args[1])
		if !ok {
			return "False", true
		}
		return boolToMSBuild(c >= 0), true
	case "versionlessthan":
		if len(args) != 2 {
			return "", false
		}
		c, ok := compareVersionArgs(args[0], args[1])
		if !ok {
			return "False", true
		}
		return boolToMSBuild(c < 0), true
	case "versionlessthanorequals":
		if len(args) != 2 {
			return "", false
		}
		c, ok := compareVersionArgs(args[0], args[1])
		if !ok {
			return "False", true
		}
		return boolToMSBuild(c <= 0), true
	case "isosplatform":
		if len(args) != 1 {
			return "", false
		}
		return boolToMSBuild(isOSPlatform(args[0])), true
	case "valueordefault":
		if len(args) != 2 {
			return "", false
		}
		if args[0] != "" {
			return args[0], true
		}
		return args[1], true
	case "escape":
		if len(args) != 1 {
			return "", false
		}
		return msbuildEscape(args[0]), true
	}
	return "", false
}

// compareVersionArgs returns the result of comparing a and b as MSBuild
// version strings, plus ok=false if either side is not a parseable version
// (per the runtime's parseVersion: 2–4 dotted non-negative integers).
// Callers map the "unparseable" case to "False" the same way MSBuild does
// for invalid version inputs.
func compareVersionArgs(a, b string) (int, bool) {
	va, oka := parseVersion(a)
	vb, okb := parseVersion(b)
	if !oka || !okb {
		return 0, false
	}
	return compareVersion(va, vb), true
}

// isOSPlatform reports whether the supplied MSBuild platform name matches
// the current GOOS. Comparison is case-insensitive. Recognized names cover
// the runtimes we actually target.
func isOSPlatform(name string) bool {
	want := strings.ToLower(strings.TrimSpace(name))
	switch runtime.GOOS {
	case "darwin":
		return want == "osx" || want == "macos" || want == "macosx" || want == "darwin"
	case "linux":
		return want == "linux"
	case "windows":
		return want == "windows"
	case "freebsd":
		return want == "freebsd"
	case "netbsd":
		return want == "netbsd"
	case "openbsd":
		return want == "openbsd"
	default:
		return want == strings.ToLower(runtime.GOOS)
	}
}

// msbuildEscape mirrors `[MSBuild]::Escape` — replaces the reserved MSBuild
// special characters with their `%XX` escape sequences. Implemented faithfully
// so downstream callers that re-tokenize lists (`;`-split) or re-evaluate
// expressions see correct results.
func msbuildEscape(s string) string {
	if s == "" {
		return ""
	}
	// MSBuild reserved set: % * ? @ $ ( ) ; '
	var sb strings.Builder
	sb.Grow(len(s))
	for i := 0; i < len(s); i++ {
		c := s[i]
		switch c {
		case '%':
			sb.WriteString("%25")
		case '*':
			sb.WriteString("%2A")
		case '?':
			sb.WriteString("%3F")
		case '@':
			sb.WriteString("%40")
		case '$':
			sb.WriteString("%24")
		case '(':
			sb.WriteString("%28")
		case ')':
			sb.WriteString("%29")
		case ';':
			sb.WriteString("%3B")
		case '\'':
			sb.WriteString("%27")
		default:
			sb.WriteByte(c)
		}
	}
	return sb.String()
}

// splitIntrinsicArgs splits a comma-separated argument list (no outer parens)
// and resolves each argument:
//
//   - Quoted string ('a' or "a") — the inner content is recursively Expand-ed
//     against the property bag, so `'$(X)'` correctly resolves.
//   - `$(Prop)` bare expansion — same as Expand for that one expression.
//   - Anything else — treated as a bare literal (trimmed). This is how
//     MSBuild handles bare version literals such as `5.0`.
//
// Returns ok=false on unbalanced quotes/parens or on nested unsupported
// constructs surfaced via Expand. This is a SEPARATE resolver from
// resolveFunctionArg (which is intentionally stricter for property method
// chains).
func splitIntrinsicArgs(s string, p PropertyBag) ([]string, bool) {
	if strings.TrimSpace(s) == "" {
		return nil, true
	}
	var rawParts []string
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
				rawParts = append(rawParts, s[start:i])
				start = i + 1
			}
		}
	}
	if quote != 0 || depth != 0 {
		return nil, false
	}
	rawParts = append(rawParts, s[start:])
	out := make([]string, 0, len(rawParts))
	for _, raw := range rawParts {
		v, ok := resolveIntrinsicArg(strings.TrimSpace(raw), p)
		if !ok {
			return nil, false
		}
		out = append(out, v)
	}
	return out, true
}

func resolveIntrinsicArg(arg string, p PropertyBag) (string, bool) {
	if arg == "" {
		return "", true
	}
	if (arg[0] == '\'' || arg[0] == '"') && len(arg) >= 2 && arg[len(arg)-1] == arg[0] {
		inner := arg[1 : len(arg)-1]
		expanded, ok := Expand(inner, p, emptyItemBag{}, nil)
		if !ok {
			return "", false
		}
		return expanded, true
	}
	if strings.HasPrefix(arg, "$(") && strings.HasSuffix(arg, ")") {
		expanded, ok := Expand(arg, p, emptyItemBag{}, nil)
		if !ok {
			return "", false
		}
		return expanded, true
	}
	return arg, true
}

// tryEvalIntrinsic checks whether the inner of `$(...)` is the supported
// `[MSBuild]::Name(args)` shape and, if so, dispatches to EvalMSBuildIntrinsic.
// Returns:
//   - handled=false → the inner isn't an intrinsic call; caller should fall
//     through to its normal expression evaluator
//   - handled=true, ok=false → intrinsic was recognized but evaluation failed
//     (unsupported name, malformed args, etc.); caller should propagate
//     unsupported upward
//   - handled=true, ok=true → value is the resolved intrinsic result
func tryEvalIntrinsic(inner string, p PropertyBag) (string, bool, bool) {
	trimmed := strings.TrimSpace(inner)
	const prefix = "[MSBuild]::"
	// Compare prefix case-insensitively (MSBuild allows `[msbuild]::Foo`).
	if len(trimmed) < len(prefix) || !strings.EqualFold(trimmed[:len(prefix)], prefix) {
		return "", false, false
	}
	rest := trimmed[len(prefix):]
	// Name then `(args)`.
	openParen := strings.IndexByte(rest, '(')
	if openParen <= 0 {
		return "", false, true
	}
	name := strings.TrimSpace(rest[:openParen])
	closeParen := findMatchingParenQuoteAware(rest, openParen)
	if closeParen < 0 {
		return "", false, true
	}
	// Nothing meaningful may follow the closing paren (no method chains on
	// intrinsics in this subset).
	if strings.TrimSpace(rest[closeParen+1:]) != "" {
		return "", false, true
	}
	argsStr := rest[openParen+1 : closeParen]
	value, ok := EvalMSBuildIntrinsic(name, argsStr, p)
	return value, ok, true
}

// emptyItemBag is used when expanding intrinsic args that should not depend
// on item state (the closed-world subset of conditions/values we accept does
// not put `@()` inside intrinsic args).
type emptyItemBag struct{}

func (emptyItemBag) Get(string) []*Item              { return nil }
func (emptyItemBag) AppendTo(string, []*Item)        {}
