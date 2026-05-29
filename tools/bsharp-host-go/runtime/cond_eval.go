package runtime

import (
	"os"
	"path/filepath"
	"strings"
)

// ProjectDir is the absolute path to the project directory. Emitted main()
// sets this before invoking targets so Exists() and other relative-path
// helpers resolve against the project root rather than the process cwd.
var ProjectDir string

// EvalCondition evaluates an MSBuild Condition string against the supplied
// property and item bags. Supported grammar (Phase A + B + G):
//
//	cond     := orExpr
//	orExpr   := andExpr ('Or' andExpr)*
//	andExpr  := notExpr ('And' notExpr)*
//	notExpr  := '!' notExpr | primary
//	primary  := '(' cond ')' | call | comparison | boolLit
//	call     := ('Exists' | 'HasTrailingSlash') '(' operand ')'
//	comparison := operand ('==' | '!=' | '<' | '>' | '<=' | '>=') operand
//	operand  := stringLiteral | expansion
//
// Property functions of the supported subset (see expand.go), Exists/
// HasTrailingSlash calls, and `[MSBuild]::F(args)` intrinsics (Version*,
// IsOSPlatform, ValueOrDefault, Escape — see msbuild_intrinsics.go) work
// because operands flow through Expand. Numeric/version `<`, `>`, `<=`, `>=`
// promote through NumericCompare (int → version → case-insensitive string).
// Regex match operators and other intrinsics are NOT supported.
//
// Empty condition is treated as true (matches MSBuild semantics).
func EvalCondition(condition string, p PropertyBag, i ItemBag) (result bool, ok bool) {
	cond := strings.TrimSpace(condition)
	if cond == "" {
		return true, true
	}
	tokens, tokOk := tokenizeCondition(cond)
	if !tokOk {
		return false, false
	}
	parser := &condParser{tokens: tokens, p: p, i: i}
	res, parseOk := parser.parseOr()
	if !parseOk || parser.pos != len(parser.tokens) {
		return false, false
	}
	return res, true
}

// MustEvalCondition is the panicking convenience wrapper for emitter-classified
// simple conditions.
func MustEvalCondition(condition string, p PropertyBag, i ItemBag) bool {
	res, ok := EvalCondition(condition, p, i)
	if !ok {
		panic("MustEvalCondition: unsupported: " + condition)
	}
	return res
}

type condTokenKind int

const (
	condTokEnd condTokenKind = iota
	condTokLParen
	condTokRParen
	condTokAnd
	condTokOr
	condTokNot
	condTokEq
	condTokNeq
	condTokLt
	condTokGt
	condTokLe
	condTokGe
	condTokString  // quoted string literal (already unquoted)
	condTokBareExp // unquoted expansion-bearing operand (e.g. `$(X)`)
	condTokTrue
	condTokFalse
	condTokCall // function call e.g. Exists(arg) or HasTrailingSlash(arg)
)

type condToken struct {
	kind condTokenKind
	text string
}

