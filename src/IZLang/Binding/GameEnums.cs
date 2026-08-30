using System;
using System.Collections.Generic;

namespace IZLang.Binding
{
    /// <summary>
    /// The hand-written half of <see cref="GameEnums"/>: how the generated tables
    /// are looked up.
    ///
    /// The game's IC10 groups its named values behind a prefix - Color.Black,
    /// AirCon.Cold, GasType.Oxygen - and IZ spells them exactly the same way, so
    /// what a player already knows from the wiki keeps working here. Every value
    /// is a plain number: the group only says which table to read it from.
    /// </summary>
    public static partial class GameEnums
    {
        /// <summary>The names that can appear before the dot, in the game's own order.</summary>
        public static IEnumerable<string> ConstantGroupNames => _ConstantGroups.Keys;

        /// <summary>Is <paramref name="name"/> one of the game's constant groups?</summary>
        public static bool IsConstantGroup(string? name) =>
            name != null && _ConstantGroups.ContainsKey(name);

        /// <summary>The values of one group, or null when there is no such group.</summary>
        public static IReadOnlyDictionary<string, int>? FindConstantGroup(string? name) =>
            name != null && _ConstantGroups.TryGetValue(name, out var group) ? group : null;

        /// <summary>Resolves 'Group.Member' to its number.</summary>
        public static bool TryGetConstant(string? group, string? member, out int value)
        {
            value = 0;
            if (group == null || member == null) return false;
            return _ConstantGroups.TryGetValue(group, out var values) &&
                   values.TryGetValue(member, out value);
        }

        /// <summary>
        /// "; did you mean 'X'?" for a name that is close to one of the candidates,
        /// or an empty string when nothing is close enough.
        ///
        /// A typo is the most common mistake in these lists - LogicType alone has
        /// hundreds of names - so pointing at the nearest one is worth the pass.
        /// </summary>
        public static string Suggest(IEnumerable<string> candidates, string name)
        {
            string? best = FindClosest(candidates, name);
            return best != null ? "; did you mean '" + best + "'?" : string.Empty;
        }

        /// <summary>The closest candidate within three edits, or null.</summary>
        public static string? FindClosest(IEnumerable<string> candidates, string name)
        {
            const int Limit = 3;

            string? best = null;
            int bestDistance = int.MaxValue;

            foreach (var candidate in candidates)
            {
                if (Math.Abs(candidate.Length - name.Length) > Limit) continue;
                int distance = EditDistance(name, candidate, bestDistance);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return bestDistance <= Limit ? best : null;
        }

        /// <summary>Levenshtein with a cutoff: stops as soon as it goes past <paramref name="limit"/>.</summary>
        private static int EditDistance(string a, string b, int limit)
        {
            int lengthA = a.Length, lengthB = b.Length;
            if (Math.Abs(lengthA - lengthB) >= limit) return limit;

            var previous = new int[lengthB + 1];
            var current = new int[lengthB + 1];
            for (int j = 0; j <= lengthB; j++) previous[j] = j;

            for (int i = 1; i <= lengthA; i++)
            {
                current[0] = i;
                int rowMin = current[0];

                for (int j = 1; j <= lengthB; j++)
                {
                    int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                    if (current[j] < rowMin) rowMin = current[j];
                }

                if (rowMin >= limit) return limit;

                var swap = previous;
                previous = current;
                current = swap;
            }

            return previous[lengthB];
        }
    }
}
