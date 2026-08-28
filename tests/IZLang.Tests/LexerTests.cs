using System.Linq;
using IZLang.Diagnostics;
using IZLang.Lexing;
using Xunit;

namespace IZLang.Tests
{
    public class LexerTests
    {
        private static Token[] Lex(string text, out DiagnosticBag diagnostics)
        {
            diagnostics = new DiagnosticBag();
            return new Lexer(text, diagnostics).Tokenize().ToArray();
        }

        private static Token[] LexOk(string text)
        {
            var tokens = Lex(text, out var diagnostics);
            Assert.False(diagnostics.HasErrors,
                "did not expect a lexical error: " + string.Join("; ", diagnostics.Select(d => d.Message)));
            return tokens;
        }

        [Theory]
        [InlineData("0", 0.0)]
        [InlineData("42", 42.0)]
        [InlineData("3.5", 3.5)]
        [InlineData("101.325", 101.325)]
        [InlineData("1e3", 1000.0)]
        [InlineData("1.5e-2", 0.015)]
        [InlineData("1_000_000", 1000000.0)]
        [InlineData("0xFF", 255.0)]
        [InlineData("0b1011", 11.0)]
        public void ReadsNumbers(string text, double expected)
        {
            var tokens = LexOk(text);
            Assert.Equal(TokenKind.Number, tokens[0].Kind);
            Assert.Equal(expected, tokens[0].NumberValue, 10);
        }

        [Fact]
        public void ADotAfterANumberFollowedByALetterIsNotPartOfTheNumber()
        {
            // '1.max(2,3)' must lex as 1 . max ( ... - not as the number "1."
            var tokens = LexOk("1.max");
            Assert.Equal(TokenKind.Number, tokens[0].Kind);
            Assert.Equal(1.0, tokens[0].NumberValue);
            Assert.Equal(TokenKind.Dot, tokens[1].Kind);
            Assert.Equal(TokenKind.Identifier, tokens[2].Kind);
        }

        [Fact]
        public void ARangeIsNotConfusedWithADecimal()
        {
            var tokens = LexOk("0..10");
            Assert.Equal(TokenKind.Number, tokens[0].Kind);
            Assert.Equal(0.0, tokens[0].NumberValue);
            Assert.Equal(TokenKind.DotDot, tokens[1].Kind);
            Assert.Equal(10.0, tokens[2].NumberValue);
        }

        [Fact]
        public void InclusiveRange()
        {
            var tokens = LexOk("0..=10");
            Assert.Equal(TokenKind.DotDotEquals, tokens[1].Kind);
        }

        [Fact]
        public void AStrayExponentDoesNotSwallowTheNextToken()
        {
            // '2e' is not a valid number with an exponent: the 'e' must become an identifier.
            var tokens = LexOk("2e");
            Assert.Equal(TokenKind.Number, tokens[0].Kind);
            Assert.Equal(2.0, tokens[0].NumberValue);
            Assert.Equal(TokenKind.Identifier, tokens[1].Kind);
            Assert.Equal("e", tokens[1].Text);
        }

        [Fact]
        public void KeywordsAreNotIdentifiers()
        {
            var tokens = LexOk("if while fn var const device loop for in yield");
            Assert.Equal(TokenKind.KwIf, tokens[0].Kind);
            Assert.Equal(TokenKind.KwWhile, tokens[1].Kind);
            Assert.Equal(TokenKind.KwFn, tokens[2].Kind);
            Assert.Equal(TokenKind.KwVar, tokens[3].Kind);
            Assert.Equal(TokenKind.KwConst, tokens[4].Kind);
            Assert.Equal(TokenKind.KwDevice, tokens[5].Kind);
            Assert.Equal(TokenKind.KwLoop, tokens[6].Kind);
            Assert.Equal(TokenKind.KwFor, tokens[7].Kind);
            Assert.Equal(TokenKind.KwIn, tokens[8].Kind);
            Assert.Equal(TokenKind.KwYield, tokens[9].Kind);
        }

        [Fact]
        public void AnIdentifierStartingWithAKeywordStaysAnIdentifier()
        {
            var tokens = LexOk("iffy forward variable");
            Assert.All(tokens.Take(3), t => Assert.Equal(TokenKind.Identifier, t.Kind));
        }

