using System;
using System.Collections.Generic;

namespace WKRace.Protocol
{
    // How far one racer got, for placing party racers (the server's final standings, and the live position the mod
    // and WKSR show). The same rules everywhere, so the live position ends as the final place.
    public sealed class RacerProgress
    {
        public int Racer;
        public string Name = "";
        public bool Finished;
        public int FinishMs = -1;
        // The order finishes reached the server (1 = first), which settles finishers on the same time; 0 when unknown.
        public int FinishOrder;
        // Died or left the race, at OutMs (race time).
        public bool Out;
        public int OutMs = -1;
        // Cheats or an impossible finish: placed last.
        public bool Disqualified;
        // The levels reached in order (split 0 is where the run started), when each was reached, and its region.
        public readonly List<string> Route = new List<string>();
        public readonly List<int> RouteMs = new List<int>();
        public readonly List<string> Regions = new List<string>();

        public int Levels { get { return Route.Count; } }
        public int LastMs { get { return RouteMs.Count == 0 ? 0 : RouteMs[RouteMs.Count - 1]; } }
        public bool Done { get { return Finished || Out || Disqualified; } }

        // In one of the game's shortcut regions right now: its place is unknown until it is back on the main route.
        public bool OnShortcut
        {
            get { return !Done && Regions.Count > 0 && Standings.IsShortcut(Regions[Regions.Count - 1]); }
        }

        public void AddSplit(string level, string region, int timeMs)
        {
            Route.Add(level ?? "");
            Regions.Add(region ?? "");
            RouteMs.Add(timeMs);
        }
    }

    public static class Standings
    {
        // The game's shortcut region ("shortcut") and its sub-regions, which is what splits carry as their region.
        private static readonly string[] ShortcutRegions = { "shortcut", "Tangled Sink", "Expulsion Chute", "Rotten Duct", "Hadal Diver" };

        public static bool IsShortcut(string region)
        {
            if (string.IsNullOrEmpty(region)) return false;
            foreach (string s in ShortcutRegions)
                if (string.Equals(s, region.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Two racers took the same route as far as the shorter of the two went.
        public static bool SameRoute(RacerProgress a, RacerProgress b)
        {
            int n = Math.Min(a.Route.Count, b.Route.Count);
            for (int i = 0; i < n; i++)
                if (!string.Equals(a.Route[i], b.Route[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        // Negative when a is ahead of b. Finishers first, by time. Everyone else: on the same route, more levels and
        // then whoever got there sooner; on different routes, whoever survived longer (someone still climbing has
        // outlasted anyone who is out). Disqualified racers come last.
        //
        // Every time here is a player's own game timer, which runs at different speeds on different routes. So a time
        // is only ever compared with a time of the same kind (finish with finish, the same level with the same level,
        // out with out), never with "now": nowMs is not used any more.
        public static int Compare(RacerProgress a, RacerProgress b, int nowMs)
        {
            if (a.Disqualified != b.Disqualified) return a.Disqualified ? 1 : -1;
            if (a.Finished != b.Finished) return a.Finished ? -1 : 1;
            if (a.Finished)
            {
                if (a.FinishMs != b.FinishMs) return a.FinishMs.CompareTo(b.FinishMs);
                if (a.FinishOrder > 0 && b.FinishOrder > 0) return a.FinishOrder.CompareTo(b.FinishOrder);
            }

            if (SameRoute(a, b))
            {
                if (a.Levels != b.Levels) return b.Levels.CompareTo(a.Levels);
                int sooner = a.LastMs.CompareTo(b.LastMs);
                if (sooner != 0) return sooner;
            }
            else
            {
                if (a.Out != b.Out) return a.Out ? 1 : -1;
                if (a.Out && a.OutMs != b.OutMs) return b.OutMs.CompareTo(a.OutMs);
                if (a.Levels != b.Levels) return b.Levels.CompareTo(a.Levels);
            }
            // Still level: whoever is still in the race, then the lower racer number (so the order never flickers).
            if (a.Out != b.Out) return a.Out ? 1 : -1;
            return a.Racer.CompareTo(b.Racer);
        }

        // Best first. An insertion sort: Compare is not transitive across routes, and this stays well defined and
        // stable for the 15 racers a party holds.
        public static List<RacerProgress> Rank(IEnumerable<RacerProgress> racers, int nowMs)
        {
            var list = new List<RacerProgress>();
            foreach (RacerProgress r in racers)
            {
                int at = list.Count;
                while (at > 0 && Compare(r, list[at - 1], nowMs) < 0) at--;
                list.Insert(at, r);
            }
            return list;
        }

        // "1st", "2nd", "3rd", "4th", ..., "11th", "12th", "13th", "21st".
        public static string Ordinal(int place)
        {
            int tens = place % 100;
            string suffix = tens >= 11 && tens <= 13 ? "th" : (place % 10) == 1 ? "st" : (place % 10) == 2 ? "nd" : (place % 10) == 3 ? "rd" : "th";
            return place + suffix;
        }
    }

    // The game's death-screen lines are written to the player who died ("FELL TO YOUR DEATH"); told about someone
    // else they read "FELL TO THEIR DEATH".
    public static class DeathText
    {
        public static string ForOthers(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string[] words = text.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                string core = w.TrimEnd('.', ',', '!', '?');
                string tail = w.Substring(core.Length);
                string swap = Swap(core);
                if (swap != null) words[i] = swap + tail;
            }
            return string.Join(" ", words);
        }

        private static string Swap(string word)
        {
            string lower = word.ToLowerInvariant();
            string to;
            switch (lower)
            {
                case "your": to = "their"; break;
                case "yours": to = "theirs"; break;
                case "yourself": to = "themselves"; break;
                case "you": to = "they"; break;
                default: return null;
            }
            if (word == word.ToUpperInvariant()) return to.ToUpperInvariant();
            if (char.IsUpper(word[0])) return char.ToUpperInvariant(to[0]) + to.Substring(1);
            return to;
        }
    }

    public static class RulesText
    {
        // "Artifact bans, curated seeds", "Curated seeds (food, perks)", "No modifiers", ...
        public static string Describe(MatchRules rules)
        {
            var parts = new List<string>();
            if ((rules & MatchRules.ArtifactBans) != 0) parts.Add("artifact bans");
            MatchRules curated = rules & MatchRules.CuratedSeeds;
            if (curated == MatchRules.CuratedSeeds) parts.Add("curated seeds");
            else if (curated != 0)
            {
                var some = new List<string>();
                if ((curated & MatchRules.DeepStorageFood) != 0) some.Add("food");
                if ((curated & MatchRules.GuaranteedPerks) != 0) some.Add("perks");
                if ((curated & MatchRules.GuaranteedItems) != 0) some.Add("items");
                parts.Add("curated seeds (" + string.Join(", ", some) + ")");
            }
            if (parts.Count == 0) return "No modifiers";
            string text = string.Join(", ", parts);
            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        public static string Mode(MatchMode mode)
        {
            return mode == MatchMode.Party ? "Party" : "1v1";
        }
    }
}
