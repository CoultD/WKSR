using System;
using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;

namespace WKRace.Server
{
    // The online player list and profiles (official server only: the Steam lobby host has neither). The list shows
    // who is signed in and what they are doing; a profile shows a player's ranked record, their run counts
    // (ServerCore.Stats) and their places on the leaderboards.
    public sealed partial class ServerCore
    {
        private const int OnlineListGapMs = 500;
        private const int ProfileGapMs = 250;

        private bool PlayerListEnabled { get { return true; } }

        private static PlayerActivity ActivityOf(Player p)
        {
            if (p.Solo != null) return PlayerActivity.RankedSolo;
            if (p.Match != null) return p.Match.Lobby != null ? PlayerActivity.LobbyInGame : PlayerActivity.RankedInGame;
            if (p.Queue != null) return PlayerActivity.RankedSearching;
            if (p.Lobby != null) return PlayerActivity.Lobby;
            return PlayerActivity.Online;
        }

        // In a match or ranked solo run that is under way.
        private static bool Watchable(Player p)
        {
            if (p.Solo != null) return p.Solo.Host.Running;
            Match m = p.Match;
            if (m == null) return false;
            if (m.Survivor != null) return m.Survivor.Player == p;
            return m.ClockStarted;
        }

        // The lobby `p` is in, when `asker` could join it right now from the list (else 0).
        private static uint JoinableLobby(Player p, Player asker)
        {
            Lobby l = p.Lobby;
            if (l == null || !l.Public || l.Match != null || l.Members.Count >= l.MaxPlayers || asker.Lobby == l) return 0;
            return BuildTag.ShortVersion(l.Host.ClientVersion) == BuildTag.ShortVersion(asker.ClientVersion) ? l.Id : 0;
        }

        private void HandleOnlineListRequest(Player player, OnlineListRequest request)
        {
            long now = _now();
            if (now - player.LastOnlineListMs < OnlineListGapMs && now >= player.LastOnlineListMs) return;
            player.LastOnlineListMs = now;

            // The player who asks first, then everyone by name.
            List<Player> online = _players.Values.Where(p => p.Conn != null)
                .OrderBy(p => p == player ? 0 : 1)
                .ThenBy(p => p.Record.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            int offset = Math.Min(request.Offset, Math.Max(0, online.Count - 1));
            var list = new OnlineList { Total = (ushort)Math.Min(online.Count, ushort.MaxValue), Offset = (ushort)offset };
            foreach (Player p in online.Skip(offset).Take(OnlineList.PageSize))
            {
                list.Entries.Add(new OnlineEntry
                {
                    Tag = p.Tag,
                    Name = p.Record.Name,
                    Activity = ActivityOf(p),
                    LobbyId = p == player ? 0 : JoinableLobby(p, player),
                    Watchable = p != player && Watchable(p),
                    You = p == player,
                });
            }
            player.Send(list);
        }

        private void HandleProfileRequest(Player player, ProfileRequest request)
        {
            long now = _now();
            if (now - player.LastProfileMs < ProfileGapMs && now >= player.LastProfileMs) return;
            player.LastProfileMs = now;

            Player who = _players.Values.FirstOrDefault(p => p.Tag == request.Tag);
            if (who == null)
            {
                player.Send(new Profile { Tag = request.Tag });
                return;
            }
            PlayerRecord r = who.Record;
            var profile = new Profile
            {
                Tag = request.Tag,
                Found = true,
                Name = r.Name,
                Rating = r.Rating,
                Wins = r.Wins,
                Losses = r.Losses,
                RankedRuns = r.RankedRuns,
                UnrankedRuns = r.UnrankedRuns,
                Resets = r.Resets,
            };
            var boards = new List<BoardKind> { BoardKind.Rating, BoardKind.DuelTime, BoardKind.SoloTime };
            foreach (SegmentInfo segment in Segments.All) boards.Add(Segments.Board(segment.Id));
            foreach (BoardKind kind in boards)
            {
                List<BoardEntry> view = BoardView(kind, now);
                int index = view.FindIndex(e => e.Id == r.Id);
                if (index >= 0 && profile.Places.Count < Profile.MaxPlaces)
                    profile.Places.Add(new ProfilePlace { Board = kind, Rank = index + 1, Value = view[index].Value });
            }
            player.Send(profile);
        }
    }
}
