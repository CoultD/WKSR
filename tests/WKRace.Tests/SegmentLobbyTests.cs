using System.Linq;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    public class SegmentLobbyTests
    {
        private static TestClient LoginAs(Harness h, string name, string clientVersion = "0.9.7")
        {
            var c = new TestClient(h, name);
            ClientHello hello = c.Hello();
            hello.ClientVersion = clientVersion;
            c.Send(hello);
            return c;
        }

        private static uint Open(TestClient host, MatchMode mode, byte segment, MatchRules rules = MatchRules.Ranked)
        {
            host.Send(new LobbyCreate { Mode = mode, Rules = rules, Segment = segment });
            return host.Last<LobbyCreated>().LobbyId;
        }

        [Fact]
        public void ASegmentLobby_PlaysItsSegment_WithoutArtifactBans()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                var b = LoginAs(h, "Bravo");
                Assert.True(a.Last<ServerFeatures>().LobbySegments);
                uint id = Open(a, MatchMode.Duel, Segments.Silos);
                Assert.Equal(Segments.Silos, a.Lobby.Segment);
                // The Silos end before the artifact room: no bans, though the host ticked them.
                Assert.Equal(MatchRules.None, a.Lobby.Rules & MatchRules.ArtifactBans);

                b.Send(new LobbyJoin { LobbyId = id });
                Assert.Equal(Segments.Silos, a.Match.Segment);
                Assert.Equal(Segments.Silos, b.Match.Segment);
                Assert.False(a.Match.ArtifactBans);
            }
        }

        [Fact]
        public void AWholeClimbLobby_KeepsItsBans_AndHasNoSegment()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                Open(a, MatchMode.Duel, Segments.None);
                Assert.Equal(Segments.None, a.Lobby.Segment);
                Assert.Equal(MatchRules.ArtifactBans, a.Lobby.Rules & MatchRules.ArtifactBans);
            }
        }

        [Fact]
        public void PlayersBefore097_CannotJoinASegmentLobby()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                var old = LoginAs(h, "Old", "0.9.6");
                Assert.False(old.Last<ServerFeatures>().LobbySegments);
                old.Send(new LobbyJoin { LobbyId = Open(a, MatchMode.Party, Segments.Silos) });
                Assert.Equal(ErrorCode.LobbyBuildMismatch, old.Last<ErrorMessage>().Code);
                Assert.Single(a.Lobby.Members);
            }
        }

        [Fact]
        public void ASegmentFinish_IsNotHeldToTheWholeClimbsMinimums()
        {
            using (var h = new Harness())
            {
                h.Options.MinFinishSeconds = 180;
                h.Options.MinFinishSplits = 15;
                var a = LoginAs(h, "Alpha");
                var b = LoginAs(h, "Bravo");
                b.Send(new LobbyJoin { LobbyId = Open(a, MatchMode.Duel, Segments.Silos) });
                a.Ready();
                b.Ready();
                h.Advance(h.Options.CountdownMs + 90000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 90000, RealTimeMs = 90000 });
                h.Advance(h.Options.FinishSettleMs + 1);
                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                Assert.Equal(90000, a.Result.YourTimeMs);
            }
        }

        [Fact]
        public void TheBrowser_ShowsSegments_AndOnlyLobbiesOfTheSameVersion()
        {
            using (var h = new Harness())
            {
                var host = LoginAs(h, "Alpha");
                Open(host, MatchMode.Party, Segments.Silos);
                host.Send(new LobbyVisibility { Public = true });
                var oldHost = LoginAs(h, "Old host", "0.9.6");
                oldHost.Send(new LobbyCreate { Mode = MatchMode.Party, Rules = MatchRules.CuratedSeeds });
                oldHost.Send(new LobbyVisibility { Public = true });

                var looker = LoginAs(h, "Bravo");
                h.Advance(2000);
                looker.Send(new LobbyListRequest());
                LobbyListEntry entry = looker.Last<LobbyList>().Entries.Single();
                Assert.Equal("Alpha", entry.Host);
                Assert.Equal(Segments.Silos, entry.Segment);

                var oldLooker = LoginAs(h, "Old looker", "0.9.6");
                h.Advance(2000);
                oldLooker.Send(new LobbyListRequest());
                LobbyList oldList = oldLooker.Last<LobbyList>();
                Assert.Equal("Old host", oldList.Entries.Single().Host);
                Assert.False(oldList.WithSegments);
            }
        }

        [Fact]
        public void SegmentFields_SurviveTheWire_AndOldFormsStillRead()
        {
            var create = (LobbyCreate)MessageCodec.Decode(MessageCodec.Encode(new LobbyCreate { Mode = MatchMode.Party, Segment = Segments.Silos }));
            Assert.Equal(Segments.Silos, create.Segment);
            Assert.Equal(Segments.None, ((LobbyCreate)MessageCodec.Decode(MessageCodec.Encode(new LobbyCreate()))).Segment);

            var state = new LobbyState { LobbyId = 5, Mode = MatchMode.Party, MaxPlayers = 15, Segment = Segments.Silos };
            state.Members.Add(new LobbyMember { Name = "Alpha", Host = true });
            Assert.Equal(Segments.Silos, ((LobbyState)MessageCodec.Decode(MessageCodec.Encode(state))).Segment);

            var list = new LobbyList { WithSegments = true };
            list.Entries.Add(new LobbyListEntry { LobbyId = 1, Host = "A", Segment = Segments.Silos });
            list.Entries.Add(new LobbyListEntry { LobbyId = 2, Host = "B" });
            var back = (LobbyList)MessageCodec.Decode(MessageCodec.Encode(list));
            Assert.Equal(Segments.Silos, back.Entries[0].Segment);
            Assert.Equal(Segments.None, back.Entries[1].Segment);

            var features = (ServerFeatures)MessageCodec.Decode(MessageCodec.Encode(new ServerFeatures { LobbyBrowser = true, LobbySegments = true }));
            Assert.True(features.LobbySegments);
            Assert.False(features.ChatTyping);
        }
    }
}
