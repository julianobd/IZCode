using System;
using System.Text;

namespace IZLang.Editor
{
    /// <summary>
    /// A text edit that has already been worked out: what to replace, with what,
    /// and where the caret ends up.
    ///
    /// It exists so the decision ("what indentation does this line deserve") stays in
    /// pure, testable code, and the Unity side only applies the result.
    /// </summary>
    public readonly struct TextEdit
    {
        /// <summary>Nothing to do - the caller should carry on with the default behaviour.</summary>
        public static readonly TextEdit None = new TextEdit(-1, 0, string.Empty, 0, 0);

        public int Start { get; }
        public int Length { get; }
        public string Replacement { get; }

        /// <summary>Selection after the edit, as an offset into the already modified text.</summary>
        public int SelectionStart { get; }
        public int SelectionEnd { get; }

        public bool IsEmpty => Start < 0;

        public TextEdit(int start, int length, string replacement, int selectionStart, int selectionEnd)
        {
            Start = start;
            Length = length;
            Replacement = replacement ?? string.Empty;
            SelectionStart = selectionStart;
            SelectionEnd = selectionEnd;
        }

        /// <summary>Applies the edit. Used by the tests and by the editor.</summary>
        public string Apply(string? text)
        {
            text ??= string.Empty;
            if (IsEmpty) return text;

            int start = Math.Max(0, Math.Min(Start, text.Length));
            int length = Math.Max(0, Math.Min(Length, text.Length - start));
            return text.Substring(0, start) + Replacement + text.Substring(start + length);
        }
    }

    /// <summary>
    /// Automatic indentation in the VS Code style, in pure code.
    ///
    /// Three rules, and that is all:
    ///
    ///   * <b>Enter</b> repeats the current line's indentation, and goes in one level
    ///     when the line opened a block. When the <c>}</c> is already right ahead, it
    ///     opens two lines and leaves the caret in between - the same thing VS Code does.
    ///   * <b>}</b> typed on a whitespace-only line goes back one level, so the closing
    ///     brace lines up with the opening one instead of hanging.
    ///   * <b>Tab / Shift+Tab</b> shift the whole selected block; with no selection,
    ///     Tab moves to the next tab stop.
    ///
    /// It always works with spaces: the game editor stores the source as plain text
    /// lines, and a <c>\t</c> in there would have an unpredictable width.
    /// </summary>
    public static class IndentEngine
    {
        /// <summary>Width of one indentation level, in spaces.</summary>
        public const int IndentWidth = 4;

        // ------------------------------------------------------------------
        //  Enter
        // ------------------------------------------------------------------

        /// <summary>
        /// What to insert when the player presses Enter.
        ///
        /// The edit already covers removing the selection, if there was one: typing
        /// over what is selected is the expected behaviour in any editor.
        /// </summary>
        public static TextEdit NewLine(string? text, int selectionStart, int selectionEnd)
        {
            string source = text ?? string.Empty;
            Order(source, ref selectionStart, ref selectionEnd);

            int lineStart = LineStart(source, selectionStart);
            string before = source.Substring(lineStart, selectionStart - lineStart);

            // The inherited indentation is the line's, but never more than what has
            // already been typed: with the caret in the middle of the indent, the rest
            // moves down with it, and counting it twice would push the text away.
            string indent = LeadingWhitespace(before);

            bool opens = EndsWithOpenBrace(before);
            string inner = opens ? indent + new string(' ', IndentWidth) : indent;

            int lineEnd = LineEnd(source, selectionEnd);
            string after = source.Substring(selectionEnd, lineEnd - selectionEnd);
            bool closes = after.TrimStart().StartsWith("}", StringComparison.Ordinal);

            int length = selectionEnd - selectionStart;

            if (opens && closes)
            {
                // '{|}' becomes three lines, with the caret on the middle one.
                string expanded = "\n" + inner + "\n" + indent;
                int middle = selectionStart + 1 + inner.Length;
                return new TextEdit(selectionStart, length, expanded, middle, middle);
            }

            string simple = "\n" + inner;
            int position = selectionStart + simple.Length;
            return new TextEdit(selectionStart, length, simple, position, position);
        }

        // ------------------------------------------------------------------
        //  Block closing
        // ------------------------------------------------------------------

        /// <summary>
        /// Indentation for a <c>}</c> typed on a line that is only whitespace so far.
        ///
        /// Returns <see cref="TextEdit.None"/> when there is code before the caret -
        /// there the <c>}</c> is not opening any line (<c>fn f() { return 1; }</c>) and
        /// touching the indentation would be wrong.
        /// </summary>
        public static TextEdit CloseBrace(string? text, int selectionStart, int selectionEnd)
        {
            string source = text ?? string.Empty;
            Order(source, ref selectionStart, ref selectionEnd);

            int lineStart = LineStart(source, selectionStart);
            string before = source.Substring(lineStart, selectionStart - lineStart);

            if (before.Length == 0) return TextEdit.None;          // already at column 0
            if (before.Trim().Length != 0) return TextEdit.None;   // there is code before it

            string replacement = new string(' ', PreviousStop(before.Length)) + "}";
            int caret = lineStart + replacement.Length;
            return new TextEdit(lineStart, selectionEnd - lineStart, replacement, caret, caret);
        }

