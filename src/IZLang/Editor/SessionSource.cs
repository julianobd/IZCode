using System;

namespace IZLang.Editor
{
    /// <summary>
    /// Which text the code area shows when it switches on.
    ///
    /// The game's editor holds 128 lines of 90 characters and an IZ program can be
    /// longer than that, so what those lines hold is only a cut-down copy of it. The
    /// program itself is remembered for as long as the editor stays open, and the
    /// question every time the code area comes back is whether that memory is still
    /// about the program in the editor, or about one that has since been replaced.
    ///
    /// The answer is decided here, in pure code, because getting it wrong either
    /// throws away everything past the 128th line or brings back a program the
    /// player had already replaced.
    /// </summary>
    public static class SessionSource
    {
        /// <summary>
        /// <paramref name="lines"/> is what the game's editor holds now,
        /// <paramref name="remembered"/> the program of this session and
        /// <paramref name="copy"/> what the lines held when it was stored.
        /// </summary>
        public static string Resolve(string? lines, string? remembered, string? copy)
        {
            string current = lines ?? string.Empty;
            if (remembered == null || copy == null) return current;

            // Nothing has touched the lines: the program is still ours, whole.
            if (string.Equals(current, copy, StringComparison.Ordinal)) return remembered;

            // The marker is on the first line, so that is the line that changes when
            // the player deletes it and types it again - and between those two moments
            // the code area was off, with the game's editor showing only the first 128
            // lines. Everything below the marker being untouched is enough to know it
            // is the same program: it comes back whole, with the first line as the
            // editor now has it.
            if (string.Equals(Below(current), Below(copy), StringComparison.Ordinal))
            {
                string below = Below(remembered);
                return below.Length == 0 ? First(current) : First(current) + "\n" + below;
            }

            // Something else wrote to the lines. They, not the memory, are the program.
            return current;
        }

        /// <summary>
        /// Whether the remembered program still describes what the editor holds.
        ///
        /// This is exactly the negation of <see cref="Resolve"/>'s last case, kept
        /// apart because two questions are asked of the same state: which text to
        /// show when the code area switches on, and whether the memory - rather than
        /// the game's 128 cut-down lines - is what leaves the editor when the player
        /// saves. Answering the second one with a stricter rule of its own is how a
        /// program came to be shown whole and saved truncated.
        /// </summary>
        public static bool KeepsMemory(string? lines, string? copy)
        {
            if (copy == null) return false;

            string current = lines ?? string.Empty;

            return string.Equals(current, copy, StringComparison.Ordinal) ||
                   string.Equals(Below(current), Below(copy), StringComparison.Ordinal);
        }

        /// <summary>The first line, without its line break.</summary>
        private static string First(string text)
        {
            int end = text.IndexOf('\n');
            return end < 0 ? text : text.Substring(0, end);
        }

        /// <summary>Everything after the first line, empty when there is only one.</summary>
        private static string Below(string text)
        {
            int end = text.IndexOf('\n');
            return end < 0 ? string.Empty : text.Substring(end + 1);
        }
    }
}
