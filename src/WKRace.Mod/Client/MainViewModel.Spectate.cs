using WKRace.Protocol;

namespace WKRace.Client
{
    using Message = WKRace.Protocol.Message;

    // Spectating (the game does the watching itself: RaceController.Spectate). WKSR asks the server to watch a player
    // (SPECTATE on their card in the online list) or a lobby's match (WATCH in the lobby browser), passes what the
    // server sends about the watched match on to the game, and offers STOP SPECTATING. A player out of their own match
    // watches the rest of it from the game (a death there), through here too.
    internal sealed partial class MainViewModel
    {
        // The match server can do it (ServerFeatures).
        private bool _spectateKnown;
        // What is being watched from outside (not the player's own match), or null.
        private SpectateStart _watching;
        private string _watchingName = "";
        // The lobby being watched (from the lobby browser), or null; and one to watch once connected (a Steam lobby's
        // host, or the match server again after a lost connection).
        private WatchedLobby _watchedLobby;
        private uint _pendingWatch;

        public bool CanSpectate { get { return _spectateKnown && _link == LinkState.Connected && _phase != ClientPhase.InMatch && !_matchActive; } }
        public bool IsWatching { get { return _watching != null || _watchedLobby != null; } }
        public WatchedLobby WatchedLobbyState { get { return _watchedLobby; } }
        public bool CanJoinWatched { get { return _watchedLobby != null && _watchedLobby.CanJoin && _watching == null && _phase != ClientPhase.InMatch; } }

        public string WatchingText
        {
            get
            {
                WatchedLobby w = _watchedLobby;
                if (w != null)
                    return "Watching " + w.Host + "'s " + (w.Mode == MatchMode.Party ? "party" : "1v1") + " lobby: " + w.Members.Count + "/" + w.MaxPlayers + " players"
                           + (w.Racing ? ", in a match" + (_watching == null ? " (you watch from the next start)" : "") : w.CanJoin ? ", a spot is free" : ", full")
                           + (w.Spectators > 1 ? ", " + w.Spectators + " watching" : "") + ".";
                return _watching == null ? "" : "Watching " + (_watchingName.Length > 0 ? _watchingName : "a match") + ".";
            }
        }

        public void SpectatePlayer(OnlineRowModel row)
        {
            if (row == null || !row.Watchable || !CanSpectate) return;
            _watchingName = row.Name;
            Log("Asking to watch " + row.Name + "...");
            _server.Send(new SpectateRequest { Tag = row.Tag });
        }

        // A public lobby from the browser (the match server's; Steam lobbies: WatchSteamLobby).
        public void SpectateLobby(uint lobbyId, string host)
        {
            if (!CanSpectate) return;
            _watchingName = host + "'s lobby";
            Log("Asking to watch " + _watchingName + "...");
            _server.Send(new SpectateRequest { LobbyId = lobbyId });
        }

        public void StopSpectating()
        {
            if (!IsWatching) return;
            _server?.Send(new SpectateStop());
            if (_watching != null) ToMod(new SpectateEnd { MatchId = _watching.MatchId, Reason = "You stopped spectating." });
            _watching = null;
            _watchedLobby = null;
            _pendingWatch = 0;
            Log("You stopped spectating.");
            Raise(nameof(IsWatching));
            // A Steam lobby was entered only to watch it.
            if (SteamMode) LeaveWatchedSteamLobby();
        }

        // JOIN while watching a lobby: a spot is free and it is between matches.
        public void JoinWatched()
        {
            if (!CanJoinWatched) return;
            uint id = _watchedLobby.LobbyId;
            if (SteamMode) JoinWatchedSteamLobby(id);
            else JoinListedLobby(id);
        }

        private void OnWatchedLobby(WatchedLobby w)
        {
            if (w.Closed)
            {
                if (_watchedLobby == null || _watchedLobby.LobbyId != w.LobbyId) return;
                _watchedLobby = null;
                Log(string.IsNullOrEmpty(w.Reason) ? "The lobby you watched closed." : w.Reason);
                Raise(nameof(IsWatching));
                if (SteamMode) LeaveWatchedSteamLobby();
                return;
            }
            bool first = _watchedLobby == null;
            _watchedLobby = w;
            if (first) Log("Watching " + w.Host + "'s lobby. Its matches show in your game from their start" + (w.CanJoin ? "; JOIN takes the free spot." : "; JOIN works when a spot is free between matches."));
            Raise(nameof(IsWatching));
        }

        // The lobby watched is the player's own now (they joined it).
        private void OnJoinedWatchedLobby(LobbyState state)
        {
            if (_watchedLobby == null || state.LobbyId != _watchedLobby.LobbyId) return;
            _watchedLobby = null;
            Raise(nameof(IsWatching));
        }

        // After a welcome: a lobby to watch (picked before connecting, or watched before the connection was lost).
        private void SendPendingWatch()
        {
            if (_pendingWatch == 0 || _server == null) return;
            _server.Send(new SpectateRequest { LobbyId = _pendingWatch });
            _pendingWatch = 0;
        }

        private void OnSpectateStart(SpectateStart start)
        {
            if (!start.Own)
            {
                // A player's own match or run starting takes over (the server ends the watching then too).
                if (_phase == ClientPhase.InMatch || _matchActive) return;
                bool first = _watching == null;
                _watching = start;
                if (first) Log("Spectating " + (_watchingName.Length > 0 ? _watchingName : "a match") + ". The game loads its world.");
                Raise(nameof(IsWatching));
            }
            ToMod(start);
        }

        private void OnSpectateEnd(SpectateEnd end)
        {
            bool mine = _watching != null && (end.MatchId == _watching.MatchId || end.MatchId == System.Guid.Empty);
            if (_watching != null && mine)
            {
                _watching = null;
                Raise(nameof(IsWatching));
            }
            if (!string.IsNullOrEmpty(end.Reason)) Log(end.Reason);
            ToMod(end);
        }

        // While watching from outside, what the server says about the watched match goes straight to the game (the
        // player's own match bookkeeping is not touched). True when the message was the watched match's.
        private bool RouteWatched(Message message)
        {
            if (_watching == null) return false;
            switch (message)
            {
                case MatchRoster roster:
                    if (roster.MatchId != _watching.MatchId) return false;
                    ToMod(roster);
                    return true;
                case OpponentSplit _:
                case OpponentAttempt _:
                case OpponentFinished _:
                    if (_match != null && _matchActive) return false;
                    ToMod(message);
                    return true;
            }
            return false;
        }

        // From the game: being out of one's own match, watch the rest of it; or the player left the watched world.
        private void OnModSpectate(Message message)
        {
            if (message is SpectateStop)
            {
                if (_watching == null) return;
                _watching = null;
                Raise(nameof(IsWatching));
                Log("You stopped spectating.");
            }
            if (_server != null && _link == LinkState.Connected && _spectateKnown) _server.Send(message);
            else if (message is SpectateRequest) ToMod(new SpectateEnd { MatchId = ((SpectateRequest)message).OwnMatch });
        }
    }
}
