using System.Linq;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    // A death ends the race: whoever is still alive wins, however far behind they are.
    public class DeathTests
    {
        [Fact]
        public void Death_TheReasonUsesTheGamesLine()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(60000);
                b.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 59000, Cause = "teeth", Text = "EATEN ALIVE" });
                Assert.Equal("Bravo died: EATEN ALIVE.", a.Result.Reason);
            }
        }

        [Fact]
        public void Death_EndsTheRace_TheSurvivorWins()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(120000);
                for (int i = 0; i < 10; i++) a.Send(new SplitReached { Attempt = 1, Index = i, Level = "L" + i, RaceTimeMs = i * 1000 });
                a.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 119000 });     // ahead, but dead

                Assert.Equal(MatchOutcome.Loss, a.Result.Outcome);
                Assert.Equal(MatchOutcome.Win, b.Result.Outcome);
                Assert.Equal(MatchEnd.Death, a.Result.End);
                Assert.Equal(MatchEnd.Death, b.Result.End);
                Assert.Equal("Alpha died.", b.Result.Reason);
                Assert.True(b.Result.NewRating > b.Result.OldRating);
                Assert.True(a.Result.NewRating < a.Result.OldRating);
            }
        }

        [Fact]
        public void Death_OfEitherRacer_EndsTheRace()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(60000);
                b.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 59000 });
                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                Assert.Equal(MatchEnd.Death, a.Result.End);
            }
        }

        [Fact]
        public void Finish_StandsWhenTheOtherDiesAfterwards()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(600000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 600000 });
                a.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 600100 });     // already finished: ignored
                Assert.Empty(a.Conn.Sent.OfType<MatchResult>());
                b.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 600200 });
                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                Assert.Equal(600000, a.Result.YourTimeMs);
            }
        }

        [Fact]
        public void Death_FromAnOldAttempt_IsIgnored()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(60000);
                a.Send(new PlayerDied { Attempt = 2, RaceTimeMs = 59000 });
                Assert.Empty(a.Conn.Sent.OfType<MatchResult>());
            }
        }

        [Fact]
        public void Finishes_Report_HowTheRaceEnded()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(600000);
                a.Send(new PlayerForfeit());
                Assert.Equal(MatchEnd.Other, b.Result.End);
            }
        }
    }
}
