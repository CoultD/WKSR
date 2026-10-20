using System;
using System.Linq;
using WKRace.Protocol;
using WKRace.Server;
using Xunit;

namespace WKRace.Tests
{
    // The online player list, profiles and the run counts behind them.
    public class PlayerListTests
    {
        private static TestClient LoginAs(Harness h, string name, string clientVersion = "0.9.6")
        {
            var c = new TestClient(h, name);
            ClientHello hello = c.Hello();
            hello.ClientVersion = clientVersion;
            c.Send(hello);
            return c;
        }

        private static OnlineList List(Harness h, TestClient c, ushort offset = 0)
        {
            h.Advance(1000);
            c.Send(new OnlineListRequest { Offset = offset });
            return c.Last<OnlineList>();
        }

        private static OnlineEntry Entry(OnlineList list, string name)
        {
            return list.Entries.Single(e => e.Name == name);
        }

        private static Profile ProfileOf(Harness h, TestClient asker, string name)
        {
            uint tag = Entry(List(h, asker), name).Tag;
            h.Advance(1000);
            asker.Send(new ProfileRequest { Tag = tag });
            return asker.Last<Profile>();
        }

        [Fact]
        public void TheFeature_IsSaidOnlyToClientsThatKnowIt()
        {
            using (var h = new Harness())
            {
                Assert.True(LoginAs(h, "New").Last<ServerFeatures>().PlayerList);
                Assert.Equal(0, LoginAs(h, "Old", "0.9.5").Conn.Count<ServerFeatures>());
            }
        }

        [Fact]
        public void TheList_ShowsWhatEveryoneIsDoing_WithTheAskerFirst()
        {
            using (var h = new Harness())
            {
                var zed = LoginAs(h, "Zed");
                var a = LoginAs(h, "Alpha");
                var b = LoginAs(h, "Bravo");
                var c = LoginAs(h, "Charlie");
                var d = LoginAs(h, "Delta");
                var e = LoginAs(h, "Echo");
                a.Send(new QueueJoin());
                b.Send(new QueueJoin());                 // Alpha and Bravo are matched
                c.Send(new QueueJoin());                 // Charlie searches
                d.Send(new LobbyCreate { Mode = MatchMode.Party, Rules = MatchRules.CuratedSeeds });
                d.Send(new LobbyVisibility { Public = true });
                e.Send(new SoloStart());

                OnlineList list = List(h, zed);
                Assert.Equal(6, list.Total);
                Assert.Equal(new[] { "Zed", "Alpha", "Bravo", "Charlie", "Delta", "Echo" }, list.Entries.Select(x => x.Name));
                Assert.True(list.Entries[0].You);
                Assert.Equal(PlayerActivity.Online, Entry(list, "Zed").Activity);
                Assert.Equal(PlayerActivity.RankedInGame, Entry(list, "Alpha").Activity);
                Assert.Equal(PlayerActivity.RankedSearching, Entry(list, "Charlie").Activity);
                Assert.Equal(PlayerActivity.Lobby, Entry(list, "Delta").Activity);
                Assert.Equal(PlayerActivity.RankedSolo, Entry(list, "Echo").Activity);
                // Delta's lobby is public and open: it can be joined from the list.
                Assert.Equal(d.Last<LobbyCreated>().LobbyId, Entry(list, "Delta").LobbyId);
                Assert.Equal(0u, Entry(list, "Alpha").LobbyId);
                // The match has not started yet: nothing to watch.
                Assert.False(Entry(list, "Alpha").Watchable);
                a.Ready();
                b.Ready();
                h.Advance(h.Options.CountdownMs);
                Assert.True(Entry(List(h, zed), "Alpha").Watchable);
            }
        }

