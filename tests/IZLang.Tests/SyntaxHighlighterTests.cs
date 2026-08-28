using System;
using System.Text;
using IZLang.Editor;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// The in-game editor's syntax highlighting.
    ///
    /// What these tests protect, in practice, are the two things the IC10 highlighter
    /// broke in the editor: whole IZ programs coming out red (the color of whatever
    /// IC10 does not recognize) and <c>#</c> eating the rest of the line as a comment.
    /// </summary>
    public class SyntaxHighlighterTests
    {
        private const string NoParseOpen = "<noparse>";
        private const string NoParseClose = "</noparse>";

        /// <summary>
        /// The text the player sees, without the formatting tags - the same thing
        /// TextMeshPro draws. The <c>&lt;noparse&gt;</c> block comes out ahead of the
        /// other tags because what it wraps is literal, not markup.
        /// </summary>
        private static string Visible(string markup)
        {
            var sb = new StringBuilder();
            int i = 0;

            while (i < markup.Length)
            {
                if (markup[i] != '<') { sb.Append(markup[i++]); continue; }

                // <noparse>X</noparse> draws X literally; any other tag draws
                // nothing.
                if (string.CompareOrdinal(markup, i, NoParseOpen, 0, NoParseOpen.Length) == 0)
                {
                    int content = i + NoParseOpen.Length;
                    int close = markup.IndexOf(NoParseClose, content, StringComparison.Ordinal);
                    if (close < 0) { sb.Append(markup, content, markup.Length - content); break; }

                    sb.Append(markup, content, close - content);
                    i = close + NoParseClose.Length;
                    continue;
                }

                int tagEnd = markup.IndexOf('>', i);
                if (tagEnd < 0) break;
                i = tagEnd + 1;
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  The invariant that holds up the rest
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("const TARGET = 101.325")]
        [InlineData("device sensor = d0;")]
        [InlineData("fn average(num a, num b) -> num { return (a + b) / 2; }")]
        [InlineData("    inlet.On = p < TARGET - MARGIN;   // turn the pump on")]
        [InlineData("const DISPLAY = #\"StructureConsoleLED5\"")]
        [InlineData("var mask = 0b1010 << 2 | 0xFF;")]
        [InlineData("for i in 0..=10 { total += i; }")]
        public void TheVisibleTextDoesNotChange(string line)
        {
            // The colored TextMeshPro sits on top of the raw text field: if the
            // highlighting adds or eats a character, the caret starts landing on the
            // wrong column. The tags have to be genuinely invisible.
            Assert.Equal(line, Visible(SyntaxHighlighter.HighlightLine(line)));
        }

        // ------------------------------------------------------------------
        //  The '#' bug
        // ------------------------------------------------------------------

        [Fact]
        public void AHashLiteralDoesNotBecomeAComment()
        {
            // In IC10 '#' opens a comment - that is why half of
            // 'const X = #"Prefab"' used to vanish into gray.
            string markup = SyntaxHighlighter.HighlightLine("const DISPLAY = #\"StructureConsoleLED5\"");

            Assert.Contains("#\"StructureConsoleLED5\"", Visible(markup));
            Assert.Contains("<color=#" + HighlightTheme.Default.Hash + ">", markup);
            Assert.DoesNotContain("<color=#" + HighlightTheme.Default.Comment + ">", markup);
        }

        [Fact]
        public void AHashLiteralInsideACallStaysALiteral()
        {
            string markup = SyntaxHighlighter.HighlightLine("all(#\"StructureDiode\").On = true;");
            Assert.DoesNotContain("<color=#" + HighlightTheme.Default.Comment + ">", markup);
        }

        // ------------------------------------------------------------------
        //  Categories
        // ------------------------------------------------------------------

        [Fact]
        public void AKeywordGetsTheKeywordColor()
        {
            string markup = SyntaxHighlighter.HighlightLine("loop {");
            Assert.Contains("<color=#" + HighlightTheme.Default.Keyword + ">loop</color>", markup);
        }

        [Fact]
        public void ATypeNameHasItsOwnColor()
        {
            string markup = SyntaxHighlighter.HighlightLine("fn f(num a) -> bool {");
            Assert.Contains("<color=#" + HighlightTheme.Default.TypeName + ">num</color>", markup);
            Assert.Contains("<color=#" + HighlightTheme.Default.TypeName + ">bool</color>", markup);
        }

        [Fact]
        public void APropertyAfterADotUsesTheCompletionColor()
        {
            // The same color the popup shows in the list: what was picked in light blue
            // stays light blue once accepted.
            string markup = SyntaxHighlighter.HighlightLine("sensor.Pressure");
            Assert.Contains("<color=#" + HighlightTheme.Default.Property + ">Pressure</color>", markup);
        }

        [Fact]
        public void AFunctionCallGetsTheFunctionColor()
        {
            string markup = SyntaxHighlighter.HighlightLine("var x = clamp(a, 0, 1);");
            Assert.Contains("<color=#" + HighlightTheme.Default.Function + ">clamp</color>", markup);
        }

        [Fact]
        public void ANumberGetsTheNumberColor()
        {
            string markup = SyntaxHighlighter.HighlightLine("const TARGET = 101.325");
            Assert.Contains("<color=#" + HighlightTheme.Default.Number + ">101.325</color>", markup);
        }

        [Fact]
        public void ALineCommentTurnsGray()
        {
            string markup = SyntaxHighlighter.HighlightLine("yield; // hands the tick back");
            Assert.Contains("<color=#" + HighlightTheme.Default.Comment + ">// hands the tick back</color>", markup);
        }

        [Fact]
        public void TheMarkerHasItsOwnColor()
        {
            string markup = SyntaxHighlighter.HighlightLine("#iz");
            Assert.Equal("<color=#" + HighlightTheme.Default.Marker + ">#iz</color>", markup);
        }

        [Fact]
        public void AnInvalidCharacterTurnsRed()
        {
            // Here red is the right answer: '$' does not exist in IZ.
            string markup = SyntaxHighlighter.HighlightLine("var x = $;");
            Assert.Contains("<color=#" + HighlightTheme.Default.Invalid + ">$</color>", markup);
        }

        // ------------------------------------------------------------------
        //  Edge cases
        // ------------------------------------------------------------------

        [Fact]
        public void AnEmptyLineGivesEmptyText()
        {
            Assert.Equal(string.Empty, SyntaxHighlighter.HighlightLine(string.Empty));
            Assert.Equal(string.Empty, SyntaxHighlighter.HighlightLine("      "));
            Assert.Equal(string.Empty, SyntaxHighlighter.HighlightLine(null));
        }

        [Fact]
        public void LessThanIsEscapedSoItDoesNotBecomeATag()
        {
            // Without escaping, TextMeshPro would read '<<' as a tag opening and swallow
            // the rest of the line.
            string markup = SyntaxHighlighter.HighlightLine("var m = 1 << 3;");
            Assert.Contains("<noparse><</noparse>", markup);
            Assert.Equal("var m = 1 << 3;", Visible(markup));
        }

        [Fact]
        public void OnTheMotherboardScreenTheEscapeIsADifferentOne()
        {
            // The motherboard screen is UnityEngine.UI.Text, which does not know
            // <noparse>. There the way to escape is the game's own trick: splice in an
            // empty tag so the parser gives up on reading a tag name.
            string markup = SyntaxHighlighter.HighlightLine("var m = 1 << 3;", null,
                                                            RichTextFlavor.LegacyText);
            Assert.DoesNotContain(NoParseOpen, markup);
            Assert.Contains("<<b></b>", markup);
        }

        [Fact]
        public void IndentationIsPreserved()
        {
            string markup = SyntaxHighlighter.HighlightLine("        yield;");
            Assert.StartsWith("        ", Visible(markup));
        }

        [Fact]
        public void TheWholeSourceIsPaintedLineByLine()
        {
            var source = new StringBuilder()
                .Append("#iz\n")
                .Append("const A = 1\n")
                .Append("\n")
                .Append("fn main() { }")
                .ToString();

            string markup = SyntaxHighlighter.Highlight(source);
            Assert.Equal(4, markup.Split('\n').Length);
            Assert.Equal(source, Visible(markup));
        }

        [Fact]
        public void WindowsLineEndingsDoNotLeakIntoTheText()
        {
            // The editor hands the source over with \r\n; the \r must not end up inside
            // a color tag.
            string markup = SyntaxHighlighter.Highlight("#iz\r\nconst A = 1\r\n");
            Assert.DoesNotContain("\r", markup);
        }
    }
}
