using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using IZLang.Diagnostics;

namespace IZLang.Lexing
{
    /// <summary>
    /// Turns text into tokens. Never throws on invalid input: errors become
    /// diagnostics and the lexer keeps going, so it reports as many problems as
    /// possible in a single pass.
    /// </summary>
    public sealed class Lexer
    {
        private static readonly Dictionary<string, TokenKind> Keywords =
            new Dictionary<string, TokenKind>(StringComparer.Ordinal)
            {
                ["var"] = TokenKind.KwVar,
                ["const"] = TokenKind.KwConst,
                ["device"] = TokenKind.KwDevice,
                ["fn"] = TokenKind.KwFn,
                ["return"] = TokenKind.KwReturn,
                ["struct"] = TokenKind.KwStruct,
                ["if"] = TokenKind.KwIf,
                ["else"] = TokenKind.KwElse,
                ["while"] = TokenKind.KwWhile,
                ["loop"] = TokenKind.KwLoop,
                ["for"] = TokenKind.KwFor,
                ["in"] = TokenKind.KwIn,
                ["break"] = TokenKind.KwBreak,
                ["continue"] = TokenKind.KwContinue,
                ["yield"] = TokenKind.KwYield,
                ["true"] = TokenKind.KwTrue,
                ["false"] = TokenKind.KwFalse,
                ["num"] = TokenKind.KwNum,
                ["bool"] = TokenKind.KwBool,
                ["str"] = TokenKind.KwStr,
                ["dev"] = TokenKind.KwDev,
                ["all"] = TokenKind.KwAll,
                ["named"] = TokenKind.KwNamed,
            };

        private readonly string _text;
        private readonly DiagnosticBag _diagnostics;
        private int _position;

        public Lexer(string text, DiagnosticBag diagnostics)
        {
            _text = text ?? throw new ArgumentNullException(nameof(text));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        private char Current => Peek(0);
        private char Lookahead => Peek(1);

        private char Peek(int offset)
        {
            int index = _position + offset;
            return index < _text.Length ? _text[index] : '\0';
        }

        public List<Token> Tokenize()
        {
            var tokens = new List<Token>();
            while (true)
            {
                var token = NextToken();
                tokens.Add(token);
                if (token.Kind == TokenKind.EndOfFile) break;
            }
            return tokens;
        }

        private Token NextToken()
        {
            SkipTrivia();

            int start = _position;
            if (_position >= _text.Length)
                return new Token(TokenKind.EndOfFile, new SourceSpan(start, 0), string.Empty);

            char c = Current;

            if (char.IsDigit(c) || (c == '.' && char.IsDigit(Lookahead)))
                return ReadNumber();

            if (IsIdentifierStart(c))
                return ReadIdentifierOrKeyword();

            if (c == '"')
                return ReadString();

            if (c == '#' && Lookahead == '"')
                return ReadHashLiteral();

            return ReadOperator();
        }

        private void SkipTrivia()
        {
            while (_position < _text.Length)
            {
                char c = Current;

                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    _position++;
                }
                else if (c == '/' && Lookahead == '/')
                {
                    while (_position < _text.Length && Current != '\n') _position++;
                }
                else if (c == '/' && Lookahead == '*')
                {
                    int commentStart = _position;
                    _position += 2;
                    int depth = 1;                       // block comments nest
                    while (_position < _text.Length && depth > 0)
                    {
                        if (Current == '/' && Lookahead == '*') { depth++; _position += 2; }
                        else if (Current == '*' && Lookahead == '/') { depth--; _position += 2; }
                        else _position++;
                    }
                    if (depth > 0)
                    {
                        _diagnostics.Report(IZErrorCode.UnterminatedBlockComment,
                            SourceSpan.FromBounds(commentStart, _text.Length),
                            "block comment '/*' was never closed with '*/'");
                    }
                }
                else
                {
                    break;
                }
            }
        }

        private static bool IsIdentifierStart(char c) =>
            c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        private static bool IsIdentifierPart(char c) =>
            IsIdentifierStart(c) || (c >= '0' && c <= '9');

        private Token ReadIdentifierOrKeyword()
        {
            int start = _position;
            while (_position < _text.Length && IsIdentifierPart(Current)) _position++;

            string text = _text.Substring(start, _position - start);
            var kind = Keywords.TryGetValue(text, out var kw) ? kw : TokenKind.Identifier;
            return new Token(kind, SourceSpan.FromBounds(start, _position), text);
        }

