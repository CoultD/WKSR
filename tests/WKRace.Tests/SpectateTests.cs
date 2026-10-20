using System.Linq;
using WKRace.Protocol;
using WKRace.Server;
using Xunit;

namespace WKRace.Tests
{
    // Spectating matches and ranked solo runs.
    public class SpectateTests
    {
        private static TestClient LoginAs(Harness h, string name, string clientVersion = "0.9.6")
        {
            var c = new TestClient(h, name);
            ClientHello hello = c.Hello();
            hello.ClientVersion = clientVersion;
            c.Send(hello);
            return c;
        }

        private static uint TagOf(Harness h, TestClient asker, string name)
        {
            h.Advance(1000);
            asker.Send(new OnlineListRequest());
            return asker.Last<OnlineList>().Entries.Single(e => e.Name == name).Tag;
        }

        // A ranked 1v1 under way.
        private static void Ranked(Harness h, out TestClient a, out TestClient b)
        {
            a = LoginAs(h, "Alpha");
            b = LoginAs(h, "Bravo");
            a.Send(new QueueJoin());
            b.Send(new QueueJoin());
            a.Ready();
            b.Ready();
            h.Advance(h.Options.CountdownMs);
        }

        [Fact]
        public void ARankedMatch_CanBeWatched_AndTheSpectatorGetsWhatOpponentsGet()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                Ranked(h, out a, out b);
                h.Advance(30000);
                a.Send(new SplitReached { Attempt = 1, Index = 1, Level = "M1_Silos_Storage_02", Region = "Silos", RaceTimeMs = 20000 });
                var s = LoginAs(h, "Watcher");
                Assert.True(s.Last<ServerFeatures>().Spectate);
                s.Send(new SpectateRequest { Tag = TagOf(h, s, "Bravo") });

                SpectateStart start = s.Last<SpectateStart>();
                Assert.Equal(a.Match.MatchId, start.MatchId);
                Assert.Equal(a.Match.Seed, start.Seed);
                Assert.Equal(b.Match.YourRacer, start.WatchRacer);
                Assert.True(start.GoServerTimeMs > 0);
                Assert.False(start.Own);
                // The roster (as nobody's), and the split so far.
                Assert.Equal(ServerCore.SpectatorRacer, s.Last<MatchRoster>().YourRacer);
                Assert.Equal(1, s.Last<OpponentSplit>().Index);

                a.Send(new PlayerState { Attempt = 1, RaceTimeMs = 21000, X = 1, Y = 2, Z = 3 });
                OpponentState state = s.Last<OpponentState>();
                Assert.Equal(a.Match.YourRacer, state.Racer);
                Assert.Equal(2f, state.Y);

                // The players and the spectator hear how many watch.
                Assert.Equal(1, a.Last<SpectatorCount>().Count);
                Assert.Equal(1, b.Last<SpectatorCount>().Count);

                // Chat in the Match room reaches the spectator, and theirs reaches the players.
                a.Send(new ChatSend { Room = ChatRooms.Match, Text = "gl" });
                Assert.Equal("gl", s.Last<ChatMessage>().Text);
                s.Send(new ChatSend { Room = ChatRooms.Match, Text = "go go" });
                Assert.Equal("go go", b.Last<ChatMessage>().Text);

