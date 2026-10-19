using System.Linq;
using WKRace.Client;
using Xunit;

namespace WKRace.Tests
{
    public class NameTests
    {
        [Fact]
        public void RandomNames_AreValidRacerNames()
        {
            for (int i = 0; i < 200; i++)
            {
                string name = NameGenerator.Next();
                Assert.Equal(name, NameGenerator.Clean(name));
                Assert.Equal(name, WKRace.Server.NameSanitizer.CleanName(name));   // the server keeps it as is
                Assert.True(name.All(char.IsLetterOrDigit));
            }
        }

        [Fact]
        public void Clean_MatchesWhatTheServerAccepts()
        {
            Assert.Equal("Rusty Rebar", NameGenerator.Clean("  Rusty  Rebar "));
            Assert.Equal("[WK]Climber_1!", NameGenerator.Clean("[WK]Climber_1!"));
            Assert.Null(NameGenerator.Clean("a"));
            Assert.Null(NameGenerator.Clean("<b>bold</b>"));
            Assert.Null(NameGenerator.Clean(new string('x', 25)));
        }
    }

    public class SteamLinkCheckTests
    {
        [Theory]
        [InlineData("https://steamcommunity.com/openid/login?openid.mode=checkid_setup", true)]
        [InlineData("https://STEAMCOMMUNITY.com/openid/login?x=1", true)]
        [InlineData("http://steamcommunity.com/openid/login?x=1", false)]
        [InlineData("https://steamcommunity.com.evil.example/openid/login", false)]
        [InlineData("https://steamcommunity.com@evil.example/openid/login", false)]
        [InlineData("https://evil.example/?u=https://steamcommunity.com/openid/", false)]
        [InlineData("https://steamcommunity.com:8443/openid/login", false)]
        [InlineData("https://steamcommunity.com/profiles/123", false)]
        [InlineData("not a url", false)]
        public void OnlySteamsOpenIdPageIsOpened(string url, bool expected)
        {
            Assert.Equal(expected, SteamLinks.IsSteamOpenIdPage(url));
        }
    }
}