        // ------------------------------------------------------------------
        //  Tab / Shift+Tab
        // ------------------------------------------------------------------

        /// <summary>
        /// Tab and Shift+Tab.
        ///
        /// With a selection (or with Shift), it shifts every line the selection touches
        /// and returns the selection covering the block - that way Tab can be pressed
        /// several times in a row. With no selection, Tab just moves to the next stop.
        /// </summary>
        public static TextEdit Indent(string? text, int selectionStart, int selectionEnd, bool outdent)
        {
            string source = text ?? string.Empty;
            Order(source, ref selectionStart, ref selectionEnd);

            if (selectionStart == selectionEnd && !outdent)
            {
                int column = selectionStart - LineStart(source, selectionStart);
                int spaces = IndentWidth - (column % IndentWidth);
                int caret = selectionStart + spaces;
                return new TextEdit(selectionStart, 0, new string(' ', spaces), caret, caret);
            }

            int blockStart = LineStart(source, selectionStart);
            int blockEnd = LineEnd(source, LastTouchedLine(source, selectionStart, selectionEnd));

            string block = source.Substring(blockStart, blockEnd - blockStart);
            string[] lines = block.Split('\n');

            var sb = new StringBuilder(block.Length + lines.Length * IndentWidth);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(outdent ? Outdent(lines[i]) : IndentOne(lines[i]));
            }

            string replacement = sb.ToString();
            return new TextEdit(blockStart, blockEnd - blockStart, replacement,
                                blockStart, blockStart + replacement.Length);
        }

        /// <summary>An empty line gets no indent: it would just be stray whitespace.</summary>
        private static string IndentOne(string line) =>
            line.Length == 0 ? line : new string(' ', IndentWidth) + line;

        private static string Outdent(string line)
        {
            string indent = LeadingWhitespace(line);
            if (indent.Length == 0) return line;

            return new string(' ', PreviousStop(indent.Length)) + line.Substring(indent.Length);
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        /// <summary>The tab stop immediately before <paramref name="width"/>.</summary>
        private static int PreviousStop(int width)
        {
            if (width <= 0) return 0;
            return (width - 1) / IndentWidth * IndentWidth;
        }

        /// <summary>Start of the line containing <paramref name="offset"/>.</summary>
        public static int LineStart(string? text, int offset)
        {
            if (string.IsNullOrEmpty(text) || offset <= 0) return 0;
            if (offset > text!.Length) offset = text.Length;

            int index = text.LastIndexOf('\n', offset - 1);
            return index < 0 ? 0 : index + 1;
        }

        /// <summary>End of the line containing <paramref name="offset"/>, before the '\n'.</summary>
        public static int LineEnd(string? text, int offset)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (offset < 0) offset = 0;
            if (offset > text!.Length) offset = text.Length;

            int index = text.IndexOf('\n', offset);
            return index < 0 ? text.Length : index;
        }

        /// <summary>
        /// Offset inside the last line the selection actually touches.
        ///
        /// A selection ending exactly at column 0 of the next line does not include
        /// that line - which is what happens when dragging the mouse downwards, and
        /// indenting one line too many would be visible.
        /// </summary>
        private static int LastTouchedLine(string text, int selectionStart, int selectionEnd)
        {
            if (selectionEnd > selectionStart && selectionEnd == LineStart(text, selectionEnd))
                return selectionEnd - 1;
            return selectionEnd;
        }

        private static string LeadingWhitespace(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return line.Substring(0, i);
        }

        /// <summary>
        /// Does the line open a block? A trailing comment does not count: in
        /// <c>if (x) {   // turn on</c> what matters is the brace, not the text after it.
        /// </summary>
        private static bool EndsWithOpenBrace(string before)
        {
            string code = StripLineComment(before).TrimEnd();
            return code.Length > 0 && code[code.Length - 1] == '{';
        }

        private static string StripLineComment(string line)
        {
            bool inString = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"' && (i == 0 || line[i - 1] != '\\')) inString = !inString;
                else if (!inString && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                    return line.Substring(0, i);
            }
            return line;
        }

        private static void Order(string text, ref int start, ref int end)
        {
            if (start > end) { int swap = start; start = end; end = swap; }
            if (start < 0) start = 0;
            if (start > text.Length) start = text.Length;
            if (end > text.Length) end = text.Length;
            if (end < start) end = start;
        }
    }
}
