using IZLang.Editor;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// What the code area does with text that did not come from its own keyboard:
    /// the clipboard, a script in the library, the source already on the chip.
    /// </summary>
    public class SourceLimitTests
    {
        private const int Lines = 100;
        private const int LineLength = 20;
        private const int Chars = 1000;

        private static string Clamp(string? text) => SourceLimits.Clamp(text, Lines, LineLength, Chars);

        [Fact]
        public void NullAndEmptyComeOutEmpty()
        {
            Assert.Equal(string.Empty, Clamp(null));
            Assert.Equal(string.Empty, Clamp(string.Empty));
        }

        [Fact]
        public void WindowsLineEndingsBecomeOne()
        {
            Assert.Equal("a\nb", Clamp("a\r\nb"));
        }

        [Fact]
        public void ALoneCarriageReturnAlsoEndsTheLine()
        {
            Assert.Equal("a\nb", Clamp("a\rb"));
        }

        [Fact]
        public void ATabBecomesASpace()
        {
            Assert.Equal("a b", Clamp("a\tb"));
        }

        [Fact]
        public void NonAsciiIsDropped()
        {
            Assert.Equal("ao", Clamp("ação"));   // dropped, not transliterated
        }

        [Fact]
        public void TheLinesAreCutToLength()
        {
            string line = new string('x', LineLength + 10);
            Assert.Equal(new string('x', LineLength), Clamp(line));
        }

        [Fact]
        public void CuttingALineDoesNotEatTheNextOne()
        {
            string source = new string('x', LineLength + 5) + "\nkeep";
            Assert.Equal(new string('x', LineLength) + "\nkeep", Clamp(source));
        }

        [Fact]
        public void TheSurplusLinesAreDropped()
        {
            var text = string.Join("\n", System.Linq.Enumerable.Range(0, Lines + 20));
            var result = Clamp(text);
            Assert.Equal(Lines, result.Split('\n').Length);
            Assert.StartsWith("0\n1\n", result);
        }

        [Fact]
        public void TrailingBlanksGoAway()
        {
            Assert.Equal("a\nb", Clamp("a   \nb  \n\n\n"));
        }

        [Fact]
        public void AnEmptyLineInTheMiddleStays()
        {
            Assert.Equal("a\n\nb", Clamp("a\n\nb"));
        }

        [Fact]
        public void IndentationIsKept()
        {
            Assert.Equal("fn main() {\n    x = 1;\n}", Clamp("fn main() {\n    x = 1;\n}"));
        }

        [Fact]
        public void TheWholeThingIsCutToSize()
        {
            var text = string.Join("\n", System.Linq.Enumerable.Repeat(new string('x', LineLength), 90));
            var result = SourceLimits.Clamp(text, Lines, LineLength, 50);
            Assert.Equal(50, result.Length);
        }

        [Fact]
        public void SourceThatAlreadyFitsComesBackUntouched()
        {
            const string source = "#iz\n\nfn main() {\n    loop {\n        yield;\n    }\n}";
            Assert.Equal(source, Clamp(source));
        }
    }
}
