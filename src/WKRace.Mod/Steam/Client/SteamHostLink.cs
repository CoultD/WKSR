using System;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;
using Steamworks.Data;
using WKRace.Client;
using WKRace.Protocol;

namespace WKRace.Lobbies
{
    using Message = WKRace.Protocol.Message;

    // STEAM LOBBIES: the connection to another player's lobby host, through Steam's relay. Like the match server's
    // connection it reconnects by itself until stopped, so a player whose connection drops gets back into their match
    // (the host keeps their place for a while, as the server does).
    internal sealed class SteamHostLink : IServerLink
    {
        private const int PingIntervalMs = 2000;
        private const int ConnectTimeoutMs = 15000;

        private readonly ulong _hostId;
        private readonly Func<ClientHello> _hello;
        private volatile bool _stop;
        private volatile HostConnection _current;

        public SteamHostLink(ulong hostId, Func<ClientHello> hello)
        {
            _hostId = hostId;
            _hello = hello;
        }

        public event Action<Message> Received;
        public event Action<LinkState, string> StateChanged;

        public void Start()
        {
            new Thread(Run) { IsBackground = true, Name = "WKSR Steam lobby link" }.Start();
        }

        public void Stop()
        {
            _stop = true;
        }

        public void Send(Message message)
        {
            HostConnection current = _current;
            if (current == null || !current.Connected) return;
            byte[] data = MessageCodec.Encode(message);
            try { current.Connection.SendMessage(data, SendType.Reliable); }
            catch (Exception) { }
        }

        private void Run()
        {
            int failures = 0;
            while (!_stop)
            {
                Raise(LinkState.Connecting, "");
                bool wasConnected = false;
                string reason;
                HostConnection conn = null;
                try
                {
                    conn = SteamNetworkingSockets.ConnectRelay<HostConnection>(_hostId, LobbyHost.VirtualPort);
                    conn.Link = this;
                    _current = conn;
                    long started = Environment.TickCount;
                    while (!_stop && !conn.Connected && !conn.Ended && Environment.TickCount - started < ConnectTimeoutMs)
                    {
                        conn.Receive();
                        Thread.Sleep(10);
                    }
                    if (conn.Connected && !_stop)
                    {
                        wasConnected = true;
                        Send(_hello());
                        Raise(LinkState.Connected, "");
                        long nextPing = 0;
                        while (!_stop && !conn.Ended)
                        {
                            conn.Receive();
                            if (Environment.TickCount - nextPing >= 0)
                            {
                                nextPing = Environment.TickCount + PingIntervalMs;
                                Send(new Ping { ClientTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                            }
                            Thread.Sleep(10);
                        }
                        reason = conn.Ended ? "The connection to the lobby's host was lost." : "";
                    }
                    else reason = conn.Ended ? "Could not reach the lobby's host through Steam." : _stop ? "" : "The lobby's host did not answer.";
                }
                catch (Exception e)
                {
                    reason = "Steam could not connect to the lobby's host: " + e.Message;
                }
                _current = null;
                if (conn != null) { try { conn.Close(); } catch (Exception) { } }
                if (_stop) break;

                Raise(LinkState.Disconnected, reason);
                failures = wasConnected ? 0 : failures + 1;
                int delay = Math.Min(5000, 1000 << Math.Min(failures, 3));
                for (int waited = 0; waited < delay && !_stop; waited += 100) Thread.Sleep(100);
            }
            Raise(LinkState.Disconnected, "");
        }

        internal void Deliver(byte[] data)
        {
            Message message;
            try { message = MessageCodec.Decode(data); }
            catch (ProtocolException) { return; }
            Received?.Invoke(message);
        }

        private void Raise(LinkState state, string reason)
        {
            StateChanged?.Invoke(state, reason);
        }
    }

    // One relay connection to the host. Its state changes come in on the game's thread, its messages on the link's.
    internal sealed class HostConnection : ConnectionManager
    {
        public SteamHostLink Link;
        private volatile bool _ended;

        public bool Ended { get { return _ended; } }

        public override void OnDisconnected(ConnectionInfo info)
        {
            _ended = true;
            base.OnDisconnected(info);
        }

        public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            if (size <= 0 || size > ProtocolConstants.MaxFrameSize) return;
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, size);
            Link?.Deliver(bytes);
        }
    }
}
