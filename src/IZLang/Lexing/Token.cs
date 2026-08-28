using IZLang.Diagnostics;

namespace IZLang.Lexing
{
    public readonly struct Token
    {
        public readonly TokenKind Kind;
        public readonly SourceSpan Span;

        /// <summary>Text exactly as it appears in the source.</summary>
        public readonly string Text;

        /// <summary>Already decoded value: double for Number, string for String/HashLiteral.</summary>
        public readonly object? Value;

        public Token(TokenKind kind, SourceSpan span, string text, object? value = null)
        {
            Kind = kind;
            Span = span;
            Text = text;
            Value = value;
        }

        public double NumberValue => Value is double d ? d : 0.0;
        public string StringValue => Value as string ?? string.Empty;

        public override string ToString() => $"{Kind} '{Text}' {Span}";
    }

    public static class TokenKindExtensions
    {
        /// <summary>Human readable name used in error messages ("expected ')'").</summary>
        public static string Display(this TokenKind kind)
        {
            switch (kind)
            {
                case TokenKind.EndOfFile: return "end of file";
                case TokenKind.Number: return "number";
                case TokenKind.String: return "string";
                case TokenKind.HashLiteral: return "hash literal";
                case TokenKind.Identifier: return "identifier";

                case TokenKind.KwVar: return "'var'";
                case TokenKind.KwConst: return "'const'";
                case TokenKind.KwDevice: return "'device'";
                case TokenKind.KwFn: return "'fn'";
                case TokenKind.KwReturn: return "'return'";
                case TokenKind.KwStruct: return "'struct'";
                case TokenKind.KwIf: return "'if'";
                case TokenKind.KwElse: return "'else'";
                case TokenKind.KwWhile: return "'while'";
                case TokenKind.KwLoop: return "'loop'";
                case TokenKind.KwFor: return "'for'";
                case TokenKind.KwIn: return "'in'";
                case TokenKind.KwBreak: return "'break'";
                case TokenKind.KwContinue: return "'continue'";
                case TokenKind.KwYield: return "'yield'";
                case TokenKind.KwTrue: return "'true'";
                case TokenKind.KwFalse: return "'false'";
                case TokenKind.KwNum: return "'num'";
                case TokenKind.KwBool: return "'bool'";
                case TokenKind.KwStr: return "'str'";
                case TokenKind.KwDev: return "'dev'";
                case TokenKind.KwList: return "'list'";
                case TokenKind.KwAll: return "'all'";
                case TokenKind.KwNamed: return "'named'";

                case TokenKind.LParen: return "'('";
                case TokenKind.RParen: return "')'";
                case TokenKind.LBrace: return "'{'";
                case TokenKind.RBrace: return "'}'";
                case TokenKind.LBracket: return "'['";
                case TokenKind.RBracket: return "']'";
                case TokenKind.Comma: return "','";
                case TokenKind.Semicolon: return "';'";
                case TokenKind.Colon: return "':'";
                case TokenKind.Dot: return "'.'";
                case TokenKind.DotDot: return "'..'";
                case TokenKind.DotDotEquals: return "'..='";
                case TokenKind.Arrow: return "'->'";
                case TokenKind.FatArrow: return "'=>'";

                case TokenKind.Plus: return "'+'";
                case TokenKind.Minus: return "'-'";
                case TokenKind.Star: return "'*'";
                case TokenKind.Slash: return "'/'";
                case TokenKind.Percent: return "'%'";
                case TokenKind.Amp: return "'&'";
                case TokenKind.Pipe: return "'|'";
                case TokenKind.Caret: return "'^'";
                case TokenKind.Tilde: return "'~'";
                case TokenKind.AmpAmp: return "'&&'";
                case TokenKind.PipePipe: return "'||'";
                case TokenKind.Bang: return "'!'";
                case TokenKind.LessLess: return "'<<'";
                case TokenKind.GreaterGreater: return "'>>'";
                case TokenKind.Less: return "'<'";
                case TokenKind.LessEquals: return "'<='";
                case TokenKind.Greater: return "'>'";
                case TokenKind.GreaterEquals: return "'>='";
                case TokenKind.EqualsEquals: return "'=='";
                case TokenKind.BangEquals: return "'!='";

                case TokenKind.Equals: return "'='";
                case TokenKind.PlusEquals: return "'+='";
                case TokenKind.MinusEquals: return "'-='";
                case TokenKind.StarEquals: return "'*='";
                case TokenKind.SlashEquals: return "'/='";
                case TokenKind.PercentEquals: return "'%='";

                default: return kind.ToString();
            }
        }
    }
}
