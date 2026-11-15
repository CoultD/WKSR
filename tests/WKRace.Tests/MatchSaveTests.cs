using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    // The fixed save that match worlds are built from (game 0.62a forces unvisited sections and gates sections on
    // progress flags; two players' saves must not change the world).
    public class MatchSaveTests
    {
        [Theory]
        // Every section counts as visited, so none is forced in (Arterial Bypass is "pipeworkszeta").
        [InlineData("hasvisited-pipeworkszeta", true)]
        [InlineData("hasvisited-Lost Pier", true)]
        [InlineData("hasvisited-shutter-pier", true)]
        [InlineData("hasvisitedhauntedpier", true)]
        // Progress: achievements and unlocks earned, intros seen.
        [InlineData("ACH_NEST", true)]
        [InlineData("ACH_MOTHER", true)]
        [InlineData("unlockedduct", true)]
        [InlineData("playgroundaccess", true)]
        [InlineData("hasexperienced-bodycavityintro", true)]
        // Not earned by playing, and anything unknown: off for everyone.
        [InlineData("ACH_TEST", false)]
        [InlineData("cheatrooms-basic", false)]
        [InlineData("pipe_organ", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Flag_IsTheSameForEveryPlayer(string name, bool expected)
        {
            Assert.Equal(expected, MatchSave.Flag(name));
        }

        [Fact]
        public void RunFlags_AreRecognised()
        {
            Assert.True(MatchSave.IsRunFlag("hasgeneratedthisrun-Pipe Organ"));
            Assert.True(MatchSave.IsRunFlag("session-nest-locked-hotzone"));
            Assert.False(MatchSave.Flag("session-nest-locked-hotzone"));
            Assert.False(MatchSave.IsRunFlag("hasvisited-Pipe Organ"));
            Assert.False(MatchSave.IsRunFlag(null));
        }

        [Fact]
        public void LifetimeStats_ReadLikeAVeteran()
        {
            Assert.True(MatchSave.LifetimeStat > 1000000);
        }
    }
}
