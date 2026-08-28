using System.Collections.Generic;
using System.Text;

namespace IZLang.Editor
{
    /// <summary>
    /// Conversion between the game editor's separate lines and the whole source.
    ///
    /// The Stationeers editor has no single text field: there are up to 128
    /// independent TMP_InputFields, one per line. The compiler, the completion
    /// engine and the hover, on the other hand, work over the whole source with
    /// absolute offsets. That seam lives here, in pure code, because an off-by-one
    /// would make completion suggest in the wrong place with no sign of it.
    /// </summary>
    public static class LineOffsets
    {
        /// <summary>
        /// Joins the lines with '\n'. Trailing empty lines are dropped - the editor
        /// always has 128 fields, nearly all empty - but the ones in the middle stay,
        /// otherwise the editor's line numbers stop matching the compiler's.
        /// </summary>
        public static string Join(IReadOnlyList<string?> lines)
        {
            if (lines == null || lines.Count == 0) return string.Empty;

            int last = -1;
            for (int i = 0; i < lines.Count; i++)
                if (!string.IsNullOrEmpty(lines[i])) last = i;

            if (last < 0) return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i <= last; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(lines[i] ?? string.Empty);
            }
            return sb.ToString();
        }

        /// <summary>Absolute offset where line <paramref name="lineIndex"/> starts.</summary>
        public static int GetLineStart(IReadOnlyList<string?> lines, int lineIndex)
        {
            if (lines == null || lineIndex <= 0) return 0;

            int offset = 0;
            int limit = lineIndex < lines.Count ? lineIndex : lines.Count;

            for (int i = 0; i < limit; i++)
                offset += (lines[i]?.Length ?? 0) + 1;      // +1 for the '\n'

            return offset;
        }

        /// <summary>
        /// Absolute offset of (line, column). The column is clamped to the line
        /// length: the editor caret can sit past the end after a delete.
        /// </summary>
        public static int ToOffset(IReadOnlyList<string?> lines, int lineIndex, int column)
        {
            if (lines == null || lineIndex < 0) return 0;

            int lineLength = lineIndex < lines.Count ? (lines[lineIndex]?.Length ?? 0) : 0;
            if (column < 0) column = 0;
            if (column > lineLength) column = lineLength;

            return GetLineStart(lines, lineIndex) + column;
        }

        /// <summary>The way back: from absolute offset to (line, column).</summary>
        public static void ToLineColumn(IReadOnlyList<string?> lines, int offset,
                                        out int lineIndex, out int column)
        {
            lineIndex = 0;
            column = 0;
            if (lines == null || offset <= 0) return;

            int remaining = offset;
            for (int i = 0; i < lines.Count; i++)
            {
                int length = lines[i]?.Length ?? 0;

                // It fits on this line (the position right after the last character counts).
                if (remaining <= length)
                {
                    lineIndex = i;
                    column = remaining;
                    return;
                }

                remaining -= length + 1;                   // consume the line and the '\n'
            }

            // Past the end: clamp to the end of the last non-empty line.
            lineIndex = lines.Count > 0 ? lines.Count - 1 : 0;
            column = lines.Count > 0 ? (lines[lineIndex]?.Length ?? 0) : 0;
        }
    }
}
