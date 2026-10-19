using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using WKRace.Protocol;

namespace WKRace.Server
{
    // Matches: a 1v1 (ranked, or friendly from a lobby) or a party race from a lobby. Every match loads, runs a ready
    // check, counts down and races. A 1v1 ends with the first finish or death; a party race goes on until everyone has
    // finished or is out (ServerCore.Party).
    public sealed partial class ServerCore
    {
        // A client may report a finish this much earlier than the server saw it, to allow for network delay.
        private const int MaxLatencyAllowanceMs = 1500;
        private const int SplitFutureToleranceMs = 1500;
        // Players report the game's own run timer. It counts the game's shortcut sections three times over, and it
        // runs slower than real time while the game loads, so a reported time is held to what is possible at all:
        // never more than this many times the match's real time.
        private const int MaxTimerFactor = 3;
        // ... and never less than this share of it: the slowest the game's timer ever runs (in part of one shortcut).
        // In a match a pause does not stop the timer (the mod adds the paused time back).
        private const int MinTimerPercent = 15;

        private static long MinGameMs(long elapsed)
        {
            return Math.Max(0, Math.Max(0, elapsed) * MinTimerPercent / 100 - SplitFutureToleranceMs);
        }

        // The highest game-timer value anyone can truthfully report once `elapsed` ms of the match have passed.
        private static long MaxGameMs(long elapsed)
        {
            return Math.Max(0, elapsed) * MaxTimerFactor + SplitFutureToleranceMs;
        }

        private Match CreateMatch(IList<Player> players, bool ranked, MatchMode mode, MatchRules rules, Lobby lobby)
        {
            if (mode == MatchMode.Party) rules &= ~MatchRules.ArtifactBans;
            long now = _now();
            var match = new Match
            {
                Id = Guid.NewGuid(),
                Ranked = ranked,
                Mode = mode,
                Rules = rules,
                Lobby = lobby,
                Seed = RandomNumberGenerator.GetInt32(ProtocolConstants.MinSeed, ProtocolConstants.MaxSeed + 1),
                CreatedMs = now,
                PrepareSinceMs = now,
                Phase = MatchPhase.Preparing,
                BansDone = (rules & MatchRules.ArtifactBans) == 0,
            };
            for (int i = 0; i < players.Count; i++)
            {
                Player p = players[i];
                var slot = new Slot { Player = p, Racer = (byte)i, Name = p.Record.Name, StartRating = p.Record.Rating };
                if (ranked && mode == MatchMode.Duel)
                {
                    slot.Recorder = NewReplayRecorder();
                    slot.Watch = new TimerWatch();
                }
                match.Slots.Add(slot);
                p.Match = match;
            }
            _matches[match.Id] = match;

            string who = mode == MatchMode.Party ? "party of " + players.Count + " (" + string.Join(", ", match.Slots.Select(s => s.Name)) + ")"
                : match.A.Name + " vs " + match.B.Name;
            _log("match " + Short(match) + ": " + who + " seed " + match.Seed + (ranked ? "" : " (friendly)") + " rules " + rules);
            foreach (Slot slot in match.Slots) SendMatchFound(match, slot, -1);
            BroadcastRoster(match);
            SendVoicePeers(players);
            return match;
        }

        private static string Short(Match match)
        {
            return match.Id.ToString("N").Substring(0, 8);
        }

        private void SendMatchFound(Match match, Slot to, int resumeElapsedMs)
        {
            string opponent;
            int opponentRating;
            if (match.Party)
            {
                opponent = match.Slots.Count + " players";
                opponentRating = 0;
            }
            else
            {
                Slot other = match.Other(to);
                opponent = other.Name;
                opponentRating = other.StartRating;
            }
            to.Player.Send(new MatchFound
            {
                MatchId = match.Id,
                Seed = match.Seed,
                OpponentName = opponent,
                OpponentRating = opponentRating,
                Competitive = _options.Competitive,
                TimeLimitSeconds = _options.MatchTimeLimitSeconds,
                ResumeElapsedMs = resumeElapsedMs,
                Attempt = to.Attempt,
                GraceSeconds = _options.ReconnectGraceSeconds,
                Ranked = match.Ranked,
                Rules = match.Rules,
                Mode = match.Mode,
                YourRacer = to.Racer,
                PlayerCount = match.Slots.Count,
                ReadySeconds = Math.Max(0, _options.ReadyCheckSeconds),
            });
        }

