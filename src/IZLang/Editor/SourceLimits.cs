using System.Text;

namespace IZLang.Editor
{
    /// <summary>
    /// Brings source that came from outside into the shape the code area holds.
    ///
    /// Text arrives from the clipboard, from a script in the library and from the
    /// chip itself, and none of it has been through the editor's own input filter:
    /// it can carry tabs, carriage returns, accented characters and lines longer
    /// than the area shows. The chip stores ASCII, so anything else would be
    /// dropped later anyway, without a word - better to do it here, once, where it
    /// can be tested.
    ///
    /// The limits are the code area's, not the game's: what a program can be is
    /// decided by what TextMeshPro can draw, and that is where the caller's numbers
    /// come from.
    /// </summary>
    public static class SourceLimits
    {
        /// <summary>
        /// ASCII, no tabs, at most <paramref name="maxLines"/> lines of
        /// <paramref name="maxLineLength"/> characters, and no longer than
        /// <paramref name="maxChars"/> in all.
        /// </summary>
        public static string Clamp(string? text, int maxLines, int maxLineLength, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var sb = new StringBuilder(text!.Length);

            int lines = 1;
            int column = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                // A '\r' on its own also ends a line: text pasted from Windows and
                // text pasted from an old Mac editor both have to come out the same.
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                    c = '\n';
                }

                if (c == '\n')
                {
                    if (lines >= maxLines) break;
                    lines++;
                    column = 0;
                    sb.Append('\n');
                    continue;
                }

                // The game turns a tab into a single space; matching it keeps the
                // columns of a program that has been through the vanilla editor.
                if (c == '\t') c = ' ';

                // Printable ASCII only, which is what AsciiString keeps.
                if (c < ' ' || c > '~') continue;

                if (column >= maxLineLength) continue;

                sb.Append(c);
                column++;
            }

            var result = TrimLineEnds(sb);
            return result.Length > maxChars ? result.Substring(0, maxChars) : result;
        }

        /// <summary>
        /// Drops the blanks at the end of each line and the empty lines at the end of
        /// the program, the way the game does when it stores the source.
        /// </summary>
        private static string TrimLineEnds(StringBuilder sb)
        {
            var result = new StringBuilder(sb.Length);
            int lineStart = 0;

            for (int i = 0; i <= sb.Length; i++)
            {
                bool end = i == sb.Length || sb[i] == '\n';
                if (!end) continue;

                int stop = i;
                while (stop > lineStart && sb[stop - 1] == ' ') stop--;

                for (int j = lineStart; j < stop; j++) result.Append(sb[j]);
                if (i < sb.Length) result.Append('\n');

                lineStart = i + 1;
            }

            int length = result.Length;
            while (length > 0 && result[length - 1] == '\n') length--;
            result.Length = length;

            return result.ToString();
        }
    }
}
