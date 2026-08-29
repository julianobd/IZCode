using IZLang.Editor;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// Deciding whether the program the editor session remembers is still the one in
    /// the editor. Getting it wrong either loses everything past the game's 128th
    /// line or brings back a program the player had already replaced.
    /// </summary>
    public class SessionSourceTests
    {
        /// <summary>A program with a tail the game's 128 lines could not hold.</summary>
        private const string Program = "#iz\nfn main() {\n}\ntail";

        /// <summary>What the lines hold: the same program, cut short.</summary>
        private const string Copy = "#iz\nfn main() {\n}";

        [Fact]
        public void WithNothingRememberedTheLinesAnswer()
        {
            Assert.Equal("#iz\nx", SessionSource.Resolve("#iz\nx", null, null));
        }

        [Fact]
        public void UntouchedLinesGiveTheProgramBackWhole()
        {
            Assert.Equal(Program, SessionSource.Resolve(Copy, Program, Copy));
        }

        [Fact]
        public void TheMarkerTypedAgainKeepsTheTail()
        {
            // The player deleted '#iz' and typed it again: only the first line moved.
            string lines = "#iz\nfn main() {\n}";
            string copy = "\nfn main() {\n}";

            Assert.Equal(Program, SessionSource.Resolve(lines, Program, copy));
        }

        [Fact]
        public void TheFirstLineComesFromTheEditor()
        {
            string lines = "#iz  // now with a comment\nfn main() {\n}";
            string result = SessionSource.Resolve(lines, Program, Copy);

            Assert.Equal("#iz  // now with a comment\nfn main() {\n}\ntail", result);
        }

        [Fact]
        public void ALineChangedBelowTheMarkerHandsItOverToTheEditor()
        {
            string lines = "#iz\nfn other() {\n}";
            Assert.Equal(lines, SessionSource.Resolve(lines, Program, Copy));
        }

        [Fact]
        public void ClearedLinesAreNotAProgramComingBack()
        {
            Assert.Equal(string.Empty, SessionSource.Resolve(string.Empty, Program, Copy));
        }

        [Fact]
        public void ASingleLineIsNotGluedToTheTail()
        {
            // Both are one line, so nothing below them differs; the tail is only kept
            // when the program had one.
            Assert.Equal("#iz", SessionSource.Resolve("#iz", "#iz", "#iz"));
            Assert.Equal("#iz", SessionSource.Resolve("#iz", "other", "other"));
        }

        [Fact]
        public void OneLineInTheEditorStillGetsTheTailWithItsBreak()
        {
            string result = SessionSource.Resolve("#iz", "x\ntail", string.Empty);
            Assert.Equal("#iz\ntail", result);
        }

        [Fact]
        public void NullLinesAreEmpty()
        {
            Assert.Equal(string.Empty, SessionSource.Resolve(null, null, null));
        }
    }
}