        // A player signed in again while their match is still going: bring their client fully up to date.
        private void ResumeMatch(Player player)
        {
            Match match = player.Match;
            Slot slot = match.SlotOf(player);
            long now = _now();

            UpdatePresence(match);
            SendVoicePeers(player);

            bool underway = match.Phase == MatchPhase.Running || match.Phase == MatchPhase.Settling;
            SendMatchFound(match, slot, underway ? (int)Math.Max(0, match.RaceElapsed(now)) : -1);
            if (match.ArtifactBans) SendBanState(match, slot);
            player.Send(RosterFor(match, slot, now));
            if (match.Phase != MatchPhase.Preparing)
            {
                player.Send(new MatchStart { MatchId = match.Id, GoServerTimeMs = match.GoMs });
            }
            foreach (Slot other in match.Slots)
            {
                if (other == slot) continue;
                foreach (SplitReached split in other.Splits.Values.OrderBy(s => s.Index))
                {
                    player.Send(ToOpponentSplit(split, other));
                }
                if (other.Finished)
                {
                    player.Send(new OpponentFinished { Attempt = other.Attempt, RaceTimeMs = other.FinishMs, Racer = other.Racer });
                }
            }
            if (match.ClockStarted)
            {
                // The client froze its game when it lost us; tell it whether to stay frozen or carry on.
                if (match.Paused) player.Send(PausedMessage(match, now));
                else player.Send(ResumedMessage(match));
            }
        }

        // ---- the roster ------------------------------------------------------

        private static RacerStatus StatusOf(Match match, Slot slot)
        {
            if (slot.Finished) return RacerStatus.Finished;
            if (slot.Died) return RacerStatus.Out;
            if (slot.Out || slot.Forfeited || slot.Cheated) return RacerStatus.Left;
            if (match.Phase != MatchPhase.Preparing) return RacerStatus.Racing;
            return slot.Ready ? RacerStatus.Ready : slot.Loaded ? RacerStatus.Loaded : RacerStatus.Loading;
        }

        private MatchRoster RosterFor(Match match, Slot to, long now)
        {
            var roster = new MatchRoster
            {
                MatchId = match.Id,
                YourRacer = to.Racer,
                ReadySecondsLeft = match.Phase == MatchPhase.Preparing && match.ReadyDeadlineMs >= 0
                    ? (int)Math.Max(0, (match.ReadyDeadlineMs - now + 999) / 1000) : -1,
            };
            foreach (Slot s in match.Slots)
            {
                RacerStatus status = StatusOf(match, s);
                int time = status == RacerStatus.Finished ? s.FinishMs : (status == RacerStatus.Out || status == RacerStatus.Left) ? s.OutMs : -1;
                roster.Racers.Add(new RosterEntry { Racer = s.Racer, Name = s.Name, Rating = s.StartRating, Status = status, TimeMs = time });
            }
            return roster;
        }

        private void BroadcastRoster(Match match)
        {
            long now = _now();
            foreach (Slot slot in match.Slots) slot.Player.Send(RosterFor(match, slot, now));
        }

        // ---- presence and pausing ------------------------------------------

        // A racer is present when their client is connected and, once the race clock exists, their game is too.
        private static bool IsPresent(Match match, Slot slot)
        {
            return slot.Player.Conn != null && (match.Phase == MatchPhase.Preparing || slot.Player.GameConnected);
        }

        // Only a 1v1 racer who can still affect the result stops the clock; someone who has finished cannot. Party
        // races never stop: whoever is gone is left behind.
        private static bool BlocksRace(Match match, Slot slot)
        {
            return !match.Party && slot.AwaySinceMs >= 0 && !slot.Finished;
        }

        // Re-evaluates who is away after any connection change, and pauses or resumes the race clock to match.
        private void UpdatePresence(Match match)
        {
            if (match.Phase == MatchPhase.Completed) return;
            long now = _now();
            foreach (Slot slot in match.Slots)
            {
                bool present = IsPresent(match, slot);
                if (!present && slot.AwaySinceMs < 0)
                {
                    slot.AwaySinceMs = now;
                    _log("match " + Short(match) + ": " + slot.Name + " is away");
                }
                else if (present && slot.AwaySinceMs >= 0)
                {
                    slot.AwaySinceMs = -1;
                    _log("match " + Short(match) + ": " + slot.Name + " is back");
                }
            }

            if (!match.ClockStarted || match.Party) return;
            bool blocked = match.Slots.Any(s => BlocksRace(match, s));
            if (blocked && !match.Paused) Pause(match, now);
            else if (!blocked && match.Paused) Resume(match, now);
        }