// tokenizeCondition splits the condition into a flat token stream. Returns
// ok=false on any input we can't tokenize cleanly (unmatched quotes, bare
// numeric operators, etc.).
func tokenizeCondition(s string) ([]condToken, bool) {
	var out []condToken
	for i := 0; i < len(s); {
		c := s[i]
		switch {
		case c == ' ' || c == '\t' || c == '\n' || c == '\r':
			i++
		case c == '(':
			out = append(out, condToken{kind: condTokLParen})
			i++
		case c == ')':
			out = append(out, condToken{kind: condTokRParen})
			i++
		case c == '!':
			if i+1 < len(s) && s[i+1] == '=' {
				out = append(out, condToken{kind: condTokNeq})
				i += 2
			} else {
				out = append(out, condToken{kind: condTokNot})
				i++
			}
		case c == '=' && i+1 < len(s) && s[i+1] == '=':
			out = append(out, condToken{kind: condTokEq})
			i += 2
		case c == '<':
			if i+1 < len(s) && s[i+1] == '=' {
				out = append(out, condToken{kind: condTokLe})
				i += 2
			} else {
				out = append(out, condToken{kind: condTokLt})
				i++
			}
		case c == '>':
			if i+1 < len(s) && s[i+1] == '=' {
				out = append(out, condToken{kind: condTokGe})
				i += 2
			} else {
				out = append(out, condToken{kind: condTokGt})
				i++
			}
		case c == '\'' || c == '"':
			quote := c
			end := i + 1
			for end < len(s) {
				if s[end] == quote {
					break
				}
				// MSBuild treats `$(...)`, `@(...)`, `%(...)` inside a quoted
				// string as expansion constructs whose contents (including
				// nested single quotes used as function-call arg delimiters)
				// are NOT string terminators. Scan past the matching close
				// paren so e.g. `'@(X->F('a', 'b'))' == ''` tokenizes as
				// one string operand, not three.
				if (s[end] == '$' || s[end] == '@' || s[end] == '%') &&
					end+1 < len(s) && s[end+1] == '(' {
					closeIdx := findMatchingParenQuoteAware(s, end+1)
					if closeIdx < 0 {
						return nil, false
					}
					end = closeIdx + 1
					continue
				}
				end++
			}
			if end >= len(s) {
				return nil, false
			}
			out = append(out, condToken{kind: condTokString, text: s[i+1 : end]})
			i = end + 1
		case c == '$' || c == '@' || c == '%':
			// Capture the whole expansion as one bare token. Find the matching
			// paren; if it doesn't start with `$(`/`@(`/`%(`, treat as literal.
			if i+1 < len(s) && s[i+1] == '(' {
				end := findMatchingParen(s, i+1)
				if end < 0 {
					return nil, false
				}
				out = append(out, condToken{kind: condTokBareExp, text: s[i : end+1]})
				i = end + 1
			} else {
				return nil, false
			}
		default:
			// Bare numeric literals — common pattern in SDK conditions like
			// `@(X->Count()) > 0`. Treat as a quoted-string token so the
			// existing comparison/equality logic kicks in (NumericCompare
			// for </> and string-equality for ==/!=).
			if c >= '0' && c <= '9' {
				end := i
				for end < len(s) && ((s[end] >= '0' && s[end] <= '9') || s[end] == '.') {
					end++
				}
				out = append(out, condToken{kind: condTokString, text: s[i:end]})
				i = end
				continue
			}
			// Try to read a keyword (And/Or/True/False) or function call name.
			word, n := readBareWord(s[i:])
			if n == 0 {
				return nil, false
			}
			switch strings.ToLower(word) {
			case "and":
				out = append(out, condToken{kind: condTokAnd})
				i += n
			case "or":
				out = append(out, condToken{kind: condTokOr})
				i += n
			case "true":
				out = append(out, condToken{kind: condTokTrue})
				i += n
			case "false":
				out = append(out, condToken{kind: condTokFalse})
				i += n
			case "exists", "hastrailingslash":
				// Must be followed by '(' (skipping whitespace) to be a call.
				rest := i + n
				for rest < len(s) && (s[rest] == ' ' || s[rest] == '\t') {
					rest++
				}
				if rest >= len(s) || s[rest] != '(' {
					return nil, false
				}
				closeIdx := findMatchingParenQuoteAware(s, rest)
				if closeIdx < 0 {
					return nil, false
				}
				// Pack the call as "fnname(args)" — the parser will split it.
				out = append(out, condToken{kind: condTokCall, text: word + s[rest : closeIdx+1]})
				i = closeIdx + 1
			default:
				// Anything else (MSBuild::Foo, numbers outside quotes, etc.) → unsupported.
				return nil, false
			}
		}
	}
	return out, true
}

// readBareWord consumes one identifier-like run, returning the run and its
// byte length. Returns ("", 0) if the first byte isn't an identifier start.
func readBareWord(s string) (string, int) {
	if len(s) == 0 {
		return "", 0
	}
	c := s[0]
	if !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_') {
		return "", 0
	}
	n := 1
	for n < len(s) {
		c := s[n]
		if (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' {
			n++
			continue
		}
		break
	}
	return s[:n], n
}

type condParser struct {
	tokens []condToken
	pos    int
	p      PropertyBag
	i      ItemBag
}

func (cp *condParser) peek() condTokenKind {
	if cp.pos >= len(cp.tokens) {
		return condTokEnd
	}
	return cp.tokens[cp.pos].kind
}

func (cp *condParser) parseOr() (bool, bool) {
	left, ok := cp.parseAnd()
	if !ok {
		return false, false
	}
	for cp.peek() == condTokOr {
		cp.pos++
		right, rok := cp.parseAnd()
		if !rok {
			return false, false
		}
		left = left || right
	}
	return left, true
}

func (cp *condParser) parseAnd() (bool, bool) {
	left, ok := cp.parseNot()
	if !ok {
		return false, false
	}
	for cp.peek() == condTokAnd {
		cp.pos++
		right, rok := cp.parseNot()
		if !rok {
			return false, false
		}
		left = left && right
	}
	return left, true
}

func (cp *condParser) parseNot() (bool, bool) {
	if cp.peek() == condTokNot {
		cp.pos++
		v, ok := cp.parseNot()
		if !ok {
			return false, false
		}
		return !v, true
	}
	return cp.parsePrimary()
}

func (cp *condParser) parsePrimary() (bool, bool) {
	switch cp.peek() {
	case condTokLParen:
		cp.pos++
		v, ok := cp.parseOr()
		if !ok || cp.peek() != condTokRParen {
			return false, false
		}
		cp.pos++
		return v, true
	case condTokTrue:
		cp.pos++
		return true, true
	case condTokFalse:
		cp.pos++
		return false, true
	case condTokCall:
		t := cp.tokens[cp.pos]
		cp.pos++
		left, ok := evalCondCall(t.text, cp.p, cp.i)
		if !ok {
			return false, false
		}
		// A call result can be compared to a string (rare but allowed) or
		// treated as a bool literal.
		switch cp.peek() {
		case condTokEq, condTokNeq, condTokLt, condTokGt, condTokLe, condTokGe:
			return cp.finishComparison(left)
		}
		switch strings.ToLower(strings.TrimSpace(left)) {
		case "true", "1":
			return true, true
		case "", "false", "0":
			return false, true
		}
		return false, false
	case condTokString, condTokBareExp:
		left, ok := cp.parseOperand()
		if !ok {
			return false, false
		}
		switch cp.peek() {
		case condTokEq, condTokNeq, condTokLt, condTokGt, condTokLe, condTokGe:
			return cp.finishComparison(left)
		}
		// Bare operand in primary position → treat as bool literal after
		// expansion (matches MSBuild). 'true'/'1' truthy, anything else false.
		switch strings.ToLower(strings.TrimSpace(left)) {
		case "true", "1":
			return true, true
		case "", "false", "0":
			return false, true
		}
		// Non-boolean bare operand is not supported.
		return false, false
	}
	return false, false
}

