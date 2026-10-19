using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;
using WKRace.Server;
using Xunit;

namespace WKRace.Tests
{
    // Ranked solo runs: one player, hosted by the server, a new seed on every run.
    public class SoloTests
    {
        private static MatchFound Start(TestClient c)
        {
            c.Send(new SoloStart { GameBuild = "steam-1|mod-0.9.1" });
            return c.Match;
        }

        [Fact]
        public void RankedSolo_RunsToAFinish_WithTheGameTimerAsItsTime()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                MatchFound found = Start(a);
                Assert.Equal(MatchMode.Solo, found.Mode);
                Assert.True(found.Ranked);
                Assert.Equal(h.Options.RankedRules, found.Rules);
                Assert.Equal(1, h.Core.SoloRunCount);

                a.Loaded();
                Assert.Equal(found.MatchId, a.Last<MatchStart>().MatchId);
                h.Advance(60000);
                a.Send(new PlayerState { Attempt = 1, RaceTimeMs = 52000 });
                a.Send(new SplitReached { Attempt = 1, Index = 1, Level = "M1_Silos_Storage_02", Region = "Deep Storage", RaceTimeMs = 30000 });
                // 60 s went by here; the game was paused for 8 of them, so its timer reads 52 s.
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 52000, RealTimeMs = 60000 });

                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                Assert.Equal(52000, a.Result.YourTimeMs);
                Assert.Equal(a.Result.OldRating, a.Result.NewRating);   // no rating change
                Assert.Equal(0, h.Core.SoloRunCount);
            }
        }

        [Fact]
        public void RankedSolo_TimeCannotExceedThreeTimesTheRealTime()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                Start(a);
                a.Loaded();
                h.Advance(10000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 9999999 });
                Assert.Equal(10000 * 3 + 1000, a.Result.YourTimeMs);
            }
        }

        [Fact]
        public void Restart_IsANewRunOnANewSeed_WithoutAResult()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                MatchFound first = Start(a);
                a.Loaded();
                h.Advance(20000);

                a.Send(new PracticeRestart { MatchId = first.MatchId });
                MatchFound second = a.Match;
                Assert.NotEqual(first.MatchId, second.MatchId);
                Assert.Equal(MatchMode.Solo, second.Mode);
                Assert.Equal(0, a.Conn.Count<MatchResult>());
                Assert.Equal(1, h.Core.SoloRunCount);

                // The run that was dropped takes nothing any more; the new one runs as usual.
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 20000 });
                Assert.Equal(0, a.Conn.Count<MatchResult>());
                a.Loaded();
                h.Advance(30000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 30000 });
                Assert.Equal(30000, a.Result.YourTimeMs);
            }
        }

        [Fact]
        public void Restart_KeepsTheArtifactBans_SoThereIsNoSecondBanPhase()
        {
            using (var h = new Harness())
            {
                h.Options.RankedRules = MatchRules.Ranked;
                TestClient a = h.Login("Alpha");
                MatchFound first = Start(a);
                Assert.True(first.ArtifactBans);

                a.Send(new BanReady { MatchId = first.MatchId, Pool = new List<string> { "Item_A", "Item_B", "Item_C" } });
                Assert.True(a.Last<BanState>().YourTurn);
                // A restart during the ban phase is not taken.
                a.Send(new PracticeRestart { MatchId = first.MatchId });
                Assert.Equal(first.MatchId, a.Match.MatchId);

                a.Send(new BanPick { MatchId = first.MatchId, Item = "Item_B" });
                a.Send(new BanPick { MatchId = first.MatchId, Item = "" });
                Assert.False(a.Last<BanState>().YourTurn);
                Assert.Equal(new[] { "Item_B" }, a.Last<BanState>().Banned);

                a.Loaded();
                h.Advance(5000);
                int banStates = a.Conn.Count<BanState>();
                a.Send(new PracticeRestart { MatchId = first.MatchId });
                MatchFound second = a.Match;
                Assert.NotEqual(first.MatchId, second.MatchId);
                BanState carried = a.Last<BanState>();
                Assert.Equal(banStates + 1, a.Conn.Count<BanState>());
                Assert.Equal(second.MatchId, carried.MatchId);
                Assert.False(carried.YourTurn);
                Assert.Equal(new[] { "Item_B" }, carried.Banned);
            }
        }

        [Fact]
        public void ARestartAfterDying_IsANewRankedRun_WithTheSameBans()
        {
            using (var h = new Harness())
            {
                h.Options.RankedRules = MatchRules.Ranked;
                TestClient a = h.Login("Alpha");
                MatchFound first = Start(a);
                a.Send(new BanReady { MatchId = first.MatchId, Pool = new List<string> { "Item_A", "Item_B", "Item_C" } });
                a.Send(new BanPick { MatchId = first.MatchId, Item = "Item_B" });
                a.Send(new BanPick { MatchId = first.MatchId, Item = "" });
                a.Loaded();
                h.Advance(15000);
                a.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 15000, Cause = "falling", Text = "FELL TO YOUR DEATH" });
                Assert.Equal(MatchEnd.Death, a.Result.End);
                Assert.Equal(0, h.Core.SoloRunCount);

                // The player restarts in the game: the session stays ranked solo (it used to fall back to unranked).
                h.Advance(3000);
                a.Send(new PracticeRestart { MatchId = first.MatchId });
                MatchFound second = a.Match;
                Assert.NotEqual(first.MatchId, second.MatchId);
                Assert.Equal(MatchMode.Solo, second.Mode);
                Assert.True(second.Ranked);
                Assert.NotEqual(first.Seed, second.Seed);
                Assert.Equal(1, h.Core.SoloRunCount);
                Assert.Equal(new[] { "Item_B" }, a.Last<BanState>().Banned);   // no second ban phase
                Assert.False(a.Last<BanState>().YourTurn);

                // The new run finishes and counts like any other.
                a.Loaded();
                h.Advance(200000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 200000, RealTimeMs = 200000 });
                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                Assert.True(a.Last<BoardNote>().PersonalBest);

                // The same restart sent again does nothing, and neither does one for a run of long ago.
                int found = a.Conn.Count<MatchFound>();
                a.Send(new PracticeRestart { MatchId = first.MatchId });
                Assert.Equal(found, a.Conn.Count<MatchFound>());

                // After a finish too: restart gives the next ranked run. Not once they have gone on to the queue.
                h.Advance(3000);
                a.Send(new PracticeRestart { MatchId = second.MatchId });
                Assert.Equal(found + 1, a.Conn.Count<MatchFound>());
                MatchFound third = a.Match;
                a.Loaded();
                a.Send(new PlayerForfeit());
                a.Send(new QueueJoin());
                a.Send(new PracticeRestart { MatchId = third.MatchId });
                Assert.Equal(found + 1, a.Conn.Count<MatchFound>());

                // A run the server no longer has (the connection dropped during it): the player is told what to
                // press, and no run starts on its own.
                TestClient b = h.Login("Bravo");
                b.Send(new PracticeRestart { MatchId = System.Guid.NewGuid() });
                Assert.Contains("Press SOLO", b.Last<ErrorMessage>().Text);
                Assert.Equal(0, b.Conn.Count<MatchFound>());
            }
        }

        [Fact]
        public void DeathAndForfeit_EndTheRunWithoutATime()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                Start(a);
                a.Loaded();
                h.Advance(15000);
                a.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 15000, Cause = "falling", Text = "FELL TO YOUR DEATH" });
                Assert.Equal(MatchOutcome.Loss, a.Result.Outcome);
                Assert.Equal(MatchEnd.Death, a.Result.End);
                Assert.True(a.Result.YourTimeMs < 0);

                Start(a);
                a.Loaded();
                a.Send(new PlayerForfeit());
                Assert.Equal(MatchOutcome.Aborted, a.Result.Outcome);
                Assert.Equal(0, h.Core.SoloRunCount);
            }
        }

        [Fact]
        public void QueueingOrDisconnecting_DropsTheRun()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                MatchFound solo = Start(a);
                a.Send(new QueueJoin());
                Assert.Equal(0, h.Core.SoloRunCount);
                Assert.True(a.Last<QueueStatus>().Queued);
                // What the dropped run would have taken is now nobody's.
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 1000 });
                Assert.Equal(0, a.Conn.Count<MatchResult>());

                a.Send(new QueueLeave());
                Start(a);
                Assert.Equal(1, h.Core.SoloRunCount);
                a.Disconnect();
                Assert.Equal(0, h.Core.SoloRunCount);
                Assert.NotEqual(solo.MatchId, a.Match.MatchId);
            }
        }

        [Fact]
        public void StartingARun_LeavesTheQueue_AndIsRefusedDuringAMatch()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                a.Send(new QueueJoin());
                Start(a);
                Assert.Equal(0, h.Core.QueuedCount);
                Assert.Equal(1, h.Core.SoloRunCount);
            }

            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Send(new SoloStart());
                Assert.Equal(ErrorCode.AlreadyInMatch, a.Last<ErrorMessage>().Code);
                Assert.Equal(0, h.Core.SoloRunCount);
            }
        }

        [Fact]
        public void TheTimeLimit_EndsARunThatNeverFinishes()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                Start(a);
                a.Loaded();
                h.Advance(h.Options.MatchTimeLimitSeconds * 1000L + 1000);
                Assert.Equal(MatchOutcome.Aborted, a.Result.Outcome);
                Assert.Equal(0, h.Core.SoloRunCount);
            }
        }
    }
}
