using System;
using System.Collections.Generic;

namespace WKRace.Protocol
{
    // The save every player's game reads while it builds a match world, in place of the player's own.
    //
    // Since game 0.62a (26 Oct 2026) world generation reads the player's save: a section is forced into the climb until
    // the player has visited it ("hasvisited-<section id>"), sections need progress flags such as ACH_NEST, and spawn
    // rules can compare lifetime stats. Two players with different progress then build different worlds from the same
    // seed. In a match the game reads this fixed "veteran" save instead: every section visited (nothing is forced; each
    // section appears at its normal chance), every achievement and progress unlock earned. A flag that is not known here
    // reads as not set, the same for everyone, so dev and test content never switches on.
    public static class MatchSave
    {
        // Lifetime stats in spawn rules ("has died more than N times") read as this: large, so a veteran's answer.
        public const int LifetimeStat = int.MaxValue / 2;

        // Progress flags a fully progressed save has that are neither visits nor achievements (shortcut doors opened,
        // areas unlocked, intros already seen).
        private static readonly HashSet<string> Unlocks = new HashSet<string>(StringComparer.Ordinal)
        {
            "habentryunlocked", "unlockedsink", "unlockedduct", "hadaldiverunlocked", "chutedoorunlocked",
            "playgroundaccess", "upg_recycler",
        };

        // Achievement flags that are not earned by playing.
        private static readonly HashSet<string> NotEarned = new HashSet<string>(StringComparer.Ordinal) { "ACH_TEST" };

        public static bool Flag(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("hasvisited", StringComparison.Ordinal)) return true;
            if (name.StartsWith("hasexperienced", StringComparison.Ordinal)) return true;
            if (name.StartsWith("ACH_", StringComparison.Ordinal)) return !NotEarned.Contains(name);
            return Unlocks.Contains(name);
        }

        // Flags that belong to the current run and are simply not set yet; reading them as not set is expected.
        public static bool IsRunFlag(string name)
        {
            return name != null && (name.StartsWith("hasgeneratedthisrun", StringComparison.Ordinal)
                || name.StartsWith("session-", StringComparison.Ordinal));
        }
    }
}
