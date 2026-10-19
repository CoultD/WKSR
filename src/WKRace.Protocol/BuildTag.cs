using System;
using System.Collections.Generic;

namespace WKRace.Protocol
{
    // What a player is running, sent when queueing or using lobbies: "steam-25612096|mod-0.2.1".
    // Two players are only paired when every part they both know matches; a part one side does not know matches anything.
    public static class BuildTag
    {
        public static string Compose(string steamBuild, string modVersion)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(steamBuild)) parts.Add(steamBuild.StartsWith("steam-", StringComparison.Ordinal) ? steamBuild : "steam-" + steamBuild);
            string mod = ShortVersion(modVersion);
            if (mod.Length > 0) parts.Add("mod-" + mod);
            return string.Join("|", parts);
        }

        public static bool Compatible(string a, string b)
        {
            Dictionary<string, string> x = Parse(a), y = Parse(b);
            foreach (KeyValuePair<string, string> part in x)
            {
                string other;
                if (y.TryGetValue(part.Key, out other) && other != part.Value) return false;
            }
            return true;
        }

        // Like Compatible, but a different build of the game itself ("steam") is no reason on its own: the server
        // lets players on different game builds play together until their worlds turn out to differ.
        public static bool CompatibleBesidesGame(string a, string b)
        {
            Dictionary<string, string> x = Parse(a), y = Parse(b);
            foreach (KeyValuePair<string, string> part in x)
            {
                string other;
                if (part.Key != "steam" && y.TryGetValue(part.Key, out other) && other != part.Value) return false;
            }
            return true;
        }

        // The game build in a tag ("steam-123|mod-0.9.1" gives "123"), or "".
        public static string GameBuild(string tag)
        {
            string build;
            return Parse(tag).TryGetValue("steam", out build) ? build : "";
        }

        // "0.2.1.9999" -> "0.2.1": test builds of a release race against that release.
        public static string ShortVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return "";
            string[] parts = version.Trim().Split('.');
            return parts.Length > 3 ? string.Join(".", parts, 0, 3) : version.Trim();
        }

        // "0.9.6" or newer (test builds count as their release: "0.9.6.9999" is 0.9.6). Unreadable versions are not.
        public static bool AtLeast(string version, string minimum)
        {
            System.Version have, need;
            return System.Version.TryParse(ShortVersion(version ?? ""), out have) && System.Version.TryParse(minimum, out need) && have >= need;
        }

        // The WKRace version in a build tag ("steam-123|mod-0.9.1" gives "0.9.1"), or "".
        public static string ModVersion(string tag)
        {
            string mod;
            return Parse(tag).TryGetValue("mod", out mod) ? mod : "";
        }

        private static Dictionary<string, string> Parse(string tag)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(tag)) return result;
            foreach (string part in tag.Split('|'))
            {
                int dash = part.IndexOf('-');
                if (dash <= 0 || dash == part.Length - 1) continue;
                result[part.Substring(0, dash)] = part.Substring(dash + 1);
            }
            return result;
        }
    }
}