        private void Pause(Match match, long now)
        {
            match.PausedAtMs = now;
            _log("match " + Short(match) + ": paused at " + RaceFormat.Time(Math.Max(0, match.RaceElapsed(now))));
            MatchPaused message = PausedMessage(match, now);
            foreach (Slot slot in match.Slots) slot.Player.Send(message);
        }

        // Play continues after a short countdown, with GO moved later by the whole pause so the clock carries on where it stopped.
        private void Resume(Match match, long now)
        {
            long resumeAt = now + _options.ResumeCountdownMs;
            long shift = resumeAt - match.PausedAtMs;
            match.GoMs += shift;
            if (match.Phase == MatchPhase.Settling) match.SettleDeadlineMs += shift;
            match.PausedAtMs = -1;
            match.ResumeAtMs = resumeAt;
            _log("match " + Short(match) + ": resuming after " + (shift / 1000) + " s");
            MatchResumed message = ResumedMessage(match);
            foreach (Slot slot in match.Slots) slot.Player.Send(message);
        }

        private MatchPaused PausedMessage(Match match, long now)
        {
            var away = new List<string>();
            long since = now;
            foreach (Slot slot in match.Slots)
            {
                if (!BlocksRace(match, slot)) continue;
                away.Add(slot.Name);
                since = Math.Min(since, slot.AwaySinceMs);
            }
            long left = _options.ReconnectGraceSeconds * 1000L - (now - since);
            return new MatchPaused
            {
                MatchId = match.Id,
                AwayName = string.Join(" and ", away),
                SecondsLeft = (int)Math.Max(0, (left + 999) / 1000),
                RaceTimeMs = (int)match.RaceElapsed(now),
            };
        }

        private static MatchResumed ResumedMessage(Match match)
        {
            return new MatchResumed { MatchId = match.Id, GoServerTimeMs = match.GoMs, ResumeServerTimeMs = match.ResumeAtMs };
        }

        // ---- loading and the ready check -----------------------------------------

        // The game loaded the race world (MatchReady). Once every world has, the ready check starts.
        private void HandleLoaded(Player player, MatchReady ready)
        {
            Match match = player.Match;
            if (match == null || match.Id != ready.MatchId)
            {
                player.Send(new ErrorMessage(ErrorCode.NotInMatch, "No such match."));
                return;
            }
            // Worlds load only after the ban phase; a report before that is stale.
            if (match.Phase != MatchPhase.Preparing || !match.BansDone) return;

            Slot slot = match.SlotOf(player);
            if (!slot.Active) return;
            bool changed = !slot.Loaded;
            slot.Loaded = true;
            slot.WorldHash = ready.WorldHash;
            if (changed) BroadcastRoster(match);
            TryStartReadyCheck(match, false);
        }

        // Starts the ready check once every racer still in the match has loaded (or, for a party, when `force` says the
        // loading time is up). Worlds that differ never race.
        private void TryStartReadyCheck(Match match, bool force)
        {
            if (match.Phase != MatchPhase.Preparing || match.ReadyDeadlineMs >= 0) return;
            List<Slot> active = match.Slots.Where(s => s.Active).ToList();
            if (!force && active.Any(s => !s.Loaded)) return;

            if (!match.Party)
            {
                if (match.A.WorldHash != 0 && match.B.WorldHash != 0 && match.A.WorldHash != match.B.WorldHash)
                {
                    _log("match " + Short(match) + ": world hash mismatch " + match.A.WorldHash.ToString("X8") + " vs " + match.B.WorldHash.ToString("X8"));
                    NoteWorlds(match.A.Player.GameBuild, match.B.Player.GameBuild, false);
                    Complete(match, null, true, "Your worlds did not match (different game versions or mods?). No rating change.");
                    return;
                }
                if (match.A.WorldHash != 0 && match.B.WorldHash != 0) NoteWorlds(match.A.Player.GameBuild, match.B.Player.GameBuild, true);
            }
            else
            {
                // The world most racers have is the race's; anyone with a different one cannot race in it.
                var counts = active.Where(s => s.Loaded && s.WorldHash != 0).GroupBy(s => s.WorldHash).OrderByDescending(g => g.Count()).ThenBy(g => g.Min(s => s.Racer)).ToList();
                if (counts.Count > 1)
                {
                    uint world = counts[0].Key;
                    Slot usual = active.First(s => s.Loaded && s.WorldHash == world);
                    foreach (Slot odd in active.Where(s => s.Loaded && s.WorldHash != 0 && s.WorldHash != world))
                    {
                        NoteWorlds(odd.Player.GameBuild, usual.Player.GameBuild, false);
                        _log("match " + Short(match) + ": " + odd.Name + " has a different world " + odd.WorldHash.ToString("X8") + " (match " + world.ToString("X8") + ")");
                        odd.Player.Send(new ErrorMessage(ErrorCode.Unknown, "Your world is different from the others' (a different game version or mods?), so you cannot play in this match."));
                        MarkLeft(match, odd, 0, " could not play: their world was different.");
                    }
                    if (match.Phase == MatchPhase.Completed) return;
                }
            }

            if (_options.ReadyCheckSeconds <= 0)
            {
                foreach (Slot s in match.Slots) s.Ready = s.Active && s.Loaded;
                StartCountdown(match);
                return;
            }
            match.ReadyDeadlineMs = _now() + _options.ReadyCheckSeconds * 1000L;
            _log("match " + Short(match) + ": ready check");
            BroadcastRoster(match);
            if (AllReady(match)) StartCountdown(match);
        }

