namespace TleReportingDashboard.Web.Services;

/// <summary>
/// Tiny expression language for dashboard calculated columns. Evaluates
/// formulas like <c>[Abandoned] / [Calls] * 100</c> against a per-row
/// dictionary of named numeric values supplied by the dashboard at render
/// time.
///
/// Grammar:
/// <code>
///   expr   = term (('+' | '-') term)*
///   term   = factor (('*' | '/') factor)*
///   factor = NUMBER | IDENT | '(' expr ')' | '-' factor
///   IDENT  = '[' &lt;non-bracket characters&gt; ']'
///   NUMBER = digits ('.' digits)? (('e' | 'E') ('+' | '-')? digits)?
/// </code>
///
/// Identifier resolution is the caller's job — pass a lookup function that
/// returns the value for a bracketed name. The evaluator treats unknown
/// names and divide-by-zero as "no value" (NaN) rather than throwing, so a
/// single broken row never crashes a tile render.
/// </summary>
public static class CalculatedColumnEvaluator
{
    /// <summary>
    /// Parses and evaluates <paramref name="formula"/> using
    /// <paramref name="resolve"/> to look up bracketed identifiers. Returns
    /// the computed double, or NaN when:
    ///   - The formula is empty / null.
    ///   - An identifier doesn't resolve.
    ///   - A division by zero or operation on NaN occurs.
    /// Throws <see cref="FormatException"/> for syntax errors so the
    /// authoring dialog can surface a parse error inline.
    /// </summary>
    public static double Evaluate(string? formula, Func<string, double?> resolve)
    {
        if (string.IsNullOrWhiteSpace(formula)) return double.NaN;
        var tokens = Tokenize(formula);
        var parser = new Parser(tokens, resolve);
        var result = parser.ParseExpression();
        parser.ExpectEnd();
        return result;
    }

    /// <summary>
    /// Validates a formula's syntax without needing a resolver. Returns
    /// (true, null) when the formula parses; (false, errorMessage) when
    /// it doesn't. Used by the editor dialog for live feedback.
    /// </summary>
    public static (bool Ok, string? Error) Validate(string? formula)
    {
        if (string.IsNullOrWhiteSpace(formula)) return (false, "Formula is required.");
        try
        {
            // Identifiers resolve to 1.0 during validation — we only care
            // about syntax, not whether the names are real.
            var tokens = Tokenize(formula);
            var parser = new Parser(tokens, _ => 1.0);
            parser.ParseExpression();
            parser.ExpectEnd();
            return (true, null);
        }
        catch (FormatException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Returns every distinct bracketed identifier found in the formula,
    /// in source order. Powers the autocomplete preview in the editor and
    /// the "what does this column reference?" tooltip.
    /// </summary>
    public static List<string> ExtractIdentifiers(string? formula)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(formula)) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Tokenize(formula))
        {
            if (token.Type == TokenType.Identifier && seen.Add(token.Text))
                result.Add(token.Text);
        }
        return result;
    }

    // ── Tokenizer ──────────────────────────────────────────────────────

    private enum TokenType { Number, Identifier, Plus, Minus, Star, Slash, LParen, RParen }

    private readonly record struct Token(TokenType Type, string Text, double NumberValue);

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '+') { tokens.Add(new Token(TokenType.Plus,   "+", 0)); i++; continue; }
            if (c == '-') { tokens.Add(new Token(TokenType.Minus,  "-", 0)); i++; continue; }
            if (c == '*') { tokens.Add(new Token(TokenType.Star,   "*", 0)); i++; continue; }
            if (c == '/') { tokens.Add(new Token(TokenType.Slash,  "/", 0)); i++; continue; }
            if (c == '(') { tokens.Add(new Token(TokenType.LParen, "(", 0)); i++; continue; }
            if (c == ')') { tokens.Add(new Token(TokenType.RParen, ")", 0)); i++; continue; }

            if (c == '[')
            {
                var end = s.IndexOf(']', i + 1);
                if (end < 0)
                    throw new FormatException($"Unclosed identifier starting at position {i}.");
                var name = s.Substring(i + 1, end - i - 1).Trim();
                if (name.Length == 0)
                    throw new FormatException($"Empty identifier at position {i}.");
                tokens.Add(new Token(TokenType.Identifier, name, 0));
                i = end + 1;
                continue;
            }

            if (char.IsDigit(c) || c == '.')
            {
                var start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                // Optional exponent: 1e3, 2.5E-1, etc.
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                {
                    i++;
                    if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                }
                var text = s[start..i];
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                    throw new FormatException($"Invalid number '{text}' at position {start}.");
                tokens.Add(new Token(TokenType.Number, text, value));
                continue;
            }

            throw new FormatException($"Unexpected character '{c}' at position {i}.");
        }
        return tokens;
    }

    // ── Parser ─────────────────────────────────────────────────────────

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly Func<string, double?> _resolve;
        private int _pos;

        public Parser(List<Token> tokens, Func<string, double?> resolve)
        {
            _tokens = tokens;
            _resolve = resolve;
        }

        public double ParseExpression() => ParseAdditive();

        // Lowest precedence: + and -, left-associative.
        private double ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (_pos < _tokens.Count && (_tokens[_pos].Type == TokenType.Plus || _tokens[_pos].Type == TokenType.Minus))
            {
                var op = _tokens[_pos++].Type;
                var right = ParseMultiplicative();
                left = op == TokenType.Plus ? left + right : left - right;
            }
            return left;
        }

        // Higher precedence: * and /, left-associative.
        private double ParseMultiplicative()
        {
            var left = ParseUnary();
            while (_pos < _tokens.Count && (_tokens[_pos].Type == TokenType.Star || _tokens[_pos].Type == TokenType.Slash))
            {
                var op = _tokens[_pos++].Type;
                var right = ParseUnary();
                if (op == TokenType.Star)
                {
                    left *= right;
                }
                else
                {
                    // Divide-by-zero returns NaN rather than throwing.
                    // Lets a "% Abandoned" column survive an empty group
                    // ([Calls] = 0) — the cell formats blank instead of
                    // crashing the whole tile render.
                    left = right == 0 ? double.NaN : left / right;
                }
            }
            return left;
        }

        // Unary minus: -[x] or --5. No unary plus (rarely useful, more
        // likely a typo).
        private double ParseUnary()
        {
            if (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Minus)
            {
                _pos++;
                return -ParseUnary();
            }
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            if (_pos >= _tokens.Count)
                throw new FormatException("Unexpected end of formula.");

            var token = _tokens[_pos];
            switch (token.Type)
            {
                case TokenType.Number:
                    _pos++;
                    return token.NumberValue;

                case TokenType.Identifier:
                    _pos++;
                    var resolved = _resolve(token.Text);
                    return resolved ?? double.NaN;

                case TokenType.LParen:
                    _pos++;
                    var inner = ParseAdditive();
                    if (_pos >= _tokens.Count || _tokens[_pos].Type != TokenType.RParen)
                        throw new FormatException("Missing closing parenthesis.");
                    _pos++;
                    return inner;

                default:
                    throw new FormatException($"Unexpected '{token.Text}' at token position {_pos}.");
            }
        }

        public void ExpectEnd()
        {
            if (_pos < _tokens.Count)
                throw new FormatException($"Trailing tokens after formula end (starting with '{_tokens[_pos].Text}').");
        }
    }
}
