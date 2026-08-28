using System;
using System.Collections.Generic;

namespace IZLang.Diagnostics
{
    /// <summary>
    /// The program text plus a line-start index, used to turn an absolute offset
    /// into (line, column) - what the in-game editor needs to display.
    /// </summary>
    public sealed class SourceText
    {
        private readonly int[] _lineStarts;

        public string Text { get; }
        public int LineCount => _lineStarts.Length;

        public SourceText(string text)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            _lineStarts = ComputeLineStarts(text);
        }

        private static int[] ComputeLineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    // \r\n counts as a single break.
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    starts.Add(i + 1);
                }
                else if (c == '\n')
                {
                    starts.Add(i + 1);
                }
            }
            return starts.ToArray();
        }

        /// <summary>Zero-based index of the line containing <paramref name="position"/>.</summary>
        public int GetLineIndex(int position)
        {
            if (position < 0) return 0;
            int lo = 0, hi = _lineStarts.Length - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                int start = _lineStarts[mid];
                if (position == start) return mid;
                if (position < start) hi = mid - 1;
                else lo = mid + 1;
            }
            // lo overshot: the correct line is the previous one.
            return Math.Max(0, lo - 1);
        }

        public int GetLineStart(int lineIndex) => _lineStarts[Math.Max(0, Math.Min(lineIndex, _lineStarts.Length - 1))];

        public int GetLineEnd(int lineIndex)
        {
            if (lineIndex + 1 < _lineStarts.Length)
            {
                int next = _lineStarts[lineIndex + 1];
                // step back over the line terminator
                int e = next;
                if (e > 0 && e - 1 < Text.Length && Text[e - 1] == '\n') e--;
                if (e > 0 && e - 1 < Text.Length && Text[e - 1] == '\r') e--;
                return e;
            }
            return Text.Length;
        }

        public string GetLineText(int lineIndex)
        {
            int s = GetLineStart(lineIndex);
            int e = GetLineEnd(lineIndex);
            return Text.Substring(s, Math.Max(0, e - s));
        }

        /// <summary>One-based line and column, ready to show to the player.</summary>
        public LinePosition GetLinePosition(int position)
        {
            int line = GetLineIndex(position);
            return new LinePosition(line + 1, position - _lineStarts[line] + 1);
        }
    }

    public readonly struct LinePosition
    {
        public readonly int Line;
        public readonly int Column;
        public LinePosition(int line, int column) { Line = line; Column = column; }
        public override string ToString() => $"{Line}:{Column}";
    }
}
