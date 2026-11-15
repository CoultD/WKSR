using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;

namespace WKRace.Server
{
    // Diagnostics for game updates (0.9.8). Clients from DiagnosticsVersion on are told (ServerFeatures.Diagnostics)
    // and then send their match world's level list (WorldLevels, before MatchReady) and the parts of WKSR that did not
    // load in their game (ClientIssues). Nothing here changes a match: it only writes to the log.
    public sealed partial class ServerCore
    {
        internal const string DiagnosticsVersion = "0.9.8";

        // The level list of a player's world for the current match, filled in part by part.
        private void HandleWorldLevels(Player player, WorldLevels part)
        {
            Match match = player.Match;
            if (match == null || match.Id != part.MatchId || match.Phase != MatchPhase.Preparing) return;
            Slot slot = match.SlotOf(player);
            if (slot == null || !slot.Active) return;
            if (slot.Levels == null || slot.Levels.Length != part.Total) slot.Levels = new WorldLevel?[part.Total];
            for (int i = 0; i < part.Levels.Count; i++) slot.Levels[part.First + i] = part.Levels[i];
        }

        // Two worlds did not match: log where they split, if both players sent their level list.
        private void LogWorldSplit(Match match, Slot a, Slot b)
        {
            List<WorldLevel> levelsA = Complete(a), levelsB = Complete(b);
            if (levelsA == null || levelsB == null)
            {
                _log("match " + Short(match) + ": no level list from " + (levelsA == null ? a.Name : b.Name) + " to compare");
                return;
            }
            int i = WorldLevels.FirstDifference(levelsA, levelsB);
            if (i < 0)
            {
                // Same levels, different fingerprint: the rules or artifact bans differ.
                _log("match " + Short(match) + ": same levels (" + levelsA.Count + "), so the rules or bans differ");
                return;
            }
            _log("match " + Short(match) + ": worlds split at level " + (i + 1) + ": "
                + a.Name + " has " + At(levelsA, i) + " (" + levelsA.Count + " levels, " + BuildTag.GameBuild(a.Player.GameBuild) + "), "
                + b.Name + " has " + At(levelsB, i) + " (" + levelsB.Count + " levels, " + BuildTag.GameBuild(b.Player.GameBuild) + ")");
        }

        private static List<WorldLevel> Complete(Slot slot)
        {
            if (slot.Levels == null || slot.Levels.Any(l => l == null)) return null;
            return slot.Levels.Select(l => l.Value).ToList();
        }

        private static string At(List<WorldLevel> levels, int i)
        {
            return i < levels.Count ? levels[i].ToString() : "no more levels";
        }

        // The parts of WKSR that did not load in a player's game. Logged once per connection.
        private void HandleClientIssues(Player player, ClientIssues issues)
        {
            if (player.IssuesLogged || issues.Items.Count == 0) return;
            player.IssuesLogged = true;
            string who = player.Record != null ? player.Record.Name : "a player";
            _log(who + " (mod " + BuildTag.ShortVersion(player.ClientVersion) + ", game " + BuildTag.GameBuild(player.GameBuild) + "): "
                + issues.Items.Count + (issues.Items.Count == 1 ? " part of WKSR did not load: " : " parts of WKSR did not load: ")
                + string.Join("; ", issues.Items.Select(LogSafe)));
        }

        // Text from a client goes into the log on one line.
        private static string LogSafe(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var chars = text.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
            return new string(chars);
        }
    }
}