        private static bool AllReady(Match match)
        {
            return match.Slots.Where(s => s.Active).All(s => s.Ready);
        }

        // The player pressed ready (in the game, or in WKSR). It counts once their world has loaded.
        private void HandlePlayerReady(Player player, PlayerReady ready)
        {
            Match match = player.Match;
            if (match == null || match.Id != ready.MatchId || match.Phase != MatchPhase.Preparing || !match.BansDone) return;
            Slot slot = match.SlotOf(player);
            if (!slot.Active || slot.Ready) return;
            if (!slot.Loaded)
            {
                player.Send(new ErrorMessage(ErrorCode.NotInMatch, "Your game has not loaded the match world yet: start your run in the game first (F9 on the menu)."));
                return;
            }
            slot.Ready = true;
            BroadcastRoster(match);
            if (match.ReadyDeadlineMs >= 0 && AllReady(match)) StartCountdown(match);
        }

        // The ready check ran out. A 1v1: ranked, whoever did not ready up forfeits; friendly, the race is called off
        // (back to the lobby). A party race starts anyway, with everyone in it.
        private void ReadyCheckExpired(Match match)
        {
            if (match.Party)
            {
                _log("match " + Short(match) + ": ready check over, starting with everyone");
                StartCountdown(match);
                return;
            }
            List<Slot> unready = match.Slots.Where(s => !s.Ready).ToList();
            if (unready.Count == 2)
            {
                Complete(match, null, true, "Neither player readied up in time. No rating change.");
                return;
            }
            Slot late = unready[0];
            if (match.Ranked)
            {
                late.Forfeited = true;
                Complete(match, match.Other(late), false, late.Name + " did not ready up in time.");
            }
            else
            {
                Complete(match, null, true, late.Name + " did not ready up in time. Back to the lobby.");
            }
        }

        private void StartCountdown(Match match)
        {
            if (match.Phase != MatchPhase.Preparing) return;
            match.GoMs = _now() + _options.CountdownMs;
            match.Phase = MatchPhase.Countdown;
            var start = new MatchStart { MatchId = match.Id, GoServerTimeMs = match.GoMs };
            foreach (Slot slot in match.Slots) slot.Player.Send(start);
            BroadcastRoster(match);
            // A game that dropped while the others were still loading now counts as away.
            UpdatePresence(match);
        }

        // ---- race messages ---------------------------------------------------

        private void HandleState(Player player, PlayerState state)
        {
            Match match = player.Match;
            if (match == null) return;
            Promote(match);
            if (match.Phase != MatchPhase.Running && match.Phase != MatchPhase.Settling) return;

            Slot from = match.SlotOf(player);
            from.PosX = state.X;
            from.PosY = state.Y;
            from.PosZ = state.Z;
            from.PosAtMs = _now();
            if (state.RaceTimeMs > from.GameMs && state.RaceTimeMs <= MaxGameMs(match.RaceElapsed(from.PosAtMs))) from.GameMs = state.RaceTimeMs;
            if (from.Recorder != null && from.Active)
            {
                from.Recorder.Add(state);
                if (_options.TimerCheck) from.Watch.Report(from.PosAtMs, state.RaceTimeMs, state.X, state.Y, state.Z);
            }
            var relay = new OpponentState { Racer = from.Racer };
            relay.CopyFrom(state);
            foreach (Slot slot in match.Slots)
                if (slot != from) slot.Player.Send(relay);
        }

