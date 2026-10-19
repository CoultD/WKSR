using System;
using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;

namespace WKRace.Server
{
    // Party races (up to 15 players from a lobby, unranked). One life each, no restarts, nobody waits for anyone: a
    // death puts the player out, and someone gone too long is left behind. The race ends when everyone has finished
    // or is out, PartyFinishWindowSeconds after the first finish, or at the time limit. Places: finishers by time,
    // then everyone else by Standings.Compare.
    public sealed partial class ServerCore
    {
        private void OnPartyFinish(Match match, Slot slot, long now)
        {
            int place = match.Slots.Count(s => s.Finished);
            slot.FinishOrder = place;
            _log("match " + Short(match) + ": " + slot.Name + " finished " + Standings.Ordinal(place) + " in " + RaceFormat.Time(slot.FinishMs));
            if (match.PartyDeadlineMs < 0) match.PartyDeadlineMs = now + Math.Max(0, _options.PartyFinishWindowSeconds) * 1000L;
            SystemChat(match, slot.Name + " finished " + Standings.Ordinal(place) + " in " + RaceFormat.Time(slot.FinishMs, 1) + "."
                + (place == 1 && match.Slots.Any(s => s.Active) ? " The match ends in " + WindowText() + "." : ""));
            BroadcastRoster(match);
            CheckPartyEnd(match, now);
        }

        // "5 minutes", "1 minute", "90 seconds".
        private string WindowText()
        {
            int s = Math.Max(0, _options.PartyFinishWindowSeconds);
            if (s % 60 != 0) return s + " seconds";
            return s / 60 + (s == 60 ? " minute" : " minutes");
        }

        private void OnPartyDeath(Match match, Slot slot, int raceTimeMs)
        {
            long elapsed = match.RaceElapsed(_now());
            slot.Died = true;
            slot.Out = true;
            slot.OutMs = (int)Math.Max(0, Math.Min(raceTimeMs, MaxGameMs(elapsed)));
            if (slot.OutMs > slot.GameMs) slot.GameMs = slot.OutMs;
            _log("match " + Short(match) + ": " + slot.Name + " died at " + RaceFormat.Time(slot.OutMs));
            SystemChat(match, slot.Name + (slot.DeathText.Length > 0 ? " is out: " + slot.DeathText + "." : " died and is out."));
            BroadcastRoster(match);
            CheckPartyEnd(match, _now());
        }

        // Out of the race without dying: left, gone too long, or a world that did not match. `what` follows the name.
        private void MarkLeft(Match match, Slot slot, int raceMs, string what)
        {
            if (!slot.Active) return;
            slot.Out = true;
            slot.Forfeited = true;
            slot.OutMs = raceMs;
            _log("match " + Short(match) + ": " + slot.Name + what);
            SystemChat(match, slot.Name + what);
            BroadcastRoster(match);
            AfterSomeoneOut(match);
        }

        private void Disqualify(Match match, Slot slot, string why)
        {
            slot.Cheated = true;
            slot.Out = true;
            slot.OutMs = slot.GameMs;
            _log("match " + Short(match) + ": " + slot.Name + " disqualified: " + why);
            SystemChat(match, slot.Name + " was disqualified: " + why + ".");
            BroadcastRoster(match);
            AfterSomeoneOut(match);
        }

        // Someone dropped out: before the race that can complete the loading or the ready check; during it, the race.
        private void AfterSomeoneOut(Match match)
        {
            if (match.Phase == MatchPhase.Completed) return;
            if (!match.Slots.Any(s => s.Active) && !match.Slots.Any(s => s.Finished))
            {
                CompleteParty(match, "Everyone left the match.", match.Phase == MatchPhase.Preparing);
                return;
            }
            if (match.Phase == MatchPhase.Preparing)
            {
                if (match.ReadyDeadlineMs < 0) TryStartReadyCheck(match, false);
                else if (AllReady(match)) StartCountdown(match);
                return;
            }
            CheckPartyEnd(match, _now());
        }

