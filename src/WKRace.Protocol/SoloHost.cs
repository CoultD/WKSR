using System;
using System.Collections.Generic;
using System.Linq;

namespace WKRace.Protocol
{
    // The best run a solo run is played against (its ghost and splits), where there is one.
    public interface ISoloBest
    {
        bool Finished { get; }
        int EndMs { get; }
        int Levels { get; }
        string Describe();
        // The recorded run: what to send to the game at which time on the run's clock.
        IList<KeyValuePair<int, Message>> Replay { get; }
    }

    // Runs one player's solo run: the same steps as a match (artifact bans, world ready, countdown, finish or death)
    // for a single player. WKSR uses it on the player's own PC for an unranked solo run, where it stands in for the
    // match server; the server uses it for a ranked solo run. Messages for the client go through `send`, exactly as
    // the server's do.
    public sealed class SoloHost
    {
        // No countdown in practice: the run starts as soon as the world is loaded.
        public const int CountdownMs = 0;
        public const int BanTurnSeconds = 20;
        // Both of a race's two bans are the player's here, so the world can be the same every time.
        public const int BanTurns = 2;
        public const string BestName = "Your best";

        private enum Step { Banning, Preparing, Running, Done }

        private readonly Func<long> _now;
        private readonly Action<Message> _send;
        private readonly string _playerName;
        private readonly int _rating;
        private Step _step;
        private long _goMs;
        private int _attempt = 1;
        private readonly SortedDictionary<int, SplitReached> _splits = new SortedDictionary<int, SplitReached>();

        private List<string> _pool;
        private readonly List<string> _banned = new List<string>();
        private int _banTurnsTaken;
        private long _banDeadlineMs;

        private int _replayIndex;
        // The game's run timer as last reported (-1 before the first report). Written on the link's thread.
        private volatile int _gameMs = -1;
        // Bans carried over from the run being repeated (a restart, or the race it practises): no ban phase then.
        private readonly List<string> _presetBans;

        private readonly bool _ranked;

        // rules: the modifiers of the run. presetBans: the bans of the run being repeated (no ban phase then).
        // ranked: a ranked solo run (hosted by the server): a new seed every run, so there is no best run on it.
        // segment: a run of that segment (Segments), which ends where the segment does.
        public SoloHost(int seed, MatchRules rules, string playerName, int rating, ISoloBest best, Func<long> now, Action<Message> send,
            IList<string> presetBans = null, bool ranked = false, string bestName = null, byte segment = Segments.None)
        {
            _ranked = ranked;
            SegmentInfo part = Segments.Find(segment);
            // A segment that ends before the artifact room has nothing to ban.
            if (part != null && !part.Artifacts)
            {
                presetBans = null;
                rules &= ~MatchRules.ArtifactBans;
            }
            if (presetBans != null)
            {
                rules |= MatchRules.ArtifactBans;
                _presetBans = presetBans.Where(b => !string.IsNullOrEmpty(b)).Distinct().Take(ProtocolConstants.MaxBanPool).ToList();
            }
            _playerName = playerName ?? "";
            _rating = rating;
            _now = now;
            _send = send;
            Best = best;
            Match = new MatchFound
            {
                MatchId = Guid.NewGuid(),
                Seed = seed,
                // The ghost: the player's own best on the seed, or (bestName) someone's run they picked to run against.
                OpponentName = best != null ? (string.IsNullOrEmpty(bestName) ? BestName : bestName) : ranked ? "Ranked solo" : "Practice",
                OpponentRating = 0,
                Competitive = true,
                Attempt = 1,
                GraceSeconds = 60,
                Ranked = ranked,
                Rules = rules & MatchRules.All,
                Mode = ranked ? MatchMode.Solo : MatchMode.Duel,
                // The best run being replayed is racer 0 (recorded runs carry no racer number).
                YourRacer = 1,
                PlayerCount = 2,
                // No ready check in practice: the run starts as soon as the world is loaded.
                ReadySeconds = 0,
                Segment = part != null ? segment : Segments.None,
            };
        }

