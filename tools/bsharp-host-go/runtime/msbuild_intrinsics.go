package runtime

import (
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"sync"
)

// EvalIntrinsic evaluates a static method/property call from a supported
// namespace. Recognized prefixes (case-insensitive on the type name; method
// name is also case-insensitive):
//
//   - [MSBuild]::F(args) — see msbuildIntrinsic
//   - [System.IO.Path]::F(args) or [System.IO.Path]::Property — see pathIntrinsic
//   - [System.Text.RegularExpressions.Regex]::F(args) — see regexIntrinsic
//
// Returns (value, ok). ok=false signals an unsupported intrinsic or argument
// shape. The classifier mirrors this whitelist; new intrinsics must be added
// in both places.
func EvalIntrinsic(typeName, member, argsStr string, hasArgs bool, p PropertyBag) (string, bool) {
	return EvalIntrinsicWithMeta(typeName, member, argsStr, hasArgs, p, nil)
}

// EvalIntrinsicWithMeta is the batch-aware variant. `meta` provides the
// per-source-item metadata bag so %(...) references inside intrinsic args
// can expand against it. When meta is nil, %(...) refs fail loudly via
// Expand.
func EvalIntrinsicWithMeta(typeName, member, argsStr string, hasArgs bool, p PropertyBag, meta map[string]string) (string, bool) {
	switch strings.ToLower(typeName) {
	case "msbuild":
		if !hasArgs {
			return "", false
		}
		return msbuildIntrinsic(member, argsStr, p, meta)
	case "system.io.path":
		return pathIntrinsic(member, argsStr, hasArgs, p, meta)
	case "system.text.regularexpressions.regex":
		if !hasArgs {
			return "", false
		}
		return regexIntrinsic(member, argsStr, p, meta)
	case "system.guid":
		if !hasArgs {
			return "", false
		}
		return guidIntrinsic(member, argsStr, p, meta)
	}
	return "", false
}

// EvalMSBuildIntrinsic preserves the older API for tests; new callers should
// use EvalIntrinsic.
func EvalMSBuildIntrinsic(name, argsStr string, p PropertyBag) (string, bool) {
	return msbuildIntrinsic(name, argsStr, p, nil)
}

func msbuildIntrinsic(name, argsStr string, p PropertyBag, meta map[string]string) (string, bool) {
	args, ok := splitIntrinsicArgs(argsStr, p, meta)
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
	case "unescape":
		if len(args) != 1 {
			return "", false
		}
		return msbuildUnescape(args[0]), true
	case "ensuretrailingslash":
		if len(args) != 1 {
			return "", false
		}
		return ensureTrailingSlash(args[0]), true
	case "makerelative":
		if len(args) != 2 {
			return "", false
		}
		return makeRelative(args[0], args[1]), true
	case "normalizepath":
		if len(args) == 0 {
			return "", false
		}
		return normalizePathJoin(args, false), true
	case "normalizedirectory":
		if len(args) == 0 {
			return "", false
		}
		return normalizePathJoin(args, true), true
	case "doestaskhostexist":
		// In MSBuild, DoesTaskHostExist asks the build engine whether a
		// task-host process is available for a specific (Runtime,
		// Architecture) pair. We always run tasks in-process via
		// bsharp-taskd in the closed-world host, so every supported pair
		// is "available". Returning True unconditionally matches the
		// semantics the SDK conditions are gating on (they use this to
		// avoid emitting fallback warnings when the task host exists).
		return "True", true
	case "arefeaturesenabled":
		// Match the C# emitter's optimistic closed-world assumption: SDK
		// change-wave probes are treated as enabled.
		return "True", true
	case "istargetframeworkcompatible":
		// `$([MSBuild]::IsTargetFrameworkCompatible(currentTfm, targetTfm))`
		// → True iff `currentTfm` can consume libraries built for
		// `targetTfm`. We already model the .NET 5+/netstandard/netcoreapp
		// compatibility table in IsTargetFrameworkCompatible for cond_eval;
		// expose it here too so SDK conditions checking compatibility (e.g.
		// AOT TFM gates) evaluate consistently.
		if len(args) != 2 {
			return "", false
		}
		return boolToMSBuild(IsTargetFrameworkCompatible(args[0], args[1])), true
	}
	return "", false
}

// normalizeSlashes converts MSBuild-style mixed `/` and `\` paths to the
// host-native separator so Go's `path/filepath` operations behave the same
// regardless of input slash style. On Windows this is a no-op; on Unix it
// converts backslashes to forward slashes.
func normalizeSlashes(s string) string {
	if filepath.Separator == '\\' {
		return s
	}
	return strings.ReplaceAll(s, "\\", "/")
}

// ensureTrailingSlash matches `[MSBuild]::EnsureTrailingSlash`. Returns the
// input with a single trailing path separator appended if it doesn't already
// end with one (either `/` or `\\`). Empty input yields empty output.
func ensureTrailingSlash(s string) string {
	if s == "" {
		return s
	}
	last := s[len(s)-1]
	if last == '/' || last == '\\' {
		return s
	}
	return s + string(filepath.Separator)
}