        private void HandleSplit(Player player, SplitReached split)
        {
            Match match = player.Match;
            if (match == null) return;
            Promote(match);
            if (match.Phase != MatchPhase.Running && match.Phase != MatchPhase.Settling) return;

            Slot slot = match.SlotOf(player);
            if (match.Party && !slot.Active) return;
            long elapsed = match.RaceElapsed(_now());
            if (split.Attempt != slot.Attempt || split.Index < 0 || split.Index > ProtocolConstants.MaxSplitIndex) return;
            if (split.RaceTimeMs < 0 || split.RaceTimeMs > MaxGameMs(elapsed)) return;

            split.Level = NameSanitizer.CleanLabel(split.Level);
            split.Region = NameSanitizer.CleanLabel(split.Region);
            slot.Splits[split.Index] = split;
            if (slot.Recorder != null) slot.Recorder.Add(split);
            OpponentSplit relay = ToOpponentSplit(split, slot);
            foreach (Slot other in match.Slots)
                if (other != slot) other.Player.Send(relay);
        }

        private static OpponentSplit ToOpponentSplit(SplitReached s, Slot from)
        {
            return new OpponentSplit { Attempt = s.Attempt, Index = s.Index, Level = s.Level, Region = s.Region, RaceTimeMs = s.RaceTimeMs, Racer = from.Racer };
        }

        // Attempts only move forward. They may skip numbers: a client that was offline replays only its latest attempt.
        private void HandleAttempt(Player player, AttemptStarted attempt)
        {
            Match match = player.Match;
            if (match == null) return;
            Promote(match);
            if (match.Phase != MatchPhase.Running && match.Phase != MatchPhase.Settling) return;

            Slot slot = match.SlotOf(player);
            if (!slot.Active || attempt.Attempt <= slot.Attempt || attempt.Attempt > ProtocolConstants.MaxAttempt) return;

            slot.Attempt = attempt.Attempt;
            slot.Splits.Clear();
            if (slot.Recorder != null) slot.Recorder.Add(attempt);
            var relay = new OpponentAttempt { Attempt = attempt.Attempt, RaceTimeMs = attempt.RaceTimeMs, Racer = slot.Racer };
            foreach (Slot other in match.Slots)
                if (other != slot) other.Player.Send(relay);
        }

        private void HandleFinished(Player player, PlayerFinished finished)
        {
            Match match = player.Match;
            if (match == null) return;
            Promote(match);
            if (match.Phase != MatchPhase.Running && match.Phase != MatchPhase.Settling) return;

            Slot slot = match.SlotOf(player);
            if (!slot.Active || finished.Attempt != slot.Attempt) return;

            if (finished.Cheated)
            {
                if (match.Party) { Disqualify(match, slot, "cheats were used"); return; }
                slot.Cheated = true;
                Complete(match, match.Other(slot), false, slot.Name + " was disqualified: cheats were used.");
                return;
            }

            long now = _now();
            long elapsed = match.RaceElapsed(now);
            // Two clocks. Who finished first is decided on the match's shared real-time clock: the client's own
            // reading of it, kept within the latency the server allows. The time that is shown and kept is the game's
            // run timer, held to what is possible in the real time that has passed.
            long claimedReal = finished.RealTimeMs >= 0 ? finished.RealTimeMs : elapsed;
            long acceptedReal = Math.Max(0, Math.Min(Math.Max(claimedReal, elapsed - MaxLatencyAllowanceMs), elapsed));
            long accepted = Math.Max(MinGameMs(elapsed), Math.Min(finished.RaceTimeMs, MaxGameMs(elapsed)));

            // Finishes are claimed by the client. One that is impossibly fast, or that skipped most of the climb,
            // disqualifies (the limits sit far below any real finish).
            string implausible = ImplausibleFinish(slot, Math.Min(accepted, acceptedReal));
            if (implausible != null)
            {
                _log("match " + Short(match) + ": rejected the finish of " + slot.Name + ": " + implausible);
                if (match.Party) { Disqualify(match, slot, "the finish was not plausible"); return; }
                slot.Cheated = true;
                Complete(match, match.Other(slot), false, slot.Name + " was disqualified: the finish was not plausible (" + implausible + ").");
                return;
            }

            slot.Finished = true;
            slot.FinishMs = (int)Math.Min(int.MaxValue, accepted);
            slot.FinishRealMs = (int)Math.Min(int.MaxValue, acceptedReal);
            slot.FinishArrivalMs = now;
            if (slot.FinishMs > slot.GameMs) slot.GameMs = slot.FinishMs;
            // A finished ranked 1v1 run counts for the fastest-time board, whoever wins the match.
            if (match.Ranked && !match.Party)
                SubmitBoardRun(BoardKind.DuelTime, player, slot.FinishMs, match.Seed, match.Rules, match.Banned, player.GameBuild, slot.Recorder, slot.Watch);

            var relay = new OpponentFinished { Attempt = finished.Attempt, RaceTimeMs = slot.FinishMs, Racer = slot.Racer };
            foreach (Slot other in match.Slots)
                if (other != slot) other.Player.Send(relay);

            if (match.Party)
            {
                OnPartyFinish(match, slot, now);
                return;
            }

            Slot opponent = match.Other(slot);
            if (opponent.Finished)
            {
                ResolveFinish(match);
            }
            else if (match.Phase == MatchPhase.Running)
            {
                match.Phase = MatchPhase.Settling;
                match.SettleDeadlineMs = now + _options.FinishSettleMs;
            }
        }