        private Token ReadNumber()
        {
            int start = _position;

            // 0x... and 0b... are integers; everything else is decimal with an optional exponent.
            if (Current == '0' && (Lookahead == 'x' || Lookahead == 'X'))
                return ReadRadix(start, 16, IsHexDigit, "hexadecimal");
            if (Current == '0' && (Lookahead == 'b' || Lookahead == 'B'))
                return ReadRadix(start, 2, IsBinaryDigit, "binary");

            while (_position < _text.Length && (char.IsDigit(Current) || Current == '_')) _position++;

            // '.' is only part of the number when a digit follows - otherwise it is the
            // member access operator, or the start of '..' in a range.
            if (Current == '.' && char.IsDigit(Lookahead))
            {
                _position++;
                while (_position < _text.Length && (char.IsDigit(Current) || Current == '_')) _position++;
            }

            if (Current == 'e' || Current == 'E')
            {
                int save = _position;
                _position++;
                if (Current == '+' || Current == '-') _position++;
                if (char.IsDigit(Current))
                {
                    while (_position < _text.Length && char.IsDigit(Current)) _position++;
                }
                else
                {
                    _position = save;     // a stray 'e' belongs to the next token
                }
            }

            var span = SourceSpan.FromBounds(start, _position);
            string text = _text.Substring(start, _position - start);
            string cleaned = text.Replace("_", string.Empty);

            if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                _diagnostics.Report(IZErrorCode.InvalidNumber, span, "'" + text + "' is not a valid number");
                value = 0.0;
            }
            return new Token(TokenKind.Number, span, text, value);
        }

        private static bool IsHexDigit(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private static bool IsBinaryDigit(char c) => c == '0' || c == '1';

        private Token ReadRadix(int start, int radix, Func<char, bool> isDigit, string label)
        {
            _position += 2;                              // consume '0x' / '0b'
            int digitsStart = _position;
            while (_position < _text.Length && (isDigit(Current) || Current == '_')) _position++;

            var span = SourceSpan.FromBounds(start, _position);
            string text = _text.Substring(start, _position - start);
            string digits = _text.Substring(digitsStart, _position - digitsStart).Replace("_", string.Empty);

            if (digits.Length == 0)
            {
                _diagnostics.Report(IZErrorCode.InvalidNumber, span, label + " literal has no digits");
                return new Token(TokenKind.Number, span, text, 0.0);
            }

            // Unsigned 64 bits; overflow is an error, not a silent wrap.
            ulong acc = 0;
            foreach (char d in digits)
            {
                ulong digitValue = (ulong)(char.IsDigit(d) ? d - '0' : char.ToLowerInvariant(d) - 'a' + 10);
                if (acc > (ulong.MaxValue - digitValue) / (ulong)radix)
                {
                    _diagnostics.Report(IZErrorCode.InvalidNumber, span,
                        label + " literal '" + text + "' does not fit in 64 bits");
                    return new Token(TokenKind.Number, span, text, 0.0);
                }
                acc = acc * (ulong)radix + digitValue;
            }
            return new Token(TokenKind.Number, span, text, (double)acc);
        }

        private Token ReadString()
        {
            int start = _position;
            _position++;                                 // consume the opening quote
            var sb = new StringBuilder();

            while (true)
            {
                if (_position >= _text.Length || Current == '\n' || Current == '\r')
                {
                    _diagnostics.Report(IZErrorCode.UnterminatedString,
                        SourceSpan.FromBounds(start, _position),
                        "string was never closed with a quote");
                    break;
                }
                if (Current == '"') { _position++; break; }

                if (Current == '\\')
                {
                    int escStart = _position;
                    _position++;
                    char esc = Current;
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); _position++; break;
                        case 't': sb.Append('\t'); _position++; break;
                        case 'r': sb.Append('\r'); _position++; break;
                        case '0': sb.Append('\0'); _position++; break;
                        case '\\': sb.Append('\\'); _position++; break;
                        case '"': sb.Append('"'); _position++; break;
                        default:
                            _diagnostics.Report(IZErrorCode.InvalidEscapeSequence,
                                SourceSpan.FromBounds(escStart, Math.Min(_position + 1, _text.Length)),
                                "escape '\\" + esc + "' does not exist; valid ones: \\n \\t \\r \\0 \\\\ \\\"");
                            _position++;
                            break;
                    }
                    continue;
                }

                if (Current > 0x7F)
                {
                    _diagnostics.Report(IZErrorCode.NonAsciiCharacter,
                        new SourceSpan(_position, 1),
                        "'" + Current + "' is not ASCII; the in-game chip only stores ASCII text");
                }
                sb.Append(Current);
                _position++;
            }

