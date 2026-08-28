using System;

namespace IZLang.Diagnostics
{
    /// <summary>A range of source code, in absolute character offsets.</summary>
    public readonly struct SourceSpan : IEquatable<SourceSpan>
    {
        public readonly int Start;
        public readonly int Length;

        public int End => Start + Length;

        public SourceSpan(int start, int length)
        {
            if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            Start = start;
            Length = length;
        }

        public static SourceSpan FromBounds(int start, int end) => new SourceSpan(start, end - start);

        /// <summary>Span covering this one and <paramref name="other"/>, including anything in between.</summary>
        public SourceSpan To(SourceSpan other) =>
            FromBounds(Math.Min(Start, other.Start), Math.Max(End, other.End));

        public bool Equals(SourceSpan other) => Start == other.Start && Length == other.Length;
        public override bool Equals(object? obj) => obj is SourceSpan s && Equals(s);
        public override int GetHashCode() => unchecked((Start * 397) ^ Length);
        public override string ToString() => $"[{Start}..{End})";
    }
}