        private string ImplausibleFinish(Slot slot, long raceMs)
        {
            if (_options.MinFinishSeconds > 0 && raceMs < _options.MinFinishSeconds * 1000L)
                return "finished after " + RaceFormat.Time(raceMs, 0) + ", under the minimum of " + RaceFormat.Time(_options.MinFinishSeconds * 1000L, 0);
            if (_options.MinFinishSplits > 0 && slot.Splits.Count < _options.MinFinishSplits)
                return "only " + slot.Splits.Count + " levels reached, under the minimum of " + _options.MinFinishSplits;
            return null;
        }

        private void HandleForfeit(Player player)
        {
            Match match = player.Match;
            if (match == null || match.Phase == MatchPhase.Completed) return;

            Slot slot = match.SlotOf(player);
            if (match.Party)
            {
                if (!slot.Active) return;
                MarkLeft(match, slot, match.ClockStarted ? slot.GameMs : 0, " left the match.");
                return;
            }
            if (match.Phase == MatchPhase.Preparing || match.Phase == MatchPhase.Countdown)
            {
                Complete(match, null, true, slot.Name + " left before the match started.");
                return;
            }
            slot.Forfeited = true;
            Complete(match, match.Other(slot), false, slot.Name + " forfeited.");
        }

        // A death ends a 1v1: whoever is still alive wins, however far behind they are. In a party race the player is
        // out. A player who already finished cannot die any more (their finish stands).
        private void HandleDied(Player player, PlayerDied died)
        {
            Match match = player.Match;
            if (match == null) return;
            Promote(match);
            if (match.Phase != MatchPhase.Running && match.Phase != MatchPhase.Settling) return;

            Slot slot = match.SlotOf(player);
            if (!slot.Active || died.Attempt != slot.Attempt) return;
            slot.DeathText = DeathText.ForOthers(CleanDeathText(died.Text));
            if (match.Party)
            {
                OnPartyDeath(match, slot, died.RaceTimeMs);
                return;
            }
            slot.Died = true;
            Complete(match, match.Other(slot), false, slot.Name + (slot.DeathText.Length > 0 ? " died: " + slot.DeathText + "." : " died."), MatchEnd.Death);
        }

        // The game's death line, from the player's mod: printable, no rich-text tags, short.
        internal static string CleanDeathText(string raw)
        {
            string text = NameSanitizer.CleanLabel(raw ?? "").Trim();
            text = text.Replace("?", "").Length == 0 ? "" : text;
            if (text.Length > ProtocolConstants.MaxDeathTextLength) text = text.Substring(0, ProtocolConstants.MaxDeathTextLength);
            return text;
        }

        private void Promote(Match match)
        {
            if (match.Phase == MatchPhase.Countdown && !match.Paused && _now() >= match.GoMs) match.Phase = MatchPhase.Running;
        }

