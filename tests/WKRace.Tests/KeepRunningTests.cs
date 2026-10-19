using System;
using System.Linq;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    public class KeepRunningTests
    {
        private static TestClient LoginAs(Harness h, string name, string clientVersion = "0.9.7")
        {
            var c = new TestClient(h, name);
            ClientHello hello = c.Hello();
            hello.ClientVersion = clientVersion;
            c.Send(hello);
            return c;
        }

        // A ranked 1v1 (both on the given version), ready and past the countdown.
        private static void Ranked(Harness h, out TestClient a, out TestClient b, string versionA = "0.9.7")
        {
            a = LoginAs(h, "Alpha", versionA);
            b = LoginAs(h, "Bravo");
            a.Send(new QueueJoin());
            b.Send(new QueueJoin());
            a.Ready();
            b.Ready();
            h.Advance(h.Options.CountdownMs);
        }

        // A friendly lobby (1v1 or party) with these players, its match ready and past the countdown.
        private static void Friendly(Harness h, MatchMode mode, bool letLastFinish, params TestClient[] players)
        {
            players[0].Send(new LobbyCreate { Mode = mode, Rules = MatchRules.CuratedSeeds });
            if (!letLastFinish) players[0].Send(new LobbyOptions { LetLastFinish = false });
            uint id = players[0].Last<LobbyCreated>().LobbyId;
            foreach (TestClient p in players.Skip(1)) p.Send(new LobbyJoin { LobbyId = id });
            if (mode == MatchMode.Party) players[0].Send(new LobbyStart());
            foreach (TestClient p in players) p.Ready();
            h.Advance(h.Options.CountdownMs);
        }

        [Fact]
        public void ADeathDecidesTheMatch_AndTheWinnerKeepsRunning()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                Ranked(h, out a, out b);
                h.Advance(60000);
                b.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 60000 });

                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                Assert.True(a.Result.KeepRunning);
                Assert.Equal(MatchOutcome.Loss, b.Result.Outcome);
                Assert.False(b.Result.KeepRunning);
                // Ratings moved at the death.
                Assert.Equal(1016, h.Store.Find(a.Id).Rating);
                // The match stays for Alpha's run.
                Assert.Equal(1, h.Core.ActiveMatchCount);

                // Alpha finishes later: the time goes on the 1v1 board, and the match is gone.
                h.Advance(240000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 300000, RealTimeMs = 300000 });
                Assert.Equal(0, h.Core.ActiveMatchCount);
                h.Advance(6000);
                a.Send(new BoardRequest { Board = BoardKind.DuelTime, Count = 10 });
                BoardRow row = a.Last<BoardPage>().Rows.Single();
                Assert.Equal("Alpha", row.Name);
                Assert.Equal(300000, row.Value);
                // Still one result for each: nothing was sent again.
                Assert.Equal(1, a.Conn.Count<MatchResult>());
            }
        }

        [Fact]
        public void EndRun_EndsTheWinnersRun()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                Ranked(h, out a, out b);
                h.Advance(60000);
                b.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 60000 });
                a.Send(new EndRun { MatchId = a.Result.MatchId });
                Assert.Equal(0, h.Core.ActiveMatchCount);
                // The player is free again.
                a.Send(new QueueJoin());
                Assert.True(a.Last<QueueStatus>().Queued);
            }
        }

        [Fact]
        public void AWinnerOnAnOlderVersion_DoesNotKeepRunning()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                Ranked(h, out a, out b, versionA: "0.9.6");
                h.Advance(60000);
                b.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 60000 });
                Assert.False(a.Result.KeepRunning);
                Assert.Equal(0, h.Core.ActiveMatchCount);
            }
        }

        [Fact]
        public void AFriendlyLobby_CanSwitchItOff_AndShowsTheOption()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                var b = LoginAs(h, "Bravo");
                Friendly(h, MatchMode.Duel, false, a, b);
                Assert.True(b.Lobby.OptionsKnown);
                Assert.False(b.Lobby.LetLastFinish);
                h.Advance(30000);
                b.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 30000 });
                Assert.False(a.Result.KeepRunning);
                Assert.Equal(0, h.Core.ActiveMatchCount);
                Assert.False(a.Lobby.Racing);
            }
        }

        [Fact]
        public void AFriendlyWinnersRun_KeepsTheLobbyRacing_UntilItEnds()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                var b = LoginAs(h, "Bravo");
                Friendly(h, MatchMode.Duel, true, a, b);
                h.Advance(30000);
                b.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 30000 });
                Assert.True(a.Result.KeepRunning);
                Assert.True(b.Lobby.Racing);
                a.Disconnect();
                Assert.Equal(0, h.Core.ActiveMatchCount);
                Assert.False(b.Lobby.Racing);
            }
        }

        [Fact]
        public void ThePartysLastOneStanding_IsAsked_AndEndingTheRunEndsTheParty()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                var b = LoginAs(h, "Bravo");
                var c = LoginAs(h, "Charlie");
                Friendly(h, MatchMode.Party, true, a, b, c);
                h.Advance(30000);
                b.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 30000 });
                Assert.Equal(0, a.Conn.Count<LastStanding>());
                c.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 31000 });
                Guid id = a.Last<LastStanding>().MatchId;
                Assert.Equal(0, a.Conn.Count<MatchResult>());

                a.Send(new EndRun { MatchId = id });
                Assert.Equal(1, a.Result.Place);
                Assert.Equal(0, h.Core.ActiveMatchCount);
            }
        }

        [Fact]
        public void APartyWithTheOptionOff_EndsWhenOnePlayerIsLeft()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                var b = LoginAs(h, "Bravo");
                var c = LoginAs(h, "Charlie");
                Friendly(h, MatchMode.Party, false, a, b, c);
                h.Advance(30000);
                b.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 30000 });
                c.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 31000 });
                Assert.Equal(0, a.Conn.Count<LastStanding>());
                Assert.Equal(1, a.Result.Place);
            }
        }

        [Fact]
        public void NewMessages_SurviveTheWire()
        {
            var id = System.Guid.NewGuid();
            Assert.Equal(id, ((EndRun)MessageCodec.Decode(MessageCodec.Encode(new EndRun { MatchId = id }))).MatchId);
            Assert.Equal(id, ((LastStanding)MessageCodec.Decode(MessageCodec.Encode(new LastStanding { MatchId = id }))).MatchId);
            Assert.False(((LobbyOptions)MessageCodec.Decode(MessageCodec.Encode(new LobbyOptions { LetLastFinish = false }))).LetLastFinish);
            Assert.True(((MatchResult)MessageCodec.Decode(MessageCodec.Encode(new MatchResult { MatchId = id, KeepRunning = true }))).KeepRunning);
            Assert.False(((MatchResult)MessageCodec.Decode(MessageCodec.Encode(new MatchResult { MatchId = id }))).KeepRunning);
            var keys = (ModKeys)MessageCodec.Decode(MessageCodec.Encode(new ModKeys { Keys = new KeyBindings { EndRun = 269, KeepRunning = 270 } }));
            Assert.Equal(269, keys.Keys.EndRun);
            Assert.Equal(270, keys.Keys.KeepRunning);
        }
    }
}
