using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using WKRace.Protocol;

namespace WKRace.Client
{
    // One player in the online list.
    internal sealed class OnlineRowModel
    {
        public uint Tag { get; set; }
        public string Name { get; set; }
        public string Activity { get; set; }
        public Brush Brush { get; set; }
        public bool You { get; set; }
        // Their lobby, when it is public and can be joined right now (else 0).
        public uint LobbyId { get; set; }
        public bool Watchable { get; set; }
    }

    // The online player list and profiles: the official server's only (never in STEAM LOBBIES), when it says it has
    // them (ServerFeatures). Clicking a player opens their card: PROFILE, MUTE or UNMUTE (their chat lines are hidden),
    // and JOIN GAME when their lobby is public and open.
    internal sealed partial class MainViewModel
    {
        // The list refreshes itself this often while its page is open.
        private static readonly TimeSpan PlayersRefresh = TimeSpan.FromSeconds(5);

        private bool _playerList;
        private ushort _playersOffset;
        private int _playersTotal;
        private DateTime _playersAskedAt;
        private OnlineRowModel _selectedPlayer;
        private Profile _profile;
        private uint _profileAsked;
        private string _playersStatus = "";

        public ObservableCollection<OnlineRowModel> PlayerRows { get; } = new ObservableCollection<OnlineRowModel>();
        public string PlayersStatus { get { return _playersStatus; } private set { Set(ref _playersStatus, value); } }

        // The PLAYERS page is offered.
        public bool HasPlayerList { get { return !SteamMode && _playerList && _link == LinkState.Connected; } }

        public OnlineRowModel SelectedPlayer { get { return _selectedPlayer; } }

        public bool PlayersHasPrevious { get { return _playersOffset > 0; } }
        public bool PlayersHasNext { get { return _playersOffset + OnlineList.PageSize < _playersTotal; } }

        public void RefreshPlayers()
        {
            if (!HasPlayerList) return;
            _playersAskedAt = DateTime.UtcNow;
            _server.Send(new OnlineListRequest { Offset = _playersOffset });
        }

        // Called while the page is open.
        public void KeepPlayersFresh()
        {
            if (DateTime.UtcNow - _playersAskedAt >= PlayersRefresh) RefreshPlayers();
        }

        public void PlayersPage(int step)
        {
            int offset = Math.Max(0, _playersOffset + step * OnlineList.PageSize);
            if (offset >= _playersTotal && step > 0) return;
            _playersOffset = (ushort)offset;
            RefreshPlayers();
        }

        public void SelectPlayer(OnlineRowModel row)
        {
            _selectedPlayer = _selectedPlayer != null && row != null && _selectedPlayer.Tag == row.Tag ? null : row;
        }

        private void OnOnlineList(OnlineList list)
        {
            _playersTotal = list.Total;
            _playersOffset = list.Offset;
            PlayerRows.Clear();
            foreach (OnlineEntry e in list.Entries)
            {
                PlayerRows.Add(new OnlineRowModel
                {
                    Tag = e.Tag,
                    Name = e.Name,
                    Activity = ActivityText(e.Activity),
                    Brush = e.You ? Theme.Good : e.Activity == PlayerActivity.Online ? Theme.Accent : Theme.Warn,
                    You = e.You,
                    LobbyId = e.LobbyId,
                    Watchable = e.Watchable,
                });
            }
            // The card stays on the same player while they are still listed (with what they do now).
            if (_selectedPlayer != null) _selectedPlayer = PlayerRows.FirstOrDefault(r => r.Tag == _selectedPlayer.Tag);
            int last = Math.Min(_playersTotal, _playersOffset + PlayerRows.Count);
            PlayersStatus = _playersTotal == 1 ? "Only you are online right now."
                : _playersTotal + " players online" + (_playersTotal > OnlineList.PageSize ? " (" + (_playersOffset + 1) + "-" + last + ")" : "") + ".";
        }

        public static string ActivityText(PlayerActivity activity)
        {
            switch (activity)
            {
                case PlayerActivity.RankedSearching: return "RANKED - SEARCHING";
                case PlayerActivity.RankedInGame: return "RANKED - IN GAME";
                case PlayerActivity.RankedSolo: return "RANKED SOLO - IN GAME";
                case PlayerActivity.Lobby: return "IN A LOBBY";
                case PlayerActivity.LobbyInGame: return "LOBBY - IN GAME";
                case PlayerActivity.Spectating: return "SPECTATING";
                default: return "ONLINE";
            }
        }

