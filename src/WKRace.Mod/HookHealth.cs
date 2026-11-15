using System.Collections.Generic;

namespace WKRace.Mod
{
    // The parts of WKSR that hook into the game and did not load: a game update renamed or changed what they hook.
    // Before 0.9.8 such a failure only went to BepInEx's log, so a broken hook could go unnoticed for weeks (the Deep
    // Storage food rule after game 0.62a). Now each one is logged, listed on the WKSR window's first page and reported to
    // the match server, which logs it with the player's game build.
    internal static class HookHealth
    {
        private const int MaxFailures = 32;
        private static readonly List<string> Failed = new List<string>();

        public static int Count { get { return Failed.Count; } }

        public static IList<string> Failures { get { return Failed.AsReadOnly(); } }

        // part: what stopped working (e.g. "WorldLoader.Initialize"); why: the reason in a few words.
        public static void Fail(string part, string why)
        {
            string line = part + (string.IsNullOrEmpty(why) ? "" : " (" + why + ")");
            Plugin.Log.LogError("WKSR part did not load: " + line + ". The game may have been updated.");
            if (Failed.Count < MaxFailures && !Failed.Contains(line)) Failed.Add(line);
        }

        public static string Summary()
        {
            if (Failed.Count == 0) return "";
            return Failed.Count == 1
                ? "1 part of WKSR did not load (" + Failed[0] + ")."
                : Failed.Count + " parts of WKSR did not load (" + Failed[0] + ", ...).";
        }
    }
}
