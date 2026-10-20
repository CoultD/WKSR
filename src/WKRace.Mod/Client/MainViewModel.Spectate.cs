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

        public bool CanSpectate { get { return _spectateKnown && _link == LinkState.Connected && _phase != ClientPhase.InMatch && !_matchActive; } }
        public bool IsWatching { get { return _watching != null; } }
        public string WatchingText { get { return _watching == null ? "" : "Watching " + (_watchingName.Length > 0 ? _watchingName : "a match") + "."; } }

        public void SpectatePlayer(OnlineRowModel row)
        {
            if (row == null || !row.Watchable || !CanSpectate) return;
            _watchingName = row.Name;
            Log("Asking to watch " + row.Name + "...");
            _server.Send(new SpectateRequest { Tag = row.Tag });
        }

        public void SpectateLobby(uint lobbyId, string host)
        {
            if (!CanSpectate) return;
            _watchingName = host + "'s lobby";
            Log("Asking to watch " + _watchingName + "...");
            _server.Send(new SpectateRequest { LobbyId = lobbyId });
        }

        public void StopSpectating()
        {
            if (_watching == null) return;
            var end = new SpectateEnd { MatchId = _watching.MatchId, Reason = "You stopped spectating." };
            _watching = null;
            _server?.Send(new SpectateStop());
            ToMod(end);
            Log(end.Reason);
            Raise(nameof(IsWatching));
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