        public MatchFound Match { get; private set; }
        public ISoloBest Best { get; private set; }
        public bool Ranked { get { return _ranked; } }
        // The artifacts banned for this run (to carry over to the next one after a restart).
        public IList<string> Banned { get { return _banned; } }
        public bool IsOver { get { return _step == Step.Done; } }
        // The ban phase is over (or there was none).
        public bool BansDone { get { return _step != Step.Banning; } }
        // Past the countdown's start and not over yet.
        public bool Running { get { return _step == Step.Running; } }

        // What the game is told about the best run it races (PracticeMark).
        public PracticeMark Mark()
        {
            return new PracticeMark
            {
                MatchId = Match.MatchId,
                BestTimeMs = Best != null ? Best.EndMs : -1,
                BestFinished = Best != null && Best.Finished,
                BestLevels = Best != null ? Best.Levels : 0,
            };
        }

        public void Start()
        {
            _step = Match.ArtifactBans ? Step.Banning : Step.Preparing;
            _send(Match);
            if (_presetBans != null)
            {
                _pool = new List<string>(_presetBans);
                _banned.AddRange(_presetBans);
                FinishBans();
            }
        }

        // What the game reported, passed on by the client in place of sending it to the server.
        public void OnMessage(Message message)
        {
            if (_step == Step.Done) return;
            switch (message)
            {
                case BanReady ready: OnBanReady(ready); break;
                case BanPick pick: OnBanPick(pick); break;
                case MatchReady ready: OnReady(ready); break;
                case SplitReached split:
                    if (_step == Step.Running && split.Attempt == _attempt) _splits[split.Index] = split;
                    break;
                case AttemptStarted attempt:
                    if (_step == Step.Running && attempt.Attempt > _attempt)
                    {
                        _attempt = attempt.Attempt;
                        _splits.Clear();
                    }
                    break;
                case PlayerFinished finished: OnFinished(finished); break;
                case PlayerDied died: OnDied(died); break;
                case PlayerForfeit _: End(MatchOutcome.Aborted, -1, _ranked ? "Ranked solo run ended." : "Unranked run ended.", MatchEnd.Other); break;
            }
        }

        // Every 100 ms or so: ban turn timeouts, and the best run's replay.
        public void Tick()
        {
            long now = _now();
            if (_step == Step.Banning && _pool != null && now >= _banDeadlineMs) NextBanTurn();
            // The best run is replayed against the game's timer, not the wall clock: it was recorded on that timer,
            // and it stops with the game (a pause, a loading screen).
            if (_step == Step.Running && Best != null && _gameMs >= 0) Replay(_gameMs);
        }

        public void NoteGameTime(int gameMs)
        {
            _gameMs = gameMs;
        }

        // ---- bans ----------------------------------------------------------------

        private void OnBanReady(BanReady ready)
        {
            if (_step != Step.Banning || ready.MatchId != Match.MatchId || _pool != null) return;
            _pool = ready.Pool.Where(p => !string.IsNullOrEmpty(p)).Distinct().Take(ProtocolConstants.MaxBanPool).ToList();
            if (_pool.Count == 0)
            {
                FinishBans();
                return;
            }
            _banDeadlineMs = _now() + BanTurnSeconds * 1000L;
            SendBanState();
        }

        private void OnBanPick(BanPick pick)
        {
            if (_step != Step.Banning || _pool == null || pick.MatchId != Match.MatchId) return;
            if (pick.Item.Length > 0)
            {
                if (!_pool.Contains(pick.Item) || _banned.Contains(pick.Item)) return;
                _banned.Add(pick.Item);
            }
            NextBanTurn();
        }

        private void NextBanTurn()
        {
            _banTurnsTaken++;
            if (_banTurnsTaken >= BanTurns || _banned.Count >= _pool.Count)
            {
                FinishBans();
                return;
            }
            _banDeadlineMs = _now() + BanTurnSeconds * 1000L;
            SendBanState();
        }

        private void FinishBans()
        {
            _step = Step.Preparing;
            SendBanState();
        }

