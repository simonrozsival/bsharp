package runtime

import (
	"path/filepath"
	"runtime"
	"strings"
)

// EvalIntrinsic evaluates a static method/property call from a supported
// namespace. Recognized prefixes (case-insensitive on the type name; method
// name is also case-insensitive):
//
//   - [MSBuild]::F(args) — see msbuildIntrinsic
//   - [System.IO.Path]::F(args) or [System.IO.Path]::Property — see pathIntrinsic
//
// Returns (value, ok). ok=false signals an unsupported intrinsic or argument
// shape. The classifier mirrors this whitelist; new intrinsics must be added
// in both places.
func EvalIntrinsic(typeName, member, argsStr string, hasArgs bool, p PropertyBag) (string, bool) {
	switch strings.ToLower(typeName) {
	case "msbuild":
		if !hasArgs {
			return "", false
		}
		return msbuildIntrinsic(member, argsStr, p)
	case "system.io.path":
		return pathIntrinsic(member, argsStr, hasArgs, p)
	}
	return "", false
}

// EvalMSBuildIntrinsic preserves the older API for tests; new callers should
// use EvalIntrinsic.
func EvalMSBuildIntrinsic(name, argsStr string, p PropertyBag) (string, bool) {
	return msbuildIntrinsic(name, argsStr, p)
}

func msbuildIntrinsic(name, argsStr string, p PropertyBag) (string, bool) {
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

// pathIntrinsic handles [System.IO.Path]::* members.
func pathIntrinsic(name, argsStr string, hasArgs bool, p PropertyBag) (string, bool) {
	lower := strings.ToLower(name)
	// Static properties (no parens).
	if !hasArgs {
		switch lower {
		case "directoryseparatorchar":
			return string(filepath.Separator), true
		case "pathseparator":
			return string(filepath.ListSeparator), true
		case "altdirectoryseparatorchar":
			// On Unix this matches DirectorySeparatorChar; on Windows it's '/'.
			if runtime.GOOS == "windows" {
				return "/", true
			}
			return string(filepath.Separator), true
		}
		return "", false
	}
	args, ok := splitIntrinsicArgs(argsStr, p)
	if !ok {
		return "", false
	}
	switch lower {
	case "combine":
		if len(args) == 0 {
			return "", false
		}
		// Per .NET semantics, if any segment is rooted, earlier segments are
		// discarded. filepath.Join doesn't do that automatically, so apply
		// manually for closer fidelity.
		start := 0
		for i, a := range args {
			if filepath.IsAbs(a) {
				start = i
			}
		}
		return filepath.Join(args[start:]...), true
	case "getfullpath":
		if len(args) != 1 {
			return "", false
		}
		abs, err := filepath.Abs(args[0])
		if err != nil {
			return "", false
		}
		return abs, true
	case "getfilename":
		if len(args) != 1 {
			return "", false
		}
		// .NET returns "" if the input ends with a separator; filepath.Base
		// returns "." for empty input but "/" for "/". Be defensive.
		if args[0] == "" {
			return "", true
		}
		if strings.HasSuffix(args[0], string(filepath.Separator)) {
			return "", true
		}
		return filepath.Base(args[0]), true
	case "getfilenamewithoutextension":
		if len(args) != 1 {
			return "", false
		}
		if args[0] == "" {
			return "", true
		}
		base := filepath.Base(args[0])
		ext := filepath.Ext(base)
		if ext == "" {
			return base, true
		}
		return base[:len(base)-len(ext)], true
	case "getextension":
		if len(args) != 1 {
			return "", false
		}
		return filepath.Ext(args[0]), true
	case "getdirectoryname":
		if len(args) != 1 {
			return "", false
		}
		if args[0] == "" {
			return "", true
		}
		dir := filepath.Dir(args[0])
		// .NET returns "" (not ".") when there's no directory portion.
		if dir == "." {
			return "", true
		}
		return dir, true
	case "ispathrooted":
		if len(args) != 1 {
			return "", false
		}
		return boolToMSBuild(filepath.IsAbs(args[0])), true
	case "changeextension":
		if len(args) != 2 {
			return "", false
		}
		ext := args[1]
		if ext != "" && !strings.HasPrefix(ext, ".") {
			ext = "." + ext
		}
		base := args[0]
		curExt := filepath.Ext(base)
		if curExt == "" {
			return base + ext, true
		}
		return base[:len(base)-len(curExt)] + ext, true
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
		case '\'', '"', '`':
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
	// Accept backtick quoting (MSBuild allows it for callsite quoting).
	if (arg[0] == '\'' || arg[0] == '"' || arg[0] == '`') && len(arg) >= 2 && arg[len(arg)-1] == arg[0] {
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
// `[TypeName]::Member(args?)` shape and, if so, dispatches to EvalIntrinsic.
// Returns:
//   - handled=false → the inner isn't an intrinsic call; caller should fall
//     through to its normal expression evaluator
//   - handled=true, ok=false → intrinsic was recognized but evaluation failed
//     (unsupported name, malformed args, etc.); caller should propagate
//     unsupported upward
//   - handled=true, ok=true → value is the resolved intrinsic result
func tryEvalIntrinsic(inner string, p PropertyBag) (string, bool, bool) {
	trimmed := strings.TrimSpace(inner)
	if len(trimmed) < 2 || trimmed[0] != '[' {
		return "", false, false
	}
	closeBracket := strings.IndexByte(trimmed, ']')
	if closeBracket <= 1 {
		return "", false, false
	}
	typeName := strings.TrimSpace(trimmed[1:closeBracket])
	rest := trimmed[closeBracket+1:]
	if !strings.HasPrefix(rest, "::") {
		return "", false, false
	}
	rest = rest[2:]
	// Member name: identifier-like chars + '.' (for nested type members we don't
	// model; reject those). Split into name and optional arg paren group.
	nameEnd := 0
	for nameEnd < len(rest) {
		c := rest[nameEnd]
		if (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' {
			nameEnd++
			continue
		}
		break
	}
	if nameEnd == 0 {
		return "", false, true
	}
	member := rest[:nameEnd]
	tail := rest[nameEnd:]
	hasArgs := false
	argsStr := ""
	if len(tail) > 0 && tail[0] == '(' {
		closeParen := findMatchingParenQuoteAware(tail, 0)
		if closeParen < 0 {
			return "", false, true
		}
		if strings.TrimSpace(tail[closeParen+1:]) != "" {
			// Trailing chain or junk after the call paren — unsupported.
			return "", false, true
		}
		hasArgs = true
		argsStr = tail[1:closeParen]
	} else if strings.TrimSpace(tail) != "" {
		// Trailing junk after the member name (e.g. chained `.Method()`) —
		// unsupported.
		return "", false, true
	}
	value, ok := EvalIntrinsic(typeName, member, argsStr, hasArgs, p)
	return value, ok, true
}

// emptyItemBag is used when expanding intrinsic args that should not depend
// on item state (the closed-world subset of conditions/values we accept does
// not put `@()` inside intrinsic args).
type emptyItemBag struct{}

func (emptyItemBag) Get(string) []*Item       { return nil }
func (emptyItemBag) AppendTo(string, []*Item) {}

