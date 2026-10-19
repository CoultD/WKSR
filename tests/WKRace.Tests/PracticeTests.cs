using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WKRace.Client;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    public class PracticeTests
    {
        private long _now = 1700000000000;
        private readonly List<Message> _sent = new List<Message>();

        private SoloHost Host(bool bans = false, RunRecording best = null)
        {
            return new SoloHost(4242, bans ? MatchRules.Ranked : MatchRules.CuratedSeeds, "Climber", 1016, best, () => _now, m => _sent.Add(m));
        }

        private T Last<T>() where T : Message
        {
            return _sent.OfType<T>().Last();
        }

        // Ready, then the countdown and the race clock up to `raceMs`.
        private void Go(SoloHost host, int raceMs)
        {
            host.OnMessage(new MatchReady { MatchId = host.Match.MatchId, WorldHash = 1, LevelCount = 30 });
            _now = Last<MatchStart>().GoServerTimeMs + raceMs;
        }

        [Fact]
        public void Run_GoesThroughTheRaceSteps_WithoutARating()
        {
            SoloHost host = Host();
            host.Start();
            MatchFound found = Last<MatchFound>();
            Assert.False(found.Ranked);
            Assert.True(found.Competitive);
            Assert.Equal(4242, found.Seed);
            Assert.False(found.ArtifactBans);

            host.OnMessage(new MatchReady { MatchId = found.MatchId });
            Assert.Equal(_now + SoloHost.CountdownMs, Last<MatchStart>().GoServerTimeMs);

            _now += SoloHost.CountdownMs + 600000;
            host.OnMessage(new PlayerFinished { Attempt = 1, RaceTimeMs = 600000 });
            MatchResult result = Last<MatchResult>();
            Assert.Equal(MatchOutcome.Win, result.Outcome);
            Assert.Equal(600000, result.YourTimeMs);
            Assert.Equal(-1, result.OpponentTimeMs);
            Assert.Equal(1016, result.OldRating);
            Assert.Equal(1016, result.NewRating);
            Assert.Contains("first finish", result.Reason);
            Assert.True(host.IsOver);
        }

        [Fact]
        public void Finish_IsCappedByTheClock_AndCheatsDoNotCount()
        {
            SoloHost host = Host();
            host.Start();
            Go(host, 1000);
            // The game's timer can run three times as fast as real time (a shortcut), never faster.
            host.OnMessage(new PlayerFinished { Attempt = 1, RaceTimeMs = 999999 });
            Assert.Equal(4000, Last<MatchResult>().YourTimeMs);

            _sent.Clear();
            host = Host();
            host.Start();
            Go(host, 300000);
            host.OnMessage(new PlayerFinished { Attempt = 2, RaceTimeMs = 300000 });   // an attempt that never started
            Assert.Empty(_sent.OfType<MatchResult>());
            host.OnMessage(new PlayerFinished { Attempt = 1, RaceTimeMs = 300000, Cheated = true });
            Assert.Equal(MatchOutcome.Loss, Last<MatchResult>().Outcome);
        }

        [Fact]
        public void Forfeit_EndsTheRun()
        {
            SoloHost host = Host();
            host.Start();
            Go(host, 5000);
            host.OnMessage(new PlayerForfeit());
            Assert.Equal(MatchOutcome.Aborted, Last<MatchResult>().Outcome);
            host.OnMessage(new PlayerFinished { Attempt = 1, RaceTimeMs = 5000 });
            Assert.Single(_sent.OfType<MatchResult>());
        }

        [Fact]
        public void Bans_AreBothThePlayers_AndTimeOut()
        {
            SoloHost host = Host(bans: true);
            host.Start();
            Guid id = host.Match.MatchId;
            host.OnMessage(new MatchReady { MatchId = id });
            Assert.Empty(_sent.OfType<MatchStart>());   // worlds load after the bans

            host.OnMessage(new BanReady { MatchId = id, Pool = new List<string> { "Remote", "Rapier", "EVAGlove" } });
            BanState state = Last<BanState>();
            Assert.True(state.YourTurn);
            Assert.False(state.Done);
            Assert.Equal(SoloHost.BanTurnSeconds, state.SecondsLeft);

            host.OnMessage(new BanPick { MatchId = id, Item = "Nonsense" });
            Assert.Empty(Last<BanState>().Banned);
            host.OnMessage(new BanPick { MatchId = id, Item = "Rapier" });
            Assert.Equal(new[] { "Rapier" }, Last<BanState>().Banned);
            Assert.True(Last<BanState>().YourTurn);

            _now += SoloHost.BanTurnSeconds * 1000L;
            host.Tick();                                  // the second turn ran out: passed
            state = Last<BanState>();
            Assert.True(state.Done);
            Assert.Equal(new[] { "Rapier" }, state.Banned);
            Assert.Equal(new[] { "Climber" }, state.BannedBy);

            host.OnMessage(new MatchReady { MatchId = id });
            Assert.Single(_sent.OfType<MatchStart>());
        }

        private static RunRecording Recorded(int finishMs)
        {
            var recorder = new RunRecorder();
            recorder.Add(new PlayerState { Attempt = 1, RaceTimeMs = 0, X = 1 });
            recorder.Add(new PlayerState { Attempt = 1, RaceTimeMs = 50, X = 2 });     // too close to the last: dropped
            recorder.Add(new PlayerState { Attempt = 1, RaceTimeMs = 100, X = 3 });
            recorder.Add(new SplitReached { Attempt = 1, Index = 0, Level = "M1_Silos_SafeArea_01", RaceTimeMs = 0 });
            recorder.Add(new AttemptStarted { Attempt = 2, RaceTimeMs = 150 });
            recorder.Add(new PlayerState { Attempt = 2, RaceTimeMs = 200, X = 4 });
            recorder.Add(new SplitReached { Attempt = 2, Index = 1, Level = "M1_Silos_Storage_01", RaceTimeMs = 250 });
            recorder.Add(new PlayerState { Attempt = 2, RaceTimeMs = 300, X = 5 });
            recorder.Add(new PlayerState { Attempt = 2, RaceTimeMs = finishMs + 500, X = 6 });   // after the finish: dropped
            return recorder.Finish(4242, finishMs, true, false);
        }

        [Fact]
        public void Recorder_KeepsTheRunAsOpponentMessages()
        {
            RunRecording run = Recorded(1000);
            Assert.Equal(new[] { 0, 0, 100, 150, 200, 250, 300, 1000 }, run.Events.Select(e => e.Key));
            Assert.Equal(new[] { 1f, 3f, 4f, 5f }, run.Events.Select(e => e.Value).OfType<OpponentState>().Select(s => s.X));
            OpponentFinished finish = (OpponentFinished)run.Events.Last().Value;
            Assert.Equal(2, finish.Attempt);
            Assert.Equal(1000, finish.RaceTimeMs);

            var cheat = new RunRecorder();
            cheat.Add(new PlayerFinished { Attempt = 1, RaceTimeMs = 5, Cheated = true });
            Assert.True(cheat.Cheated);
        }

        [Fact]
        public void Replay_SendsTheBestRunOnTheGameTimer()
        {
            SoloHost host = Host(best: Recorded(1000));
            host.Start();
            Assert.Equal(SoloHost.BestName, Last<MatchFound>().OpponentName);
            Assert.Equal(1000, host.Mark().BestTimeMs);
            Assert.True(host.Mark().BestFinished);
            Go(host, 220);
            _sent.Clear();
            // Nothing is replayed until the game has reported its timer.
            host.Tick();
            Assert.Empty(_sent);

            host.NoteGameTime(220);
            host.Tick();
            // Due by 220 ms: the split at 0, the reset at 150, and only the newest state (200) for the ghost.
            Assert.Equal(new[] { MessageType.OpponentSplit, MessageType.OpponentAttempt, MessageType.OpponentState }, _sent.Select(m => m.Type));
            Assert.Equal(4f, ((OpponentState)_sent.Last()).X);

            _sent.Clear();
            host.Tick();
            Assert.Empty(_sent);

            // The game is paused: real time goes by, its timer does not, and neither does the ghost.
            _now += 5000;
            host.Tick();
            Assert.Empty(_sent);

            host.NoteGameTime(1220);
            host.Tick();
            Assert.Equal(1000, Last<OpponentFinished>().RaceTimeMs);

            host.OnMessage(new PlayerFinished { Attempt = 1, RaceTimeMs = 900 });
            Assert.Equal(1000, Last<MatchResult>().OpponentTimeMs);
            Assert.StartsWith("New best", Last<MatchResult>().Reason);
        }

        [Fact]
        public void Store_KeepsOnlyTheBestRunPerSeed()
        {
            string dir = Path.Combine(Path.GetTempPath(), "wkrace-practice-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new PracticeStore(dir);
                Assert.Null(store.Load(4242));
                Assert.True(store.SaveIfBest(Recorded(1000)));
                Assert.False(store.SaveIfBest(Recorded(1200)));
                Assert.True(store.SaveIfBest(Recorded(800)));

                RunRecording back = store.Load(4242);
                Assert.Equal(800, back.EndMs);
                Assert.True(back.Finished);
                Assert.Equal(Recorded(800).Events.Select(e => e.Key), back.Events.Select(e => e.Key));
                Assert.Null(store.Load(7));

                File.WriteAllBytes(Directory.GetFiles(dir).Single(), new byte[] { 1, 2, 3 });
                Assert.Null(store.Load(4242));                // damaged: as if there were none
                Assert.True(store.SaveIfBest(Recorded(1500)));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        // A run that reached `levels` levels (one split each, 1000 ms apart) and ended at endMs.
        private static RunRecording Run(int levels, int endMs, bool finished, int firstSplitMs = 0)
        {
            var recorder = new RunRecorder();
            for (int i = 0; i < levels; i++)
                recorder.Add(new SplitReached { Attempt = 1, Index = i, Level = "L" + i, RaceTimeMs = firstSplitMs + i * 1000 });
            if (!finished) recorder.Add(new PlayerDied { Attempt = 1, RaceTimeMs = endMs });
            return recorder.Finish(4242, endMs, finished, false);
        }

        [Fact]
        public void BestRule_FinishBeatsDeath_ThenFurthest_ThenSoonest()
        {
            Assert.True(Run(30, 900000, true).IsBetterThan(Run(40, 100000, false)));
            Assert.False(Run(40, 100000, false).IsBetterThan(Run(30, 900000, true)));
            Assert.True(Run(30, 800000, true).IsBetterThan(Run(30, 900000, true)));
            Assert.True(Run(12, 50000, false).IsBetterThan(Run(11, 20000, false)));
            Assert.True(Run(12, 90000, false, 0).IsBetterThan(Run(12, 20000, false, 5000)));   // got to level 12 sooner
            Assert.True(Run(1, 1, false).IsBetterThan(null));

            RunRecording died = Run(5, 7000, false);
            Assert.Equal(5, died.Levels);
            Assert.Equal(4000, died.LastSplitMs);
            Assert.Equal("out after 5 levels", died.Describe());
            Assert.DoesNotContain(died.Events, e => e.Value is OpponentFinished);   // a died run has no finish to replay
        }

        [Fact]
        public void Death_EndsThePracticeRun()
        {
            SoloHost host = Host(best: Run(3, 9000, false));
            host.Start();
            Go(host, 5000);
            host.OnMessage(new SplitReached { Attempt = 1, Index = 0, Level = "M1_Silos_SafeArea_01", RaceTimeMs = 0 });
            host.OnMessage(new SplitReached { Attempt = 1, Index = 1, Level = "M1_Silos_Storage_01", RaceTimeMs = 2000 });
            host.OnMessage(new PlayerDied { Attempt = 1, RaceTimeMs = 4800 });
            MatchResult result = Last<MatchResult>();
            Assert.Equal(MatchOutcome.Loss, result.Outcome);
            Assert.Equal(MatchEnd.Death, result.End);
            Assert.Equal(-1, result.YourTimeMs);
            Assert.Equal(-1, result.OpponentTimeMs);                  // the best did not finish either
            Assert.Contains("You died", result.Reason);
            Assert.Contains("2 levels", result.Reason);
            Assert.False(host.Mark().BestFinished);
            Assert.Equal(3, host.Mark().BestLevels);
        }

        [Fact]
        public void Restart_RepeatsTheBans_WithoutABanPhase()
        {
            var host = new SoloHost(4242, MatchRules.CuratedSeeds, "Climber", 1016, null, () => _now, m => _sent.Add(m), new List<string> { "Rapier", "Remote" });
            host.Start();
            Assert.True(Last<MatchFound>().ArtifactBans);
            BanState bans = Last<BanState>();
            Assert.True(bans.Done);
            Assert.Equal(new[] { "Rapier", "Remote" }, bans.Banned);
            host.OnMessage(new BanReady { MatchId = host.Match.MatchId, Pool = new List<string> { "Rapier" } });   // late: ignored
            Assert.Single(_sent.OfType<BanState>());
            host.OnMessage(new MatchReady { MatchId = host.Match.MatchId });
            Assert.Single(_sent.OfType<MatchStart>());
        }

        [Fact]
        public void Store_KeepsDiedRunsUntilSomethingBetter()
        {
            string dir = Path.Combine(Path.GetTempPath(), "wkrace-practice-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new PracticeStore(dir);
                Assert.True(store.SaveIfBest(Run(5, 7000, false)));
                Assert.False(store.SaveIfBest(Run(4, 3000, false)));
                Assert.True(store.SaveIfBest(Run(8, 9000, false)));
                Assert.True(store.SaveIfBest(Run(30, 900000, true)));
                Assert.False(store.SaveIfBest(Run(40, 100000, false)));
                RunRecording best = store.Load(4242);
                Assert.True(best.Finished);
                Assert.Equal(900000, best.EndMs);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void RunFiles_FromBeforeTheGameTimer_AreNotRead()
        {
            // Files written before 0.9.1 ("WKRUN1", "WKRUN2") were timed on the real-time clock: their ghost and
            // splits would not line up with a run timed on the game's timer.
            var w = new PacketWriter();
            foreach (char c in "WKRUN2") w.WriteByte((byte)c);
            w.WriteInt32(4242);
            w.WriteInt32(600000);
            w.WriteInt64(1700000000000);
            w.WriteBool(false);
            w.WriteInt32(1);
            byte[] finish = MessageCodec.Encode(new OpponentFinished { Attempt = 1, RaceTimeMs = 600000 });
            w.WriteInt32(600000);
            w.WriteUInt16((ushort)finish.Length);
            w.WriteFixedBytes(finish, finish.Length);
            Assert.Throws<ProtocolException>(() => RunRecording.FromBytes(w.ToArray()));

            RunRecording died = RunRecording.FromBytes(Run(3, 5000, false).ToBytes());
            Assert.False(died.Finished);
            Assert.Equal(3, died.Levels);
        }

        [Fact]
        public void Store_SitsNextToTheSettingsFile()
        {
            Assert.Equal(Path.Combine("C:", "x", "practice"), PrivateDir(PracticeStore.For(Path.Combine("C:", "x", "client.json"))));
            Assert.Equal(Path.Combine("C:", "x", "practice.test"), PrivateDir(PracticeStore.For(Path.Combine("C:", "x", "client.test.json"))));
        }

        private static string PrivateDir(PracticeStore store)
        {
            return (string)typeof(PracticeStore).GetField("_dir", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(store);
        }
    }
}
