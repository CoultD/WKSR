using System;
using System.Linq;
using WKRace.Protocol;
using WKRace.Server;
using Xunit;

namespace WKRace.Tests
{
    public class ServerCoreSessionTests
    {
        [Fact]
        public void Hello_RegistersPlayerAndSendsWelcome()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var welcome = a.Last<ServerWelcome>();
                Assert.Equal(a.Id, welcome.PlayerId);
                Assert.Equal("Alpha", welcome.Name);
                Assert.Equal(Elo.StartRating, welcome.Rating);
                Assert.True(a.Conn.Authenticated);
            }
        }

        [Fact]
        public void Hello_WithWrongToken_IsRejected()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var impostor = new FakeConnection();
                var hello = a.Hello();
                hello.Token = new byte[ProtocolConstants.TokenLength];
                h.Core.OnMessage(impostor, hello);
                Assert.Equal(ErrorCode.BadToken, impostor.Last<ErrorMessage>().Code);
                Assert.True(impostor.Closed);
                Assert.False(a.Conn.Closed);
            }
        }

        [Fact]
        public void Hello_WithOldProtocolVersion_IsRejected()
        {
            using (var h = new Harness())
            {
                var c = new FakeConnection();
                h.Core.OnMessage(c, new TestClient(h, "Alpha").Hello(version: 0));
                Assert.Equal(ErrorCode.VersionMismatch, c.Last<ErrorMessage>().Code);
                Assert.True(c.Closed);
            }
        }

        [Fact]
        public void Hello_WithOldProtocolVersion_SaysHowToUpdate_AndWhatFollowsIsIgnored()
        {
            using (var h = new Harness())
            {
                var c = new FakeConnection();
                var old = new TestClient(h, "Alpha");
                h.Core.OnMessage(c, old.Hello(version: ProtocolConstants.ProtocolVersion - 1));
                Assert.Contains("Update the WKRace mod in your mod manager", c.Last<ErrorMessage>().Text);

                // An old client keeps talking for a moment after it was refused. That is not a protocol violation.
                int errors = c.Count<ErrorMessage>();
                h.Core.OnMessage(c, new Ping());
                h.Core.OnMessage(c, new QueueJoin());
                Assert.Equal(errors, c.Count<ErrorMessage>());
            }
        }

        [Fact]
        public void Forfeit_WhileTheGameIsGone_EndsTheMatchForTheOtherPlayer()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);

                // What WKSR sends when the game closes during a run: the game is gone, then the forfeit.
                a.Send(new GameStatus { Connected = false });
                a.Send(new PlayerForfeit());

                Assert.Equal(MatchOutcome.Win, b.Result.Outcome);
                Assert.Equal(MatchOutcome.Loss, a.Result.Outcome);
            }
        }

        [Fact]
        public void Status_GoesOutAtLogin_AndWheneverThePlayerCountChanges()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                Assert.Equal(1, a.Last<ServerStatus>().PlayersOnline);
                Assert.False(string.IsNullOrEmpty(a.Last<ServerStatus>().LatestVersion));

                TestClient b = h.Login("Bravo");
                h.Advance(4000);
                Assert.Equal(2, a.Last<ServerStatus>().PlayersOnline);

                int sent = a.Conn.Count<ServerStatus>();
                h.Advance(4000);
                Assert.Equal(sent, a.Conn.Count<ServerStatus>());   // nothing changed: nothing sent

                b.Disconnect();
                h.Advance(4000);
                Assert.Equal(1, a.Last<ServerStatus>().PlayersOnline);

                h.Advance(31000);
                Assert.True(a.Conn.Count<ServerStatus>() > sent + 1);   // and a repeat now and then
            }
        }

        [Fact]
        public void Hello_WithUnusableName_IsRejected()
        {
            using (var h = new Harness())
            {
                var c = new FakeConnection();
                var hello = new TestClient(h, "x").Hello();
                hello.Name = "   \u202E ";
                h.Core.OnMessage(c, hello);
                Assert.Equal(ErrorCode.InvalidName, c.Last<ErrorMessage>().Code);
            }
        }

        [Fact]
        public void Hello_FromSecondConnection_ReplacesFirst()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var first = a.Conn;
                a.Reconnect();
                Assert.True(first.Closed);
                Assert.False(a.Conn.Closed);
                Assert.Equal(1, h.Core.OnlineCount);
            }
        }

        [Fact]
        public void MessagesBeforeHello_CloseTheConnection()
        {
            using (var h = new Harness())
            {
                var c = new FakeConnection();
                h.Core.OnMessage(c, new QueueJoin());
                Assert.Equal(ErrorCode.ProtocolViolation, c.Last<ErrorMessage>().Code);
                Assert.True(c.Closed);
            }
        }

        [Fact]
        public void Ping_ReturnsServerTime()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                a.Send(new Ping { ClientTimeMs = 77 });
                var pong = a.Last<Pong>();
                Assert.Equal(77, pong.ClientTimeMs);
                Assert.Equal(h.Now, pong.ServerTimeMs);
            }
        }
    }

    public class ServerCoreMatchmakingTests
    {
        [Fact]
        public void TwoQueuedPlayers_AreMatchedWithSharedSeed()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                a.Send(new QueueJoin());
                Assert.Equal(1, h.Core.QueuedCount);
                Assert.True(a.Last<QueueStatus>().Queued);
                b.Send(new QueueJoin());

                Assert.Equal(0, h.Core.QueuedCount);
                Assert.Equal(1, h.Core.ActiveMatchCount);
                Assert.Equal(a.Match.MatchId, b.Match.MatchId);
                Assert.Equal(a.Match.Seed, b.Match.Seed);
                Assert.InRange(a.Match.Seed, ProtocolConstants.MinSeed, ProtocolConstants.MaxSeed);
                Assert.Equal("Bravo", a.Match.OpponentName);
                Assert.Equal("Alpha", b.Match.OpponentName);
                Assert.True(a.Match.Competitive);
                Assert.Equal(-1, a.Match.ResumeElapsedMs);
            }
        }

        [Fact]
        public void QueueLeave_RemovesPlayer()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                a.Send(new QueueJoin());
                a.Send(new QueueLeave());
                Assert.Equal(0, h.Core.QueuedCount);
                Assert.False(a.Last<QueueStatus>().Queued);
            }
        }

        [Fact]
        public void QueuedPlayerWhoDisconnects_LeavesQueue()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                a.Send(new QueueJoin());
                a.Disconnect();
                Assert.Equal(0, h.Core.QueuedCount);
            }
        }

        [Fact]
        public void QueueJoin_WhileInMatch_IsRefused()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Send(new QueueJoin());
                Assert.Equal(ErrorCode.AlreadyInMatch, a.Last<ErrorMessage>().Code);
            }
        }

        [Fact]
        public void FarApartRatings_AreMatchedOnceTheWindowGrows()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                h.Store.Find(a.Id).Rating = 1000;
                h.Store.Find(b.Id).Rating = 1500;
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                Assert.Equal(2, h.Core.QueuedCount);

                h.Advance(10000);
                Assert.Equal(2, h.Core.QueuedCount);

                h.Advance(10000);
                Assert.Equal(0, h.Core.QueuedCount);
                Assert.Equal(1, h.Core.ActiveMatchCount);
            }
        }

        [Fact]
        public void QueueStatus_IsRefreshedPeriodically()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                a.Send(new QueueJoin());
                int before = a.Conn.Count<QueueStatus>();
                h.Advance(6000);
                Assert.True(a.Conn.Count<QueueStatus>() > before);
                Assert.Equal(6, a.Last<QueueStatus>().WaitSeconds);
            }
        }
    }

    public class ServerCoreMatchTests
    {
        [Fact]
        public void BothReady_StartsSynchronizedCountdown()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                a.Ready();
                Assert.Equal(0, a.Conn.Count<MatchStart>());
                long before = h.Now;
                b.Ready();

                Assert.Equal(before + h.Options.CountdownMs, a.Last<MatchStart>().GoServerTimeMs);
                Assert.Equal(a.Last<MatchStart>().GoServerTimeMs, b.Last<MatchStart>().GoServerTimeMs);
            }
        }

        [Fact]
        public void DifferentWorldHashes_AbortBeforeTheCountdown()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b, hashA: 111, hashB: 222);
                Assert.Equal(0, a.Conn.Count<MatchStart>());
                Assert.Equal(MatchOutcome.Aborted, a.Result.Outcome);
                Assert.Equal(MatchOutcome.Aborted, b.Result.Outcome);
                Assert.Equal(1000, h.Store.Find(a.Id).Rating);
                Assert.Equal(0, h.Core.ActiveMatchCount);
            }
        }

        [Fact]
        public void UnknownWorldHash_DoesNotAbort()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b, hashA: 0, hashB: 222);
                Assert.Equal(1, a.Conn.Count<MatchStart>());
            }
        }

        [Fact]
        public void DifferentGameBuilds_AreMatched_UntilTheirWorldsTurnOutDifferent()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                // The current release and the public test release: paired like anyone else.
                a.Send(new QueueJoin { GameBuild = "steam-1|mod-0.9.3" });
                b.Send(new QueueJoin { GameBuild = "steam-2|mod-0.9.3" });
                Assert.Equal(1, a.Conn.Count<MatchFound>());
                Assert.Equal(1, b.Conn.Count<MatchFound>());

                // Their worlds differ: the match is called off without a rating change. Once is not proof (a mod can
                // do that too), so they may meet again; twice, and the two builds are kept apart.
                a.Loaded(111);
                b.Loaded(222);
                Assert.Equal(MatchOutcome.Aborted, a.Result.Outcome);
                a.Send(new QueueJoin { GameBuild = "steam-1|mod-0.9.3" });
                b.Send(new QueueJoin { GameBuild = "steam-2|mod-0.9.3" });
                Assert.Equal(2, a.Conn.Count<MatchFound>());
                a.Loaded(333);
                b.Loaded(444);
                Assert.Equal(MatchOutcome.Aborted, b.Result.Outcome);
                a.Send(new QueueJoin { GameBuild = "steam-1|mod-0.9.3" });
                b.Send(new QueueJoin { GameBuild = "steam-2|mod-0.9.3" });
                h.Advance(60000);
                Assert.Equal(2, a.Conn.Count<MatchFound>());
                Assert.Equal(2, b.Conn.Count<MatchFound>());

                // A player on the same build as one of them still gets a match, and so does one whose build is unknown.
                var c = h.Login("Charlie");
                c.Send(new QueueJoin { GameBuild = "steam-2|mod-0.9.3" });
                Assert.Equal(3, b.Conn.Count<MatchFound>());
                Assert.Equal(1, c.Conn.Count<MatchFound>());
                var d = h.Login("Delta");
                d.Send(new QueueJoin());
                Assert.Equal(3, a.Conn.Count<MatchFound>());
            }
        }

        [Fact]
        public void GameBuildsThatOnceMadeTheSameWorld_StayTogether_AndModVersionsNeverMix()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                a.Send(new QueueJoin { GameBuild = "steam-1|mod-0.9.3" });
                b.Send(new QueueJoin { GameBuild = "steam-2|mod-0.9.3" });
                a.Ready(7);
                b.Ready(7);
                h.Advance(h.Options.CountdownMs);
                a.Send(new PlayerForfeit());

                // Later mismatches between the two (someone's mod, say) do not split builds that are known to agree.
                for (int i = 0; i < 3; i++)
                {
                    a.Send(new QueueJoin { GameBuild = "steam-1|mod-0.9.3" });
                    b.Send(new QueueJoin { GameBuild = "steam-2|mod-0.9.3" });
                    a.Loaded(100);
                    b.Loaded(200);
                }
                a.Send(new QueueJoin { GameBuild = "steam-1|mod-0.9.3" });
                b.Send(new QueueJoin { GameBuild = "steam-2|mod-0.9.3" });
                Assert.Equal(5, a.Conn.Count<MatchFound>());

                // Different versions of the mod are never paired, whatever the game build.
                var c = h.Login("Charlie");
                var d = h.Login("Delta");
                c.Send(new QueueJoin { GameBuild = "steam-1|mod-0.9.3" });
                d.Send(new QueueJoin { GameBuild = "steam-1|mod-0.9.1" });
                h.Advance(60000);
                Assert.Equal(0, c.Conn.Count<MatchFound>());
                Assert.Equal(0, d.Conn.Count<MatchFound>());
            }
        }

        [Fact]
        public void ReadyWithWrongMatchId_IsRefused()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Send(new MatchReady { MatchId = Guid.NewGuid() });
                Assert.Equal(ErrorCode.NotInMatch, a.Last<ErrorMessage>().Code);
            }
        }

        [Fact]
        public void Running_RelaysStateSplitsAndAttemptsToTheOpponent()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(2000);

                a.Send(new PlayerState { Attempt = 1, RaceTimeMs = 1900, X = 1, Y = 2, Z = 3, Yaw = 4, Flags = StateFlags.RightHold, RightX = 5, RightY = 6, RightZ = 7 });
                var state = b.Last<OpponentState>();
                Assert.Equal(2f, state.Y);
                Assert.Equal(7f, state.RightZ);
                Assert.Equal(0, a.Conn.Count<OpponentState>());

                a.Send(new SplitReached { Attempt = 1, Index = 0, Level = "M1_Intro_01", Region = "silos", RaceTimeMs = 1800 });
                Assert.Equal("M1_Intro_01", b.Last<OpponentSplit>().Level);

                a.Send(new AttemptStarted { Attempt = 2, RaceTimeMs = 2000 });
                Assert.Equal(2, b.Last<OpponentAttempt>().Attempt);
            }
        }

        [Fact]
        public void StateSentRightAtGo_IsRelayedWithoutWaitingForATick()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                a.Ready();
                b.Ready();
                h.Now += h.Options.CountdownMs;
                a.Send(new PlayerState { Attempt = 1, Y = 5 });
                Assert.Equal(5f, b.Last<OpponentState>().Y);
            }
        }

        [Fact]
        public void Reconnect_ReportsTheCurrentAttempt()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(1000);
                a.Send(new AttemptStarted { Attempt = 2, RaceTimeMs = 1000 });
                a.Disconnect();
                a.Reconnect();
                Assert.Equal(2, a.Match.Attempt);
            }
        }

        [Fact]
        public void StateBeforeGo_IsNotRelayed()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                a.Ready();
                b.Ready();
                a.Send(new PlayerState());
                Assert.Equal(0, b.Conn.Count<OpponentState>());
            }
        }

        [Fact]
        public void Split_FromTheFutureOrWrongAttempt_IsDropped()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(1000);

                a.Send(new SplitReached { Attempt = 1, Index = 1, RaceTimeMs = 60000 });
                a.Send(new SplitReached { Attempt = 5, Index = 2, RaceTimeMs = 500 });
                a.Send(new SplitReached { Attempt = 1, Index = -1, RaceTimeMs = 500 });
                Assert.Equal(0, b.Conn.Count<OpponentSplit>());
            }
        }

        [Fact]
        public void Attempt_OnlyMovesForward_ButMaySkipNumbers()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Send(new AttemptStarted { Attempt = 1 });
                Assert.Equal(0, b.Conn.Count<OpponentAttempt>());
                // A client that was offline through two resets replays only its latest attempt.
                a.Send(new AttemptStarted { Attempt = 3 });
                Assert.Equal(3, b.Last<OpponentAttempt>().Attempt);
                a.Send(new AttemptStarted { Attempt = 2 });
                Assert.Equal(1, b.Conn.Count<OpponentAttempt>());
                h.Advance(1000);
                a.Send(new PlayerFinished { Attempt = 3, RaceTimeMs = 1000 });
                Assert.Equal(3, b.Last<OpponentFinished>().Attempt);
            }
        }

        [Fact]
        public void FirstFinisher_WinsAfterSettleWindow_AndRatingsMove()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(60000);

                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 60000 });
                Assert.Equal(60000, b.Last<OpponentFinished>().RaceTimeMs);
                Assert.Equal(0, a.Conn.Count<MatchResult>());

                h.Advance(1000);
                Assert.Equal(0, a.Conn.Count<MatchResult>());
                h.Advance(600);

                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                Assert.Equal(MatchOutcome.Loss, b.Result.Outcome);
                Assert.Equal(1000, a.Result.OldRating);
                // Both are new (provisional), so ratings move twice as far as the usual 16.
                Assert.Equal(1032, a.Result.NewRating);
                Assert.Equal(968, b.Result.NewRating);
                Assert.Equal(60000, a.Result.YourTimeMs);
                Assert.Equal(-1, a.Result.OpponentTimeMs);
                Assert.Equal(0, h.Core.ActiveMatchCount);
                Assert.Equal(1, h.Store.Find(a.Id).Wins);
                Assert.Equal(1, h.Store.Find(b.Id).Losses);
                Assert.Equal(60000, h.Store.Find(a.Id).BestTimeMs);
            }
        }

        [Fact]
        public void NearSimultaneousFinish_EarlierOnTheRealClockWins()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(60000);

                // Alpha's finish arrives first, but Bravo's own clock says Bravo was there 300 ms sooner.
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 60000, RealTimeMs = 60000 });
                h.Advance(400);
                b.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 59700, RealTimeMs = 59700 });

                Assert.Equal(MatchOutcome.Win, b.Result.Outcome);
                Assert.Equal(MatchOutcome.Loss, a.Result.Outcome);
                Assert.Equal(59700, b.Result.YourTimeMs);
                Assert.Equal(60000, b.Result.OpponentTimeMs);
            }
        }

        [Fact]
        public void FirstToFinishWins_EvenWithTheHigherGameTimer()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(60000);

                // Alpha took a shortcut: there first on the real clock, but the game's timer ran three times as fast.
                b.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 60000, RealTimeMs = 60000 });
                h.Advance(300);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 150000, RealTimeMs = 59800 });

                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                Assert.Equal(MatchOutcome.Loss, b.Result.Outcome);
                // The times shown are the game's timers.
                Assert.Equal(150000, a.Result.YourTimeMs);
                Assert.Equal(60000, a.Result.OpponentTimeMs);
            }
        }

        [Fact]
        public void ClaimedFinishTime_IsHeldToWhatTheGameTimerCanShow()
        {
            // The game's timer runs between 0.15 and 3 times real time, so after 100 s it reads 15 to 300 s
            // (with 1.5 s of leeway either way).
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(100000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 100 });
                Assert.Equal(15000 - 1500, b.Last<OpponentFinished>().RaceTimeMs);
            }

            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(100000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 99999999 });
                Assert.Equal(300000 + 1500, b.Last<OpponentFinished>().RaceTimeMs);
            }

            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(100000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 240000 });   // a run with long shortcut sections
                Assert.Equal(240000, b.Last<OpponentFinished>().RaceTimeMs);
            }
        }

        [Fact]
        public void ClaimedRealFinishTime_IsClampedToTheServerTimeline()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(10000);
                // Bravo claims to have been there after 100 ms. The server saw 10 s pass, so that reads as 8.5 s
                // (10 s less the allowed latency), still ahead of Alpha's honest 9.9 s.
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 9900, RealTimeMs = 9900 });
                b.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 9000, RealTimeMs = 100 });
                Assert.Equal(MatchOutcome.Win, b.Result.Outcome);
            }

            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(10000);
                // Without a real-clock reading (a test tool, an old recording) the server's own clock is used.
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 9000 });
                h.Advance(200);
                b.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 9000, RealTimeMs = 10100 });
                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
            }
        }

        [Fact]
        public void FinishForStaleAttempt_IsIgnored()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(10000);
                a.Send(new PlayerFinished { Attempt = 3, RaceTimeMs = 10000 });
                Assert.Equal(0, b.Conn.Count<OpponentFinished>());
            }
        }

        [Fact]
        public void CheatedFinish_DisqualifiesThePlayer()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(10000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 10000, Cheated = true });

                Assert.Equal(MatchOutcome.Loss, a.Result.Outcome);
                Assert.Equal(MatchOutcome.Win, b.Result.Outcome);
                Assert.Contains("disqualified", b.Result.Reason);
            }
        }

        [Fact]
        public void Forfeit_AfterStart_GivesOpponentTheWin()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Send(new PlayerForfeit());
                Assert.Equal(MatchOutcome.Loss, a.Result.Outcome);
                Assert.Equal(MatchOutcome.Win, b.Result.Outcome);
                Assert.True(a.Result.NewRating < a.Result.OldRating);
            }
        }

        [Fact]
        public void Forfeit_BeforeStart_AbortsWithoutRatingChange()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                a.Send(new PlayerForfeit());

                Assert.Equal(MatchOutcome.Aborted, a.Result.Outcome);
                Assert.Equal(MatchOutcome.Aborted, b.Result.Outcome);
                Assert.Equal(1000, a.Result.NewRating);
                Assert.Equal(1000, b.Result.NewRating);
            }
        }

        [Fact]
        public void Disconnect_DuringRace_PausesTheClockForTheOpponent()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(20000);
                a.Disconnect();

                MatchPaused paused = b.Last<MatchPaused>();
                Assert.Equal("Alpha", paused.AwayName);
                Assert.Equal(60, paused.SecondsLeft);
                Assert.Equal(20000, paused.RaceTimeMs);

                // While paused the clock stands still, so a split claiming more than the game's timer can show for
                // the 20 s that were played (three times as much, on a shortcut) is refused.
                h.Advance(30000);
                b.Send(new SplitReached { Attempt = 1, Index = 1, Level = "S_Air_01", RaceTimeMs = 70000 });
                a.Reconnect();
                Assert.Equal(0, a.Conn.Count<OpponentSplit>());
                b.Send(new SplitReached { Attempt = 1, Index = 1, Level = "S_Air_01", RaceTimeMs = 45000 });
                Assert.Equal(45000, a.Last<OpponentSplit>().RaceTimeMs);
            }
        }

        [Fact]
        public void Reconnect_WithinGrace_ResumesWhereTheClockStopped()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(20000);
                b.Send(new SplitReached { Attempt = 1, Index = 3, Level = "S_Air_01", Region = "silos", RaceTimeMs = 19000 });

                a.Disconnect();
                h.Advance(40000);
                a.Reconnect();

                MatchResumed resumed = b.Last<MatchResumed>();
                Assert.Equal(h.Now + h.Options.ResumeCountdownMs, resumed.ResumeServerTimeMs);
                // At the resume moment the clock reads exactly what it read when it stopped.
                Assert.Equal(20000, resumed.ResumeServerTimeMs - resumed.GoServerTimeMs);

                Assert.Equal(a.Match.MatchId, b.Match.MatchId);
                Assert.Equal(20000, a.Match.ResumeElapsedMs);
                Assert.Equal(resumed.GoServerTimeMs, a.Last<MatchStart>().GoServerTimeMs);
                Assert.Equal(resumed.GoServerTimeMs, a.Last<MatchResumed>().GoServerTimeMs);
                Assert.Equal(3, a.Last<OpponentSplit>().Index);

                h.Advance(120000);
                Assert.Equal(1, h.Core.ActiveMatchCount);
            }
        }

        [Fact]
        public void Disconnect_BeyondGrace_ForfeitsTheMatch()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Disconnect();

                h.Advance(59000);
                Assert.Equal(0, b.Conn.Count<MatchResult>());
                h.Advance(2000);
                Assert.Equal(MatchOutcome.Win, b.Result.Outcome);
                Assert.Equal(0, h.Core.ActiveMatchCount);

                a.Reconnect();
                Assert.Equal(MatchOutcome.Loss, a.Result.Outcome);
            }
        }

        [Fact]
        public void GameClosing_PausesLikeADisconnect()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(5000);
                a.Send(new GameStatus { Connected = false });
                Assert.Equal("Alpha", b.Last<MatchPaused>().AwayName);
                Assert.Equal(1, a.Conn.Count<MatchPaused>());

                h.Advance(10000);
                a.Send(new GameStatus { Connected = true });
                Assert.Equal(1, b.Conn.Count<MatchResumed>());
                Assert.Equal(5000, b.Last<MatchResumed>().ResumeServerTimeMs - b.Last<MatchResumed>().GoServerTimeMs);
            }
        }

        [Fact]
        public void SigningInWithoutTheGame_KeepsTheMatchPaused()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Disconnect();
                a.Conn = new FakeConnection();
                var hello = a.Hello();
                hello.GameConnected = false;
                a.Send(hello);
                Assert.Equal(0, b.Conn.Count<MatchResumed>());
                Assert.Equal("Alpha", a.Last<MatchPaused>().AwayName);
            }
        }

        [Fact]
        public void PauseDuringCountdown_MovesGoLater()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                a.Ready();
                b.Ready();
                long go = b.Last<MatchStart>().GoServerTimeMs;
                h.Advance(2000);
                a.Disconnect();
                Assert.Equal(-3000, b.Last<MatchPaused>().RaceTimeMs);
                h.Advance(10000);
                a.Reconnect();
                MatchResumed resumed = b.Last<MatchResumed>();
                Assert.Equal(go + 10000 + h.Options.ResumeCountdownMs, resumed.GoServerTimeMs);

                // The race must not have started while it was paused.
                h.Now = resumed.GoServerTimeMs - 1;
                b.Send(new PlayerState { Attempt = 1 });
                Assert.Equal(0, a.Conn.Count<OpponentState>());
            }
        }

        [Fact]
        public void FinishedPlayer_LeavingDoesNotPauseOrForfeit()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(30000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 30000 });
                a.Disconnect();
                Assert.Equal(0, b.Conn.Count<MatchPaused>());
                h.Advance(2000);
                Assert.Equal(MatchOutcome.Loss, b.Result.Outcome);
                a.Reconnect();
                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
            }
        }

        [Fact]
        public void UnfinishedPlayerAway_HoldsTheSettleWindow()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(30000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 30000 });
                b.Disconnect();
                h.Advance(20000);
                Assert.Equal(0, a.Conn.Count<MatchResult>());

                // Back in time, and the rest of the settle window still applies.
                b.Reconnect();
                h.Advance(h.Options.ResumeCountdownMs + 300);
                b.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 30200 });
                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                Assert.Equal(30200, a.Result.OpponentTimeMs);
            }
        }

        [Fact]
        public void PausedTime_DoesNotCountTowardTheTimeLimit()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(500000);
                a.Disconnect();
                h.Advance(50000);
                a.Reconnect();
                h.Advance(h.Options.ResumeCountdownMs + 90000);
                Assert.Equal(1, h.Core.ActiveMatchCount);
                h.Advance(15000);
                Assert.Equal(0, h.Core.ActiveMatchCount);
            }
        }

        [Fact]
        public void BothDisconnected_AbortsWithoutRatingChange()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Disconnect();
                b.Disconnect();
                h.Advance(61000);
                Assert.Equal(0, h.Core.ActiveMatchCount);
                a.Reconnect();
                Assert.Equal(MatchOutcome.Aborted, a.Result.Outcome);
                Assert.Equal(1000, h.Store.Find(a.Id).Rating);
            }
        }

        [Fact]
        public void PreparingTimeout_AbortsTheMatch()
        {
            using (var h = new Harness())
            {
                var a = h.Login("Alpha");
                var b = h.Login("Bravo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                a.Ready();
                h.Advance(121000);
                Assert.Equal(MatchOutcome.Aborted, a.Result.Outcome);
                Assert.Equal(MatchOutcome.Aborted, b.Result.Outcome);
            }
        }

        [Fact]
        public void TimeLimit_AbortsTheMatch()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Send(new PlayerState());
                h.Advance(601000);
                Assert.Equal(MatchOutcome.Aborted, a.Result.Outcome);
                Assert.Contains("Time limit", a.Result.Reason);
            }
        }

        [Fact]
        public void PlayerCanQueueAgain_AfterTheMatchEnds()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Send(new PlayerForfeit());
                a.Send(new QueueJoin());
                Assert.True(a.Last<QueueStatus>().Queued);
            }
        }

        [Fact]
        public void Ratings_ArePersistedAcrossStoreReloads()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Send(new PlayerForfeit());
                h.Store.Flush();

                using (var db = new ServerDatabase(h.Options.DataDirectory, null, null))
                {
                    var reloaded = new PlayerStore(db);
                    Assert.Equal(2, reloaded.Count);
                    Assert.Equal(968, reloaded.Find(a.Id).Rating);
                    Assert.Equal(1032, reloaded.Find(b.Id).Rating);
                    Assert.Equal(1, reloaded.Find(b.Id).Wins);
                }
            }
        }
    }

    public class EloAndNameTests
    {
        [Fact]
        public void Elo_UpsetMovesMoreThanExpectedWin()
        {
            int low = 1000, high = 1400;
            Elo.Update(ref low, ref high, 1.0);
            Assert.True(low - 1000 > 24);
            Assert.True(1400 - high > 24);

            int a = 1400, b = 1000;
            Elo.Update(ref a, ref b, 1.0);
            Assert.True(a - 1400 < 8);
        }

        [Fact]
        public void Elo_NeverDropsBelowFloor()
        {
            int a = Elo.MinRating, b = 2000;
            Elo.Update(ref a, ref b, 0.0);
            Assert.Equal(Elo.MinRating, a);
        }

        [Theory]
        [InlineData("  Hello   World ", "Hello World")]
        [InlineData("A", null)]
        [InlineData("<b>x</b>", "bxb")]
        [InlineData("Игрок_1", "Игрок_1")]
        [InlineData("evil\u202Ename", "evilname")]
        public void CleanName_NormalizesNames(string raw, string expected)
        {
            Assert.Equal(expected, NameSanitizer.CleanName(raw));
        }

        [Fact]
        public void CleanName_TruncatesLongNames()
        {
            Assert.Equal(ProtocolConstants.MaxNameLength, NameSanitizer.CleanName(new string('z', 80)).Length);
        }

        [Fact]
        public void CleanLabel_ReplacesControlCharacters()
        {
            Assert.Equal("a?b?", NameSanitizer.CleanLabel("a\nb\u0001"));
        }
    }
}