        private void CheckPartyEnd(Match match, long now)
        {
            if (match.Phase != MatchPhase.Running && match.Phase != MatchPhase.Settling && match.Phase != MatchPhase.Countdown) return;
            List<Slot> active = match.Slots.Where(s => s.Active).ToList();
            if (active.Count == 0)
            {
                CompleteParty(match, "Everyone has finished or is out.", false);
                return;
            }
            if (match.PartyDeadlineMs >= 0 && now >= match.PartyDeadlineMs)
            {
                CompleteParty(match, "Time is up: " + WindowText() + " after the first finish.", false);
                return;
            }
            if (match.ClockStarted && match.RaceElapsed(now) > _options.MatchTimeLimitSeconds * 1000L)
            {
                CompleteParty(match, "Time limit reached.", false);
                return;
            }
            // Only racers who never readied up (and never left the start) are still "racing": nobody is really, so
            // there is nothing to wait for.
            if (active.All(s => !s.Ready && s.Splits.Count <= 1))
            {
                CompleteParty(match, "Only players who never readied up were left.", false);
            }
        }

        private static RacerProgress ProgressOf(Slot s)
        {
            var p = new RacerProgress
            {
                Racer = s.Racer,
                Name = s.Name,
                Finished = s.Finished,
                FinishMs = s.FinishMs,
                FinishOrder = s.FinishOrder,
                Out = !s.Finished && (s.Out || s.Died || s.Forfeited),
                OutMs = s.OutMs,
                Disqualified = s.Cheated,
            };
            foreach (SplitReached split in s.Splits.Values.OrderBy(x => x.Index)) p.AddSplit(split.Level, split.Region, split.RaceTimeMs);
            return p;
        }

        // Ends a party race and tells everyone their place. aborted: it never really started (no places).
        private void CompleteParty(Match match, string reason, bool aborted)
        {
            if (match.Phase == MatchPhase.Completed) return;
            long now = _now();
            int raceMs = match.ClockStarted ? (int)Math.Max(0, Math.Min(int.MaxValue, match.RaceElapsed(now))) : 0;
            match.Phase = MatchPhase.Completed;

            List<RacerProgress> ranked = Standings.Rank(match.Slots.Select(ProgressOf), raceMs);
            var standings = new List<Standing>();
            for (int i = 0; i < ranked.Count; i++)
            {
                RacerProgress p = ranked[i];
                Slot s = match.Slots.First(x => x.Racer == p.Racer);
                RacerStatus status = StatusOf(match, s);
                if (status == RacerStatus.Racing || status == RacerStatus.Ready || status == RacerStatus.Loaded || status == RacerStatus.Loading) status = RacerStatus.Racing;
                standings.Add(new Standing
                {
                    Racer = (byte)p.Racer,
                    Name = p.Name,
                    Place = i + 1,
                    Status = status,
                    TimeMs = p.Finished ? p.FinishMs : p.Out ? p.OutMs : -1,
                    Levels = p.Levels,
                    Note = s.Died ? s.DeathText : "",
                });
            }
            Slot first = match.Slots.First(x => x.Racer == ranked[0].Racer);
            int winnerMs = first.Finished ? first.FinishMs : -1;

            foreach (Slot slot in match.Slots)
            {
                int place = standings.First(x => x.Racer == slot.Racer).Place;
                string text = aborted ? reason
                    : reason + (place == 1 ? " You won!" : " " + first.Name + " won.");
                Deliver(slot, new MatchResult
                {
                    MatchId = match.Id,
                    Outcome = aborted ? MatchOutcome.Aborted : place == 1 ? MatchOutcome.Win : MatchOutcome.Loss,
                    YourTimeMs = slot.Finished ? slot.FinishMs : -1,
                    OpponentTimeMs = winnerMs,
                    OldRating = slot.Player.Record.Rating,
                    NewRating = slot.Player.Record.Rating,
                    Reason = text,
                    End = slot.Finished ? MatchEnd.Finish : slot.Died ? MatchEnd.Death : MatchEnd.Other,
                    Place = aborted ? 0 : place,
                    Standings = aborted ? new List<Standing>() : standings,
                });
                slot.Player.Match = null;
            }
            _matches.Remove(match.Id);
            SendVoicePeers(match.Slots.Select(s => s.Player));
            _log("match " + Short(match) + " complete: " + reason + (aborted ? "" : " Winner " + ranked[0].Name + "."));
            ReturnToLobby(match);
        }
    }
}
