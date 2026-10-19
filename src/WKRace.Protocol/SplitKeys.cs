using System.Collections.Generic;

namespace WKRace.Protocol
{
    // Splits are compared by level, not by position. Racers can take different routes (the Hadal Diver instead of
    // the Abyss, for example) with different numbers of levels, so "split 12" can be a different place for each of
    // them. A split's key is its level name, plus "#2", "#3"... should the same level come up again in one attempt.
    public static class SplitKeys
    {
        public static string Key(string level, int occurrence)
        {
            string name = level ?? "";
            return occurrence <= 1 ? name : name + "#" + occurrence;
        }

        // Keys for levels listed in the order they were reached.
        public static List<string> Build(IEnumerable<string> levelsInOrder)
        {
            var seen = new Dictionary<string, int>();
            var keys = new List<string>();
            foreach (string level in levelsInOrder)
            {
                string name = level ?? "";
                int n;
                seen.TryGetValue(name, out n);
                seen[name] = ++n;
                keys.Add(Key(name, n));
            }
            return keys;
        }
    }
}
