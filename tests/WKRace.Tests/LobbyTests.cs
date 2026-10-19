using System;
using System.Linq;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    public class LobbyTests
    {
        private static LobbyCreated Open(TestClient owner, string build = "")
        {
            owner.Send(new LobbyCreate { GameBuild = build, Rules = MatchRules.CuratedSeeds });
            return owner.Last<LobbyCreated>();
        }

        [Fact]
        public void JoiningALobby_StartsAnUnrankedMatchBetweenTheTwo()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                LobbyCreated lobby = Open(a);
                Assert.NotEqual(0u, lobby.LobbyId);
                Assert.Equal(h.Options.LobbyTimeoutSeconds, lobby.ExpiresInSeconds);

                b.Send(new LobbyJoin { LobbyId = lobby.LobbyId });
                Assert.Equal(a.Match.MatchId, b.Match.MatchId);
                Assert.False(a.Match.Ranked);
                Assert.Equal("Alpha", b.Match.OpponentName);
                // The lobby stays for a rematch.
                Assert.Equal(1, h.Core.LobbyCount);
                Assert.True(b.Lobby.Racing);
            }
        }

        [Fact]
        public void FriendlyRace_LeavesRatingsAndRecordsAlone()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                b.Send(new LobbyJoin { LobbyId = Open(a).LobbyId });
                a.Ready();
                b.Ready();
                h.Advance(h.Options.CountdownMs + 30000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 30000 });
                h.Advance(h.Options.FinishSettleMs + 1);

                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                Assert.Equal(MatchOutcome.Loss, b.Result.Outcome);
                Assert.Equal(a.Result.OldRating, a.Result.NewRating);
                Assert.Equal(1000, h.Store.Find(a.Id).Rating);
                Assert.Equal(0, h.Store.Find(a.Id).Wins);
                Assert.Equal(0, h.Store.Find(b.Id).Losses);
            }
        }

        [Fact]
        public void UnknownCode_IsRefused()
        {
            using (var h = new Harness())
            {
                var b = h.Login("Bravo");
                b.Send(new LobbyJoin { LobbyId = 12345 });
                Assert.Equal(ErrorCode.LobbyNotFound, b.Last<ErrorMessage>().Code);
                Assert.Equal(0, b.Conn.Count<MatchFound>());
            }
        }

        [Fact]
        public void OwnCode_IsRefused()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                a.Send(new LobbyJoin { LobbyId = Open(a).LobbyId });
                Assert.Equal(ErrorCode.LobbyUnavailable, a.Last<ErrorMessage>().Code);
                Assert.Equal(1, h.Core.LobbyCount);
            }
        }

        [Fact]
        public void DifferentGameBuilds_CanJoin_ButADifferentModVersionCannot()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                LobbyCreated lobby = Open(a, "steam-1|mod-0.9.3");
                // Another version of the mod makes another world: refused, and told which of the two is the reason.
                b.Send(new LobbyJoin { LobbyId = lobby.LobbyId, GameBuild = "steam-1|mod-0.9.1" });
                Assert.Equal(ErrorCode.LobbyBuildMismatch, b.Last<ErrorMessage>().Code);
                Assert.Contains("WK Race mod", b.Last<ErrorMessage>().Text);
                // The current release and the public test release of the game play together.
                b.Send(new LobbyJoin { LobbyId = lobby.LobbyId, GameBuild = "steam-2|mod-0.9.3" });
                Assert.Equal(1, b.Conn.Count<MatchFound>());
            }
        }

        [Fact]
        public void Lobby_ExpiresAndTellsTheOwner()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                LobbyCreated lobby = Open(a);
                h.Advance(h.Options.LobbyTimeoutSeconds * 1000L + 1);
                Assert.Equal(lobby.LobbyId, a.Last<LobbyClosed>().LobbyId);
                b.Send(new LobbyJoin { LobbyId = lobby.LobbyId });
                Assert.Equal(ErrorCode.LobbyNotFound, b.Last<ErrorMessage>().Code);
            }
        }

        [Fact]
        public void CancelQueueAndDisconnect_AllCloseTheLobby()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                Open(a);
                a.Send(new LobbyCancel());
                Assert.Equal(0, h.Core.LobbyCount);

                Open(a);
                a.Send(new QueueJoin());
                Assert.Equal(0, h.Core.LobbyCount);

                a.Send(new QueueLeave());
                Open(a);
                a.Disconnect();
                Assert.Equal(0, h.Core.LobbyCount);
            }
        }

        [Fact]
        public void OpeningALobby_LeavesTheQueue_AndReplacesTheOldLobby()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                a.Send(new QueueJoin());
                LobbyCreated first = Open(a);
                Assert.Equal(0, h.Core.QueuedCount);
                LobbyCreated second = Open(a);
                Assert.Equal(1, h.Core.LobbyCount);

                b.Send(new LobbyJoin { LobbyId = first.LobbyId });
                Assert.Equal(ErrorCode.LobbyNotFound, b.Last<ErrorMessage>().Code);
                b.Send(new LobbyJoin { LobbyId = second.LobbyId });
                Assert.Equal(1, b.Conn.Count<MatchFound>());
            }
        }

        [Fact]
        public void HostAlreadyRacing_CannotBeJoined()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                var c = h.Login("Charlie");
                LobbyCreated lobby = Open(a);
                a.Disconnect();
                b.Send(new LobbyJoin { LobbyId = lobby.LobbyId });
                Assert.Equal(ErrorCode.LobbyNotFound, b.Last<ErrorMessage>().Code);
                Assert.Equal(0, c.Conn.Count<MatchFound>());
            }
        }

        [Fact]
        public void PublicAddress_IsSentWithTheLobby()
        {
            using (var h = new Harness())
            {
                h.Options.PublicAddress = "203.0.113.7:47600";
                var a = h.Login("Alpha");
                Assert.Equal("203.0.113.7:47600", Open(a).PublicAddress);
            }
        }

        // ---- the code itself ----------------------------------------------------

        [Theory]
        [InlineData(0xC0FFEE42u)]
        [InlineData(1u)]
        [InlineData(uint.MaxValue)]
        public void Code_RoundTrips(uint lobbyId)
        {
            string text = LobbyCode.Encode(lobbyId);
            Assert.Matches(@"^WKR-[0-9A-Z]{4}-[0-9A-Z]{4}$", text);

            LobbyCode code;
            Assert.True(LobbyCode.TryDecode(text, out code));
            Assert.Equal(lobbyId, code.LobbyId);
        }

        [Fact]
        public void Code_ToleratesSloppyTyping_ButNotTypos()
        {
            string text = LobbyCode.Encode(777u);
            LobbyCode code;
            Assert.True(LobbyCode.TryDecode("  " + text.ToLowerInvariant().Replace("-", " ") + " ", out code));
            Assert.True(LobbyCode.TryDecode(text.Substring(4), out code));
            Assert.Equal(777u, code.LobbyId);

            // Change one character: the check byte catches it.
            for (int at = 4; at < text.Length; at++)
            {
                if (text[at] == '-') continue;
                char swapped = text[at] == 'A' ? 'B' : 'A';
                Assert.False(LobbyCode.TryDecode(text.Substring(0, at) + swapped + text.Substring(at + 1), out code));
            }
            Assert.False(LobbyCode.TryDecode("WKR-HELLO", out code));
            Assert.False(LobbyCode.TryDecode("", out code));
        }

        [Fact]
        public void OldCodesWithAnAddress_AreRejected()
        {
            LobbyCode code;
            Assert.False(LobbyCode.TryDecode("WKR-2A46-F92E-FW00-00FG-Q740", out code));
        }

        [Theory]
        [InlineData("203.0.113.7:47600", "203.0.113.7", 47600)]
        [InlineData("example.org", "example.org", 47600)]
        [InlineData("[2001:db8::1]:5000", "2001:db8::1", 5000)]
        [InlineData("2001:db8::1", "2001:db8::1", 47600)]
        public void Address_Parses(string text, string host, int port)
        {
            string h;
            int p;
            Assert.True(LobbyCode.TryParseAddress(text, out h, out p));
            Assert.Equal(host, h);
            Assert.Equal(port, p);
        }
    }
}
