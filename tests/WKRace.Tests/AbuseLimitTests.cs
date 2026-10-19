using System.Net;
using WKRace.Protocol;
using WKRace.Server;
using Xunit;

namespace WKRace.Tests
{
    public class AbuseLimitTests
    {
        private static TestClient Join(Harness h, string name, string address)
        {
            var c = new TestClient(h, name);
            c.Conn.Address = address;
            c.Login();
            return c;
        }

        // ---- registration ----------------------------------------------------------------

        [Fact]
        public void NewPlayers_AreLimitedPerAddressPerDay()
        {
            using (var h = new Harness())
            {
                h.Options.NewPlayersPerIpPerDay = 3;
                for (int i = 0; i < 3; i++)
                    Assert.True(Join(h, "Player" + i, "198.51.100.1").Conn.Authenticated);

                TestClient extra = Join(h, "Extra", "198.51.100.1");
                Assert.False(extra.Conn.Authenticated);
                Assert.Equal(ErrorCode.RegistrationLimited, extra.Last<ErrorMessage>().Code);
                Assert.True(extra.Conn.Closed);
                Assert.Equal(3, h.Store.Count);

                // Another address is unaffected; so is an existing player from the limited address.
                Assert.True(Join(h, "Elsewhere", "198.51.100.2").Conn.Authenticated);

                // A day later the address may register again.
                h.Advance(24L * 60 * 60 * 1000 + 1);
                extra.Reconnect();
                Assert.True(extra.Conn.Authenticated);
            }
        }

        [Fact]
        public void ExistingPlayers_CanAlwaysSignIn()
        {
            using (var h = new Harness())
            {
                h.Options.NewPlayersPerIpPerDay = 1;
                TestClient first = Join(h, "First", "198.51.100.9");
                Assert.False(Join(h, "Second", "198.51.100.9").Conn.Authenticated);

                first.Disconnect();
                first.Reconnect();
                Assert.True(first.Conn.Authenticated);
            }
        }

        [Fact]
        public void PlayerCap_StopsRegistrations()
        {
            using (var h = new Harness())
            {
                h.Options.MaxPlayers = 2;
                h.Login("One");
                h.Login("Two");
                TestClient three = h.Login("Three");
                Assert.False(three.Conn.Authenticated);
                Assert.Equal(ErrorCode.RegistrationLimited, three.Last<ErrorMessage>().Code);
            }
        }

        // ---- lobbies ----------------------------------------------------------------------

        [Fact]
        public void LobbyAttempts_AreLimitedPerMinute()
        {
            using (var h = new Harness())
            {
                h.Options.LobbyAttemptsPerMinute = 3;
                TestClient a = h.Login("Alpha");
                for (int i = 0; i < 3; i++)
                {
                    a.Send(new LobbyJoin { LobbyId = 12345u + (uint)i });
                    Assert.Equal(ErrorCode.LobbyNotFound, a.Last<ErrorMessage>().Code);
                }
                a.Send(new LobbyCreate());
                Assert.Equal(ErrorCode.RateLimited, a.Last<ErrorMessage>().Code);
                Assert.Equal(0, a.Conn.Count<LobbyCreated>());

                h.Advance(60001);
                a.Send(new LobbyCreate());
                Assert.Equal(1, a.Conn.Count<LobbyCreated>());
            }
        }

        // ---- relayed text ------------------------------------------------------------------