        // ---- the card's buttons -----------------------------------------------------------------------------

        public bool IsMuted(string name)
        {
            return _settings.Muted.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }

        // MUTE / UNMUTE: the player's chat lines (and "is typing") are hidden, in every room.
        public void ToggleMute(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            bool muted = IsMuted(name);
            _settings.Muted.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (!muted) _settings.Muted.Add(name);
            _settings.Save();
            if (!muted) foreach (Dictionary<string, DateTime> room in _typers.Values) room.Remove(name);
            Log(muted ? name + " is no longer muted." : name + " is muted: their chat lines are hidden.");
        }

        public bool CanJoinFromList(OnlineRowModel row)
        {
            return row != null && row.LobbyId != 0 && _phase != ClientPhase.InMatch && !(_hasLobby && _lobbyId == row.LobbyId);
        }

        public void JoinFromList(OnlineRowModel row)
        {
            if (CanJoinFromList(row)) JoinListedLobby(row.LobbyId);
        }

        // ---- profiles ---------------------------------------------------------------------------------------

        public Profile ShownProfile { get { return _profile != null && _profile.Tag == _profileAsked ? _profile : null; } }

        public void RequestProfile(OnlineRowModel row)
        {
            if (row == null || !HasPlayerList) return;
            _profileAsked = row.Tag;
            _profile = null;
            _server.Send(new ProfileRequest { Tag = row.Tag });
        }

        private void OnProfile(Profile profile)
        {
            _profile = profile;
        }

        // The profile as lines of label and value.
        public List<KeyValuePair<string, string>> ProfileLines()
        {
            var lines = new List<KeyValuePair<string, string>>();
            Profile p = ShownProfile;
            if (p == null || !p.Found) return lines;
            ProfilePlace best = p.Places.Where(x => x.Board == BoardKind.DuelTime || x.Board == BoardKind.SoloTime).OrderBy(x => x.Value).FirstOrDefault();
            lines.Add(Line("BEST TIME", best != null ? RaceFormat.Time(best.Value) + (best.Board == BoardKind.SoloTime ? " (ranked solo)" : " (ranked 1v1)") : "-"));
            lines.Add(Line("RATING", p.Rating.ToString()));
            int games = p.Wins + p.Losses;
            lines.Add(Line("RANKED 1V1", p.Wins + "-" + p.Losses + (games > 0 ? "   " + (100.0 * p.Wins / games).ToString("0.#") + "% won" : "")));
            lines.Add(Line("RUNS FINISHED", p.RankedRuns + " ranked   " + p.UnrankedRuns + " unranked"));
            lines.Add(Line("RESETS", p.Resets.ToString()));
            foreach (ProfilePlace place in p.Places)
                lines.Add(Line(BoardLabel(place.Board), "#" + place.Rank + "   " + (place.Board == BoardKind.Rating ? place.Value.ToString() : RaceFormat.Time(place.Value))));
            if (p.Places.Count == 0) lines.Add(Line("LEADERBOARDS", "Not on a leaderboard yet."));
            return lines;
        }

        public string ProfileStatus
        {
            get
            {
                Profile p = ShownProfile;
                if (p == null) return "Loading the profile...";
                return p.Found ? "" : "That player is no longer online.";
            }
        }

        private static KeyValuePair<string, string> Line(string label, string value)
        {
            return new KeyValuePair<string, string>(label, value);
        }

        private static string BoardLabel(BoardKind kind)
        {
            SegmentInfo segment = Segments.Find(Segments.OfBoard(kind));
            if (segment != null) return segment.Name + " SEGMENT";
            return kind == BoardKind.Rating ? "RATING BOARD" : kind == BoardKind.DuelTime ? "1V1 TIMES" : "SOLO TIMES";
        }

        // ---- run counts -------------------------------------------------------------------------------------

        // An unranked solo run on this PC finished or restarted: the server counts it for the profile (the server
        // counts every other kind of run itself).
        private void NoteRun(byte kind, byte segment)
        {
            if (HasPlayerList) _server.Send(new RunNote { Kind = kind, Segment = segment });
        }
    }
}
