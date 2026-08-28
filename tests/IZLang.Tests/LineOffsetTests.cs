using System.Collections.Generic;
using System.Linq;
using IZLang.Editor;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// The seam between the editor's 128 lines and the whole source. An off-by-one
    /// here makes completion suggest in the wrong place with no sign of it.
    /// </summary>
    public class LineOffsetTests
    {
        /// <summary>Simulates the editor: 128 fields, nearly all empty.</summary>
        private static List<string?> Editor(params string[] used)
        {
            var lines = new List<string?>(used);
            while (lines.Count < 128) lines.Add(string.Empty);
            return lines;
        }

        [Fact]
        public void JoinsOnlyUpToTheLastNonEmptyLine()
        {
            var lines = Editor("one", "two");
            Assert.Equal("one\ntwo", LineOffsets.Join(lines));
        }

        [Fact]
        public void AnEmptyLineInTheMiddleIsPreserved()
        {
            // If it disappeared, the compiler's line numbers would stop matching the
            // editor's, and the error would show up on the wrong line.
            var lines = Editor("one", "", "three");
            Assert.Equal("one\n\nthree", LineOffsets.Join(lines));
        }

        [Fact]
        public void AnEntirelyEmptyEditorGivesAnEmptySource()
        {
            Assert.Equal(string.Empty, LineOffsets.Join(Editor()));
            Assert.Equal(string.Empty, LineOffsets.Join(new List<string?>()));
        }

        [Fact]
        public void TheLineStartCountsTheSeparator()
        {
            var lines = Editor("abc", "de", "f");

            Assert.Equal(0, LineOffsets.GetLineStart(lines, 0));
            Assert.Equal(4, LineOffsets.GetLineStart(lines, 1));   // "abc" + '\n'
            Assert.Equal(7, LineOffsets.GetLineStart(lines, 2));   // + "de" + '\n'
        }

        [Fact]
        public void TheLineAndColumnOffsetPointsAtTheRightCharacter()
        {
            var lines = Editor("device pump = d0;", "fn main() {", "    pump.On = true;");
            string source = LineOffsets.Join(lines);

            int offset = LineOffsets.ToOffset(lines, 2, 4);        // start of 'pump'
            Assert.Equal("pump", source.Substring(offset, 4));

            int dotOffset = LineOffsets.ToOffset(lines, 2, 8);
            Assert.Equal('.', source[dotOffset]);
        }

        [Fact]
        public void AColumnPastTheEndIsClampedToTheLineEnd()
        {
            var lines = Editor("abc");
            Assert.Equal(3, LineOffsets.ToOffset(lines, 0, 99));
        }

        [Fact]
        public void ANegativeColumnBecomesZero()
        {
            Assert.Equal(0, LineOffsets.ToOffset(Editor("abc"), 0, -5));
        }

        [Fact]
        public void AnOffsetAtTheEndOfTheLineIsValid()
        {
            // The caret after the last character is the most common position while typing.
            var lines = Editor("abc", "de");
            Assert.Equal(3, LineOffsets.ToOffset(lines, 0, 3));
        }

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(3, 0, 3)]
        [InlineData(4, 1, 0)]
        [InlineData(6, 1, 2)]
        [InlineData(7, 2, 0)]
        public void TheInverseConversionMatches(int offset, int expectedLine, int expectedColumn)
        {
            var lines = Editor("abc", "de", "f");

            LineOffsets.ToLineColumn(lines, offset, out int line, out int column);
            Assert.Equal(expectedLine, line);
            Assert.Equal(expectedColumn, column);
        }

        [Fact]
        public void TheRoundTripIsStableForEveryPositionInTheSource()
        {
            var lines = Editor("device pump = d0;", "", "fn main() {", "    pump.On = true;", "}");
            string source = LineOffsets.Join(lines);

            for (int offset = 0; offset <= source.Length; offset++)
            {
                LineOffsets.ToLineColumn(lines, offset, out int line, out int column);
                int roundTrip = LineOffsets.ToOffset(lines, line, column);

                Assert.Equal(offset, roundTrip);
            }
        }

        [Fact]
        public void AnOffsetPastTheEndClampsToTheEnd()
        {
            var lines = Editor("abc", "de");

            LineOffsets.ToLineColumn(lines, 9999, out int line, out int column);
            Assert.Equal(127, line);         // last line of the editor
            Assert.Equal(0, column);
        }

        [Fact]
        public void OffsetsMatchWhatTheCompilerReports()
        {
            // The test that ties it all together: the line the compiler reports has to
            // be the same one the editor shows.
            var lines = Editor(
                "device pump = d0;",
                "fn main() {",
                "    var x = doesNotExist;",
                "}");

            var result = IZCompiler.Compile(LineOffsets.Join(lines));

            Assert.False(result.Success);
            Assert.Equal(3, result.FirstErrorLine);      // one-based -> index 2

            var error = result.Diagnostics.First(d => d.IsError);
            LineOffsets.ToLineColumn(lines, error.Span.Start, out int line, out _);
            Assert.Equal(2, line);                       // editor index, zero-based
        }
    }
}
