using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    public class BuildTagTests
    {
        [Fact]
        public void Compose_JoinsTheKnownParts()
        {
            Assert.Equal("steam-25612096|mod-0.2.1", BuildTag.Compose("steam-25612096", "0.2.1"));
            Assert.Equal("steam-25612096|mod-0.2.1", BuildTag.Compose("25612096", "0.2.1.9999"));
            Assert.Equal("mod-0.2.1", BuildTag.Compose(null, "0.2.1"));
            Assert.Equal("", BuildTag.Compose(null, null));
        }

        [Theory]
        [InlineData("steam-1|mod-0.2.1", "steam-1|mod-0.2.1", true)]
        [InlineData("steam-1|mod-0.2.1", "steam-1|mod-0.2.0", false)]
        [InlineData("steam-1|mod-0.2.1", "steam-2|mod-0.2.1", false)]
        [InlineData("steam-1|mod-0.2.1", "steam-1", true)]
        [InlineData("steam-1|mod-0.2.1", "mod-0.2.1", true)]
        [InlineData("mod-0.2.1", "steam-9", true)]
        [InlineData("", "steam-1|mod-0.2.1", true)]
        public void Compatible_ComparesOnlyWhatBothSidesKnow(string a, string b, bool expected)
        {
            Assert.Equal(expected, BuildTag.Compatible(a, b));
            Assert.Equal(expected, BuildTag.Compatible(b, a));
        }

        [Fact]
        public void DifferentModVersions_AreNotMatched()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                var c = h.Login("Charlie");
                a.Send(new QueueJoin { GameBuild = "steam-1|mod-0.2.1" });
                b.Send(new QueueJoin { GameBuild = "steam-1|mod-0.2.0" });
                h.Advance(60000);
                Assert.Equal(0, a.Conn.Count<MatchFound>());

                c.Send(new QueueJoin { GameBuild = "steam-1|mod-0.2.1" });
                Assert.Equal(1, a.Conn.Count<MatchFound>());
                Assert.Equal(1, c.Conn.Count<MatchFound>());
                Assert.Equal(0, b.Conn.Count<MatchFound>());
            }
        }

        [Fact]
        public void LobbyJoin_WithADifferentModVersion_IsRefused()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                a.Send(new LobbyCreate { GameBuild = "steam-1|mod-0.2.1" });
                uint id = a.Last<LobbyCreated>().LobbyId;
                b.Send(new LobbyJoin { LobbyId = id, GameBuild = "steam-1|mod-0.2.0" });
                Assert.Equal(ErrorCode.LobbyBuildMismatch, b.Last<ErrorMessage>().Code);
                b.Send(new LobbyJoin { LobbyId = id, GameBuild = "steam-1|mod-0.2.1" });
                Assert.Equal(1, b.Conn.Count<MatchFound>());
            }
        }
    }
}