                // The match ends: so does the watching.
                h.Advance(200000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 200000, RealTimeMs = 200000 });
                b.Send(new PlayerForfeit());
                Assert.Contains("over", s.Last<SpectateEnd>().Reason);
            }
        }

        [Fact]
        public void StoppingAndLeaving_UpdateTheCount()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                Ranked(h, out a, out b);
                var s = LoginAs(h, "Watcher");
                var t = LoginAs(h, "Second");
                s.Send(new SpectateRequest { Tag = TagOf(h, s, "Alpha") });
                t.Send(new SpectateRequest { Tag = TagOf(h, t, "Alpha") });
                Assert.Equal(2, a.Last<SpectatorCount>().Count);
                s.Send(new SpectateStop());
                Assert.Equal(1, a.Last<SpectatorCount>().Count);
                t.Disconnect();
                Assert.Equal(0, a.Last<SpectatorCount>().Count);
                a.Send(new PlayerState { Attempt = 1, RaceTimeMs = 1000 });
                Assert.Equal(0, s.Conn.Count<OpponentState>());
            }
        }

        [Fact]
        public void APrivateLobbysMatch_CannotBeWatched_APublicOnesCan()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                var b = LoginAs(h, "Bravo");
                var s = LoginAs(h, "Watcher");
                a.Send(new LobbyCreate { Mode = MatchMode.Duel, Rules = MatchRules.CuratedSeeds });
                uint lobby = a.Last<LobbyCreated>().LobbyId;
                b.Send(new LobbyJoin { LobbyId = lobby });
                a.Ready();
                b.Ready();
                h.Advance(h.Options.CountdownMs);

                h.Advance(1000);
                s.Send(new OnlineListRequest());
                Assert.False(s.Last<OnlineList>().Entries.Single(e => e.Name == "Alpha").Watchable);
                s.Send(new SpectateRequest { Tag = TagOf(h, s, "Alpha") });
                Assert.Equal(0, s.Conn.Count<SpectateStart>());
                Assert.NotNull(s.Last<SpectateEnd>());

                a.Send(new LobbyVisibility { Public = true });
                s.Send(new SpectateRequest { LobbyId = lobby });
                Assert.Equal(a.Match.MatchId, s.Last<SpectateStart>().MatchId);
            }
        }

        [Fact]
        public void APlayerOutOfAParty_WatchesTheRestOfIt()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                var b = LoginAs(h, "Bravo");
                var c = LoginAs(h, "Charlie");
                a.Send(new LobbyCreate { Mode = MatchMode.Party, Rules = MatchRules.CuratedSeeds });
                uint lobby = a.Last<LobbyCreated>().LobbyId;
                b.Send(new LobbyJoin { LobbyId = lobby });
                c.Send(new LobbyJoin { LobbyId = lobby });
                a.Send(new LobbyStart());
                a.Ready(); b.Ready(); c.Ready();
                h.Advance(h.Options.CountdownMs + 10000);
                b.Send(new SplitReached { Attempt = 1, Index = 1, Level = "L1", Region = "R", RaceTimeMs = 5000 });

                // Still running: not yet.
                a.Send(new SpectateRequest { OwnMatch = a.Match.MatchId });
                Assert.Equal(0, a.Conn.Count<SpectateStart>());

                a.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 10000 });
                a.Send(new SpectateRequest { OwnMatch = a.Match.MatchId });
                SpectateStart start = a.Last<SpectateStart>();
                Assert.True(start.Own);
                // The leader: Bravo, who reached a level.
                Assert.Equal(b.Match.YourRacer, start.WatchRacer);
                Assert.Equal(1, b.Last<SpectatorCount>().Count);
                // Already a player of the match: nothing is sent twice.
                int before = a.Conn.Count<OpponentState>();
                b.Send(new PlayerState { Attempt = 1, RaceTimeMs = 11000 });
                Assert.Equal(before + 1, a.Conn.Count<OpponentState>());
            }
        }

        [Fact]
        public void TheLoserOfA1v1_WatchesTheWinnerKeepRunning()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                Ranked(h, out a, out b);
                System.Guid id = a.Match.MatchId;
                h.Advance(30000);
                b.Send(new PlayerDied { Attempt = 1, RaceTimeMs = 30000 });
                Assert.True(a.Result.KeepRunning);
                b.Send(new SpectateRequest { OwnMatch = id });
                Assert.Equal(a.Match.YourRacer, b.Last<SpectateStart>().WatchRacer);
                a.Send(new PlayerState { Attempt = 1, RaceTimeMs = 31000, Y = 9 });
                Assert.Equal(9f, b.Last<OpponentState>().Y);
                // The winner ends the run: the watching ends.
                a.Send(new EndRun { MatchId = id });
                Assert.NotNull(b.Last<SpectateEnd>());
            }
        }

        [Fact]
        public void ARankedSoloRun_CanBeWatched_AndARestartIsANewWorld()
        {
            using (var h = new Harness())
            {
                var a = LoginAs(h, "Alpha");
                a.Send(new SoloStart());
                MatchFound first = a.Match;
                a.Loaded();
                var s = LoginAs(h, "Watcher");
                s.Send(new SpectateRequest { Tag = TagOf(h, s, "Alpha") });
                SpectateStart start = s.Last<SpectateStart>();
                Assert.Equal(first.Seed, start.Seed);
                Assert.Equal(MatchMode.Solo, start.Mode);
                Assert.Equal(1, a.Last<SpectatorCount>().Count);

                a.Send(new PlayerState { Attempt = 1, RaceTimeMs = 2000, Y = 5 });
                Assert.Equal(5f, s.Last<OpponentState>().Y);

                h.Advance(5000);
                a.Send(new PracticeRestart { MatchId = first.MatchId });
                Assert.NotEqual(first.MatchId, s.Last<SpectateStart>().MatchId);
                Assert.Equal(a.Match.Seed, s.Last<SpectateStart>().Seed);
            }
        }

        [Fact]
        public void APlayerInAMatch_CannotWatchAnother_AndWatchersStopWhenTheirOwnStarts()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                Ranked(h, out a, out b);
                a.Send(new SpectateRequest { Tag = TagOf(h, a, "Bravo") });
                Assert.Equal(0, a.Conn.Count<SpectateStart>());

                var s = LoginAs(h, "Watcher");
                var t = LoginAs(h, "Other");
                s.Send(new SpectateRequest { Tag = TagOf(h, s, "Alpha") });
                s.Send(new QueueJoin());
                t.Send(new QueueJoin());
                Assert.Equal("Your own match is starting.", s.Last<SpectateEnd>().Reason);
                Assert.Equal(0, a.Last<SpectatorCount>().Count);
            }
        }
    }
}
