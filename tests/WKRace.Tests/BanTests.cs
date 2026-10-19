using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    public class BanTests
    {
        private static readonly List<string> Pool = new List<string>
        {
            "Item_Artifact_Translocator", "Item_Artifact_Timepiece", "Item_Artifact_Remote",
            "Item_Artifact_EVAGlove", "Item_Artifact_Rapier", "Item_Artifact_CraftCube",
        };

        private static void StartBanMatch(Harness h, out TestClient a, out TestClient b)
        {
            h.Options.RankedRules = MatchRules.Ranked;
            a = h.Login("Alpha");
            b = h.Login("Bravo");
            a.Send(new QueueJoin());
            b.Send(new QueueJoin());
        }

        private static void ReportPools(TestClient a, TestClient b)
        {
            a.Send(new BanReady { MatchId = a.Match.MatchId, Pool = new List<string>(Pool) });
            b.Send(new BanReady { MatchId = b.Match.MatchId, Pool = new List<string>(Pool) });
        }

        // The player whose turn it is first, by the same rule as the server (the seed's parity).
        private static void Order(TestClient a, TestClient b, out TestClient first, out TestClient second)
        {
            bool aFirst = a.Last<BanState>().YourTurn;
            first = aFirst ? a : b;
            second = aFirst ? b : a;
        }

        [Fact]
        public void Ranked_AlwaysHasBansAndCuratedSeeds()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                StartBanMatch(h, out a, out b);
                Assert.Equal(MatchRules.Ranked, a.Match.Rules);
                Assert.Equal(MatchRules.Ranked, b.Match.Rules);
                Assert.True(a.Match.Ranked);
            }
        }

        [Fact]
        public void Lobby_TheHostsRulesApplyToWhoeverJoins()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                a.Send(new LobbyCreate { Mode = MatchMode.Duel, Rules = MatchRules.ArtifactBans | MatchRules.GuaranteedPerks });
                uint id = a.Last<LobbyCreated>().LobbyId;
                Assert.Equal(MatchRules.ArtifactBans | MatchRules.GuaranteedPerks, a.Lobby.Rules);

                b.Send(new LobbyJoin { LobbyId = id });
                Assert.True(b.Match.ArtifactBans);
                Assert.Equal(MatchRules.ArtifactBans | MatchRules.GuaranteedPerks, b.Match.Rules);
                Assert.False(b.Match.Ranked);
            }
        }

        [Fact]
        public void Bans_AlternateAndBothPlayersSeeThem()
        {
            using (var h = new Harness())
            {
                TestClient a, b, first, second;
                StartBanMatch(h, out a, out b);

                // Nothing happens until both games have reported their pools.
                a.Send(new BanReady { MatchId = a.Match.MatchId, Pool = new List<string>(Pool) });
                Assert.Empty(a.Last<BanState>().Pool);
                b.Send(new BanReady { MatchId = b.Match.MatchId, Pool = new List<string>(Pool) });
                Assert.Equal(Pool, a.Last<BanState>().Pool);

                Order(a, b, out first, out second);
                Assert.Equal((a.Match.Seed & 1) == 0 ? a : b, first);
                Assert.False(second.Last<BanState>().YourTurn);
                Assert.Equal(first.Name, second.Last<BanState>().TurnName);

                first.Send(new BanPick { MatchId = first.Match.MatchId, Item = "Item_Artifact_Translocator" });
                BanState seen = second.Last<BanState>();
                Assert.Equal(new[] { "Item_Artifact_Translocator" }, seen.Banned);
                Assert.Equal(new[] { first.Name }, seen.BannedBy);
                Assert.True(seen.YourTurn);
                Assert.False(seen.Done);

                second.Send(new BanPick { MatchId = second.Match.MatchId, Item = "Item_Artifact_Timepiece" });
                BanState done = first.Last<BanState>();
                Assert.True(done.Done);
                Assert.Equal(new[] { "Item_Artifact_Translocator", "Item_Artifact_Timepiece" }, done.Banned);
                Assert.Equal(new[] { first.Name, second.Name }, done.BannedBy);
                Assert.Equal("", done.TurnName);
            }
        }

        [Fact]
        public void Worlds_LoadOnlyAfterTheBans()
        {
            using (var h = new Harness())
            {
                TestClient a, b, first, second;
                StartBanMatch(h, out a, out b);
                a.Ready();
                b.Ready();
                Assert.Equal(0, a.Conn.Count<MatchStart>());

                ReportPools(a, b);
                Order(a, b, out first, out second);
                first.Send(new BanPick { MatchId = first.Match.MatchId, Item = "" });
                second.Send(new BanPick { MatchId = second.Match.MatchId, Item = "" });
                Assert.True(a.Last<BanState>().Done);
                Assert.Empty(a.Last<BanState>().Banned);

                a.Ready();
                b.Ready();
                Assert.Equal(1, a.Conn.Count<MatchStart>());
            }
        }

        [Fact]
        public void BadPicks_AreIgnored()
        {
            using (var h = new Harness())
            {
                TestClient a, b, first, second;
                StartBanMatch(h, out a, out b);
                ReportPools(a, b);
                Order(a, b, out first, out second);

                second.Send(new BanPick { MatchId = second.Match.MatchId, Item = "Item_Artifact_Rapier" });   // not their turn
                first.Send(new BanPick { MatchId = first.Match.MatchId, Item = "Item_Not_In_Pool" });         // not offered
                Assert.Empty(first.Last<BanState>().Banned);
                Assert.True(first.Last<BanState>().YourTurn);

                first.Send(new BanPick { MatchId = first.Match.MatchId, Item = "Item_Artifact_Rapier" });
                second.Send(new BanPick { MatchId = second.Match.MatchId, Item = "Item_Artifact_Rapier" });   // already banned
                Assert.Single(second.Last<BanState>().Banned);
                Assert.True(second.Last<BanState>().YourTurn);
            }
        }

        [Fact]
        public void TurnsThatRunOut_PassWithoutABan()
        {
            using (var h = new Harness())
            {
                TestClient a, b, first, second;
                StartBanMatch(h, out a, out b);
                ReportPools(a, b);
                Order(a, b, out first, out second);

                h.Advance(h.Options.BanTurnSeconds * 1000L + 1);
                Assert.True(second.Last<BanState>().YourTurn);
                h.Advance(h.Options.BanTurnSeconds * 1000L + 1);
                Assert.True(a.Last<BanState>().Done);
                Assert.Empty(a.Last<BanState>().Banned);
            }
        }

        [Fact]
        public void Pool_IsWhatBothGamesOffer_AndOnlyCleanNames()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                StartBanMatch(h, out a, out b);
                a.Send(new BanReady { MatchId = a.Match.MatchId, Pool = new List<string> { "Item_A", "Item_B", "<b>bad</b>", "Item_C" } });
                b.Send(new BanReady { MatchId = b.Match.MatchId, Pool = new List<string> { "Item_C", "Item_A" } });
                Assert.Equal(new[] { "Item_A", "Item_C" }, a.Last<BanState>().Pool);
            }
        }

        [Fact]
        public void EmptyPools_SkipTheBanPhase()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                StartBanMatch(h, out a, out b);
                a.Send(new BanReady { MatchId = a.Match.MatchId });
                b.Send(new BanReady { MatchId = b.Match.MatchId });
                Assert.True(a.Last<BanState>().Done);
            }
        }

        [Fact]
        public void Reconnecting_ResendsTheBanState()
        {
            using (var h = new Harness())
            {
                TestClient a, b, first, second;
                StartBanMatch(h, out a, out b);
                ReportPools(a, b);
                Order(a, b, out first, out second);
                first.Send(new BanPick { MatchId = first.Match.MatchId, Item = "Item_Artifact_Remote" });

                second.Disconnect();
                second.Reconnect();
                Assert.True(second.Match.ArtifactBans);
                BanState state = second.Last<BanState>();
                Assert.Equal(new[] { "Item_Artifact_Remote" }, state.Banned);
                Assert.True(state.YourTurn);
            }
        }

        [Fact]
        public void MatchesWithoutBans_IgnoreBanMessages()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                Assert.False(a.Match.ArtifactBans);
                a.Send(new BanReady { MatchId = a.Match.MatchId, Pool = new List<string>(Pool) });
                Assert.Equal(0, a.Conn.Count<BanState>());
                a.Ready();
                b.Ready();
                Assert.Equal(1, a.Conn.Count<MatchStart>());
            }
        }

        [Fact]
        public void BanMessages_RoundTrip()
        {
            var state = new BanState
            {
                MatchId = System.Guid.NewGuid(),
                Pool = new List<string>(Pool),
                Banned = new List<string> { "Item_Artifact_Rapier" },
                BannedBy = new List<string> { "Alpha" },
                TurnName = "Bravo",
                YourTurn = true,
                SecondsLeft = 17,
            };
            var back = (BanState)MessageCodec.Decode(MessageCodec.Encode(state));
            Assert.Equal(state.Pool, back.Pool);
            Assert.Equal(state.Banned, back.Banned);
            Assert.Equal(state.BannedBy, back.BannedBy);
            Assert.Equal("Bravo", back.TurnName);
            Assert.True(back.YourTurn);
            Assert.Equal(17, back.SecondsLeft);
            Assert.False(back.Done);

            var pick = (BanPick)MessageCodec.Decode(MessageCodec.Encode(new BanPick { MatchId = state.MatchId, Item = "Item_Artifact_Remote" }));
            Assert.Equal("Item_Artifact_Remote", pick.Item);
        }
    }
}