            var span = SourceSpan.FromBounds(start, _position);
            return new Token(TokenKind.String, span, _text.Substring(start, _position - start), sb.ToString());
        }

        private Token ReadHashLiteral()
        {
            int start = _position;
            _position++;                                 // consume '#'
            var inner = ReadString();
            var span = SourceSpan.FromBounds(start, _position);
            return new Token(TokenKind.HashLiteral, span,
                _text.Substring(start, _position - start), inner.StringValue);
        }

        private Token Make(TokenKind kind, int start) =>
            new Token(kind, SourceSpan.FromBounds(start, _position), _text.Substring(start, _position - start));

        private Token Single(TokenKind kind)
        {
            int start = _position;
            _position += 1;
            return Make(kind, start);
        }

        private Token Pair(TokenKind kind)
        {
            int start = _position;
            _position += 2;
            return Make(kind, start);
        }

        private Token Triple(TokenKind kind)
        {
            int start = _position;
            _position += 3;
            return Make(kind, start);
        }

        private Token ReadOperator()
        {
            char c = Current;
            char n = Lookahead;

            switch (c)
            {
                case '(': return Single(TokenKind.LParen);
                case ')': return Single(TokenKind.RParen);
                case '{': return Single(TokenKind.LBrace);
                case '}': return Single(TokenKind.RBrace);
                case '[': return Single(TokenKind.LBracket);
                case ']': return Single(TokenKind.RBracket);
                case ',': return Single(TokenKind.Comma);
                case ';': return Single(TokenKind.Semicolon);
                case ':': return Single(TokenKind.Colon);
                case '~': return Single(TokenKind.Tilde);
                case '^': return Single(TokenKind.Caret);

                case '.':
                    if (n == '.' && Peek(2) == '=') return Triple(TokenKind.DotDotEquals);
                    if (n == '.') return Pair(TokenKind.DotDot);
                    return Single(TokenKind.Dot);

                case '+': return n == '=' ? Pair(TokenKind.PlusEquals) : Single(TokenKind.Plus);
                case '*': return n == '=' ? Pair(TokenKind.StarEquals) : Single(TokenKind.Star);
                case '/': return n == '=' ? Pair(TokenKind.SlashEquals) : Single(TokenKind.Slash);
                case '%': return n == '=' ? Pair(TokenKind.PercentEquals) : Single(TokenKind.Percent);

                case '-':
                    if (n == '=') return Pair(TokenKind.MinusEquals);
                    if (n == '>') return Pair(TokenKind.Arrow);
                    return Single(TokenKind.Minus);

                case '&': return n == '&' ? Pair(TokenKind.AmpAmp) : Single(TokenKind.Amp);
                case '|': return n == '|' ? Pair(TokenKind.PipePipe) : Single(TokenKind.Pipe);
                case '!': return n == '=' ? Pair(TokenKind.BangEquals) : Single(TokenKind.Bang);
                case '=': return n == '=' ? Pair(TokenKind.EqualsEquals) : Single(TokenKind.Equals);

                case '<':
                    if (n == '<') return Pair(TokenKind.LessLess);
                    if (n == '=') return Pair(TokenKind.LessEquals);
                    return Single(TokenKind.Less);

                case '>':
                    if (n == '>') return Pair(TokenKind.GreaterGreater);
                    if (n == '=') return Pair(TokenKind.GreaterEquals);
                    return Single(TokenKind.Greater);
            }

            int badStart = _position;
            _position++;
            var badSpan = SourceSpan.FromBounds(badStart, _position);
            _diagnostics.Report(IZErrorCode.UnexpectedCharacter, badSpan,
                c > 0x7F
                    ? "non-ASCII character '" + c + "' outside a string or comment"
                    : "unexpected character '" + c + "'");
            return new Token(TokenKind.Bad, badSpan, _text.Substring(badStart, 1));
        }
    }
}
