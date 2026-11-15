using System;
using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    // 0.9.8 diagnostics: the server logs where two worlds split, and the parts of WKSR that did not load in a game.
    public class DiagnosticsTests
    {
        private static List<WorldLevel> Climb(params string[] names)
        {
            return names.Select((n, i) => new WorldLevel(n, (uint)(1000 + i))).ToList();
        }

        private static void SendLevels(TestClient c, IList<WorldLevel> levels)
        {
            foreach (WorldLevels part in WorldLevels.Split(c.Match.MatchId, levels)) c.Send(part);
        }

        private static ClientHello Hello(TestClient c, string version)
        {
            ClientHello hello = c.Hello();
            hello.ClientVersion = version;
            return hello;
        }

        [Fact]
        public void Mismatch_LogsTheFirstLevelWhereTheWorldsSplit()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha"), b = h.Login("Bravo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                SendLevels(a, Climb("M1_Intro_01", "M1_Silos_Storage_05", "M2_Pipeworks_Organism_01", "M2_Pipeworks_Organ_03"));
                SendLevels(b, Climb("M1_Intro_01", "M1_Silos_Storage_05", "M2_Pipeworks_Organ_02"));
                a.Loaded(0xAAAA);
                b.Loaded(0xBBBB);

                Assert.Contains("did not match", a.Result.Reason);
                string split = h.Logs.Single(l => l.Contains("worlds split"));
                Assert.Contains("at level 3", split);
                Assert.Contains("M2_Pipeworks_Organism_01", split);
                Assert.Contains("M2_Pipeworks_Organ_02", split);
                Assert.Contains("4 levels", split);
                Assert.Contains("3 levels", split);
            }
        }

        [Fact]
        public void Mismatch_WithoutALevelList_SaysSo()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha"), b = h.Login("Bravo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                SendLevels(a, Climb("M1_Intro_01"));
                a.Loaded(0xAAAA);
                b.Loaded(0xBBBB);
                Assert.Contains(h.Logs, l => l.Contains("no level list from Bravo"));
            }
        }

        [Fact]
        public void SameLevels_DifferentFingerprint_PointsAtRulesOrBans()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha"), b = h.Login("Bravo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                SendLevels(a, Climb("M1_Intro_01", "M1_Silos_Storage_05"));
                SendLevels(b, Climb("M1_Intro_01", "M1_Silos_Storage_05"));
                a.Loaded(0xAAAA);
                b.Loaded(0xBBBB);
                Assert.Contains(h.Logs, l => l.Contains("same levels (2)"));
            }
        }

        [Fact]
        public void MatchingWorlds_LogNothingExtra()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha"), b = h.Login("Bravo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                SendLevels(a, Climb("M1_Intro_01", "M1_Silos_Storage_05"));
                SendLevels(b, Climb("M1_Intro_01", "M1_Silos_Storage_05"));
                a.Loaded(7);
                b.Loaded(7);
                Assert.DoesNotContain(h.Logs, l => l.Contains("worlds split") || l.Contains("level list"));
            }
        }

        [Fact]
        public void LevelsForAnotherMatch_AreIgnored()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha"), b = h.Login("Bravo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                foreach (WorldLevels part in WorldLevels.Split(Guid.NewGuid(), Climb("X"))) a.Send(part);
                SendLevels(b, Climb("M1_Intro_01"));
                a.Loaded(0xAAAA);
                b.Loaded(0xBBBB);
                Assert.Contains(h.Logs, l => l.Contains("no level list from Alpha"));
            }
        }

        [Fact]
        public void LongClimbs_GoInParts_AndCompareWhole()
        {
            var names = Enumerable.Range(0, 40).Select(i => "Level_" + i).ToArray();
            List<WorldLevel> a = Climb(names), b = Climb(names);
            b[37] = new WorldLevel("Level_37", 1);
            List<WorldLevels> parts = WorldLevels.Split(Guid.NewGuid(), a);
            Assert.Equal(3, parts.Count);
            Assert.All(parts, p => Assert.Equal(40, p.Total));
            Assert.Equal(37, WorldLevels.FirstDifference(a, b));
            Assert.Equal(-1, WorldLevels.FirstDifference(a, Climb(names)));
            Assert.Equal(2, WorldLevels.FirstDifference(Climb("A", "B", "C"), Climb("A", "B")));
        }

        [Fact]
        public void WorldLevels_RoundTrip()
        {
            var m = new WorldLevels { MatchId = Guid.NewGuid(), Total = 20, First = 16 };
            m.Levels.Add(new WorldLevel("M2_Pipeworks_Organism_01", 0xDEADBEEF));
            var back = (WorldLevels)MessageCodec.Decode(MessageCodec.Encode(m));
            Assert.Equal(m.MatchId, back.MatchId);
            Assert.Equal(16, back.First);
            Assert.Equal("M2_Pipeworks_Organism_01", back.Levels[0].Name);
            Assert.Equal(0xDEADBEEFu, back.Levels[0].Hash);
        }

        [Fact]
        public void ClientIssues_AreLoggedOncePerConnection()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                var issues = new ClientIssues();
                issues.Items.Add("RaceGuarantees (Cannot assign method return type)");
                issues.Items.Add("bad\nline");
                a.Send(issues);
                a.Send(issues);
                string line = h.Logs.Single(l => l.Contains("did not load"));
                Assert.Contains("Alpha", line);
                Assert.Contains("2 parts of WKSR did not load", line);
                Assert.Contains("bad line", line);
            }
        }

        [Fact]
        public void Diagnostics_AreOfferedFrom098On()
        {
            using (var h = new Harness())
            {
                var newer = new TestClient(h, "Newer");
                newer.Send(Hello(newer, "0.9.8"));
                Assert.True(newer.Last<ServerFeatures>().Diagnostics);

                var older = new TestClient(h, "Older");
                older.Send(Hello(older, "0.9.7"));
                Assert.False(older.Last<ServerFeatures>().Diagnostics);
            }
        }

        [Fact]
        public void ServerFeatures_WithDiagnostics_RoundTrip_AndOlderReadersSkipIt()
        {
            var f = new ServerFeatures { LobbyBrowser = true, Spectate = true, Diagnostics = true };
            var back = (ServerFeatures)MessageCodec.Decode(MessageCodec.Encode(f));
            Assert.True(back.Diagnostics);
            Assert.True(back.Spectate);
            var without = (ServerFeatures)MessageCodec.Decode(MessageCodec.Encode(new ServerFeatures { LobbyBrowser = true, Spectate = true }));
            Assert.False(without.Diagnostics);
        }
    }
}
