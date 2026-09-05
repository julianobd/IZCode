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

        // ------------------------------------------------------------------
        //  KeepsMemory: the same question, asked on the way out
        // ------------------------------------------------------------------
        //  What is shown and what is saved have to agree. A stricter rule here is
        //  how a program came to be shown whole and written to the chip truncated
        //  to the game's 128 lines.

        [Fact]
        public void UntouchedLinesKeepTheMemory()
        {
            Assert.True(SessionSource.KeepsMemory(Copy, Copy));
        }

        [Fact]
        public void TheMarkerTypedAgainKeepsTheMemory()
        {
            // The first line is where the marker lives, so it is the line that moves
            // on its own. Everything below it is untouched, so the program is ours.
            string lines = "#iz\nfn main() {\n}";
            string copy = "\nfn main() {\n}";

            Assert.True(SessionSource.KeepsMemory(lines, copy));
        }

        [Fact]
        public void AFirstLineEditedAnyOtherWayKeepsTheMemoryToo()
        {
            string lines = "#iz  // now with a comment\nfn main() {\n}";

            Assert.True(SessionSource.KeepsMemory(lines, Copy));
        }

        [Fact]
        public void ALineChangedBelowTheMarkerGivesUpTheMemory()
        {
            Assert.False(SessionSource.KeepsMemory("#iz\nfn main() {\n}\nelse", Copy));
        }

        [Fact]
        public void ClearedLinesGiveUpTheMemory()
        {
            Assert.False(SessionSource.KeepsMemory(string.Empty, Copy));
        }

        [Fact]
        public void WithNothingRememberedThereIsNoMemoryToKeep()
        {
            Assert.False(SessionSource.KeepsMemory(Copy, null));
        }

        [Fact]
        public void KeepsMemoryAgreesWithResolve()
        {
            // The two have to be the same decision: whenever Resolve hands back the
            // program, KeepsMemory has to say so, and whenever it hands back the
            // lines it has to say the opposite. Saving reads the second, showing
            // reads the first, and they cannot disagree.
            string[] candidates =
            {
                Copy,
                "#iz\nfn main() {\n}",
                "#iz  // edited\nfn main() {\n}",
                "#iz\nfn main() {\n}\nelse",
                "",
                "#iz",
            };

            foreach (string lines in candidates)
            {
                string resolved = SessionSource.Resolve(lines, Program, Copy);
                bool keptTheLines = resolved == lines;
                Assert.Equal(!keptTheLines, SessionSource.KeepsMemory(lines, Copy));
            }
        }

        [Fact]
        public void NullLinesAreEmpty()
        {
            Assert.Equal(string.Empty, SessionSource.Resolve(null, null, null));
        }
    }
}
