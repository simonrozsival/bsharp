package runtime

import (
	"strings"
)

// EvalCondition evaluates an MSBuild Condition string against the supplied
// property and item bags. Supported Phase A grammar:
//
//	cond     := orExpr
//	orExpr   := andExpr ('Or' andExpr)*
//	andExpr  := notExpr ('And' notExpr)*
//	notExpr  := '!' notExpr | primary
//	primary  := '(' cond ')' | comparison | boolLit
//	comparison := operand ('==' | '!=') operand
//	operand  := stringLiteral | expansion
//
// Property functions, Exists(), HasTrailingSlash(), regex match operators,
// numeric < > <= >=, etc. are all NOT supported in Phase A. Encountering one
// makes EvalCondition return (false, false) so the emitter can fall back to
// a stub for that target.
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
	condTokString  // quoted string literal (already unquoted)
	condTokBareExp // unquoted expansion-bearing operand (e.g. `$(X)`)
	condTokTrue
	condTokFalse
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
		case c == '\'' || c == '"':
			quote := c
			end := i + 1
			for end < len(s) && s[end] != quote {
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
			// Try to read a keyword (And/Or/True/False) or reject.
			word, n := readBareWord(s[i:])
			if n == 0 {
				return nil, false
			}
			switch strings.ToLower(word) {
			case "and":
				out = append(out, condToken{kind: condTokAnd})
			case "or":
				out = append(out, condToken{kind: condTokOr})
			case "true":
				out = append(out, condToken{kind: condTokTrue})
			case "false":
				out = append(out, condToken{kind: condTokFalse})
			default:
				// Anything else (Exists, HasTrailingSlash, MSBuild::Foo, numbers
				// outside quotes, identifiers in comparison position) → unsupported.
				return nil, false
			}
			i += n
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
	case condTokString, condTokBareExp:
		left, ok := cp.parseOperand()
		if !ok {
			return false, false
		}
		switch cp.peek() {
		case condTokEq, condTokNeq:
			op := cp.tokens[cp.pos].kind
			cp.pos++
			right, rok := cp.parseOperand()
			if !rok {
				return false, false
			}
			if op == condTokEq {
				return strings.EqualFold(left, right), true
			}
			return !strings.EqualFold(left, right), true
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
	}
	return "", false
}
