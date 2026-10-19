using System;
using System.Text;

namespace WKRace.Server
{
    // A player and a finished match as the core sees them, with no storage in them: shared with the lobby host
    // inside the game (Steam lobbies), which keeps them in memory only.
    public sealed class PlayerRecord
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string TokenHash { get; set; } = "";
        public int Rating { get; set; } = Elo.StartRating;
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int BestTimeMs { get; set; } = -1;
        public long CreatedUnixMs { get; set; }
        public long LastSeenUnixMs { get; set; }
        // Keyed hash of the linked Steam account (see ServerCore.Steam), or null.
        public string SteamHash { get; set; }
        public bool Banned { get; set; }
        public string BanReason { get; set; }
        // Set by an admin rename: the server keeps this name whatever the client sends.
        public bool NameLocked { get; set; }
    }

    public static class Elo
    {
        public const int StartRating = 1000;
        public const int MinRating = 100;
        // Every ranked match moves both ratings by the same K (there is no provisional period, from 0.9.6).
        public const int K = 32;

        // scoreA is 1 for an A win and 0 for a loss.
        public static void Update(ref int ratingA, ref int ratingB, double scoreA)
        {
            double expectedA = 1.0 / (1.0 + Math.Pow(10.0, (ratingB - ratingA) / 400.0));
            int delta = (int)Math.Round(K * (scoreA - expectedA));
            ratingA = Math.Max(MinRating, ratingA + delta);
            ratingB = Math.Max(MinRating, ratingB - delta);
        }
    }

    public static class NameSanitizer
    {
        private const string AllowedPunctuation = "_-.[]()!'";

        // Returns null when nothing usable is left.
        public static string CleanName(string raw)
        {
            if (raw == null) return null;
            var sb = new StringBuilder();
            bool lastWasSpace = true;
            foreach (char c in raw)
            {
                bool allowed = char.IsLetterOrDigit(c) || AllowedPunctuation.IndexOf(c) >= 0;
                if (allowed)
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
                else if (c == ' ' && !lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                if (sb.Length >= WKRace.Protocol.ProtocolConstants.MaxNameLength) break;
            }
            string name = sb.ToString().Trim();
            return name.Length >= 2 ? name : null;
        }

        // Level and region names are relayed to the opponent's HUD, which renders rich text: '<' and '>' are removed
        // so a modified client cannot inject tags such as <size=500>.
        public static string CleanLabel(string raw)
        {
            if (raw == null) return "";
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (c == '<' || c == '>') continue;
                sb.Append(c < 32 || c == 127 || char.IsSurrogate(c) ? '?' : c);
            }
            return sb.ToString();
        }
    }

    // One finished match, as stored.
    public sealed class MatchEntry
    {
        public Guid Id;
        public int Seed;
        public bool Ranked;
        public long EndedUnixMs;
        public string Reason = "";
        public bool Aborted;
        public string WorldHashes = "";
        // [{"item":"...","by":"..."}] for a match with artifact bans, else null.
        public string BansJson;
        public MatchSide A;
        public MatchSide B;
    }

    public sealed class MatchSide
    {
        public Guid Id;
        public string Name = "";
        public string Outcome = "";
        public int TimeMs = -1;
        public int Rating;
    }
}