        [Fact]
        public void ALobbyInAMatch_ShowsAsSuch_AndCannotBeJoined()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                var b = LoginAs(h, "Bravo");
                var c = LoginAs(h, "Charlie");
                a.Send(new LobbyCreate { Mode = MatchMode.Duel, Rules = MatchRules.CuratedSeeds });
                a.Send(new LobbyVisibility { Public = true });
                b.Send(new LobbyJoin { LobbyId = a.Last<LobbyCreated>().LobbyId });
                OnlineEntry entry = Entry(List(h, c), "Alpha");
                Assert.Equal(PlayerActivity.LobbyInGame, entry.Activity);
                Assert.Equal(0u, entry.LobbyId);
            }
        }

        [Fact]
        public void ALongList_ComesInPages()
        {
            using (var h = new Harness())
            {
                var me = LoginAs(h, "Aaron");
                for (int i = 0; i < 25; i++) LoginAs(h, "Player " + i.ToString("00"));
                OnlineList first = List(h, me);
                Assert.Equal(26, first.Total);
                Assert.Equal(OnlineList.PageSize, first.Entries.Count);
                OnlineList second = List(h, me, OnlineList.PageSize);
                Assert.Equal(6, second.Entries.Count);
                Assert.Equal("Player 24", second.Entries.Last().Name);
            }
        }

        [Fact]
        public void Profiles_CountFinishedRuns_RankedAndUnrankedApart_AndRestarts()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(60000);
                a.Send(new AttemptStarted { Attempt = 2, RaceTimeMs = 60000 });       // a restart
                h.Advance(240000);
                a.Send(new PlayerFinished { Attempt = 2, RaceTimeMs = 240000, RealTimeMs = 300000 });
                h.Advance(h.Options.FinishSettleMs + 1);

                // An unranked solo run on Alpha's own PC: finished, then restarted (reported by the client).
                a.Send(new RunNote { Kind = RunNote.Finished });
                a.Send(new RunNote { Kind = RunNote.Finished });                      // too soon: not a run
                a.Send(new RunNote { Kind = RunNote.Restarted });
                // A segment run is not a finished climb.
                h.Advance(70000);
                a.Send(new RunNote { Kind = RunNote.Finished, Segment = Segments.Silos });

                var c = LoginAs(h, "Charlie");
                Profile p = ProfileOf(h, c, "Alpha");
                Assert.True(p.Found);
                Assert.Equal(1, p.RankedRuns);
                Assert.Equal(1, p.UnrankedRuns);
                Assert.Equal(2, p.Resets);
                Assert.Equal(1, p.Wins);
                Assert.Equal(0, p.Losses);
                // The 1v1 time board and the rating board.
                Assert.Equal(240000, p.Places.Single(x => x.Board == BoardKind.DuelTime).Value);
                Assert.Equal(1, p.Places.Single(x => x.Board == BoardKind.Rating).Rank);

                // Bravo did not finish: a match lost without finishing is no finished run.
                Profile q = ProfileOf(h, c, "Bravo");
                Assert.Equal(0, q.RankedRuns);
                Assert.Equal(1, q.Losses);
            }
        }

        [Fact]
        public void ALobbyFinish_IsUnranked_AndRankedSoloRestartsCount()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                var b = LoginAs(h, "Bravo");
                a.Send(new LobbyCreate { Mode = MatchMode.Duel, Rules = MatchRules.CuratedSeeds });
                b.Send(new LobbyJoin { LobbyId = a.Last<LobbyCreated>().LobbyId });
                a.Ready();
                b.Ready();
                h.Advance(h.Options.CountdownMs + 200000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 200000, RealTimeMs = 200000 });
                Assert.Equal(1, h.Store.Find(a.Id).UnrankedRuns);
                Assert.Equal(0, h.Store.Find(a.Id).RankedRuns);

                var c = LoginAs(h, "Charlie");
                c.Send(new SoloStart());
                MatchFound first = c.Match;
                c.Loaded();
                h.Advance(5000);
                c.Send(new PracticeRestart { MatchId = first.MatchId });
                Assert.Equal(1, h.Store.Find(c.Id).Resets);
            }
        }

        [Fact]
        public void AProfileOfSomeoneWhoLeft_IsNotFound()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                var b = LoginAs(h, "Bravo");
                uint tag = Entry(List(h, a), "Bravo").Tag;
                b.Disconnect();
                h.Advance(1000);
                a.Send(new ProfileRequest { Tag = tag });
                Assert.False(a.Last<Profile>().Found);
            }
        }

        [Fact]
        public void TheCounts_AreKeptInTheDatabase()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                a.Send(new RunNote { Kind = RunNote.Restarted });
                h.Advance(2000);
                a.Send(new RunNote { Kind = RunNote.Restarted });
                PlayerRecord back = new PlayerStore(h.Database).Find(a.Id);
                Assert.Equal(2, back.Resets);
            }
        }

        [Fact]
        public void TheMessages_SurviveTheWire()
        {
            var list = new OnlineList { Total = 30, Offset = 20 };
            list.Entries.Add(new OnlineEntry { Tag = 7, Name = "Alpha", Activity = PlayerActivity.LobbyInGame, LobbyId = 42, Watchable = true, You = true });
            var back = (OnlineList)MessageCodec.Decode(MessageCodec.Encode(list));
            Assert.Equal(30, back.Total);
            Assert.Equal(20, back.Offset);
            OnlineEntry e = back.Entries.Single();
            Assert.Equal(7u, e.Tag);
            Assert.Equal(PlayerActivity.LobbyInGame, e.Activity);
            Assert.Equal(42u, e.LobbyId);
            Assert.True(e.Watchable && e.You);

            var profile = new Profile { Tag = 7, Found = true, Name = "Alpha", Rating = 1100, Wins = 4, Losses = 2, RankedRuns = 5, UnrankedRuns = 9, Resets = 80 };
            profile.Places.Add(new ProfilePlace { Board = BoardKind.SoloTime, Rank = 3, Value = 400000 });
            var p = (Profile)MessageCodec.Decode(MessageCodec.Encode(profile));
            Assert.Equal(80, p.Resets);
            Assert.Equal(400000, p.Places.Single().Value);

            Assert.True(((ServerFeatures)MessageCodec.Decode(MessageCodec.Encode(new ServerFeatures { LobbyBrowser = true, PlayerList = true }))).PlayerList);
            Assert.Equal(RunNote.Restarted, ((RunNote)MessageCodec.Decode(MessageCodec.Encode(new RunNote { Kind = RunNote.Restarted }))).Kind);
        }
    }
}
