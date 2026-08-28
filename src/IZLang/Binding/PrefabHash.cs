namespace IZLang.Binding
{
    /// <summary>
    /// Prefab name hashing.
    ///
    /// Stationeers identifies prefabs by the CRC-32 of their name, read as a
    /// SIGNED 32-bit integer - which is why hashes show up negative in the game.
    /// We use CRC-32/ISO-HDLC (reflected polynomial 0xEDB88320, init and xorout
    /// 0xFFFFFFFF), the same one zlib and PNG use.
    /// </summary>
    public static class PrefabHash
    {
        private const uint Polynomial = 0xEDB88320u;
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
                table[i] = crc;
            }
            return table;
        }

        /// <summary>Raw, unsigned CRC-32.</summary>
        public static uint ComputeUnsigned(string text)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < text.Length; i++)
            {
                // Prefab names are always ASCII; the low byte is enough.
                byte b = (byte)text[i];
                crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
            }
            return crc ^ 0xFFFFFFFFu;
        }

        /// <summary>The value as the game exposes it: signed 32 bits.</summary>
        public static int Compute(string text) => unchecked((int)ComputeUnsigned(text));
    }
}
