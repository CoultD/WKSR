using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WKRace.Protocol;
using WKRace.Server;
using Xunit;

namespace WKRace.Tests
{
    public class NameAndRatingRuleTests
    {
        [Fact]
        public void Names_AreUniqueIgnoringCase()
        {
            using (var h = new Harness())
            {
                // A player's name is their Steam name, and those are not unique: a taken name gets a number.
                h.Login("Climber");
                TestClient copy = h.Login("climber");
                Assert.Equal("climber 2", copy.Last<ServerWelcome>().Name);
                Assert.Equal(2, h.Store.Count);

                // The game sends the Steam name again at every sign-in: the number stays the same.
                copy.Reconnect();
                Assert.Equal("climber 2", copy.Last<ServerWelcome>().Name);

                // Renaming to a taken name gets the next number.
                TestClient other = h.Login("Other");
                other.Name = "CLIMBER";
                other.Reconnect();
                Assert.Equal("CLIMBER 3", h.Store.Find(other.Id).Name);

                // A name at full length makes room for its number.
                string full = new string('x', ProtocolConstants.MaxNameLength);
                h.Login(full);
                TestClient second = h.Login(full);
                Assert.Equal(full.Substring(0, full.Length - 2) + " 2", second.Last<ServerWelcome>().Name);
            }
        }

        [Fact]
        public void ProvisionalPlayers_MoveTwiceAsFast()
        {
            int a = 1000, b = 1000;
            Elo.Update(ref a, ref b, 1.0, Elo.ProvisionalK, Elo.K);
            Assert.Equal(1032, a);   // the new player moves 32
            Assert.Equal(984, b);    // the established one only 16
            Assert.True(new PlayerRecord { Wins = 5, Losses = 4 }.Provisional);
            Assert.False(new PlayerRecord { Wins = 5, Losses = 5 }.Provisional);
        }

