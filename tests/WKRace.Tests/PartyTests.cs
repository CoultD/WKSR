using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    public class ReadyCheckTests
    {
        private static void Queue(Harness h, out TestClient a, out TestClient b)
        {
            a = h.Login("Alpha");
            b = h.Login("Bravo");
            a.Send(new QueueJoin());
            b.Send(new QueueJoin());
        }

        [Fact]
        public void Countdown_WaitsUntilEveryoneHasPressedReady()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                Queue(h, out a, out b);
                Assert.Equal(60, a.Match.ReadySeconds);
                a.Loaded();
                b.Loaded();
                Assert.Equal(0, a.Conn.Count<MatchStart>());
                Assert.Equal(60, a.Roster.ReadySecondsLeft);
                Assert.Equal(RacerStatus.Loaded, a.Roster.Find(b.Match.YourRacer).Status);

                a.Send(new PlayerReady { MatchId = a.Match.MatchId });
                Assert.Equal(RacerStatus.Ready, b.Roster.Find(a.Match.YourRacer).Status);
                Assert.Equal(0, a.Conn.Count<MatchStart>());
                b.Send(new PlayerReady { MatchId = b.Match.MatchId });
                Assert.Equal(1, a.Conn.Count<MatchStart>());
                Assert.Equal(1, b.Conn.Count<MatchStart>());
            }
        }

        [Fact]
        public void ReadyBeforeLoading_IsRefused()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                Queue(h, out a, out b);
                a.Send(new PlayerReady { MatchId = a.Match.MatchId });
                Assert.Equal(ErrorCode.NotInMatch, a.Last<ErrorMessage>().Code);
                a.Loaded();
                b.Ready();
                Assert.Equal(0, a.Conn.Count<MatchStart>());
            }
        }

        [Fact]
        public void Ranked_WhoeverDidNotReadyUpForfeits()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                Queue(h, out a, out b);
                a.Ready();
                b.Loaded();
                h.Advance(59000);
                Assert.Equal(0, a.Conn.Count<MatchResult>());
                h.Advance(1001);
                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                Assert.Equal(MatchOutcome.Loss, b.Result.Outcome);
                Assert.True(a.Result.NewRating > a.Result.OldRating);
                Assert.Contains("did not ready up", b.Result.Reason);
            }
        }

        [Fact]
        public void Ranked_NeitherReady_IsAbortedWithoutRating()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                Queue(h, out a, out b);
                a.Loaded();
                b.Loaded();
                h.Advance(60001);
                Assert.Equal(MatchOutcome.Aborted, a.Result.Outcome);
                Assert.Equal(a.Result.OldRating, a.Result.NewRating);
            }
        }

        [Fact]
        public void Friendly_NotReady_CallsTheRaceOffAndGoesBackToTheLobby()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                a.Send(new LobbyCreate { Mode = MatchMode.Duel, Rules = MatchRules.None });
                b.Send(new LobbyJoin { LobbyId = a.Last<LobbyCreated>().LobbyId });
                a.Ready();
                b.Loaded();
                h.Advance(60001);
                Assert.Equal(MatchOutcome.Aborted, a.Result.Outcome);
                Assert.Contains("Bravo did not ready up", a.Result.Reason);
                Assert.False(a.Lobby.Racing);
                Assert.Equal(2, a.Lobby.Members.Count);

                // The host can start a rematch from the lobby.
                Assert.True(a.Lobby.YouAreHost);
                a.Send(new LobbyStart());
                Assert.Equal(2, a.Conn.Count<MatchFound>());
                Assert.Equal(a.Match.MatchId, b.Match.MatchId);
            }
        }
    }

    public class PartyTests
    {
        // A party lobby with these players (the first hosts), not started yet.
        private static List<TestClient> Party(Harness h, params string[] names)
        {
            var players = names.Select(n => h.Login(n)).ToList();
            players[0].Send(new LobbyCreate { Mode = MatchMode.Party, Rules = MatchRules.Ranked });
            uint id = players[0].Last<LobbyCreated>().LobbyId;
            foreach (TestClient p in players.Skip(1)) p.Send(new LobbyJoin { LobbyId = id });
            return players;
        }

        // Started, loaded, ready and past the countdown.
        private static List<TestClient> Racing(Harness h, params string[] names)
        {
            List<TestClient> players = Party(h, names);
            players[0].Send(new LobbyStart());
            foreach (TestClient p in players) p.Ready();
            h.Advance(h.Options.CountdownMs + 1000);
            return players;
        }

        private static void Split(Harness h, TestClient p, int index, string level, int ms, string region = "silos")
        {
            p.Send(new SplitReached { Attempt = 1, Index = index, Level = level, Region = region, RaceTimeMs = ms });
        }

        [Fact]
        public void PartyLobby_WaitsForTheHostAndHasNoBans()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Party(h, "Alpha", "Bravo", "Charlie");
                Assert.Equal(0, p[1].Conn.Count<MatchFound>());
                Assert.Equal(3, p[2].Lobby.Members.Count);
                Assert.Equal(MatchRules.CuratedSeeds, p[2].Lobby.Rules);
                Assert.False(p[1].Lobby.YouAreHost);

                p[1].Send(new LobbyStart());
                Assert.Equal(ErrorCode.NotHost, p[1].Last<ErrorMessage>().Code);

                p[0].Send(new LobbyStart());
                MatchFound found = p[2].Match;
                Assert.Equal(MatchMode.Party, found.Mode);
                Assert.Equal(3, found.PlayerCount);
                Assert.False(found.ArtifactBans);
                Assert.False(found.Ranked);
                Assert.Equal(new[] { 0, 1, 2 }, p.Select(x => (int)x.Match.YourRacer).OrderBy(x => x));
                Assert.True(p[0].Lobby.Racing);
            }
        }

        [Fact]
        public void PartyLobby_NeedsTwoPlayersAndRefusesLateJoins()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Party(h, "Alpha");
                p[0].Send(new LobbyStart());
                Assert.Equal(0, p[0].Conn.Count<MatchFound>());

                TestClient b = h.Login("Bravo");
                uint id = p[0].Lobby.LobbyId;
                b.Send(new LobbyJoin { LobbyId = id });
                p[0].Send(new LobbyStart());
                TestClient late = h.Login("Late");
                late.Send(new LobbyJoin { LobbyId = id });
                Assert.Equal(ErrorCode.LobbyBusy, late.Last<ErrorMessage>().Code);
            }
        }

        [Fact]
        public void PartyLobby_HoldsFifteen()
        {
            using (var h = new Harness())
            {
                h.Options.LobbyAttemptsPerMinute = 0;
                List<TestClient> p = Party(h, Enumerable.Range(1, 15).Select(i => "Racer" + i).ToArray());
                Assert.Equal(15, p[0].Lobby.Members.Count);
                TestClient extra = h.Login("Extra");
                extra.Send(new LobbyJoin { LobbyId = p[0].Lobby.LobbyId });
                Assert.Equal(ErrorCode.LobbyFull, extra.Last<ErrorMessage>().Code);
            }
        }

        [Fact]
        public void HostLeaving_HandsTheLobbyOn()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Party(h, "Alpha", "Bravo", "Charlie");
                p[0].Send(new LobbyCancel());
                Assert.True(p[1].Lobby.YouAreHost);
                Assert.Equal(2, p[2].Lobby.Members.Count);
                Assert.Contains(p[2].Conn.Sent.OfType<ChatMessage>(), m => m.System && m.Text.Contains("Bravo is the host"));
            }
        }

        [Fact]
        public void ReadyCheck_StartsThePartyAnywayWithEveryoneInIt()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Party(h, "Alpha", "Bravo", "Charlie");
                p[0].Send(new LobbyStart());
                p[0].Ready();
                p[1].Ready();
                p[2].Loaded();
                Assert.Equal(0, p[0].Conn.Count<MatchStart>());
                h.Advance(60001);
                Assert.Equal(1, p[2].Conn.Count<MatchStart>());
                Assert.All(p[0].Roster.Racers, r => Assert.Equal(RacerStatus.Racing, r.Status));
            }
        }

        [Fact]
        public void Loading_DoesNotWaitForeverForSomeoneWhoNeverLoads()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Party(h, "Alpha", "Bravo", "Charlie");
                p[0].Send(new LobbyStart());
                p[0].Ready();
                p[1].Ready();
                h.Advance(h.Options.ReadyTimeoutSeconds * 1000L + 1);
                Assert.True(p[0].Roster.ReadySecondsLeft > 0);
                h.Advance(60001);
                Assert.Equal(1, p[2].Conn.Count<MatchStart>());
            }
        }

        [Fact]
        public void States_SplitsAndFinishes_GoToEveryoneWithTheRacerNumber()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Racing(h, "Alpha", "Bravo", "Charlie");
                byte alpha = p[0].Match.YourRacer;
                p[0].Send(new PlayerState { Attempt = 1, RaceTimeMs = 100, X = 1, Y = 2, Z = 3 });
                Assert.Equal(alpha, p[1].Last<OpponentState>().Racer);
                Assert.Equal(alpha, p[2].Last<OpponentState>().Racer);
                Assert.Equal(0, p[0].Conn.Count<OpponentState>());

                Split(h, p[0], 0, "M1_Intro_01", 0);
                Assert.Equal(alpha, p[2].Last<OpponentSplit>().Racer);
            }
        }

        [Fact]
        public void Death_PutsAPlayerOutAndTheRaceGoesOn()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Racing(h, "Alpha", "Bravo", "Charlie");
                p[1].Send(new PlayerDied { Attempt = 1, RaceTimeMs = 500 });
                Assert.Equal(0, p[0].Conn.Count<MatchResult>());
                Assert.Equal(RacerStatus.Out, p[0].Roster.Find(p[1].Match.YourRacer).Status);
                Assert.Contains(p[2].Conn.Sent.OfType<ChatMessage>(), m => m.System && m.Text.Contains("Bravo died"));
            }
        }

        [Fact]
        public void Death_UsesTheGamesOwnLine()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Racing(h, "Alpha", "Bravo", "Charlie");
                p[1].Send(new PlayerDied { Attempt = 1, RaceTimeMs = 500, Cause = "falling", Text = "FELL TO YOUR DEATH" });
                Assert.Contains(p[0].Conn.Sent.OfType<ChatMessage>(), m => m.System && m.Text == "Bravo is out: FELL TO THEIR DEATH.");
                p[0].Send(new PlayerDied { Attempt = 1, RaceTimeMs = 600, Cause = "", Text = "" });
                Assert.Contains(p[2].Conn.Sent.OfType<ChatMessage>(), m => m.System && m.Text == "Alpha died and is out.");
                p[2].Send(new PlayerDied { Attempt = 1, RaceTimeMs = 700, Cause = "deathfloor", Text = "FUSED <size=900>INTO THE MASS" });
                Standing charlie = p[0].Result.Standings.First(s => s.Name == "Charlie");
                Assert.Equal("FUSED size=900INTO THE MASS", charlie.Note);
            }
        }

        [Fact]
        public void Party_EndsWhenEveryoneIsDone_AndPlacesThem()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Racing(h, "Alpha", "Bravo", "Charlie", "Delta");
                // Charlie: dies after 3 levels on the main route. Delta: dies after 2 levels, later. Bravo finishes second.
                Split(h, p[2], 0, "A", 0); Split(h, p[2], 1, "B", 100); Split(h, p[2], 2, "C", 200);
                Split(h, p[3], 0, "A", 0); Split(h, p[3], 1, "B", 150);
                h.Advance(40000);
                p[2].Send(new PlayerDied { Attempt = 1, RaceTimeMs = 30000 });
                p[3].Send(new PlayerDied { Attempt = 1, RaceTimeMs = 35000 });
                p[0].Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 39000 });
                Assert.Equal(0, p[0].Conn.Count<MatchResult>());
                h.Advance(1000);
                p[1].Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 40500 });

                MatchResult r = p[3].Result;
                Assert.Equal(new[] { "Alpha", "Bravo", "Charlie", "Delta" }, r.Standings.Select(s => s.Name));
                Assert.Equal(4, r.Place);
                Assert.Equal(1, p[0].Result.Place);
                Assert.Equal(MatchOutcome.Win, p[0].Result.Outcome);
                Assert.Equal(MatchOutcome.Loss, p[1].Result.Outcome);
                Assert.Equal(2, p[1].Result.Place);
                Assert.Contains("Alpha won", p[1].Result.Reason);
                Assert.Equal(1000, h.Store.Find(p[0].Id).Rating);
                Assert.Equal(0, h.Store.Find(p[0].Id).Wins);

                // Back in the lobby for a rematch.
                Assert.False(p[0].Lobby.Racing);
                Assert.Equal(4, p[0].Lobby.Members.Count);
            }
        }

        [Fact]
        public void Party_RanksFinishersByTheGameTimer()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Racing(h, "Alpha", "Bravo", "Charlie");
                h.Advance(60000);
                // Alpha is there first, by a shortcut: the game's timer ran three times as fast for part of the run.
                p[0].Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 140000, RealTimeMs = 60000 });
                h.Advance(30000);
                p[1].Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 90000, RealTimeMs = 90000 });
                h.Advance(20000);
                p[2].Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 110000, RealTimeMs = 110000 });
                Assert.Equal(new[] { "Bravo", "Charlie", "Alpha" }, p[0].Result.Standings.Select(s => s.Name));
                Assert.Equal(140000, p[0].Result.Standings.Single(s => s.Name == "Alpha").TimeMs);
            }
        }

        [Fact]
        public void SameFinishTime_GoesToWhoeverFinishedFirst()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Racing(h, "Alpha", "Bravo", "Charlie");
                h.Advance(30000);
                p[2].Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 30000 });
                p[0].Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 30000 });
                p[1].Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 30000 });
                Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, p[0].Result.Standings.Select(s => s.Name));
            }
        }

        [Fact]
        public void Party_EndsFiveMinutesAfterTheFirstFinish()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Racing(h, "Alpha", "Bravo", "Charlie");
                Split(h, p[1], 0, "A", 0); Split(h, p[1], 1, "B", 100);
                h.Advance(30000);
                p[0].Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 30000 });
                h.Advance(299000);
                Assert.Equal(0, p[1].Conn.Count<MatchResult>());
                h.Advance(2000);
                Assert.Equal(1, p[0].Result.Place);
                Assert.Equal(2, p[1].Result.Place);
                Assert.Equal(RacerStatus.Racing, p[1].Result.Standings[1].Status);
            }
        }

        [Fact]
        public void Disconnects_AreLeftBehindWithoutPausing()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Racing(h, "Alpha", "Bravo", "Charlie");
                p[2].Disconnect();
                Assert.Equal(0, p[0].Conn.Count<MatchPaused>());
                h.Advance(h.Options.ReconnectGraceSeconds * 1000L + 1000);
                Assert.Equal(RacerStatus.Left, p[0].Roster.Find(2).Status);
                p[0].Send(new PlayerDied { Attempt = 1, RaceTimeMs = 1000 });
                p[1].Send(new PlayerDied { Attempt = 1, RaceTimeMs = 2000 });
                Assert.Equal(3, p[0].Result.Standings.Count);
                // Charlie is let go from the lobby now the race is over.
                Assert.Equal(2, p[0].Lobby.Members.Count);
            }
        }

        [Fact]
        public void NeverReadyPlayers_DoNotKeepTheRaceGoing()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Party(h, "Alpha", "Bravo", "Charlie");
                p[0].Send(new LobbyStart());
                p[0].Ready();
                p[1].Ready();
                p[2].Loaded();
                h.Advance(60001);
                h.Advance(h.Options.CountdownMs + 1000);
                p[0].Send(new PlayerDied { Attempt = 1, RaceTimeMs = 1000 });
                Assert.Equal(0, p[0].Conn.Count<MatchResult>());
                p[1].Send(new PlayerDied { Attempt = 1, RaceTimeMs = 2000 });
                Assert.Equal(1, p[0].Conn.Count<MatchResult>());
            }
        }

        [Fact]
        public void Forfeit_LeavesTheRaceOnly()
        {
            using (var h = new Harness())
            {
                List<TestClient> p = Racing(h, "Alpha", "Bravo", "Charlie");
                p[0].Send(new PlayerForfeit());
                Assert.Equal(RacerStatus.Left, p[1].Roster.Find(p[0].Match.YourRacer).Status);
                Assert.Equal(0, p[1].Conn.Count<MatchResult>());
            }
        }
    }

    public class ChatTests
    {
        [Fact]
        public void Chat_GoesToTheLobbyAndThenTheRace()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                TestClient outsider = h.Login("Outsider");
                a.Send(new LobbyCreate { Mode = MatchMode.Party });
                b.Send(new LobbyJoin { LobbyId = a.Last<LobbyCreated>().LobbyId });

                a.Send(new ChatSend { Text = "  hi   there\n" });
                Assert.Equal("hi there", b.Last<ChatMessage>().Text);
                Assert.Equal("Alpha", b.Last<ChatMessage>().From);
                Assert.Equal("hi there", a.Last<ChatMessage>().Text);
                Assert.Equal(0, outsider.Conn.Count<ChatMessage>());

                a.Send(new LobbyStart());
                b.Send(new ChatSend { Text = "gl" });
                Assert.Equal("gl", a.Last<ChatMessage>().Text);
            }
        }

        [Fact]
        public void Chat_AloneGetsAnExplanation_AndIsRateLimited()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                a.Send(new ChatSend { Text = "anyone?" });
                Assert.True(a.Last<ChatMessage>().System);

                TestClient b = h.Login("Bravo");
                a.Send(new LobbyCreate { Mode = MatchMode.Party });
                b.Send(new LobbyJoin { LobbyId = a.Last<LobbyCreated>().LobbyId });
                // Five lines within five seconds: the fifth is still sent, and the player is silenced for five seconds.
                for (int i = 0; i < 5; i++) a.Send(new ChatSend { Text = "spam " + i });
                Assert.Equal(5, b.Conn.Sent.OfType<ChatMessage>().Count(m => !m.System));
                Assert.Equal("Slow down.. Silenced for 5 seconds", a.Last<ChatMessage>().Text);
                Assert.Equal(5000, a.Last<ChatMessage>().SilencedMs);

                // Trying while silenced sends nothing, says how long is left, and does not make the silence longer.
                h.Advance(2000);
                a.Send(new ChatSend { Text = "more" });
                Assert.Equal(3000, a.Last<ChatMessage>().SilencedMs);
                Assert.Equal("Silenced for 3 more seconds.", a.Last<ChatMessage>().Text);
                h.Advance(2500);
                a.Send(new ChatSend { Text = "more" });
                Assert.Equal(500, a.Last<ChatMessage>().SilencedMs);
                Assert.Equal("Silenced for 1 more second.", a.Last<ChatMessage>().Text);
                Assert.Equal(5, b.Conn.Sent.OfType<ChatMessage>().Count(m => !m.System));

                // Five seconds after it began the silence is over.
                h.Advance(500);
                a.Send(new ChatSend { Text = "ok" });
                Assert.Equal("ok", b.Last<ChatMessage>().Text);

                // Four lines in five seconds are fine, however long that goes on.
                for (int round = 0; round < 3; round++)
                {
                    h.Advance(5001);
                    for (int i = 0; i < 4; i++) a.Send(new ChatSend { Text = "calm " + i });
                }
                Assert.Equal("calm 3", b.Last<ChatMessage>().Text);
                Assert.Equal("ok", a.Conn.Sent.OfType<ChatMessage>().Last(m => !m.System && m.Text == "ok").Text);
                Assert.DoesNotContain(a.Conn.Sent.OfType<ChatMessage>().Skip(a.Conn.Sent.OfType<ChatMessage>().ToList().FindLastIndex(m => m.Text == "ok")), m => m.System && m.SilencedMs > 0);
            }
        }

        [Fact]
        public void Chat_DropsControlCharactersAndLongText()
        {
            Assert.Equal("ab", WKRace.Server.ServerCore.CleanChat("a\u0001‮b"));
            Assert.Equal(ProtocolConstants.MaxChatLength, WKRace.Server.ServerCore.CleanChat(new string('x', 500)).Length);
            Assert.Equal("", WKRace.Server.ServerCore.CleanChat("   "));
        }
    }

    public class StandingsTests
    {
        private static RacerProgress P(int racer, params string[] route)
        {
            var p = new RacerProgress { Racer = racer, Name = "R" + racer };
            for (int i = 0; i < route.Length; i++) p.AddSplit(route[i], route[i] == "Short" ? "Tangled Sink" : "silos", i * 1000);
            return p;
        }

        [Fact]
        public void SameRoute_MoreLevelsThenSooner()
        {
            RacerProgress a = P(0, "A", "B", "C");
            RacerProgress b = P(1, "A", "B");
            a.Out = true; a.OutMs = 5000;
            Assert.True(Standings.Compare(a, b, 99999) < 0);
            RacerProgress c = P(2, "A", "B");
            c.RouteMs[1] = 500;
            Assert.True(Standings.Compare(c, b, 99999) < 0);
        }

        [Fact]
        public void DifferentRoutes_StillClimbingBeatsOut_WhateverTheTimersSay()
        {
            // The out player's timer ran fast on a shortcut. Someone still climbing has outlasted them all the same.
            RacerProgress dead = P(0, "A", "Short");
            dead.Out = true; dead.OutMs = 90000;
            RacerProgress live = P(1, "A", "B");
            Assert.True(Standings.Compare(live, dead, 30000) < 0);
            Assert.True(Standings.Compare(dead, live, 30000) > 0);
        }

        [Fact]
        public void DifferentRoutes_SurvivedLonger()
        {
            RacerProgress a = P(0, "A", "Short");
            RacerProgress b = P(1, "A", "B", "C");
            a.Out = true; a.OutMs = 9000;
            b.Out = true; b.OutMs = 4000;
            Assert.True(Standings.Compare(a, b, 99999) < 0);
            Assert.True(a.Out);
            RacerProgress live = P(2, "A", "Short");
            Assert.True(live.OnShortcut);
            Assert.False(b.OnShortcut);
        }

        [Fact]
        public void FinishersFirst_DisqualifiedLast()
        {
            RacerProgress fast = P(0, "A"); fast.Finished = true; fast.FinishMs = 100;
            RacerProgress slow = P(1, "A"); slow.Finished = true; slow.FinishMs = 200;
            RacerProgress dq = P(2, "A", "B", "C"); dq.Disqualified = true;
            RacerProgress climbing = P(3, "A", "B");
            List<RacerProgress> order = Standings.Rank(new[] { dq, climbing, slow, fast }, 1000);
            Assert.Equal(new[] { 0, 1, 3, 2 }, order.Select(r => r.Racer));
        }

        [Fact]
        public void DeathText_IsRewordedForOthers()
        {
            Assert.Equal("FELL TO THEIR DEATH", DeathText.ForOthers("FELL TO YOUR DEATH"));
            Assert.Equal("FUSED INTO THE MASS", DeathText.ForOthers("FUSED INTO THE MASS"));
            Assert.Equal("They tripped over their own feet.", DeathText.ForOthers("You tripped over your own feet."));
            Assert.Equal("", DeathText.ForOthers(null));
        }

        [Fact]
        public void Ordinals()
        {
            Assert.Equal("1st", Standings.Ordinal(1));
            Assert.Equal("2nd", Standings.Ordinal(2));
            Assert.Equal("3rd", Standings.Ordinal(3));
            Assert.Equal("11th", Standings.Ordinal(11));
            Assert.Equal("12th", Standings.Ordinal(12));
            Assert.Equal("15th", Standings.Ordinal(15));
        }

        [Fact]
        public void RulesText_Describes()
        {
            Assert.Equal("Artifact bans, curated seeds", RulesText.Describe(MatchRules.Ranked));
            Assert.Equal("Curated seeds (food, items)", RulesText.Describe(MatchRules.DeepStorageFood | MatchRules.GuaranteedItems));
            Assert.Equal("No modifiers", RulesText.Describe(MatchRules.None));
        }
    }
}
