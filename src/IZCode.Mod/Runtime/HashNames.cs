using System.Collections.Generic;
using IZCode.Mod.Devices;
using IZLang.Binding;
using IZLang.Vm;

namespace IZCode.Mod.Runtime
{
    /// <summary>
    /// Turns a prefab or label hash back into the text it came from, for the log.
    ///
    /// A hash is one way on purpose, so this is a lookup and not an inverse: every
    /// name the program mentions is in its own string pool - the compiler interns the
    /// prefab of every <c>all(...)</c>, every <c>#"..."</c> and the label of every
    /// <c>named(...)</c> - and the device catalog covers the prefabs the program never
    /// names. A hash neither of them knows is printed as the number, which is still
    /// better than nothing.
    ///
    /// It exists because a warning like "prefab 1311586884 label 542067799" tells the
    /// player nothing they can act on: the two names are what they typed.
    /// </summary>
    public sealed class HashNames
    {
        /// <summary>Nothing to look up; every hash prints as its number.</summary>
        public static readonly HashNames Empty = new HashNames(null);

        private readonly Dictionary<int, string> _byHash = new Dictionary<int, string>();

        public HashNames(IZProgram? program)
        {
            if (program?.Strings == null) return;

            foreach (string? text in program.Strings)
            {
                if (string.IsNullOrEmpty(text)) continue;

                // The first name for a hash wins: a collision is not worth a guess,
                // and the program's own order is the least surprising tie break.
                int hash = PrefabHash.Compute(text!);
                if (!_byHash.ContainsKey(hash)) _byHash.Add(hash, text!);
            }
        }

        /// <summary>The name behind a hash, or null when nothing knows it.</summary>
        public string? Find(int hash)
        {
            if (_byHash.TryGetValue(hash, out string? text)) return text;

            var device = CatalogStore.Current.FindByHash(hash);
            return device?.PrefabName;
        }

        /// <summary>
        /// A prefab for a log line: the name with the hash beside it, so a report can
        /// still be matched against a script that says the number.
        /// </summary>
        public string DescribePrefab(int hash)
        {
            // 0 is what named(...) with no prefab pushes: it matches anything.
            if (hash == 0) return "any prefab";

            string? name = Find(hash);
            return name == null ? "prefab " + hash : "prefab " + name + " (" + hash + ")";
        }

        public string DescribeLabel(int hash)
        {
            string? name = Find(hash);
            return name == null ? "label " + hash : "label \"" + name + "\" (" + hash + ")";
        }
    }
}