        [Fact]
        public void SplitLabels_LoseRichTextTags()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(2000);
                a.Send(new SplitReached { Attempt = 1, Index = 0, Level = "<size=500>M1_Intro_01</size>", Region = "<b>silos", RaceTimeMs = 1800 });
                OpponentSplit split = b.Last<OpponentSplit>();
                Assert.Equal("size=500M1_Intro_01/size", split.Level);
                Assert.Equal("bsilos", split.Region);
            }
        }

        // ---- connection guard ----------------------------------------------------------------

        private static readonly IPAddress Remote = IPAddress.Parse("198.51.100.20");

        [Fact]
        public void Guard_BansAnAddressThatConnectsTooOften()
        {
            var guard = new ConnectionGuard(5, 10);
            long t = 1000;
            for (int i = 0; i < 5; i++) Assert.Equal(ConnectionGuard.Verdict.Allowed, guard.Admit(Remote, t + i));
            Assert.Equal(ConnectionGuard.Verdict.NewlyBanned, guard.Admit(Remote, t + 10));
            Assert.Equal(ConnectionGuard.Verdict.Banned, guard.Admit(Remote, t + 20));
            Assert.Equal(ConnectionGuard.Verdict.Allowed, guard.Admit(IPAddress.Parse("198.51.100.21"), t + 20));

            // The ban lasts ten minutes, then the address starts fresh.
            Assert.Equal(ConnectionGuard.Verdict.Banned, guard.Admit(Remote, t + 10 + 599999));
            Assert.Equal(ConnectionGuard.Verdict.Allowed, guard.Admit(Remote, t + 10 + 600000));
        }

        [Fact]
        public void Guard_AttemptsSpreadOverMinutes_AreFine()
        {
            var guard = new ConnectionGuard(5, 10);
            for (int i = 0; i < 50; i++) Assert.Equal(ConnectionGuard.Verdict.Allowed, guard.Admit(Remote, i * 15000L));
        }

        [Fact]
        public void Guard_ThreeStrikes_Ban()
        {
            var guard = new ConnectionGuard(0, 10);
            Assert.False(guard.Strike(Remote, 1000));
            Assert.False(guard.Strike(Remote, 2000));
            Assert.True(guard.Strike(Remote, 3000));
            Assert.True(guard.IsBanned(Remote, 4000));
            Assert.Equal(ConnectionGuard.Verdict.Banned, guard.Admit(Remote, 4000));
        }

        [Fact]
        public void Guard_NeverLimitsLoopback()
        {
            var guard = new ConnectionGuard(1, 10);
            for (int i = 0; i < 20; i++) Assert.Equal(ConnectionGuard.Verdict.Allowed, guard.Admit(IPAddress.Loopback, i));
            for (int i = 0; i < 5; i++) Assert.False(guard.Strike(IPAddress.IPv6Loopback, i));
        }

        [Fact]
        public void Guard_ForgetsIdleAddresses()
        {
            var guard = new ConnectionGuard(5, 10);
            guard.Admit(Remote, 1000);
            Assert.Equal(1, guard.Tracked);
            guard.Prune(1000 + 60001);
            Assert.Equal(0, guard.Tracked);
        }

        [Fact]
        public void Guard_AdminBan_LastsItsTime()
        {
            var guard = new ConnectionGuard(0, 10);
            guard.BanFor(Remote, 3600000, 1000);
            Assert.Equal(ConnectionGuard.Verdict.Banned, guard.Admit(Remote, 1000 + 3599999));
            Assert.Equal(ConnectionGuard.Verdict.Allowed, guard.Admit(Remote, 1000 + 3600000));
        }
    }

    public class LogAddressTests
    {
        [Theory]
        [InlineData("203.0.113.77", "203.0.113.x")]
        [InlineData("::ffff:203.0.113.77", "203.0.113.x")]
        [InlineData("2001:db8:12:3456:789a:bcde:f012:3456", "2001:db8:12:x")]
        [InlineData("::1", "0:0:0:x")]
        [InlineData("not an address", "?")]
        public void Addresses_AreShortenedForTheLog(string address, string expected)
        {
            Assert.Equal(expected, LogAddress.Short(address));
        }

        [Fact]
        public void PrivacyNotice_HasAnIpSection()
        {
            string notice = PrivacyNotice.Html("ops", true);
            Assert.Contains("<h2>Your IP address</h2>", notice);
            Assert.Contains("203.0.113.x", notice);
        }
    }
}