        [Fact]
        public void ImplausibleFinishes_Disqualify()
        {
            using (var h = new Harness())
            {
                h.Options.MinFinishSeconds = 180;
                h.Options.MinFinishSplits = 15;
                TestClient a, b;
                h.StartMatch(out a, out b);
                h.Advance(60000);
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 60000 });
                Assert.Equal(MatchOutcome.Loss, a.Result.Outcome);
                Assert.Equal(MatchOutcome.Win, b.Result.Outcome);
                Assert.Contains("not plausible", a.Result.Reason);
            }
        }

        [Fact]
        public void PlausibleFinishes_Count()
        {
            using (var h = new Harness())
            {
                h.Options.MinFinishSeconds = 180;
                h.Options.MinFinishSplits = 15;
                TestClient a, b;
                h.StartMatch(out a, out b);
                for (int i = 0; i < 20; i++)
                {
                    h.Advance(12000);
                    a.Send(new SplitReached { Attempt = 1, Index = i, Level = "L" + i, RaceTimeMs = (i + 1) * 12000 });
                }
                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 240000 });
                h.Advance(h.Options.FinishSettleMs + 1);
                Assert.Equal(MatchOutcome.Win, a.Result.Outcome);
            }
        }
    }

    public class AdminCommandTests
    {
        private static string Admin(Harness h, params string[] args)
        {
            return h.Core.RunAdmin(args);
        }

        [Fact]
        public void Players_AndShow_ListWhatIsStored()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Alpha");
                h.Login("Bravo");
                string list = Admin(h, "players");
                Assert.Contains("Alpha", list);
                Assert.Contains("Bravo", list);
                Assert.Contains("2 player(s)", list);
                Assert.Contains(a.Id.ToString(), Admin(h, "show", "alpha"));
                Assert.Contains(a.Id.ToString(), Admin(h, "show", a.Id.ToString("N").Substring(0, 8)));
                Assert.StartsWith("Error:", Admin(h, "show", "nobody"));
                Assert.Contains("Players: 2 stored", Admin(h, "status"));
            }
        }

        [Fact]
        public void Rename_LocksTheName()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Rude");
                Assert.Contains("Renamed", Admin(h, "rename", "Rude", "Polite", "One"));
                Assert.Equal(ErrorCode.Unknown, a.Last<ErrorMessage>().Code);   // reconnects and picks it up

                a.Reconnect();                                                    // the client still sends "Rude"
                Assert.Equal("Polite One", a.Last<ServerWelcome>().Name);
                Assert.Equal("Polite One", h.Store.Find(a.Id).Name);
            }
        }

        [Fact]
        public void Ban_RefusesSignInsUntilUnbanned()
        {
            using (var h = new Harness())
            {
                TestClient a = h.Login("Cheater");
                Admin(h, "ban", "Cheater", "speed", "hacks");
                Assert.Equal(ErrorCode.BadToken, a.Last<ErrorMessage>().Code);

                a.Reconnect();
                Assert.Contains("banned", a.Last<ErrorMessage>().Text);
                Assert.Contains("speed hacks", a.Last<ErrorMessage>().Text);
                Assert.False(a.Conn.Authenticated);

                Admin(h, "unban", "Cheater");
                a.Reconnect();
                Assert.True(a.Conn.Authenticated);
            }
        }

        [Fact]
        public void Ban_DuringARace_EndsItForTheOpponent()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                Admin(h, "ban", "Alpha");
                Assert.Equal(MatchOutcome.Win, b.Result.Outcome);
            }
        }

        [Fact]
        public void Export_HasThePlayerAndTheirMatches_ButNoSecrets()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Send(new PlayerForfeit());
                string json = Admin(h, "export", "Alpha");
                Assert.Contains("\"name\": \"Alpha\"", json);
                Assert.Contains("Bravo", json);                       // the opponent of their match
                Assert.DoesNotContain(h.Store.Find(a.Id).TokenHash, json);
            }
        }

        [Fact]
        public void Delete_ErasesThePlayer_AndAnonymisesTheirMatches()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Send(new PlayerForfeit());
                a.Disconnect();
                Assert.Contains("Deleted", Admin(h, "delete", "Alpha"));
                Assert.Null(h.Store.Find(a.Id));
                MatchEntry m = h.Database.LoadMatches().Single();
                Assert.DoesNotContain(new[] { m.A, m.B }, s => s.Name == "Alpha" || s.Id == a.Id);
                Assert.Contains(new[] { m.A, m.B }, s => s.Name == "deleted player");
                Assert.Contains(new[] { m.A, m.B }, s => s.Name == "Bravo");
            }
        }

        [Fact]
        public void Merge_FoldsOnePlayerIntoAnother()
        {
            using (var h = new Harness())
            {
                TestClient a, b;
                h.StartMatch(out a, out b);
                a.Send(new PlayerForfeit());        // Bravo wins
                TestClient alt = h.Login("BravoAlt");
                h.Store.Find(alt.Id).Wins = 3;
                h.Store.Find(alt.Id).SteamHash = "steam-hash";

                Assert.Contains("Merged", Admin(h, "merge", "BravoAlt", "Bravo"));
                PlayerRecord merged = h.Store.Find(b.Id);
                Assert.Equal(4, merged.Wins);
                Assert.Equal("steam-hash", merged.SteamHash);
                Assert.Null(h.Store.Find(alt.Id));
                Assert.Equal(ErrorCode.BadToken, alt.Last<ErrorMessage>().Code);
            }
        }

        [Fact]
        public void AdminPort_NeedsTheKey()
        {
            var port = new AdminPort(0, "secret-admin-key-123", args => Task.FromResult("ran " + string.Join(" ", args) + "\n"), null);
            using (var cts = new CancellationTokenSource())
            {
                port.Start(cts.Token);
                Assert.Equal("ran players alpha\n", Send(port.Port, "secret-admin-key-123\nplayers\nalpha\n"));
                Assert.StartsWith("Error: wrong admin key", Send(port.Port, "guess\nplayers\n"));
                cts.Cancel();
            }
        }

        private static string Send(int port, string request)
        {
            using (var tcp = new TcpClient())
            {
                tcp.Connect(IPAddress.Loopback, port);
                NetworkStream s = tcp.GetStream();
                byte[] bytes = Encoding.UTF8.GetBytes(request);
                s.Write(bytes, 0, bytes.Length);
                tcp.Client.Shutdown(SocketShutdown.Send);
                return new System.IO.StreamReader(s, Encoding.UTF8).ReadToEnd();
            }
        }
    }
}
