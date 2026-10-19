using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WKRace.Protocol;
using WKRace.Server;
using Xunit;

namespace WKRace.Tests
{
    internal sealed class WireClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly List<Message> _backlog = new List<Message>();
        public Guid Id = Guid.NewGuid();
        public byte[] Token = new byte[ProtocolConstants.TokenLength];
        public string Name;

        public WireClient(int port, string name)
        {
            Name = name;
            new Random(name.GetHashCode()).NextBytes(Token);
            _tcp = new TcpClient { NoDelay = true };
            _tcp.Connect(IPAddress.Loopback, port);
            _stream = _tcp.GetStream();
        }

        public void Send(Message m)
        {
            FrameIO.WriteFrame(_stream, MessageCodec.Encode(m));
        }

        public void SendRaw(byte[] bytes)
        {
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }

        public void Hello()
        {
            Send(new ClientHello { PlayerId = Id, Token = Token, Name = Name, ClientVersion = "test" });
        }

        // Reads messages until one of type T arrives; others are kept for later calls.
        public T Expect<T>(int timeoutMs = 5000) where T : Message
        {
            for (int i = 0; i < _backlog.Count; i++)
            {
                if (_backlog[i] is T)
                {
                    var found = (T)_backlog[i];
                    _backlog.RemoveAt(i);
                    return found;
                }
            }
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) throw new TimeoutException("Timed out waiting for " + typeof(T).Name);
                _stream.ReadTimeout = remaining;
                byte[] frame = FrameIO.ReadFrame(_stream);
                if (frame == null) throw new EndOfStreamException("Connection closed while waiting for " + typeof(T).Name);
                Message m = MessageCodec.Decode(frame);
                if (m is T) return (T)m;
                _backlog.Add(m);
            }
        }

        public bool WaitForClose(int timeoutMs = 5000)
        {
            _stream.ReadTimeout = timeoutMs;
            try
            {
                while (FrameIO.ReadFrame(_stream) != null) { }
                return true;
            }
            catch (IOException e)
            {
                return !(e.InnerException is SocketException se && se.SocketErrorCode == SocketError.TimedOut);
            }
        }

        public void Dispose()
        {
            _tcp.Close();
        }
    }

    public class TcpServerTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "wkrace-tcp-" + Guid.NewGuid().ToString("N"));
        private readonly RaceServer _server;

        public TcpServerTests()
        {
            _server = new RaceServer(new ServerOptions { Port = 0, DataDirectory = _dir, CountdownMs = 150, FinishSettleMs = 100, MaxConnectionsPerIp = 20, RankedRules = MatchRules.CuratedSeeds, VoicePort = 0 }, null);
            _server.Start();
        }

        public void Dispose()
        {
            _server.StopAsync().GetAwaiter().GetResult();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        [Fact]
        public void TwoClients_QueueRaceAndGetResults()
        {
            using (var a = new WireClient(_server.Port, "Alpha"))
            using (var b = new WireClient(_server.Port, "Bravo"))
            {
                a.Hello();
                b.Hello();
                Assert.Equal("Alpha", a.Expect<ServerWelcome>().Name);
                Assert.Equal("Bravo", b.Expect<ServerWelcome>().Name);

                a.Send(new QueueJoin());
                b.Send(new QueueJoin());
                var foundA = a.Expect<MatchFound>();
                var foundB = b.Expect<MatchFound>();
                Assert.Equal(foundA.MatchId, foundB.MatchId);
                Assert.Equal(foundA.Seed, foundB.Seed);

                a.Send(new MatchReady { MatchId = foundA.MatchId, WorldHash = 99 });
                b.Send(new MatchReady { MatchId = foundB.MatchId, WorldHash = 99 });
                a.Send(new PlayerReady { MatchId = foundA.MatchId });
                b.Send(new PlayerReady { MatchId = foundB.MatchId });
                var startA = a.Expect<MatchStart>();
                Assert.Equal(startA.GoServerTimeMs, b.Expect<MatchStart>().GoServerTimeMs);

                long wait = startA.GoServerTimeMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (wait > 0) Thread.Sleep((int)wait + 50);

                a.Send(new PlayerState { Attempt = 1, RaceTimeMs = 40, X = 1, Y = 2, Z = 3 });
                Assert.Equal(2f, b.Expect<OpponentState>().Y);

                a.Send(new SplitReached { Attempt = 1, Index = 0, Level = "M1_Intro_01", Region = "silos", RaceTimeMs = 30 });
                Assert.Equal("M1_Intro_01", b.Expect<OpponentSplit>().Level);

                a.Send(new PlayerFinished { Attempt = 1, RaceTimeMs = 100 });
                Assert.True(b.Expect<OpponentFinished>().RaceTimeMs >= 0);

                var resultA = a.Expect<MatchResult>();
                var resultB = b.Expect<MatchResult>();
                Assert.Equal(MatchOutcome.Win, resultA.Outcome);
                Assert.Equal(MatchOutcome.Loss, resultB.Outcome);
                Assert.True(resultA.NewRating > resultA.OldRating);
            }
        }

        [Fact]
        public void MessageBeforeHello_IsRejectedAndClosed()
        {
            using (var c = new WireClient(_server.Port, "Early"))
            {
                c.Send(new QueueJoin());
                Assert.Equal(ErrorCode.ProtocolViolation, c.Expect<ErrorMessage>().Code);
                Assert.True(c.WaitForClose());
            }
        }

        [Fact]
        public void OversizedFrame_ClosesTheConnection()
        {
            using (var c = new WireClient(_server.Port, "Big"))
            {
                c.SendRaw(new byte[] { 0xFF, 0xFF, 1, 2, 3 });
                Assert.True(c.WaitForClose());
            }
        }

        [Fact]
        public void GarbagePayload_ClosesTheConnection()
        {
            using (var c = new WireClient(_server.Port, "Junk"))
            {
                c.SendRaw(new byte[] { 3, 0, 200, 1, 2 });
                Assert.Equal(ErrorCode.ProtocolViolation, c.Expect<ErrorMessage>().Code);
                Assert.True(c.WaitForClose());
            }
        }

        [Fact]
        public void FloodingMessages_TriggersRateLimit()
        {
            using (var c = new WireClient(_server.Port, "Flood"))
            {
                c.Hello();
                c.Expect<ServerWelcome>();
                var ping = FrameIO.Pack(MessageCodec.Encode(new Ping { ClientTimeMs = 1 }));
                var burst = new byte[ping.Length * 600];
                for (int i = 0; i < 600; i++) Buffer.BlockCopy(ping, 0, burst, i * ping.Length, ping.Length);
                try { c.SendRaw(burst); } catch (IOException) { }
                Assert.Equal(ErrorCode.RateLimited, c.Expect<ErrorMessage>().Code);
            }
        }

        [Fact]
        public void Reconnecting_ReplacesTheOldConnection()
        {
            using (var first = new WireClient(_server.Port, "Alpha"))
            {
                first.Hello();
                first.Expect<ServerWelcome>();
                using (var second = new WireClient(_server.Port, "Alpha"))
                {
                    second.Id = first.Id;
                    second.Token = first.Token;
                    second.Hello();
                    second.Expect<ServerWelcome>();
                    Assert.True(first.WaitForClose());
                }
            }
        }
    }
}