// finishComparison consumes an operator token (already known to be a
// comparison kind) and a right-hand operand, then returns the boolean result.
// `==` and `!=` are case-insensitive string equality (matches MSBuild). The
// numeric / version operators (`<`, `>`, `<=`, `>=`) use NumericCompare
// which promotes integer then version then case-insensitive string.
func (cp *condParser) finishComparison(left string) (bool, bool) {
	op := cp.tokens[cp.pos].kind
	cp.pos++
	right, rok := cp.parseOperand()
	if !rok {
		return false, false
	}
	switch op {
	case condTokEq:
		return strings.EqualFold(left, right), true
	case condTokNeq:
		return !strings.EqualFold(left, right), true
	case condTokLt:
		return NumericCompare(left, right) < 0, true
	case condTokGt:
		return NumericCompare(left, right) > 0, true
	case condTokLe:
		return NumericCompare(left, right) <= 0, true
	case condTokGe:
		return NumericCompare(left, right) >= 0, true
	}
	return false, false
}

// evalCondCall evaluates a single "fnname(args)" condition call. The token
// text is the literal source, e.g. `Exists('$(X)')` or `HasTrailingSlash($(Y))`.
// Arguments may contain item refs (`@(X)`); for Exists this is expanded to a
// semicolon-joined path list and returns True if ANY path exists (matches
// MSBuild's IntrinsicFunctions.Exists behavior). Metadata refs (`%(...)`)
// are batching constructs and remain unsupported.
func evalCondCall(src string, p PropertyBag, i ItemBag) (string, bool) {
	openIdx := strings.IndexByte(src, '(')
	if openIdx < 0 {
		return "", false
	}
	closeIdx := findMatchingParenQuoteAware(src, openIdx)
	if closeIdx != len(src)-1 {
		return "", false
	}
	fn := strings.ToLower(strings.TrimSpace(src[:openIdx]))
	argSrc := strings.TrimSpace(src[openIdx+1 : closeIdx])
	// Metadata refs are batching and remain unsupported.
	if strings.Contains(argSrc, "%(") {
		return "", false
	}
	arg, ok := readSingleOperand(argSrc, p, i)
	if !ok {
		return "", false
	}
	switch fn {
	case "exists":
		if arg == "" {
			return "False", true
		}
		// MSBuild semantics: if the expanded arg contains ';' (an item-list
		// expansion or property whose value contains separators), test each
		// entry and return True if ANY of them exists.
		paths := []string{arg}
		if strings.Contains(arg, ";") {
			paths = SplitSemicolon(arg)
		}
		for _, path := range paths {
			if path == "" {
				continue
			}
			if !filepath.IsAbs(path) && ProjectDir != "" {
				path = filepath.Join(ProjectDir, path)
			}
			if _, err := os.Stat(path); err == nil {
				return "True", true
			}
		}
		return "False", true
	case "hastrailingslash":
		if strings.HasSuffix(arg, "/") || strings.HasSuffix(arg, "\\") {
			return "True", true
		}
		return "False", true
	}
	return "", false
}

// readSingleOperand parses one operand for a condition function argument.
// Accepts a single quoted string literal (with $(X) / %(Y) expansion inside),
// or a single bare expansion like `$(Foo)` (NOT item or batch refs).
func readSingleOperand(s string, p PropertyBag, i ItemBag) (string, bool) {
	s = strings.TrimSpace(s)
	if s == "" {
		return "", true
	}
	if (s[0] == '\'' || s[0] == '"') && len(s) >= 2 && s[len(s)-1] == s[0] {
		inner := s[1 : len(s)-1]
		return Expand(inner, p, i, nil)
	}
	if strings.HasPrefix(s, "$(") {
		return Expand(s, p, i, nil)
	}
	return "", false
}

func (cp *condParser) parseOperand() (string, bool) {
	switch cp.peek() {
	case condTokString:
		t := cp.tokens[cp.pos]
		cp.pos++
		expanded, ok := Expand(t.text, cp.p, cp.i, nil)
		return expanded, ok
	case condTokBareExp:
		t := cp.tokens[cp.pos]
		cp.pos++
		expanded, ok := Expand(t.text, cp.p, cp.i, nil)
		return expanded, ok
	case condTokTrue:
		cp.pos++
		return "True", true
	case condTokFalse:
		cp.pos++
		return "False", true
	}
	return "", false
}
