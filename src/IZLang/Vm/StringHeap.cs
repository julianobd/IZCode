using System;
using System.Collections.Generic;

namespace IZLang.Vm
{
    /// <summary>
    /// How a <c>str</c> travels inside a <c>double</c>.
    ///
    /// Every IZ value is a <c>double</c>, and a string does not fit in one. So the
    /// slot carries a handle into <see cref="StringHeap"/> instead, encoded as a
    /// quiet NaN with the index in its payload (the same trick a JavaScript engine
    /// calls NaN boxing).
    ///
    /// Two properties come out of that and the whole design leans on them:
    /// a str still copies like any other value, through the same operand stack,
    /// locals, globals and heap cells as a num; and the collector can tell a handle
    /// apart from a number by looking at the bits, which is what makes it possible
    /// to find the live strings in arrays that carry no type tag.
    ///
    /// Arithmetic never touches a handle - the compiler refuses to add a str to a
    /// num - so the payload is never mangled by an FPU operation.
    /// </summary>
    public static class StrValue
    {
        /// <summary>Exponent all ones, quiet bit set, plus a marker of our own.</summary>
        private const long Tag = 0x7FF9000000000000L;

        private const long TagMask = unchecked((long)0xFFFFFFFF00000000UL);
        private const long IndexMask = 0x00000000FFFFFFFFL;

        /// <summary>A zeroed cell is not a handle, which is why the empty string reads back from one.</summary>
        public static double FromIndex(int index) =>
            BitConverter.Int64BitsToDouble(Tag | (index & IndexMask));

        public static bool IsString(double value) =>
            (BitConverter.DoubleToInt64Bits(value) & TagMask) == Tag;

        public static int IndexOf(double value) =>
            (int)(BitConverter.DoubleToInt64Bits(value) & IndexMask);
    }

    /// <summary>
    /// The strings a running program can see.
    ///
    /// The first <c>constantCount</c> slots are the literals the compiler put in the
    /// program: they exist for as long as the program does and are never collected.
    /// Everything after them is built at runtime by a concatenation or by one of the
    /// text builtins.
    ///
    /// Two rules keep it bounded without an allocator to call:
    ///
    /// 1. <b>Interning.</b> The same text always maps to the same slot, so the
    ///    <c>loop { var tag = prefix + "-a"; }</c> that IC10 could never write
    ///    allocates exactly once, no matter how many ticks it runs.
    /// 2. <b>Mark and sweep.</b> When the table fills up, <see cref="IZVm"/> marks
    ///    every handle reachable from the operand stack, the locals, the globals and
    ///    the heap, and the slots nobody points at go back to the free list. It only
    ///    runs when there is no room left, so the ordinary tick never pays for it.
    /// </summary>
    public sealed class StringHeap
    {
        private readonly int _constantCount;
        private readonly List<string?> _slots = new List<string?>();
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Stack<int> _free = new Stack<int>();

        private bool[] _marks = new bool[0];

        public StringHeap(string[] constants)
        {
            if (constants == null) throw new ArgumentNullException(nameof(constants));

            foreach (var text in constants)
            {
                // The slot has to exist even for a repeat, because PushStr addresses
                // the pool by position; only the first of them answers a lookup.
                string entry = text ?? string.Empty;
                if (!_index.ContainsKey(entry)) _index[entry] = _slots.Count;
                _slots.Add(entry);
            }
            _constantCount = _slots.Count;
        }

        /// <summary>Slots in use, the program's own literals included.</summary>
        public int Count => _slots.Count - _free.Count;

        /// <summary>Slots built at runtime and still alive.</summary>
        public int DynamicCount => Count - _constantCount;

        /// <summary>Drops everything the run created; the program's literals stay.</summary>
        public void Reset()
        {
            if (_slots.Count == _constantCount) return;

            for (int i = _constantCount; i < _slots.Count; i++)
            {
                string? text = _slots[i];
                if (text != null) _index.Remove(text);
            }

            _slots.RemoveRange(_constantCount, _slots.Count - _constantCount);
            _free.Clear();
        }

        /// <summary>
        /// The text behind a value. Anything that is not a handle reads as the empty
        /// string, which is what makes a zeroed cell - an uninitialized global, a
        /// fresh struct field - a perfectly usable empty str.
        /// </summary>
        public string Read(double value)
        {
            if (!StrValue.IsString(value)) return string.Empty;

            int index = StrValue.IndexOf(value);
            if ((uint)index >= (uint)_slots.Count) return string.Empty;

            return _slots[index] ?? string.Empty;
        }

        /// <summary>
        /// Hands back the handle for <paramref name="text"/>, reusing the slot when
        /// the same text is already there. False means the table is full: the caller
        /// collects and tries once more.
        /// </summary>
        public bool TryIntern(string text, out double handle)
        {
            if (_index.TryGetValue(text, out int existing))
            {
                handle = StrValue.FromIndex(existing);
                return true;
            }

            int slot;
            if (_free.Count > 0)
            {
                slot = _free.Pop();
                _slots[slot] = text;
            }
            else
            {
                if (_slots.Count >= IZLimits.MaxStrings)
                {
                    handle = 0.0;
                    return false;
                }
                slot = _slots.Count;
                _slots.Add(text);
            }

            _index[text] = slot;
            handle = StrValue.FromIndex(slot);
            return true;
        }

        // ------------------------------------------------------------------
        //  Collection
        // ------------------------------------------------------------------

        /// <summary>Clears the marks. Every root is marked between this and <see cref="Sweep"/>.</summary>
        public void BeginMark()
        {
            if (_marks.Length < _slots.Count) _marks = new bool[_slots.Count];
            else Array.Clear(_marks, 0, _marks.Length);
        }

        /// <summary>Marks the slot a root points at. A value that is not a handle is ignored.</summary>
        public void Mark(double value)
        {
            if (!StrValue.IsString(value)) return;

            int index = StrValue.IndexOf(value);
            if ((uint)index < (uint)_marks.Length) _marks[index] = true;
        }

        /// <summary>Marks a whole array of slots at once - a stack, the locals, the heap.</summary>
        public void Mark(double[] values, int count)
        {
            int limit = Math.Min(count, values.Length);
            for (int i = 0; i < limit; i++) Mark(values[i]);
        }

        /// <summary>Frees every runtime slot nobody marked. Returns how many were freed.</summary>
        public int Sweep()
        {
            int freed = 0;

            for (int i = _constantCount; i < _slots.Count; i++)
            {
                string? text = _slots[i];
                if (text == null || _marks[i]) continue;

                _index.Remove(text);
                _slots[i] = null;
                _free.Push(i);
                freed++;
            }

            return freed;
        }
    }
}
