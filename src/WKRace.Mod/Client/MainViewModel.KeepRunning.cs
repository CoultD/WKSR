using System;
using System.Windows.Input;
using WKRace.Protocol;

namespace WKRace.Client
{
    using Message = WKRace.Protocol.Message;

    // Keep running after a win (0.9.6 on). A 1v1 won by the opponent's death: the result is shown at once, but the
    // run goes on (the game shows a prompt: "+" keeps running, "-" ends the run) until it finishes, dies or ends. The
    // lobby's host can switch it off (LET THE LAST PLAYER FINISH THEIR RUN); ranked always allows it.
    internal sealed partial class MainViewModel
    {
        // The match whose winner is still running, or empty; the last one standing in a party.
        private Guid _afterWin;
        private bool _lastStanding;

        private void EndAfterWin(Message how)
        {
            _afterWin = Guid.Empty;
            Raise(nameof(ForfeitText));
            _matchActive = false;
            _started = false;
            _phase = _link == LinkState.Connected ? ClientPhase.Idle : ClientPhase.Offline;
            var finished = how as PlayerFinished;
            if (finished != null)
            {
                TimerText = RaceFormat.Time(finished.RaceTimeMs);
                MatchState = "Finished after the win";
                Log("Finished after the win in " + RaceFormat.Time(finished.RaceTimeMs) + ".");
            }
            else
            {
                MatchState = how is PlayerDied ? "Died after the win" : "Run ended";
                Log(how is PlayerDied ? "You died after the win (the win stands)." : "Run ended.");
            }
            PushStatus();
            RaiseSaveReplay();
        }

        // The lobby room's box (host only), and what it shows.
        public bool ShowLetLastFinish { get { return _lobbyState != null && _lobbyState.OptionsKnown; } }
        public bool LobbyLetLastFinish { get { return _lobbyState != null && _lobbyState.LetLastFinish; } }

        public void SetLetLastFinish(bool value)
        {
            if (_lobbyState == null || !_lobbyState.YouAreHost || !_lobbyState.OptionsKnown || _server == null) return;
            _settings.LobbyLetLastFinish = value;
            _settings.Save();
            _server.Send(new LobbyOptions { LetLastFinish = value });
        }

        // A new lobby takes the host's last choice (the server starts every lobby with it on).
        private void SendLobbyOptionsIfNeeded(LobbyState state)
        {
            if (!state.YouAreHost || !state.OptionsKnown || _settings.LobbyLetLastFinish || !state.LetLastFinish || _server == null) return;
            _server.Send(new LobbyOptions { LetLastFinish = false });
        }
    }
}
