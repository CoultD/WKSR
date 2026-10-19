using System.Collections.Generic;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    public class SplitKeysTests
    {
        [Fact]
        public void DifferentRoutes_ShareKeysOnlyForSharedLevels()
        {
            // One racer goes through the Abyss, the other takes the Hadal Diver; both end up in the Nest.
            List<string> abyss = SplitKeys.Build(new[] { "Start", "M4_Abyss_Transit_06", "M4_Abyss_Garden_03", "M4_Abyss_Outro_01", "M5_Nest_Trough_Entrance_01" });
            List<string> diver = SplitKeys.Build(new[] { "Start", "S4_Diver_Segment_Tail_01", "Campaign_Interlude_Diver_To_Nest_Intro_01", "M5_Nest_Trough_Entrance_01" });

            // Split 4 of one and split 3 of the other are the same place: matched by level, not by number.
            Assert.Equal(abyss[4], diver[3]);
            Assert.DoesNotContain("M4_Abyss_Garden_03", diver);
            var shared = new HashSet<string>(abyss);
            shared.IntersectWith(diver);
            Assert.Equal(new HashSet<string> { "Start", "M5_Nest_Trough_Entrance_01" }, shared);
        }

        [Fact]
        public void RepeatedLevels_GetNumbered()
        {
            List<string> keys = SplitKeys.Build(new[] { "A", "B", "A", "A" });
            Assert.Equal(new[] { "A", "B", "A#2", "A#3" }, keys);
            Assert.Equal("A", SplitKeys.Key("A", 1));
            Assert.Equal("A#2", SplitKeys.Key("A", 2));
        }

        [Fact]
        public void MissingNames_AreEmptyKeys()
        {
            Assert.Equal(new[] { "", "#2" }, SplitKeys.Build(new string[] { null, "" }));
        }
    }
}
