using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using WKRace.Protocol;

namespace WKRace.Client
{
    using Message = WKRace.Protocol.Message;

    internal enum LinkState
    {
        Disconnected,
        Connecting,
        Connected,
    }

    // What WKSR's logic talks to: the match server (ServerConnection), or in STEAM LOBBIES the lobby host inside the
    // player's own game (LocalLink) or another player's host through Steam (SteamHostLink).
    internal interface IServerLink
    {
        // Both events fire on background threads.
        event Action<Message> Received;
        event Action<LinkState, string> StateChanged;
        void Start();
        void Stop();
        void Send(Message message);
    }

    // One TCP session to the match server at a time, reconnecting with backoff until stopped. The same job as the WKSR
    // program's connection, on plain threads (the game's runtime has no channels).
    internal sealed class ServerConnection : IServerLink
    {
        private const int PingIntervalMs = 2000;
        // The server answers every ping, so this much silence means the link is dead even if the socket has not noticed.
        private const int SilenceTimeoutMs = 8000;
        private const int ConnectTimeoutMs = 8000;
        private const int MaxQueuedFrames = 1024;

        private readonly string _host;
        private readonly int _port;
        private readonly bool _tls;
        private readonly Func<ClientHello> _hello;
        private volatile bool _stop;
        private volatile BlockingCollection<byte[]> _outbound;
        private long _lastReceiveMs;
        private volatile bool _silent;
        private volatile TcpClient _current;

        public ServerConnection(string host, int port, bool tls, Func<ClientHello> hello)
        {
            _host = host;
            _port = port;
            _tls = tls;
            _hello = hello;
        }

        // Both events fire on background threads.
        public event Action<Message> Received;
        public event Action<LinkState, string> StateChanged;

        public void Start()
        {
            new Thread(Run) { IsBackground = true, Name = "WKSR server link" }.Start();
        }

        public void Stop()
        {
            _stop = true;
            TcpClient tcp = _current;
            if (tcp != null) { try { tcp.Close(); } catch (Exception) { } }
        }

