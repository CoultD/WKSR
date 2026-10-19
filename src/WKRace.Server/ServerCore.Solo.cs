using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using WKRace.Protocol;

namespace WKRace.Server
{
    // A ranked solo run: one player, hosted here so that the run and its time are the server's to keep. Every run
    // is on a new seed the server picks (a restart in the game is a new run on a new seed), always with the ranked
    // modifiers. The steps are SoloHost's: the same class WKSR uses for an unranked solo run on the player's own PC.
    internal sealed class SoloRun
    {
        public SoloHost Host;
        public int Seed;
        public string GameBuild = "";
        public long StartedMs;
        // How many runs this session has had: 1, then one more for every restart.
        public int Runs;
        // A run of a segment (Segments), for that segment's leaderboard. 0: the whole climb.
        public byte Segment;
        // The server's own recording of the run, and its check of the timer (see ServerCore.Boards).
        public RunRecorder Recorder;
        public TimerWatch Watch = new TimerWatch();
    }

    public sealed partial class ServerCore
    {
        // A restart builds a new world in the game, which takes seconds: anything faster than this is not a player.
        private const int SoloRestartGapMs = 1000;

        private readonly List<Player> _soloPlayers = new List<Player>();

        public int SoloRunCount { get { return _soloPlayers.Count; } }

        private void HandleSoloStart(Player player, SoloStart start)
        {
            if (player.Match != null)
            {
                player.Send(new ErrorMessage(ErrorCode.AlreadyInMatch, "Finish or forfeit your current match first."));
                return;
            }
            if (start.Segment != Segments.None && Segments.Find(start.Segment) == null)
            {
                player.Send(new ErrorMessage(ErrorCode.Unknown, "The match server does not know that segment."));
                return;
            }
            if (SteamRequiredForRanked && player.Record.SteamHash == null)
            {
                player.Send(new ErrorMessage(ErrorCode.SteamLinkRequired, "Ranked solo runs need your Steam account. Start the game through Steam: it links by itself."));
                return;
            }
            string modsRefusal = RankedModsRefusal(player, "Ranked solo runs");
            if (modsRefusal != null)
            {
                player.Send(new ErrorMessage(ErrorCode.ModsNotApproved, modsRefusal));
                return;
            }
            if (player.Queue != null)
            {
                RemoveFromQueue(player);
                player.Send(new QueueStatus { Queued = false, PlayersQueued = _queue.Count });
            }
            LeaveLobby(player, null);
            EndSolo(player, null);
            player.GameBuild = start.GameBuild ?? "";
            BeginSolo(player, player.GameBuild, null, 0, start.Segment);
        }

        private void BeginSolo(Player player, string gameBuild, IList<string> bans, int runsBefore, byte segment)
        {
            long now = _now();
            var run = new SoloRun
            {
                Seed = RandomNumberGenerator.GetInt32(ProtocolConstants.MinSeed, ProtocolConstants.MaxSeed + 1),
                GameBuild = gameBuild,
                StartedMs = now,
                Runs = runsBefore + 1,
                Segment = segment,
                Recorder = NewReplayRecorder(),
            };
            run.Host = new SoloHost(run.Seed, _options.RankedRules, player.Record.Name, player.Record.Rating, null, _now,
                m => OnSoloMessage(player, run, m), bans, ranked: true, segment: segment);
            player.Solo = run;
            if (!_soloPlayers.Contains(player)) _soloPlayers.Add(player);
            _log(player.Record.Name + (run.Runs == 1 ? " started a ranked solo run" : " restarted their ranked solo run (run " + run.Runs + ")") + SegmentNote(segment) + ", seed " + run.Seed
                 + (gameBuild.Length > 0 && run.Runs == 1 ? " (" + gameBuild + ")" : ""));
            run.Host.Start();
        }

        // " (Silos segment)" for a segment run, for the log.
        private static string SegmentNote(byte segment)
        {
            SegmentInfo part = Segments.Find(segment);
            return part != null ? " (" + part.Title + " segment)" : "";
        }

        // What the host sends goes to the player. A result is the end of the run.
        private void OnSoloMessage(Player player, SoloRun run, Message message)
        {
            var result = message as MatchResult;
            player.Send(message);
            if (result != null) OnSoloResult(player, run, result);
        }

        private void OnSoloResult(Player player, SoloRun run, MatchResult result)
        {
            if (player.Solo == run)
            {
                player.Solo = null;
                _soloPlayers.Remove(player);
                player.LastSolo = run;
            }
            if (result.Outcome == MatchOutcome.Win && result.YourTimeMs >= 0)
            {
                _log(player.Record.Name + " finished a ranked solo run" + SegmentNote(run.Segment) + " in " + RaceFormat.Time(result.YourTimeMs) + " (seed " + run.Seed + ")");
                // A segment run's time goes on its segment's board.
                SubmitBoardRun(run.Segment != Segments.None ? Segments.Board(run.Segment) : BoardKind.SoloTime, player, result.YourTimeMs, run.Seed, run.Host.Match.Rules, run.Host.Banned, run.GameBuild, run.Recorder, run.Watch);
            }
            else
                _log(player.Record.Name + "'s ranked solo run ended: " + (result.End == MatchEnd.Death ? "died" : result.Outcome == MatchOutcome.Aborted ? "left" : "not counted") + " (seed " + run.Seed + ")");
        }