// makeRelative matches `[MSBuild]::MakeRelative(base, path)`. Returns `path`
// relative to `base`. If the relative computation fails, returns `path`
// unchanged (MSBuild's defensive fallback).
func makeRelative(base, path string) string {
	if base == "" || path == "" {
		return path
	}
	base = normalizeSlashes(base)
	path = normalizeSlashes(path)
	rel, err := filepath.Rel(base, path)
	if err != nil {
		return path
	}
	return rel
}

// normalizePathJoin matches `[MSBuild]::NormalizePath(...args)` and
// `[MSBuild]::NormalizeDirectory(...args)`. Joins all args as path segments
// and resolves `.`/`..`/duplicate separators. When `dir` is true, appends a
// trailing separator (NormalizeDirectory semantics).
func normalizePathJoin(args []string, dir bool) string {
	parts := make([]string, 0, len(args))
	for _, a := range args {
		parts = append(parts, normalizeSlashes(a))
	}
	joined := filepath.Join(parts...)
	abs, err := filepath.Abs(joined)
	if err != nil {
		abs = joined
	}
	if dir && abs != "" {
		return ensureTrailingSlash(abs)
	}
	return abs
}

// pathIntrinsic handles [System.IO.Path]::* members.
func pathIntrinsic(name, argsStr string, hasArgs bool, p PropertyBag, meta map[string]string) (string, bool) {
	lower := strings.ToLower(name)
	// "Parameterless" intrinsics may be invoked as `Name` (no parens) or
	// as `Name()` (empty parens). Route both shapes through the same
	// switch.
	if !hasArgs || strings.TrimSpace(argsStr) == "" {
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
		case "gettemppath":
			// .NET returns the OS temp directory with a trailing separator.
			// os.TempDir() does not guarantee a trailing separator.
			tp := os.TempDir()
			return ensureTrailingSlash(tp), true
		case "getrandomfilename":
			// .NET returns an 11-char cryptographically-random string with
			// a dot-extension (e.g. "abc12345.xyz"). We just need a unique
			// filename-safe token; build runs use it for tmp dir names.
			var buf [6]byte
			if _, err := rand.Read(buf[:]); err != nil {
				return "", false
			}
			s := strings.ToLower(hex.EncodeToString(buf[:]))
			return s[:8] + "." + s[8:11], true
		}
		if !hasArgs {
			return "", false
		}
		// Fall through: hasArgs=true but argsStr is empty — the parameterized
		// dispatch below will reject with len(args) != N for any required-arg
		// member, which is the correct loud-failure.
	}
	args, ok := splitIntrinsicArgs(argsStr, p, meta)
	if !ok {
		return "", false
	}
	// .NET's System.IO.Path treats both '/' and '\\' as path separators
	// regardless of host OS. Normalize so that Go's `path/filepath` (which
	// only recognizes the OS-native separator on Unix) behaves consistently
	// for MSBuild-style mixed-slash inputs like `$(D)\src\file.cs`.
	for i := range args {
		args[i] = normalizeSlashes(args[i])
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

func msbuildUnescape(s string) string {
	if s == "" {
		return ""
	}
	var sb strings.Builder
	sb.Grow(len(s))
	for i := 0; i < len(s); i++ {
		if s[i] == '%' && i+2 < len(s) {
			if hi, ok1 := fromHex(s[i+1]); ok1 {
				if lo, ok2 := fromHex(s[i+2]); ok2 {
					sb.WriteByte(hi<<4 | lo)
					i += 2
					continue
				}
			}
		}
		sb.WriteByte(s[i])
	}
	return sb.String()
}

func fromHex(c byte) (byte, bool) {
	switch {
	case c >= '0' && c <= '9':
		return c - '0', true
	case c >= 'a' && c <= 'f':
		return c - 'a' + 10, true
	case c >= 'A' && c <= 'F':
		return c - 'A' + 10, true
	default:
		return 0, false
	}
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
func splitIntrinsicArgs(s string, p PropertyBag, meta map[string]string) ([]string, bool) {
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
		v, ok := resolveIntrinsicArg(strings.TrimSpace(raw), p, meta)
		if !ok {
			return nil, false
		}
		out = append(out, v)
	}
	return out, true
}

func resolveIntrinsicArg(arg string, p PropertyBag, meta map[string]string) (string, bool) {
	if arg == "" {
		return "", true
	}
	// Accept backtick quoting (MSBuild allows it for callsite quoting).
	if (arg[0] == '\'' || arg[0] == '"' || arg[0] == '`') && len(arg) >= 2 && arg[len(arg)-1] == arg[0] {
		inner := arg[1 : len(arg)-1]
		expanded, ok := Expand(inner, p, emptyItemBag{}, meta)
		if !ok {
			return "", false
		}
		return expanded, true
	}
	// Unquoted args may contain `$(...)` expansions concatenated with literal
	// text (e.g. `$(_RuntimeSymbolsDir)\$(CrossgenSubOutputPath)`). Expand
	// against the property bag — Expand handles plain literals correctly.
	if strings.ContainsAny(arg, "$@%") {
		expanded, ok := Expand(arg, p, emptyItemBag{}, meta)
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
func tryEvalIntrinsic(inner string, p PropertyBag, meta map[string]string) (string, bool, bool) {
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
	value, ok := EvalIntrinsicWithMeta(typeName, member, argsStr, hasArgs, p, meta)
	return value, ok, true
}

// emptyItemBag is used when expanding intrinsic args that should not depend
// on item state (the closed-world subset of conditions/values we accept does
// not put `@()` inside intrinsic args).
type emptyItemBag struct{}

func (emptyItemBag) Get(string) []*Item       { return nil }
func (emptyItemBag) AppendTo(string, []*Item) {}

// regexCache memoizes compiled regular expressions across an entire build
// run. MSBuild evaluates `Regex::Replace` once per target invocation; for
// repeated targets the same pattern would otherwise be re-parsed. .NET regex
// syntax and Go's RE2 differ in lookarounds/backreferences/named groups but
// the patterns the SDK uses (`-(.)*`, `(\.0)*$`, simple literal escapes)
// translate verbatim. If the pattern fails to compile, we return ok=false so
// the runtime panics loudly rather than silently producing a wrong result.
var (
	regexCacheMu sync.Mutex
	regexCache   = map[string]*regexp.Regexp{}
)

func compileRegex(pattern string) (*regexp.Regexp, bool) {
	regexCacheMu.Lock()
	defer regexCacheMu.Unlock()
	if r, ok := regexCache[pattern]; ok {
		return r, true
	}
	r, err := regexp.Compile(pattern)
	if err != nil {
		return nil, false
	}
	regexCache[pattern] = r
	return r, true
}

// regexIntrinsic handles [System.Text.RegularExpressions.Regex]::* members.
// Only the static methods are supported (no Regex object construction).
//
//	Replace(input, pattern, replacement) — returns input with every match of
//	  pattern replaced by replacement. The replacement string uses Go's
//	  expansion semantics (`$1`, `${1}`, `$$`). Compatible with most SDK
//	  patterns; complex .NET-specific syntax is rejected via compile failure.
//	IsMatch(input, pattern) — returns "True"/"False" depending on whether
//	  pattern matches anywhere in input.
func regexIntrinsic(name, argsStr string, p PropertyBag, meta map[string]string) (string, bool) {
	args, ok := splitIntrinsicArgs(argsStr, p, meta)
	if !ok {
		return "", false
	}
	switch strings.ToLower(name) {
	case "replace":
		if len(args) != 3 {
			return "", false
		}
		re, ok := compileRegex(args[1])
		if !ok {
			return "", false
		}
		return re.ReplaceAllString(args[0], args[2]), true
	case "ismatch":
		if len(args) != 2 {
			return "", false
		}
		re, ok := compileRegex(args[1])
		if !ok {
			return "", false
		}
		return boolToMSBuild(re.MatchString(args[0])), true
	case "match":
		// `Regex.Match(input, pattern)` returns a System.Text.RegularExpressions.Match
		// object. When MSBuild renders the result of an intrinsic call as a
		// property value, it coerces non-string returns via ToString(), and
		// `Match.ToString()` returns the matched substring (or the empty
		// string when the match was unsuccessful). This is the contract
		// SDK targets like _FindMachOToolchain rely on
		// (`$([System.Text.RegularExpressions.Regex]::Match($(X), '[1-9]\d*'))`).
		if len(args) != 2 {
			return "", false
		}
		re, ok := compileRegex(args[1])
		if !ok {
			return "", false
		}
		m := re.FindString(args[0])
		return m, true
	}
	return "", false
}


// guidIntrinsic handles [System.Guid]::* members. Currently only NewGuid()
// is supported -- the SDK calls it to mint a unique identity for an ad-hoc
// item in a few _RestoreGraphEntry-style ItemGroups. Returns a lowercase
// hyphenated UUID string matching .NET's Guid.NewGuid().ToString() format.
func guidIntrinsic(name, argsStr string, p PropertyBag, meta map[string]string) (string, bool) {
	switch strings.ToLower(name) {
	case "newguid":
		if strings.TrimSpace(argsStr) != "" {
			return "", false
		}
		var b [16]byte
		if _, err := rand.Read(b[:]); err != nil {
			return "", false
		}
		// RFC 4122 v4 variant bits, matching .NET's behavior.
		b[6] = (b[6] & 0x0f) | 0x40
		b[8] = (b[8] & 0x3f) | 0x80
		return fmt.Sprintf("%08x-%04x-%04x-%04x-%012x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16]), true
	}
	return "", false
}