        public void Send(Message message)
        {
            BlockingCollection<byte[]> queue = _outbound;
            if (queue == null || queue.IsAddingCompleted) return;
            byte[] frame = FrameIO.Pack(MessageCodec.Encode(message));
            try
            {
                // A full queue means the connection has stalled: the oldest frame gives way to the newest.
                while (!queue.TryAdd(frame))
                {
                    byte[] dropped;
                    if (!queue.TryTake(out dropped)) break;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        private void Run()
        {
            int failures = 0;
            while (!_stop)
            {
                string reason = "";
                bool wasConnected = false;
                Stage stage = Stage.Connect;
                TcpClient tcp = null;
                try
                {
                    Raise(LinkState.Connecting, "");
                    tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                    _current = tcp;
                    IAsyncResult pending = tcp.BeginConnect(_host, _port, null, null);
                    if (!pending.AsyncWaitHandle.WaitOne(ConnectTimeoutMs)) throw new TimeoutException();
                    tcp.EndConnect(pending);
                    Stream stream = tcp.GetStream();
                    if (_tls)
                    {
                        stage = Stage.Secure;
                        tcp.ReceiveTimeout = ConnectTimeoutMs;
                        tcp.SendTimeout = ConnectTimeoutMs;
                        var ssl = new SslStream(stream, false);
                        ssl.AuthenticateAsClient(_host, null, SslProtocols.Tls12, false);
                        tcp.ReceiveTimeout = 0;
                        stream = ssl;
                    }
                    stage = Stage.Session;

                    var outbound = new BlockingCollection<byte[]>(MaxQueuedFrames);
                    _outbound = outbound;
                    Interlocked.Exchange(ref _lastReceiveMs, NowMs());
                    _silent = false;
                    Send(_hello());
                    wasConnected = true;
                    Raise(LinkState.Connected, "");

                    TcpClient session = tcp;
                    var writer = new Thread(() => WriteLoop(stream, outbound, session)) { IsBackground = true, Name = "WKSR server writer" };
                    var pinger = new Thread(() => PingLoop(outbound, session)) { IsBackground = true, Name = "WKSR server ping" };
                    writer.Start();
                    pinger.Start();
                    try
                    {
                        ReadLoop(stream);
                        reason = "The server closed the connection.";
                    }
                    finally
                    {
                        _outbound = null;
                        outbound.CompleteAdding();
                    }
                }
                catch (Exception e)
                {
                    if (_stop) break;
                    reason = _silent ? "The server stopped responding." : Describe(e, stage);
                }
                finally
                {
                    _current = null;
                    if (tcp != null) { try { tcp.Close(); } catch (Exception) { } }
                }
                if (_stop) break;

                Raise(LinkState.Disconnected, reason);
                // Retry quickly: a player only has the reconnect grace period to get back before forfeiting.
                failures = wasConnected ? 0 : failures + 1;
                int delay = Math.Min(5000, 1000 << Math.Min(failures, 3));
                for (int waited = 0; waited < delay && !_stop; waited += 100) Thread.Sleep(100);
            }
            Raise(LinkState.Disconnected, "");
        }

        private void ReadLoop(Stream stream)
        {
            while (!_stop)
            {
                byte[] frame = FrameIO.ReadFrame(stream);
                if (frame == null) return;
                Interlocked.Exchange(ref _lastReceiveMs, NowMs());
                Message message = MessageCodec.Decode(frame);
                Action<Message> handler = Received;
                if (handler != null) handler(message);
            }
        }

        private static void WriteLoop(Stream stream, BlockingCollection<byte[]> outbound, TcpClient session)
        {
            try
            {
                foreach (byte[] frame in outbound.GetConsumingEnumerable())
                {
                    stream.Write(frame, 0, frame.Length);
                    stream.Flush();
                }
            }
            catch (Exception)
            {
                // The read loop reports the failure.
                try { session.Close(); } catch (Exception) { }
            }
        }

        // Pings keep the clock estimate fresh and double as a heartbeat; a silent server ends the session.
        private void PingLoop(BlockingCollection<byte[]> outbound, TcpClient session)
        {
            while (!_stop && !outbound.IsAddingCompleted)
            {
                if (NowMs() - Interlocked.Read(ref _lastReceiveMs) > SilenceTimeoutMs)
                {
                    _silent = true;
                    try { session.Close(); } catch (Exception) { }
                    return;
                }
                if (ReferenceEquals(_outbound, outbound)) Send(new Ping { ClientTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                for (int waited = 0; waited < PingIntervalMs && !_stop && !outbound.IsAddingCompleted; waited += 100) Thread.Sleep(100);
            }
        }

        private enum Stage { Connect, Secure, Session }

        // Plain words for the usual failures. Socket errors are never shown as they are: their text includes the
        // server's IP address.
        private static string Describe(Exception e, Stage stage)
        {
            Exception inner = e;
            while (inner.InnerException != null && !(inner is SocketException) && !(inner is AuthenticationException)) inner = inner.InnerException;

            if (inner is AuthenticationException)
                return "The secure connection failed: the server's certificate could not be verified. Check that your PC's date and time are right.";
            var socket = inner as SocketException;
            if (socket != null)
            {
                switch (socket.SocketErrorCode)
                {
                    case SocketError.HostNotFound:
                    case SocketError.TryAgain:
                    case SocketError.NoData:
                        return "Could not find the match server. Check your internet connection.";
                    case SocketError.ConnectionRefused:
                        return "The match server is not accepting connections right now. It may be restarting; retrying.";
                    case SocketError.TimedOut:
                    case SocketError.HostUnreachable:
                    case SocketError.NetworkUnreachable:
                    case SocketError.NetworkDown:
                        return "Could not reach the match server. Check your internet connection or firewall.";
                    case SocketError.ConnectionReset:
                    case SocketError.ConnectionAborted:
                        return stage == Stage.Session ? "The connection to the server was cut." : "The match server closed the connection while connecting.";
                    default:
                        return "Network error (" + socket.SocketErrorCode + ").";
                }
            }
            if (inner is TimeoutException)
            {
                return stage == Stage.Connect ? "The match server did not answer. Check your internet connection or firewall."
                    : stage == Stage.Secure ? "The match server did not complete the secure handshake. It may not be set up for secure connections."
                    : "The match server stopped answering.";
            }
            if (stage == Stage.Secure)
                return "The secure handshake with the match server failed.";
            if (inner is IOException || inner is ObjectDisposedException)
                return "The connection to the server was cut.";
            return inner.Message;
        }

        private void Raise(LinkState state, string reason)
        {
            Action<LinkState, string> handler = StateChanged;
            if (handler != null) handler(state, reason);
        }
    }
}
