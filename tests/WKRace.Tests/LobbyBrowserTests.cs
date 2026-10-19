using System.Linq;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    public class LobbyBrowserTests
    {
        private static TestClient LoginAs(Harness h, string name, string clientVersion)
        {
            var c = new TestClient(h, name);
            ClientHello hello = c.Hello();
            hello.ClientVersion = clientVersion;
            c.Send(hello);
            return c;
        }

        private static uint Open(TestClient host, MatchMode mode = MatchMode.Party)
        {
            host.Send(new LobbyCreate { Mode = mode, Rules = MatchRules.CuratedSeeds });
            return host.Last<LobbyCreated>().LobbyId;
        }

        private static LobbyList List(Harness h, TestClient asker)
        {
            h.Advance(2000);
            asker.Send(new LobbyListRequest());
            return asker.Last<LobbyList>();
        }

        [Fact]
        public void OnlyClientsThatKnowTheBrowser_AreToldAboutIt()
        {
            using (var h = new Harness())
            {
                var old = LoginAs(h, "Old", "0.9.5");
                var current = LoginAs(h, "Current", "0.9.6");
                var test = LoginAs(h, "Tester", "0.9.6.9999");
                Assert.Equal(0, old.Conn.Count<ServerFeatures>());
                Assert.True(current.Last<ServerFeatures>().LobbyBrowser);
                Assert.True(test.Last<ServerFeatures>().LobbyBrowser);
            }
        }

        [Fact]
        public void ALobbyIsListedOnlyWhenItsHostMakesItPublic()
        {
            using (var h = new Harness())
            {
                var host = LoginAs(h, "Alpha", "0.9.6");
                var other = LoginAs(h, "Bravo", "0.9.6");
                uint id = Open(host);
                Assert.Empty(List(h, other).Entries);

                host.Send(new LobbyVisibility { Public = true });
                LobbyListEntry entry = List(h, other).Entries.Single();
                Assert.Equal(id, entry.LobbyId);
                Assert.Equal("Alpha", entry.Host);
                Assert.Equal(MatchMode.Party, entry.Mode);
                Assert.Equal(1, entry.Members);
                Assert.Equal(ProtocolConstants.MaxPartyPlayers, entry.MaxPlayers);
                Assert.False(entry.Racing);

                host.Send(new LobbyVisibility { Public = false });
                Assert.Empty(List(h, other).Entries);
            }
        }

        [Fact]
        public void OnlyTheHost_ListsTheLobby()
        {
            using (var h = new Harness())
            {
                var host = LoginAs(h, "Alpha", "0.9.6");
                var member = LoginAs(h, "Bravo", "0.9.6");
                member.Send(new LobbyJoin { LobbyId = Open(host) });
                member.Send(new LobbyVisibility { Public = true });
                Assert.Empty(List(h, member).Entries);
            }
        }

        [Fact]
        public void AListedLobby_ShowsItsMatchAndPlayerCount_AndGoesWhenItCloses()
        {
            using (var h = new Harness())
            {
                var host = LoginAs(h, "Alpha", "0.9.6");
                var joiner = LoginAs(h, "Bravo", "0.9.6");
                var looker = LoginAs(h, "Charlie", "0.9.6");
                uint id = Open(host, MatchMode.Duel);
                host.Send(new LobbyVisibility { Public = true });
                // A 1v1 starts when the second player joins: the lobby is full and in a match.
                joiner.Send(new LobbyJoin { LobbyId = id });
                LobbyListEntry entry = List(h, looker).Entries.Single();
                Assert.Equal(2, entry.Members);
                Assert.True(entry.Racing);

                joiner.Send(new PlayerForfeit());
                host.Send(new PlayerForfeit());
                h.Advance(h.Options.FinishSettleMs + 1);
                joiner.Send(new LobbyCancel());
                host.Send(new LobbyCancel());
                Assert.Empty(List(h, looker).Entries);
            }
        }

        [Fact]
        public void TheListIsNotSentMoreThanEverySecondAndAHalf()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha", "0.9.6");
                h.Advance(2000);
                a.Send(new LobbyListRequest());
                a.Send(new LobbyListRequest());
                Assert.Equal(1, a.Conn.Count<LobbyList>());
                h.Advance(1600);
                a.Send(new LobbyListRequest());
                Assert.Equal(2, a.Conn.Count<LobbyList>());
            }
        }

        [Fact]
        public void BrowserMessages_SurviveTheWire()
        {
            var list = new LobbyList();
            for (int i = 0; i < LobbyList.MaxEntries + 5; i++)
                list.Entries.Add(new LobbyListEntry { LobbyId = (uint)(i + 1), Host = "Host " + i, Mode = MatchMode.Party, Rules = MatchRules.CuratedSeeds, Members = 3, MaxPlayers = 15, Racing = i % 2 == 0 });
            byte[] frame = MessageCodec.Encode(list);
            Assert.True(frame.Length <= ProtocolConstants.MaxFrameSize);
            var back = (LobbyList)MessageCodec.Decode(frame);
            Assert.True(back.Cut);
            Assert.Equal(LobbyList.MaxEntries, back.Entries.Count);
            Assert.Equal("Host 3", back.Entries[3].Host);
            Assert.False(back.Entries[3].Racing);
            Assert.True(back.Entries[4].Racing);

            Assert.True(((ServerFeatures)MessageCodec.Decode(MessageCodec.Encode(new ServerFeatures { LobbyBrowser = true }))).LobbyBrowser);
            Assert.True(((LobbyVisibility)MessageCodec.Decode(MessageCodec.Encode(new LobbyVisibility { Public = true }))).Public);
            Assert.IsType<LobbyListRequest>(MessageCodec.Decode(MessageCodec.Encode(new LobbyListRequest())));
        }

        [Theory]
        [InlineData("0.9.6", "0.9.6", true)]
        [InlineData("0.9.6.9999", "0.9.6", true)]
        [InlineData("0.10.0", "0.9.6", true)]
        [InlineData("0.9.5", "0.9.6", false)]
        [InlineData("test", "0.9.6", false)]
        [InlineData("", "0.9.6", false)]
        public void AtLeast_ComparesVersions(string version, string minimum, bool expected)
        {
            Assert.Equal(expected, BuildTag.AtLeast(version, minimum));
        }
    }
}