        [Fact]
        public void ReadsStringsWithEscapes()
        {
            var tokens = LexOk("\"line\\ntwo\\ttab\"");
            Assert.Equal(TokenKind.String, tokens[0].Kind);
            Assert.Equal("line\ntwo\ttab", tokens[0].StringValue);
        }

        [Fact]
        public void AnUnclosedStringIsAnError()
        {
            Lex("\"no end", out var diagnostics);
            Assert.Contains(diagnostics, d => d.Code == IZErrorCode.UnterminatedString);
        }

        [Fact]
        public void AStringDoesNotCrossALineBreak()
        {
            Lex("\"open\nclose\"", out var diagnostics);
            Assert.Contains(diagnostics, d => d.Code == IZErrorCode.UnterminatedString);
        }

        [Fact]
        public void AnInvalidEscapeIsAnError()
        {
            Lex("\"\\q\"", out var diagnostics);
            Assert.Contains(diagnostics, d => d.Code == IZErrorCode.InvalidEscapeSequence);
        }

        [Fact]
        public void HashLiteral()
        {
            var tokens = LexOk("#\"StructureWallLight\"");
            Assert.Equal(TokenKind.HashLiteral, tokens[0].Kind);
            Assert.Equal("StructureWallLight", tokens[0].StringValue);
        }

        [Fact]
        public void ALineCommentIsIgnored()
        {
            var tokens = LexOk("1 // this disappears\n2");
            Assert.Equal(TokenKind.Number, tokens[0].Kind);
            Assert.Equal(2.0, tokens[1].NumberValue);
            Assert.Equal(TokenKind.EndOfFile, tokens[2].Kind);
        }

        [Fact]
        public void BlockCommentsNest()
        {
            var tokens = LexOk("1 /* outer /* inner */ still outer */ 2");
            Assert.Equal(1.0, tokens[0].NumberValue);
            Assert.Equal(2.0, tokens[1].NumberValue);
            Assert.Equal(TokenKind.EndOfFile, tokens[2].Kind);
        }

        [Fact]
        public void AnUnclosedBlockCommentIsAnError()
        {
            Lex("/* no end", out var diagnostics);
            Assert.Contains(diagnostics, d => d.Code == IZErrorCode.UnterminatedBlockComment);
        }

        [Theory]
        [InlineData("<=", TokenKind.LessEquals)]
        [InlineData(">=", TokenKind.GreaterEquals)]
        [InlineData("==", TokenKind.EqualsEquals)]
        [InlineData("!=", TokenKind.BangEquals)]
        [InlineData("&&", TokenKind.AmpAmp)]
        [InlineData("||", TokenKind.PipePipe)]
        [InlineData("<<", TokenKind.LessLess)]
        [InlineData(">>", TokenKind.GreaterGreater)]
        [InlineData("->", TokenKind.Arrow)]
        [InlineData("+=", TokenKind.PlusEquals)]
        [InlineData("%=", TokenKind.PercentEquals)]
        public void TwoCharacterOperators(string text, TokenKind expected)
        {
            var tokens = LexOk(text);
            Assert.Equal(expected, tokens[0].Kind);
        }

        [Fact]
        public void LessThanFollowedByMinusIsNotAShift()
        {
            var tokens = LexOk("a < -b");
            Assert.Equal(TokenKind.Less, tokens[1].Kind);
            Assert.Equal(TokenKind.Minus, tokens[2].Kind);
        }

        [Fact]
        public void ANonAsciiCharacterOutsideAStringIsAnError()
        {
            Lex("var action = 1;", out var ok);
            Assert.False(ok.HasErrors);

            Lex("var a\u00e7\u00e3o = 1;", out var diagnostics);
            Assert.Contains(diagnostics, d => d.Code == IZErrorCode.UnexpectedCharacter);
        }

        [Fact]
        public void SpansPointAtTheRightText()
        {
            const string text = "var x = 42;";
            var tokens = LexOk(text);

            var numberToken = tokens.First(t => t.Kind == TokenKind.Number);
            Assert.Equal("42", text.Substring(numberToken.Span.Start, numberToken.Span.Length));
        }
    }
}
