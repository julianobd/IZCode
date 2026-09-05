using System;

namespace IZLang.Binding
{
    /// <summary>
    /// Up to six ASCII characters carried inside one number.
    ///
    /// This is the game's own packing, the one behind IC10's <c>str"..."</c>: each
    /// character becomes one byte, the first character in the most significant one.
    /// A LED display whose Mode is <c>DisplayMode.String</c> reads its Setting back
    /// this way, which is what lets a display show "Ok" instead of digits.
    ///
    /// It is not the CRC32 of <see cref="PrefabHash"/>: that one identifies a prefab
    /// or a label and cannot be turned back into text.
    /// </summary>
    public static class PackedText
    {
        /// <summary>Characters that fit: six bytes, which is what the game accepts.</summary>
        public const int MaxLength = 6;

        /// <summary>Highest character code one byte holds.</summary>
        private const char MaxAscii = (char)0x7F;

        /// <summary>Can <paramref name="text"/> be packed at all?</summary>
        public static bool CanPack(string? text)
        {
            if (string.IsNullOrEmpty(text) || text!.Length > MaxLength) return false;

            for (int i = 0; i < text.Length; i++)
                if (text[i] > MaxAscii) return false;

            return true;
        }

        /// <summary>
        /// The packed value. Text that does not fit is truncated to the first
        /// <see cref="MaxLength"/> characters and non ASCII characters are dropped:
        /// the callers check with <see cref="CanPack"/> first and report it properly,
        /// and this must not throw inside a running chip.
        /// </summary>
        public static double Pack(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0.0;

            long packed = 0L;
            int written = 0;

            for (int i = 0; i < text!.Length && written < MaxLength; i++)
            {
                char c = text[i];
                if (c > MaxAscii) continue;

                packed = (packed << 8) | (byte)c;
                written++;
            }

            return packed;
        }

        /// <summary>
        /// The text back out of a packed value, the way the game unpacks it: the
        /// bytes above the highest non zero one are not part of the string.
        /// </summary>
        public static string Unpack(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return string.Empty;

            ulong bits = unchecked((ulong)(long)(value % 9007199254740992.0));

            int length = 0;
            for (ulong rest = bits; rest != 0UL && length < 8; rest >>= 8) length++;
            if (length == 0) return string.Empty;

            var characters = new char[length];
            for (int i = length - 1; i >= 0; i--)
            {
                characters[i] = (char)(bits & 0xFF);
                bits >>= 8;
            }
            return new string(characters);
        }
    }
}