        private void ResolveFinish(Match match)
        {
            Slot a = match.A;
            Slot b = match.B;
            Slot winner;
            if (a.Finished && b.Finished)
            {
                // First to finish wins, on the real-time clock (a shortcut makes the game timer run three times as
                // fast, so the lower timer is not always the one who got there first).
                if (a.FinishRealMs != b.FinishRealMs) winner = a.FinishRealMs < b.FinishRealMs ? a : b;
                else winner = a.FinishArrivalMs <= b.FinishArrivalMs ? a : b;
            }
            else
            {
                winner = a.Finished ? a : b;
            }
            Complete(match, winner, false, winner.Name + " finished first.", MatchEnd.Finish);
        }

        private void TickMatches(long now)
        {
            foreach (Match match in _matches.Values.ToArray())
            {
                if (!match.Paused)
                {
                    switch (match.Phase)
                    {
                        case MatchPhase.Preparing:
                            TickPreparing(match, now);
                            break;
                        case MatchPhase.Countdown:
                            if (now >= match.GoMs) match.Phase = MatchPhase.Running;
                            break;
                        case MatchPhase.Running:
                            if (match.Party) CheckPartyEnd(match, now);
                            else if (match.RaceElapsed(now) > _options.MatchTimeLimitSeconds * 1000L)
                                Complete(match, null, true, "Time limit reached.");
                            break;
                        case MatchPhase.Settling:
                            if (now >= match.SettleDeadlineMs) ResolveFinish(match);
                            break;
                    }
                }
                if (match.Phase != MatchPhase.Completed) CheckAway(match, now);
            }
        }

        private void TickPreparing(Match match, long now)
        {
            TickBans(match, now);
            if (match.Phase != MatchPhase.Preparing) return;
            if (match.ReadyDeadlineMs >= 0)
            {
                if (now >= match.ReadyDeadlineMs) ReadyCheckExpired(match);
                return;
            }
            // Ban turns have their own clock; otherwise the games get ReadyTimeoutSeconds to load.
            bool waiting = match.BansDone || match.BanPool == null;
            if (!waiting || now - match.PrepareSinceMs <= _options.ReadyTimeoutSeconds * 1000L) return;
            if (match.Party && match.Slots.Any(s => s.Active && s.Loaded))
            {
                // Whoever has not loaded by now stays in, standing at the bottom.
                TryStartReadyCheck(match, true);
                return;
            }
            string reason = match.Party ? "Nobody's game loaded the match world in time."
                : match.BansDone ? "Both players did not finish loading in time." : "A game did not start in time for the artifact bans.";
            if (match.Party) CompleteParty(match, reason, true);
            else Complete(match, null, true, reason);
        }

        // A 1v1 player who stays away longer than the grace period loses; if both do, nobody wins. A party racer who
        // does is out of the race.
        private void CheckAway(Match match, long now)
        {
            long grace = _options.ReconnectGraceSeconds * 1000L;
            if (match.Party)
            {
                foreach (Slot slot in match.Slots)
                {
                    if (!slot.Active || slot.AwaySinceMs < 0 || now - slot.AwaySinceMs <= grace) continue;
                    MarkLeft(match, slot, match.ClockStarted ? slot.GameMs : 0, " disconnected and is out.");
                    if (match.Phase == MatchPhase.Completed) return;
                }
                return;
            }

            bool aGone = match.A.AwaySinceMs >= 0 && now - match.A.AwaySinceMs > grace;
            bool bGone = match.B.AwaySinceMs >= 0 && now - match.B.AwaySinceMs > grace;
            if (match.Phase == MatchPhase.Preparing)
            {
                if (aGone || bGone) Complete(match, null, true, (aGone ? match.A : match.B).Name + " disconnected before the match started.");
                return;
            }

            // Someone who already finished keeps their finish however long they are gone.
            aGone &= !match.A.Finished;
            bGone &= !match.B.Finished;
            if (!aGone && !bGone) return;
            if (aGone && bGone)
            {
                Complete(match, null, true, "Both players disconnected.");
                return;
            }
            Slot gone = aGone ? match.A : match.B;
            gone.Forfeited = true;
            Complete(match, match.Other(gone), false, gone.Name + " did not reconnect in time and forfeits.");
        }

        // Takes a player out of their match for good (an admin ban, a Steam restore): a 1v1 counts it as a forfeit,
        // a party race as leaving.
        private void RemoveFromMatch(Player player, string reason)
        {
            Match match = player.Match;
            if (match == null || match.Phase == MatchPhase.Completed) return;
            Slot slot = match.SlotOf(player);
            if (match.Party)
            {
                if (slot.Active) MarkLeft(match, slot, match.ClockStarted ? slot.GameMs : 0, " " + reason);
                return;
            }
            slot.Forfeited = true;
            Complete(match, match.Other(slot), false, slot.Name + " " + reason);
        }

