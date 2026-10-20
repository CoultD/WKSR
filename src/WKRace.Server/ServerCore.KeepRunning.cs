using System;
using System.Linq;
using WKRace.Protocol;

namespace WKRace.Server
{
    // Keep running after a win (0.9.6 on). A 1v1 decided by a death: the winner gets the result with KeepRunning and
    // their run goes on (the match stays listed, Completed, with Survivor set) until they finish (a ranked finish still
    // counts for the 1v1 time board), die, end it (EndRun or a forfeit), leave, or reach the time limit. A party whose
    // other players are all out with nobody finished: the last one gets LastStanding and may run on or end the run;
    // the party ends as usual when they are done. A lobby's host can switch it off (LobbyOptions); then a 1v1 ends
    // at the death as before, and a party ends as soon as one player is left. Ranked always allows it.
    public sealed partial class ServerCore
    {
        internal const string KeepRunningVersion = "0.9.6";

        private static Slot SurvivorOf(Match match, Player player)
        {
            return match.Survivor != null && match.Survivor.Player == player ? match.Survivor : null;
        }

        private void SurvivorFinished(Match match, PlayerFinished finished)
        {
            Slot slot = match.Survivor;
            if (finished.Attempt != slot.Attempt || finished.Cheated)
            {
                EndSurvivor(match, finished.Cheated ? "used cheats after the win" : "ended their run");
                return;
            }
            long elapsed = match.RaceElapsed(_now());
            long accepted = Math.Max(MinGameMs(elapsed), Math.Min(finished.RaceTimeMs, MaxGameMs(elapsed)));
            slot.Finished = true;
            slot.FinishMs = (int)Math.Min(int.MaxValue, accepted);
            _log("match " + Short(match) + ": " + slot.Name + " finished after the win in " + RaceFormat.Time(slot.FinishMs));
            if (ImplausibleFinish(slot, accepted) == null) CountFinishedRun(slot.Player, match.Ranked, match.Segment);
            // A ranked 1v1 run counts for the fastest-time board however the match was won.
            if (match.Ranked && ImplausibleFinish(slot, accepted) == null)
                SubmitBoardRun(BoardKind.DuelTime, slot.Player, slot.FinishMs, match.Seed, match.Rules, match.Banned, slot.Player.GameBuild, slot.Recorder, slot.Watch);
            EndSurvivor(match, null);
        }

        // The survivor's run is over: the match goes, and its lobby is back for a rematch.
        private void EndSurvivor(Match match, string why)
        {
            Slot slot = match.Survivor;
            if (slot == null) return;
            match.Survivor = null;
            if (why != null) _log("match " + Short(match) + ": " + slot.Name + " " + why);
            if (slot.Player.Match == match) slot.Player.Match = null;
            _matches.Remove(match.Id);
            ReturnToLobby(match);
        }

        private void HandleEndRun(Player player, EndRun end)
        {
            Match match = player.Match;
            if (match == null || match.Id != end.MatchId) return;
            if (SurvivorOf(match, player) != null)
            {
                EndSurvivor(match, "ended their run after the win");
                return;
            }
            // Party: the last one standing ends their run; they are out (not forfeited), and the party is over.
            Slot slot = match.SlotOf(player);
            if (!match.Party || slot == null || !slot.Active || !slot.LastStandingSent) return;
            slot.Out = true;
            slot.OutMs = slot.GameMs;
            _log("match " + Short(match) + ": " + slot.Name + " ended their run as the last one standing");
            CheckPartyEnd(match, _now());
        }

        // Survivors run until the match's time limit.
        private void TickSurvivors(long now)
        {
            foreach (Match match in _matches.Values.Where(m => m.Survivor != null).ToList())
            {
                if (match.RaceElapsed(now) > _options.MatchTimeLimitSeconds * 1000L) EndSurvivor(match, "reached the time limit after the win");
            }
        }

        // Party: everyone else is out and nobody finished. Returns true when that ended the party (option off).
        private bool CheckLastStanding(Match match, Slot last)
        {
            if (match.Slots.Any(s => s.Finished) || match.Slots.Count(s => s.Active) != 1 || !match.ClockStarted) return false;
            if (!match.LetLastFinish)
            {
                CompleteParty(match, last.Name + " is the last one standing.", false);
                return true;
            }
            if (last.LastStandingSent) return false;
            last.LastStandingSent = true;
            if (BuildTag.AtLeast(last.Player.ClientVersion, KeepRunningVersion)) last.Player.Send(new LastStanding { MatchId = match.Id });
            SystemChat(match, last.Name + " is the last one standing.");
            return false;
        }

        // The host's lobby options.
        private void HandleLobbyOptions(Player player, LobbyOptions options)
        {
            Lobby lobby = player.Lobby;
            if (lobby == null || lobby.Host != player || lobby.LetLastFinish == options.LetLastFinish) return;
            lobby.LetLastFinish = options.LetLastFinish;
            BroadcastLobby(lobby);
        }
    }
}
