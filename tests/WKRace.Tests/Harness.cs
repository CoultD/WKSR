using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WKRace.Protocol;
using WKRace.Server;

namespace WKRace.Tests
{
    internal sealed class FakeConnection : IConnection
    {
        public readonly List<Message> Sent = new List<Message>();
        public bool Closed;
        public string CloseReason;
        public bool Authenticated;

        private static int _next;
        // Each fake connection gets its own address unless a test sets one (per-address limits).
        public string Address = "203.0.113." + System.Threading.Interlocked.Increment(ref _next);

        public string RemoteAddress { get { return Address; } }
        public void Send(Message message) { Sent.Add(message); }
        public void Close(string reason) { Closed = true; CloseReason = reason; }
        public void MarkAuthenticated() { Authenticated = true; }

        public T Last<T>() where T : Message { return Sent.OfType<T>().Last(); }
        public int Count<T>() where T : Message { return Sent.OfType<T>().Count(); }
    }

    internal sealed class TestClient
    {
        private readonly Harness _h;
        public Guid Id = Guid.NewGuid();
        public byte[] Token;
        public string Name;
        public FakeConnection Conn = new FakeConnection();

        public TestClient(Harness h, string name)
        {
            _h = h;
            Name = name;
            Token = new byte[ProtocolConstants.TokenLength];
            new Random(name.GetHashCode()).NextBytes(Token);
        }

        public ClientHello Hello(int version = ProtocolConstants.ProtocolVersion)
        {
            return new ClientHello { ProtocolVersion = version, PlayerId = Id, Token = Token, Name = Name, ClientVersion = "test" };
        }

        public void Login() { Send(Hello()); }
        public void Send(Message m) { _h.Core.OnMessage(Conn, m); }
        public void Disconnect() { _h.Core.OnDisconnected(Conn); }

        public void Reconnect()
        {
            Conn = new FakeConnection { Address = Conn.Address };
            Login();
        }

        public T Last<T>() where T : Message { return Conn.Last<T>(); }
        public MatchFound Match { get { return Conn.Last<MatchFound>(); } }
        public MatchResult Result { get { return Conn.Last<MatchResult>(); } }

        // The world loaded and the player pressed ready.
        public void Ready(uint hash = 1)
        {
            Loaded(hash);
            Send(new PlayerReady { MatchId = Match.MatchId });
        }

        // The world loaded (the ready check still waits for the player).
        public void Loaded(uint hash = 1)
        {
            Send(new MatchReady { MatchId = Match.MatchId, WorldHash = hash, LevelCount = 31 });
        }

        public MatchRoster Roster { get { return Conn.Last<MatchRoster>(); } }
        public LobbyState Lobby { get { return Conn.Last<LobbyState>(); } }
    }

    internal sealed class Harness : IDisposable
    {
        private readonly string _dir;
        public readonly ServerOptions Options;
        public readonly ServerDatabase Database;
        public readonly PlayerStore Store;
        public readonly ServerCore Core;
        public long Now = 1000000000;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "wkrace-tests-" + Guid.NewGuid().ToString("N"));
            Options = new ServerOptions
            {
                DataDirectory = _dir,
                CountdownMs = 5000,
                ReconnectGraceSeconds = 60,
                ResumeCountdownMs = 3000,
                FinishSettleMs = 1500,
                ReadyTimeoutSeconds = 120,
                MatchTimeLimitSeconds = 600,
                // Most tests are not about the ban phase; BanTests turn it back on.
                RankedRules = MatchRules.CuratedSeeds,
            };
            Database = new ServerDatabase(_dir, null, null);
            Store = new PlayerStore(Database);
            Core = new ServerCore(Options, Store, new MatchLog(Database), () => Now, null);
        }

        public TestClient Login(string name)
        {
            var c = new TestClient(this, name);
            c.Login();
            return c;
        }

        public void Advance(long ms)
        {
            Now += ms;
            Core.Tick();
        }

        // Two players queued, matched, ready, and past the countdown.
        public void StartMatch(out TestClient a, out TestClient b, uint hashA = 1, uint hashB = 1)
        {
            a = Login("Alpha");
            b = Login("Bravo");
            a.Send(new QueueJoin());
            b.Send(new QueueJoin());
            a.Ready(hashA);
            b.Ready(hashB);
            Advance(Options.CountdownMs);
        }

        public void Dispose()
        {
            Database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
