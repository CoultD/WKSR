using System;
using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;
using WKRace.Server;
using Xunit;

namespace WKRace.Tests
{
    // The leaderboards: one row per player, the replays of the fastest, and the checks on what gets on them.
    public class BoardTests
    {
        // A whole ranked solo run that finishes with this time on the game's timer. The player climbs all the way.
        private static void Solo(Harness h, TestClient c, int gameMs, string build = "steam-1|mod-0.9.1")
        {
            c.Send(new SoloStart { GameBuild = build });
            c.Loaded();
            for (int i = 1; i <= 10; i++)
            {
                h.Advance(gameMs / 10);
                c.Send(new PlayerState { Attempt = 1, RaceTimeMs = gameMs / 10 * i, X = i, Y = i * 20, Z = 0 });
            }
            c.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = gameMs, RealTimeMs = gameMs });
        }

        private static BoardPage Page(Harness h, TestClient c, BoardKind kind, int offset = 0, int count = 12, string search = "", bool findMe = false)
        {
            h.Advance(200);
            c.Send(new BoardRequest { Board = kind, Offset = offset, Count = (byte)count, Search = search, FindMe = findMe });
            return c.Last<BoardPage>();
        }

        [Fact]
        public void SoloFinish_PutsThePlayersBestOnTheBoard_AndAFasterRunReplacesIt()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                Solo(h, a, 300000);
                BoardNote note = a.Last<BoardNote>();
                Assert.Equal(BoardKind.SoloTime, note.Board);
                Assert.True(note.PersonalBest);
                Assert.Equal(1, note.Rank);
                Assert.Equal("", note.Problem);

                BoardPage page = Page(h, a, BoardKind.SoloTime);
                Assert.Equal(1, page.Total);
                Assert.Equal(1, page.YourRank);
                BoardRow row = page.Rows.Single();
                Assert.Equal("Alpha", row.Name);
                Assert.Equal(300000, row.Value);
                Assert.Equal("0.9.1", row.Detail);   // the version the time was set on
                Assert.True(row.You);
                Assert.True(row.Replay);

                // A slower run changes nothing; a faster one replaces the row (and says which version it was).
                Solo(h, a, 400000);
                Assert.False(a.Last<BoardNote>().PersonalBest);
                Assert.Equal(300000, a.Last<BoardNote>().BestMs);
                Solo(h, a, 200000, "steam-1|mod-0.9.2");
                Assert.True(a.Last<BoardNote>().PersonalBest);
                page = Page(h, a, BoardKind.SoloTime);
                Assert.Equal(1, page.Total);
                Assert.Equal(200000, page.Rows.Single().Value);
                Assert.Equal("0.9.2", page.Rows.Single().Detail);
            }
        }

        [Fact]
        public void Board_IsOrderedByTime_WithSearchPagesAndFindMe()
        {
            using (var h = new Harness())
            {
                var players = new List<TestClient>();
                string[] names = { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf" };
                for (int i = 0; i < names.Length; i++)
                {
                    TestClient c = h.Login(names[i]);
                    players.Add(c);
                    Solo(h, c, 500000 - i * 40000);   // Golf is fastest, Alpha slowest
                }

                BoardPage top = Page(h, players[0], BoardKind.SoloTime, 0, 3);
                Assert.Equal(7, top.Total);
                Assert.Equal(new[] { "Golf", "Foxtrot", "Echo" }, top.Rows.Select(r => r.Name));
                Assert.Equal(new[] { 1, 2, 3 }, top.Rows.Select(r => r.Rank));
                Assert.Equal(7, top.YourRank);

                BoardPage second = Page(h, players[0], BoardKind.SoloTime, 3, 3);
                Assert.Equal(new[] { "Delta", "Charlie", "Bravo" }, second.Rows.Select(r => r.Name));

                // FIND ME: the page the player's own row is on.
                BoardPage mine = Page(h, players[0], BoardKind.SoloTime, 0, 3, "", true);
                Assert.Equal(6, mine.Offset);
                Assert.Equal("Alpha", mine.Rows.Single().Name);
                Assert.True(mine.Rows.Single().You);

                // A search keeps the real ranks.
                BoardPage found = Page(h, players[0], BoardKind.SoloTime, 0, 3, "HA");
                Assert.Equal(2, found.Total);
                Assert.Contains(found.Rows, r => r.Name == "Charlie" && r.Rank == 5);
                Assert.Contains(found.Rows, r => r.Name == "Alpha" && r.Rank == 7 && r.You);

                // An offset past the end gives the last page.
                BoardPage last = Page(h, players[0], BoardKind.SoloTime, 500, 3);
                Assert.Equal(6, last.Offset);
            }
        }

        [Fact]
        public void TheFastestRuns_KeepAReplay_AnyoneCanDownload()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                Solo(h, a, 300000);

                h.Advance(4000);
                b.Send(new ReplayRequest { Board = BoardKind.SoloTime, Rank = 1 });
                List<ReplayChunk> pieces = b.Conn.Sent.OfType<ReplayChunk>().ToList();
                Assert.NotEmpty(pieces);
                Assert.Equal(pieces[0].Count, pieces.Count);
                Assert.Equal(Enumerable.Range(0, pieces.Count), pieces.Select(p => (int)p.Index));

                RunRecording replay = RunRecording.FromPacked(pieces.SelectMany(p => p.Bytes).ToArray());
                Assert.True(replay.Finished);
                Assert.Equal(300000, replay.EndMs);
                Assert.Equal("Alpha", replay.Player);
                Assert.Equal("0.9.1", replay.Version);
                Assert.Equal(RunKind.RankedSolo, replay.Kind);
                Assert.Equal(h.Options.RankedRules, replay.Rules);
                Assert.Equal(a.Match.Seed, replay.Seed);
                Assert.Contains(replay.Events, e => e.Value is OpponentState);
                Assert.IsType<OpponentFinished>(replay.Events.Last().Value);

                // A rank without a replay answers with one empty piece.
                h.Advance(4000);
                int before = b.Conn.Count<ReplayChunk>();
                b.Send(new ReplayRequest { Board = BoardKind.SoloTime, Rank = 5 });
                Assert.Equal(before + 1, b.Conn.Count<ReplayChunk>());
                Assert.Equal(0, b.Last<ReplayChunk>().Count);
            }
        }

        [Fact]
        public void OnlyTheTwentyFastest_KeepTheirReplay()
        {
            using (var h = new Harness())
            {
                var players = new List<TestClient>();
                for (int i = 0; i < 21; i++)
                {
                    TestClient c = h.Login("Climber" + i);
                    players.Add(c);
                    Solo(h, c, 400000 + i * 10000);
                }
                BoardPage tail = Page(h, players[0], BoardKind.SoloTime, 15, 15);
                Assert.True(tail.Rows.Single(r => r.Rank == 20).Replay);
                Assert.False(tail.Rows.Single(r => r.Rank == 21).Replay);   // 21st when it was set: never kept

                // A new fastest run pushes the 20th down to 21st: its replay is deleted.
                TestClient fast = h.Login("Rocket");
                Solo(h, fast, 100000);
                tail = Page(h, players[0], BoardKind.SoloTime, 15, 15);
                Assert.Equal("Rocket", Page(h, players[0], BoardKind.SoloTime, 0, 1).Rows.Single().Name);
                Assert.True(tail.Rows.Single(r => r.Rank == 20).Replay);
                Assert.False(tail.Rows.Single(r => r.Rank == 21).Replay);
                Assert.Equal("Climber19", tail.Rows.Single(r => r.Rank == 21).Name);
                Assert.Null(h.Database.LoadBoardReplay((int)BoardKind.SoloTime, players[19].Id));
                Assert.NotNull(h.Database.LoadBoardReplay((int)BoardKind.SoloTime, players[18].Id));
            }
        }

        [Fact]
        public void ATimerThatStandsStillWhileThePlayerMoves_IsFlaggedForTheAdmin_AndTheTimeStands()
        {
            using (var h = new Harness())
            {
                h.Options.TimerCheck = true;
                TestClient a = h.Login("Alpha");
                a.Send(new SoloStart { GameBuild = "steam-1|mod-0.9.1" });
                a.Loaded();
                // Thirty seconds of climbing 5 m a second with the game's timer stuck at 10 s.
                for (int i = 1; i <= 60; i++)
                {
                    h.Advance(500);
                    a.Send(new PlayerState { Attempt = 1, RaceTimeMs = Math.Min(10000, i * 500), X = 0, Y = i * 2.5f, Z = 0 });
                }
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 10500 });

                // The game slows its own timer too (slow-time buffs), so the check is a hint, not a verdict: the
                // player is told nothing, the time is on the board, and the admin gets it to look at.
                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                BoardNote note = a.Last<BoardNote>();
                Assert.Equal("", note.Problem);
                Assert.True(note.PersonalBest);
                Assert.Equal(10500, Page(h, a, BoardKind.SoloTime).Rows.Single().Value);
                Assert.Equal(1, h.Core.FlaggedRunCount);
                string flagged = h.Core.RunAdmin(new[] { "board", "flagged" });
                Assert.Contains("Alpha", flagged);
                Assert.Contains("0:10.50", flagged);

                // If it was not honest, the admin takes it off.
                Assert.Contains("Removed Alpha", h.Core.RunAdmin(new[] { "board", "remove", "solo", "Alpha" }));
                Assert.Equal(0, Page(h, a, BoardKind.SoloTime).Total);
            }
        }

        [Fact]
        public void TheTimerCheckIsOff_UnlessTheServerIsStartedWithIt()
        {
            using (var h = new Harness())
            {
                Assert.False(h.Options.TimerCheck);
                TestClient a = h.Login("Alpha");
                a.Send(new SoloStart { GameBuild = "steam-1|mod-0.9.3" });
                a.Loaded();
                for (int i = 1; i <= 60; i++)
                {
                    h.Advance(500);
                    a.Send(new PlayerState { Attempt = 1, RaceTimeMs = Math.Min(10000, i * 500), X = 0, Y = i * 2.5f, Z = 0 });
                }
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 10500 });

                Assert.Equal(10500, Page(h, a, BoardKind.SoloTime).Rows.Single().Value);
                Assert.Equal(0, h.Core.FlaggedRunCount);
            }
        }

        [Fact]
        public void TheAdmin_CanPutATimeOnABoardByHand()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                Solo(h, b, 550000);
                Assert.Contains("No run has been flagged", h.Core.RunAdmin(new[] { "board", "flagged" }));

                Assert.Contains("Alpha now has 9:00.75", h.Core.RunAdmin(new[] { "board", "set", "solo", "Alpha", "9:00.75", "9063666" }));
                BoardPage page = Page(h, a, BoardKind.SoloTime);
                Assert.Equal(new[] { "Alpha", "Bravo" }, page.Rows.Select(r => r.Name));
                Assert.Equal(540750, page.Rows[0].Value);
                Assert.False(page.Rows[0].Replay);

                // A set time replaces the one they had, and a faster run of theirs still beats it.
                Assert.Contains("was 9:00.75", h.Core.RunAdmin(new[] { "board", "set", "solo", "Alpha", "1:00:00" }));
                Assert.Equal(3600000, Page(h, a, BoardKind.SoloTime).Rows.Single(r => r.Name == "Alpha").Value);
                Solo(h, a, 500000);
                Assert.Equal(500000, Page(h, a, BoardKind.SoloTime).Rows[0].Value);

                Assert.Contains("is not a time", h.Core.RunAdmin(new[] { "board", "set", "solo", "Alpha", "fast" }));
                Assert.Contains("no times to set", h.Core.RunAdmin(new[] { "board", "set", "rating", "Alpha", "1:00" }));
            }
        }

        [Fact]
        public void APause_IsFine_TheTimerStopsAndSoDoesThePlayer()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                a.Send(new SoloStart { GameBuild = "steam-1|mod-0.9.1" });
                a.Loaded();
                for (int i = 1; i <= 20; i++)
                {
                    h.Advance(500);
                    a.Send(new PlayerState { Attempt = 1, RaceTimeMs = i * 500, X = 0, Y = i * 2.5f, Z = 0 });
                }
                // Two minutes paused: the same report over and over.
                for (int i = 0; i < 240; i++)
                {
                    h.Advance(500);
                    a.Send(new PlayerState { Attempt = 1, RaceTimeMs = 10000, X = 0, Y = 50, Z = 0 });
                }
                for (int i = 21; i <= 40; i++)
                {
                    h.Advance(500);
                    a.Send(new PlayerState { Attempt = 1, RaceTimeMs = i * 500, X = 0, Y = i * 2.5f, Z = 0 });
                }
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 20000 });

                BoardNote note = a.Last<BoardNote>();
                Assert.Equal("", note.Problem);
                Assert.True(note.PersonalBest);
                Assert.Equal(20000, Page(h, a, BoardKind.SoloTime).Rows.Single().Value);
            }
        }

        [Fact]
        public void ARanked1v1Finish_GoesOnThe1v1TimeBoard_WhoeverWins()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(60000);
                a.Send(new PlayerState { Attempt = 1, RaceTimeMs = 60000, X = 1, Y = 500, Z = 0 });
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 60000, RealTimeMs = 60000 });
                h.Advance(300);
                b.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 60200, RealTimeMs = 60200 });   // inside the settle window

                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                BoardPage page = Page(h, a, BoardKind.DuelTime);
                Assert.Equal(new[] { "Alpha", "Bravo" }, page.Rows.Select(r => r.Name));
                Assert.Equal(new[] { 60000, 60200 }, page.Rows.Select(r => r.Value));
                Assert.Equal(0, Page(h, a, BoardKind.SoloTime).Total);
            }
        }

        [Fact]
        public void RatingBoard_ListsPlayersPastTheirProvisionalMatches_WhoPlayedRankedSinceItExists()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                // Before the match is over nobody is listed, however many matches they have behind them.
                h.Store.Find(a.Id).Wins = 30;
                Assert.Equal(0, Page(h, a, BoardKind.Rating).Total);

                h.Advance(30000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 30000, RealTimeMs = 30000 });
                h.Advance(5000);
                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);

                // Alpha (31 wins now) is listed; Bravo has played one match and is still provisional.
                h.Advance(6000);
                BoardPage page = Page(h, b, BoardKind.Rating);
                BoardRow row = page.Rows.Single();
                Assert.Equal("Alpha", row.Name);
                Assert.Equal(a.Result.NewRating, row.Value);
                Assert.Equal("31W 0L", row.Detail);
                Assert.Equal(0, page.YourRank);
            }
        }

        [Fact]
        public void Admin_CanTakeARunOff_AndADeletedPlayerLeavesTheBoards()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                TestClient b = h.Login("Bravo");
                Solo(h, a, 300000);
                Solo(h, b, 400000);

                Assert.Contains("Alpha", h.Core.RunAdmin(new[] { "board", "list", "solo" }));
                Assert.Contains("Removed Alpha", h.Core.RunAdmin(new[] { "board", "remove", "solo", "Alpha" }));
                BoardPage page = Page(h, b, BoardKind.SoloTime);
                Assert.Equal(new[] { "Bravo" }, page.Rows.Select(r => r.Name));

                // The removed player's next finish takes the place again.
                Solo(h, a, 350000);
                Assert.Equal(new[] { "Alpha", "Bravo" }, Page(h, b, BoardKind.SoloTime).Rows.Select(r => r.Name));

                a.Disconnect();
                h.Core.RunAdmin(new[] { "delete", "Alpha" });
                Assert.Equal(new[] { "Bravo" }, Page(h, b, BoardKind.SoloTime).Rows.Select(r => r.Name));
                Assert.Null(h.Database.LoadBoardReplay((int)BoardKind.SoloTime, a.Id));
            }
        }

        [Fact]
        public void TheCopyOfAPlayersData_HasTheirLeaderboardTimes_AndTheNoticeNamesTheBoards()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                Solo(h, a, 300000);
                // The result comes first, then what it did on the board.
                Assert.True(a.Conn.Sent.FindLastIndex(m => m is MatchResult) < a.Conn.Sent.FindLastIndex(m => m is BoardNote));

                string export = h.Core.RunAdmin(new[] { "export", "Alpha" });
                Assert.Contains("\"leaderboardTimes\"", export);
                Assert.Contains("ranked solo, fastest time", export);
                Assert.Contains("\"timeMs\": 300000", export);
                Assert.Contains("\"replayKept\": true", export);

                string notice = PrivacyNotice.Html("ops", true);
                Assert.Contains("Leaderboard times", notice);
                Assert.Contains("What other players can see", notice);
            }
        }

        [Fact]
        public void Boards_SurviveARestartOfTheServer()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                Solo(h, a, 300000);

                var again = new ServerCore(h.Options, h.Store, new MatchLog(h.Database), () => h.Now, null);
                var conn = new FakeConnection();
                again.OnMessage(conn, new TestClient(h, "Bravo").Hello());
                again.OnMessage(conn, new BoardRequest { Board = BoardKind.SoloTime });
                BoardRow row = conn.Last<BoardPage>().Rows.Single();
                Assert.Equal("Alpha", row.Name);
                Assert.Equal(300000, row.Value);
                Assert.True(row.Replay);
            }
        }

        [Fact]
        public void TimerWatch_ToleratesBurstsAndShortHitches()
        {
            // Reports held up for three seconds and then arriving at once: the timer did not stand still.
            var watch = new TimerWatch();
            long real = 0;
            int game = 0;
            for (int i = 0; i < 200; i++)
            {
                real += i % 45 == 44 ? 3000 : (i % 45 > 0 && i % 45 < 45 ? 66 : 0);
                game += i % 45 == 44 ? 3000 : 66;
                watch.Report(real, game, 0, game / 1000f * 4f, 0);
            }
            Assert.False(watch.Failed);
            Assert.Equal(0, watch.MovedWhileStoppedMs);

            // A level loading for two seconds with the player standing where they were.
            watch = new TimerWatch();
            real = 0; game = 0;
            float y = 0;
            for (int i = 0; i < 300; i++)
            {
                bool loading = i >= 100 && i < 130;
                real += 66;
                if (!loading) { game += 66; y += 0.3f; }
                watch.Report(real, game, 0, y, 0);
            }
            Assert.False(watch.Failed);
        }

        [Fact]
        public void TimerWatch_AGapInTheReports_IsNotMovingOnAStoppedTimer()
        {
            // The game stops reporting for a minute with its timer stopped (frozen, suspended or minimised), then
            // the player climbs on. What they climb afterwards must not be measured against where they stood
            // before the gap. (This kept an honest run off the board in 0.9.1.)
            var watch = new TimerWatch();
            long real = 0;
            int game = 0;
            float y = 0;
            for (int i = 0; i < 100; i++) { real += 66; game += 66; y += 0.3f; watch.Report(real, game, 0, y, 0); }
            real += 60000;
            for (int i = 0; i < 200; i++) { real += 66; game += 66; y += 0.3f; watch.Report(real, game, 0, y, 0); }
            Assert.Equal(0, watch.MovedWhileStoppedMs);
            Assert.False(watch.Failed);

            // Reports that keep coming while the timer stands still and the player climbs are still caught.
            watch = new TimerWatch();
            real = 0; game = 0; y = 0;
            for (int i = 0; i < 400; i++) { real += 66; if (i < 100) game += 66; y += 0.3f; watch.Report(real, game, 0, y, 0); }
            Assert.True(watch.Failed);
        }
    }
}
