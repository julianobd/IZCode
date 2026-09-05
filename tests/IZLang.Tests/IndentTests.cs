using IZLang.Editor;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// The editor's automatic indentation. These are the rules the player feels on
    /// every Enter and every Tab, so getting them wrong is visible on the first line
    /// typed.
    ///
    /// The convention for the cases: '|' marks the caret in the input text, and the
    /// expected result uses the same mark.
    /// </summary>
    public class IndentTests
    {
        /// <summary>Applies an edit and returns the text with '|' where the caret ended up.</summary>
        private static string Apply(TextEdit edit, string text)
        {
            string result = edit.Apply(text);
            return result.Insert(edit.SelectionStart, "|");
        }

        private static (string Text, int Caret) Split(string marked)
        {
            int caret = marked.IndexOf('|');
            return (marked.Remove(caret, 1), caret);
        }

        private static string Enter(string marked)
        {
            var (text, caret) = Split(marked);
            return Apply(IndentEngine.NewLine(text, caret, caret), text);
        }

        // ==================================================================
        //  Enter
        // ==================================================================

        [Fact]
        public void EnterRepeatsTheLineIndent()
        {
            Assert.Equal("    var x = 1;\n    |", Enter("    var x = 1;|"));
        }

        [Fact]
        public void EnterGoesInOneLevelAfterOpeningABlock()
        {
            Assert.Equal("fn main() {\n    |", Enter("fn main() {|"));
        }

        [Fact]
        public void EnterGoesInOneLevelFromTheExistingIndent()
        {
            Assert.Equal("    loop {\n        |", Enter("    loop {|"));
        }

        [Fact]
        public void ABraceWithATrailingCommentStillCounts()
        {
            // The comment does not change what the line does; if it counted, the block
            // would start unindented on exactly the line that was explained.
            Assert.Equal("if (x) { // turn on\n    |", Enter("if (x) { // turn on|"));
        }

        [Fact]
        public void ABraceInsideAStringDoesNotOpenABlock()
        {
            Assert.Equal("var s = \"{\";\n|", Enter("var s = \"{\";|"));
        }

        [Fact]
        public void EnterBetweenBracesOpensTwoLinesWithTheCaretInBetween()
        {
            Assert.Equal("fn main() {\n    |\n}", Enter("fn main() {|}"));
        }

        [Fact]
        public void EnterBetweenBracesRespectsTheOuterIndent()
        {
            Assert.Equal("    loop {\n        |\n    }", Enter("    loop {|}"));
        }

        [Fact]
        public void EnterInTheMiddleOfTheIndentDoesNotDuplicateTheSpaces()
        {
            // Caret between the 2nd and 3rd space: the two behind stay, the two ahead
            // go down with the text. Inheriting the whole indent would give six.
            Assert.Equal("  \n  |  var x = 1;", Enter("  |  var x = 1;"));
        }

        [Fact]
        public void EnterWithASelectionDeletesWhatWasSelected()
        {
            var edit = IndentEngine.NewLine("    var abc = 1;", 8, 11);   // 'abc'
            Assert.Equal("    var \n     = 1;", edit.Apply("    var abc = 1;"));
        }

        [Fact]
        public void EnterAtTheEndOfAnUnindentedLineInventsNoSpaces()
        {
            Assert.Equal("#iz\n|", Enter("#iz|"));
        }

        // ==================================================================
        //  Block closing
        // ==================================================================

        [Fact]
        public void AClosingBraceGoesBackOneLevel()
        {
            var (text, caret) = Split("fn main() {\n    var x = 1;\n    |");
            var edit = IndentEngine.CloseBrace(text, caret, caret);
            Assert.Equal("fn main() {\n    var x = 1;\n}|", Apply(edit, text));
        }

        [Fact]
        public void AClosingBraceGoesBackOnlyOneLevelAtATime()
        {
            var (text, caret) = Split("        |");
            var edit = IndentEngine.CloseBrace(text, caret, caret);
            Assert.Equal("    }|", Apply(edit, text));
        }

        [Fact]
        public void ABrokenIndentFallsToThePreviousStop()
        {
            var (text, caret) = Split("      |");
            var edit = IndentEngine.CloseBrace(text, caret, caret);
            Assert.Equal("    }|", Apply(edit, text));
        }

        [Fact]
        public void ABraceAfterCodeDoesNotTouchTheIndent()
        {
            // 'fn f() { return 1; }' - here the '}' closes on the same line; outdenting
            // would drag the whole line to the left.
            var (text, caret) = Split("    fn f() { return 1; |");
            Assert.True(IndentEngine.CloseBrace(text, caret, caret).IsEmpty);
        }

        [Fact]
        public void ABraceAtColumnZeroHasNowhereToGoBackTo()
        {
            Assert.True(IndentEngine.CloseBrace("", 0, 0).IsEmpty);
        }

        // ==================================================================
        //  Block closing, after the brace is already in
        // ==================================================================
        //  The code area cannot refuse a '}' any more: refusing every one of them to
        //  put it back on the next frame is what made Ctrl+V lose braces, since a
        //  paste arrives one character at a time and only the last refusal was ever
        //  honoured. The outdent runs over the text that already holds the brace.

        [Fact]
        public void AnAlreadyTypedBraceGoesBackOneLevel()
        {
            var (text, caret) = Split("fn main() {\n    var x = 1;\n    }|");
            var edit = IndentEngine.OutdentCloseBraceLine(text, caret);
            Assert.Equal("fn main() {\n    var x = 1;\n}|", Apply(edit, text));
        }

        [Fact]
        public void AnAlreadyTypedBraceGoesBackOnlyOneLevelAtATime()
        {
            var (text, caret) = Split("        }|");
            var edit = IndentEngine.OutdentCloseBraceLine(text, caret);
            Assert.Equal("    }|", Apply(edit, text));
        }

        [Fact]
        public void AnAlreadyTypedBraceAfterCodeIsLeftAlone()
        {
            var (text, caret) = Split("    fn f() { return 1; }|");
            Assert.True(IndentEngine.OutdentCloseBraceLine(text, caret).IsEmpty);
        }

        [Fact]
        public void AnAlreadyTypedBraceAtColumnZeroHasNowhereToGoBackTo()
        {
            var (text, caret) = Split("}|");
            Assert.True(IndentEngine.OutdentCloseBraceLine(text, caret).IsEmpty);
        }

        [Fact]
        public void TheCaretHasToBeRightAfterTheBrace()
        {
            // '    }x' - the player carried on typing before the frame that would have
            // re-indented; moving the line then would be a surprise.
            var (text, caret) = Split("    }x|");
            Assert.True(IndentEngine.OutdentCloseBraceLine(text, caret).IsEmpty);
        }

        // ==================================================================
        //  Tab and Shift+Tab
        // ==================================================================

        [Fact]
        public void TabWithNoSelectionMovesToTheNextStop()
        {
            var (text, caret) = Split("ab|");
            var edit = IndentEngine.Indent(text, caret, caret, outdent: false);
            Assert.Equal("ab  |", Apply(edit, text));
        }

        [Fact]
        public void TabOnAStopMovesAWholeLevel()
        {
            var (text, caret) = Split("    |");
            var edit = IndentEngine.Indent(text, caret, caret, outdent: false);
            Assert.Equal("        |", Apply(edit, text));
        }

        [Fact]
        public void TabWithASelectionShiftsEveryLine()
        {
            const string text = "one\ntwo\nthree";
            var edit = IndentEngine.Indent(text, 1, 12, outdent: false);
            Assert.Equal("    one\n    two\n    three", edit.Apply(text));
        }

        [Fact]
        public void TheReturnedSelectionCoversTheWholeBlock()
        {
            // Without this, a second Tab in a row would catch only one line.
            const string text = "one\ntwo";
            var edit = IndentEngine.Indent(text, 1, 5, outdent: false);
            Assert.Equal(0, edit.SelectionStart);
            Assert.Equal("    one\n    two".Length, edit.SelectionEnd);
        }

        [Fact]
        public void AnEmptyLineInTheMiddleGetsNoStrayWhitespace()
        {
            const string text = "one\n\nthree";
            var edit = IndentEngine.Indent(text, 0, text.Length, outdent: false);
            Assert.Equal("    one\n\n    three", edit.Apply(text));
        }

        [Fact]
        public void ASelectionEndingAtColumnZeroDoesNotCatchTheNextLine()
        {
            const string text = "one\ntwo\nthree";
            var edit = IndentEngine.Indent(text, 0, 4, outdent: false);   // end = start of 'two'
            Assert.Equal("    one\ntwo\nthree", edit.Apply(text));
        }

        [Fact]
        public void ShiftTabRemovesOneLevel()
        {
            const string text = "        var x = 1;";
            var edit = IndentEngine.Indent(text, 0, 0, outdent: true);
            Assert.Equal("    var x = 1;", edit.Apply(text));
        }

        [Fact]
        public void ShiftTabAtColumnZeroDoesNothing()
        {
            const string text = "var x = 1;";
            var edit = IndentEngine.Indent(text, 0, 0, outdent: true);
            Assert.Equal("var x = 1;", edit.Apply(text));
        }

        [Fact]
        public void ShiftTabWithASelectionShiftsTheWholeBlock()
        {
            const string text = "    one\n        two";
            var edit = IndentEngine.Indent(text, 0, text.Length, outdent: true);
            Assert.Equal("one\n    two", edit.Apply(text));
        }
    }
}