        // The messages of a solo run go to its host. True when the message was the run's.
        private bool RouteSolo(Player player, Message message)
        {
            SoloRun run = player.Solo;
            if (run == null) return false;
            switch (message)
            {
                case PlayerState state:
                    run.Host.NoteGameTime(state.RaceTimeMs);
                    if (run.Host.Running)
                    {
                        run.Recorder.Add(state);
                        if (_options.TimerCheck) run.Watch.Report(_now(), state.RaceTimeMs, state.X, state.Y, state.Z);
                    }
                    return true;
                case SplitReached split:
                    if (split.Index < 0 || split.Index > ProtocolConstants.MaxSplitIndex) return true;
                    split.Level = NameSanitizer.CleanLabel(split.Level);
                    split.Region = NameSanitizer.CleanLabel(split.Region);
                    if (run.Host.Running) run.Recorder.Add(split);
                    run.Host.OnMessage(split);
                    return true;
                case BanReady _:
                case BanPick _:
                case MatchReady _:
                case AttemptStarted _:
                case PlayerFinished _:
                case PlayerDied _:
                case PlayerForfeit _:
                    run.Host.OnMessage(message);
                    return true;
                case PracticeRestart restart:
                    RestartSolo(player, run, restart);
                    return true;
            }
            return false;
        }

        // The player restarted in the game: this run is dropped (it does not count) and a new one starts on a new
        // seed, with the same artifact bans so the restart does not go through the ban phase again.
        private void RestartSolo(Player player, SoloRun run, PracticeRestart restart)
        {
            if (restart.MatchId != run.Host.Match.MatchId || !run.Host.BansDone || _now() - run.StartedMs < SoloRestartGapMs)
            {
                _log(player.Record.Name + "'s restart was not taken" + (restart.MatchId != run.Host.Match.MatchId ? " (it names another run)" : !run.Host.BansDone ? " (the ban phase is not over)" : " (too soon after the last one)"));
                return;
            }
            IList<string> bans = run.Host.Match.ArtifactBans ? new List<string>(run.Host.Banned) : null;
            player.Solo = null;
            BeginSolo(player, run.GameBuild, bans, run.Runs, run.Segment);
        }

        // The player restarted in the game after their ranked solo run had ended (they died, or finished): a new
        // ranked run on a new seed, with the same artifact bans. Without this the restart would have nowhere to go.
        private void RestartEndedSolo(Player player, PracticeRestart restart)
        {
            // They went on to something else in the meantime.
            if (player.Solo != null || player.Match != null || player.Queue != null || player.Lobby != null) return;
            SoloRun last = player.LastSolo;
            if (last == null || restart.MatchId != last.Host.Match.MatchId)
            {
                // The server no longer has that run (the connection dropped during it, or the server restarted).
                // Say so: the player pressed restart and must not be left waiting, or guessing which mode they are in.
                player.Send(new ErrorMessage(ErrorCode.NotInMatch, "That ranked solo run can no longer be restarted. Press SOLO under RANKED in WKSR for a new one."));
                return;
            }
            if (SteamRequiredForRanked && player.Record.SteamHash == null)
            {
                player.Send(new ErrorMessage(ErrorCode.SteamLinkRequired, "Ranked solo runs need your Steam account. Start the game through Steam: it links by itself."));
                return;
            }
            string modsRefusal = RankedModsRefusal(player, "Ranked solo runs");
            if (modsRefusal != null)
            {
                player.Send(new ErrorMessage(ErrorCode.ModsNotApproved, modsRefusal));
                return;
            }
            IList<string> bans = last.Host.Match.ArtifactBans && last.Host.BansDone ? new List<string>(last.Host.Banned) : null;
            player.LastSolo = null;
            BeginSolo(player, last.GameBuild, bans, last.Runs, last.Segment);
        }

        // Drops a player's solo run without a result (they queued, joined a lobby, or went away).
        private void EndSolo(Player player, string why)
        {
            SoloRun run = player.Solo;
            if (run == null) return;
            player.Solo = null;
            _soloPlayers.Remove(player);
            if (why != null) _log(player.Record.Name + "'s ranked solo run was dropped: " + why);
        }

        private void TickSolo(long now)
        {
            if (_soloPlayers.Count == 0) return;
            foreach (Player player in _soloPlayers.ToArray())
            {
                SoloRun run = player.Solo;
                if (run == null) { _soloPlayers.Remove(player); continue; }
                if (now - run.StartedMs > _options.MatchTimeLimitSeconds * 1000L)
                {
                    run.Host.OnMessage(new PlayerForfeit());   // ends it with a result, like a forfeit
                    continue;
                }
                run.Host.Tick();
            }
        }
    }
}