        // A 1v1 ends here. winner == null ends the match without a result for either side; aborted == true skips
        // rating changes.
        private void Complete(Match match, Slot winner, bool aborted, string reason, MatchEnd end = MatchEnd.Other)
        {
            if (match.Phase == MatchPhase.Completed) return;
            if (match.Party)
            {
                CompleteParty(match, reason, aborted);
                return;
            }
            match.Phase = MatchPhase.Completed;

            Slot a = match.A;
            Slot b = match.B;
            int oldA = a.Player.Record.Rating;
            int oldB = b.Player.Record.Rating;
            MatchOutcome outcomeA;
            MatchOutcome outcomeB;

            if (aborted || winner == null)
            {
                outcomeA = outcomeB = MatchOutcome.Aborted;
            }
            else if (!match.Ranked)
            {
                // Friendly race: the result is reported, but ratings and records stay as they are.
                outcomeA = winner == a ? MatchOutcome.Win : MatchOutcome.Loss;
                outcomeB = winner == b ? MatchOutcome.Win : MatchOutcome.Loss;
            }
            else
            {
                outcomeA = winner == a ? MatchOutcome.Win : MatchOutcome.Loss;
                outcomeB = winner == b ? MatchOutcome.Win : MatchOutcome.Loss;
                int ratingA = oldA;
                int ratingB = oldB;
                Elo.Update(ref ratingA, ref ratingB, winner == a ? 1.0 : 0.0, Elo.KFor(a.Player.Record), Elo.KFor(b.Player.Record));
                a.Player.Record.Rating = ratingA;
                b.Player.Record.Rating = ratingB;
                Credit(a, outcomeA);
                Credit(b, outcomeB);
                NoteRankedPlayed(a.Player.Record);
                NoteRankedPlayed(b.Player.Record);
                _store.MarkDirty(a.Player.Record);
                _store.MarkDirty(b.Player.Record);
            }

            Deliver(a, new MatchResult
            {
                MatchId = match.Id, Outcome = outcomeA, YourTimeMs = a.FinishMs, OpponentTimeMs = b.FinishMs,
                OldRating = oldA, NewRating = a.Player.Record.Rating, Reason = reason, End = end,
            });
            Deliver(b, new MatchResult
            {
                MatchId = match.Id, Outcome = outcomeB, YourTimeMs = b.FinishMs, OpponentTimeMs = a.FinishMs,
                OldRating = oldB, NewRating = b.Player.Record.Rating, Reason = reason, End = end,
            });

            a.Player.Match = null;
            b.Player.Match = null;
            _matches.Remove(match.Id);
            SendVoicePeers(new[] { a.Player, b.Player });
            _log("match " + Short(match) + " complete: " + reason);

            _matchLog.Append(new MatchEntry
            {
                Id = match.Id,
                Seed = match.Seed,
                Ranked = match.Ranked,
                EndedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Reason = reason,
                Aborted = outcomeA == MatchOutcome.Aborted,
                WorldHashes = match.A.WorldHash.ToString("X8") + " " + match.B.WorldHash.ToString("X8"),
                BansJson = match.ArtifactBans
                    ? MatchLog.BansJson(match.Banned, match.BannedBy)
                    : null,
                A = new MatchSide { Id = a.Player.Id, Name = a.Name, Outcome = outcomeA.ToString(), TimeMs = a.FinishMs, Rating = a.Player.Record.Rating },
                B = new MatchSide { Id = b.Player.Id, Name = b.Name, Outcome = outcomeB.ToString(), TimeMs = b.FinishMs, Rating = b.Player.Record.Rating },
            });
            ReturnToLobby(match);
        }

        private static void Credit(Slot slot, MatchOutcome outcome)
        {
            PlayerRecord record = slot.Player.Record;
            if (outcome == MatchOutcome.Win)
            {
                record.Wins++;
                if (slot.Finished && (record.BestTimeMs < 0 || slot.FinishMs < record.BestTimeMs)) record.BestTimeMs = slot.FinishMs;
            }
            else if (outcome == MatchOutcome.Loss)
            {
                record.Losses++;
            }
        }

        private void Deliver(Slot slot, MatchResult result)
        {
            Player player = slot.Player;
            if (player.Conn != null)
            {
                player.Conn.Send(result);
            }
            else
            {
                player.PendingResult = result;
            }
        }
    }
}