        private void SendBanState()
        {
            bool done = _step != Step.Banning;
            _send(new BanState
            {
                MatchId = Match.MatchId,
                Pool = new List<string>(_pool ?? new List<string>()),
                Banned = new List<string>(_banned),
                BannedBy = _banned.Select(b => _playerName).ToList(),
                TurnName = done ? "" : _playerName,
                YourTurn = !done,
                SecondsLeft = done ? 0 : (int)Math.Max(0, (_banDeadlineMs - _now() + 999) / 1000),
                Done = done,
            });
        }

        // ---- the run ---------------------------------------------------------------

        private void OnReady(MatchReady ready)
        {
            if (_step != Step.Preparing || ready.MatchId != Match.MatchId) return;
            _goMs = _now() + CountdownMs;
            _step = Step.Running;
            _send(new MatchStart { MatchId = Match.MatchId, GoServerTimeMs = _goMs });
        }

        private void OnFinished(PlayerFinished finished)
        {
            if (_step != Step.Running || finished.Attempt != _attempt) return;
            if (finished.Cheated)
            {
                End(MatchOutcome.Loss, -1, "Cheats were used, so this run does not count.", MatchEnd.Other);
                return;
            }
            // The game's own clock, kept within what can have passed here.
            int time = Clamp(finished.RaceTimeMs);
            string reason;
            if (Best == null) reason = "Finished in " + RaceFormat.Time(time) + (_ranked ? "." : ". Your first finish on this seed.");
            else if (!Best.Finished) reason = "Finished in " + RaceFormat.Time(time) + ". Your first finish on this seed (your best so far was " + Best.Describe() + ").";
            else if (time < Best.EndMs) reason = "New best: " + RaceFormat.Time(time) + ", " + RaceFormat.Delta(time - Best.EndMs) + " on your old best.";
            else reason = "Finished in " + RaceFormat.Time(time) + ", " + RaceFormat.Delta(time - Best.EndMs) + " behind your best.";
            End(MatchOutcome.Win, time, reason, MatchEnd.Finish);
        }

        private void OnDied(PlayerDied died)
        {
            if (_step != Step.Running || died.Attempt != _attempt) return;
            int time = Clamp(died.RaceTimeMs);
            int levels = _splits.Count;
            string where = levels == 0 ? "" : " in " + RaceFormat.LevelLabel(_splits.Values.Last().Level);
            string reason = (string.IsNullOrEmpty(died.Text) ? "You died" : died.Text.Trim()) + where + " after " + RaceFormat.Time(time) + " (" + levels + (levels == 1 ? " level" : " levels") + ").";
            if (Best != null && !Best.Finished && levels > Best.Levels) reason += " Your furthest run on this seed yet.";
            else if (Best != null) reason += " Your best: " + Best.Describe() + ".";
            End(MatchOutcome.Loss, -1, reason, MatchEnd.Death);
        }

        // The game's timer can run up to three times as fast as real time (the shortcut sections), never faster.
        private int Clamp(int raceMs)
        {
            long elapsed = Math.Max(0, _now() - _goMs);
            return (int)Math.Max(0, Math.Min(raceMs, elapsed * 3 + 1000));
        }

        // The game closed or crashed: the run cannot go on.
        public void GameClosed()
        {
            End(MatchOutcome.Aborted, -1, "White Knuckle closed.", MatchEnd.Other);
        }

        private void End(MatchOutcome outcome, int timeMs, string reason, MatchEnd end)
        {
            if (_step == Step.Done) return;
            _step = Step.Done;
            _send(new MatchResult
            {
                MatchId = Match.MatchId,
                Outcome = outcome,
                YourTimeMs = timeMs,
                OpponentTimeMs = Best != null && Best.Finished ? Best.EndMs : -1,
                OldRating = _rating,
                NewRating = _rating,
                Reason = reason,
                End = end,
            });
        }

        // Sends the best run's messages that are due at this race time. Only the latest due state matters for the ghost.
        private void Replay(long raceMs)
        {
            OpponentState latest = null;
            IList<KeyValuePair<int, Message>> events = Best.Replay;
            while (_replayIndex < events.Count && events[_replayIndex].Key <= raceMs)
            {
                Message m = events[_replayIndex++].Value;
                if (m is OpponentState state) latest = state;
                else _send(m);
            }
            if (latest != null) _send(latest);
        }
    }
}
