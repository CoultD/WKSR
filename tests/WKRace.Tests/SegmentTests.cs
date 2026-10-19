using System;
using System.IO;
using System.Linq;
using WKRace.Client;
using WKRace.Protocol;
using WKRace.Server;
using Xunit;

namespace WKRace.Tests
{
    // Segment runs: a ranked solo run that ends where its segment does, with a leaderboard per segment.
    public class SegmentTests
    {
        private static void Run(Harness h, TestClient c, int gameMs, byte segment)
        {
            c.Send(new SoloStart { GameBuild = "steam-1|mod-0.9.5", Segment = segment });
            c.Loaded();
            for (int i = 1; i <= 10; i++)
            {
                h.Advance(gameMs / 10);
                c.Send(new PlayerState { Attempt = 1, RaceTimeMs = gameMs / 10 * i, X = i, Y = i * 20, Z = 0 });
            }
            c.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = gameMs, RealTimeMs = gameMs });
        }

        private static BoardPage Page(Harness h, TestClient c, BoardKind kind)
        {
            h.Advance(200);
            c.Send(new BoardRequest { Board = kind });
            return c.Last<BoardPage>();
        }

        [Fact]
        public void ASegmentRun_IsARankedSoloRunOfThatSegment_WithoutAnArtifactBanPhase()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                a.Send(new SoloStart { GameBuild = "steam-1|mod-0.9.5", Segment = Segments.Silos });
                MatchFound found = a.Match;
                Assert.Equal(Segments.Silos, found.Segment);
                Assert.Equal(MatchMode.Solo, found.Mode);
                Assert.True(found.Ranked);
                // The Silos end before the artifact room: the ranked modifiers, less the bans.
                Assert.False(found.ArtifactBans);
                Assert.Equal(h.Options.RankedRules & ~MatchRules.ArtifactBans, found.Rules);
            }
        }

        [Fact]
        public void ASegmentRunsTime_GoesOnItsSegmentsBoard_AndNotOnTheSoloBoard()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                BoardKind board = Segments.Board(Segments.Silos);
                Run(h, a, 240000, Segments.Silos);
                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
                Assert.Equal(240000, a.Result.YourTimeMs);
                BoardNote note = a.Last<BoardNote>();
                Assert.Equal(board, note.Board);
                Assert.True(note.PersonalBest);
                Assert.Equal(1, note.Rank);

                BoardPage page = Page(h, a, board);
                Assert.Equal(board, page.Board);
                Assert.Equal(240000, page.Rows.Single().Value);
                Assert.True(page.Rows.Single().Replay);
                Assert.Equal(0, Page(h, a, BoardKind.SoloTime).Total);

                // A run of the whole climb goes on the solo board, and leaves the segment's board alone.
                Run(h, a, 500000, Segments.None);
                Assert.Equal(BoardKind.SoloTime, a.Last<BoardNote>().Board);
                Assert.Equal(500000, Page(h, a, BoardKind.SoloTime).Rows.Single().Value);
                Assert.Equal(240000, Page(h, a, board).Rows.Single().Value);
            }
        }

        [Fact]
        public void ARestart_StaysInTheSegment()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                a.Send(new SoloStart { GameBuild = "steam-1|mod-0.9.5", Segment = Segments.Silos });
                MatchFound first = a.Match;
                a.Loaded();
                h.Advance(20000);
                a.Send(new PracticeRestart { MatchId = first.MatchId });
                MatchFound second = a.Match;
                Assert.NotEqual(first.MatchId, second.MatchId);
                Assert.Equal(Segments.Silos, second.Segment);
            }
        }

        [Fact]
        public void AnUnknownSegment_IsRefused()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                a.Send(new SoloStart { GameBuild = "steam-1|mod-0.9.5", Segment = 200 });
                Assert.Equal(0, h.Core.SoloRunCount);
                Assert.Equal(0, a.Conn.Count<MatchFound>());
                Assert.Throws<ProtocolException>(() => MessageCodec.Decode(MessageCodec.Encode(new BoardRequest { Board = (BoardKind)99 })));
            }
        }

        [Fact]
        public void AnUnrankedSegmentRun_HasItsSegment_AndItsBestIsKeptApartFromTheWholeClimbs()
        {
            var host = new SoloHost(4242, MatchRules.Ranked, "Alpha", 1000, null, () => 0, m => { }, null, false, null, Segments.Silos);
            Assert.Equal(Segments.Silos, host.Match.Segment);
            Assert.False(host.Match.ArtifactBans);
            Assert.False(host.Match.Ranked);

            string dir = Path.Combine(Path.GetTempPath(), "wkrace-segment-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new PracticeStore(dir);
                RunRecording whole = new RunRecorder().Finish(4242, 900000, true, false);
                RunRecording part = new RunRecorder().Finish(4242, 240000, true, false);
                part.Segment = Segments.Silos;
                Assert.True(store.SaveIfBest(whole));
                Assert.True(store.SaveIfBest(part));
                Assert.Equal(900000, store.Load(4242).EndMs);
                RunRecording back = store.Load(4242, Segments.Silos);
                Assert.Equal(240000, back.EndMs);
                Assert.Equal(Segments.Silos, back.Segment);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
