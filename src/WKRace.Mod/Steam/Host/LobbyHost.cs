using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Steamworks;
using Steamworks.Data;
using WKRace.Client;
using WKRace.Protocol;
using WKRace.Server;

namespace WKRace.Lobbies
{
    using Message = WKRace.Protocol.Message;

    // STEAM LOBBIES: the lobby's host runs the match server's core (ServerCore) inside their own game, on a thread of
    // its own so the game's loading hitches never hold a match up. The core is the official server's, unchanged: it
    // talks to the host's own WKSR in memory (LocalLink), to the other players through Steam's relay (SteamPeer: Valve
    // carries the traffic, so nobody sees anyone's IP address), and, for tests only, to the DevTools bot over a local
    // TCP port (Debug/HostTcpPort).
    internal sealed class LobbyHost
    {
        // The relay's "port": both sides of a Steam lobby connection use it.
        public const int VirtualPort = 47;
        private const int TickMs = 250;

        private readonly ServerCore _core;
        private readonly Action<string> _log;
        private readonly BlockingCollection<Action> _work = new BlockingCollection<Action>();
        // Steam connections by their handle (host thread only).
        private readonly Dictionary<uint, SteamPeer> _peers = new Dictionary<uint, SteamPeer>();
        private HostSocket _socket;
        private TcpListener _debug;
        private Thread _thread;
        private volatile bool _stop;
        private string _closeReason;

        public LobbyHost(Action<string> log)
        {
            _log = log ?? (s => { });
            var options = new ServerOptions
            {
                // Only the people in this one lobby sign in, and nothing is stored: no limits on new players.
                MaxPlayers = 0,
                NewPlayersPerIpPerDay = 0,
                LobbyAttemptsPerMinute = 0,
                // The host is in their own lobby for as long as they want it open.
                LobbyTimeoutSeconds = 7 * 24 * 3600,
                VoicePort = 0,
                Reports = false,
            };
            _core = new ServerCore(options, new PlayerStore(), new MatchLog(), NowMs, s => _log("lobby host: " + s));
        }

        public static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // Opens the lobby to other players (Steam relay) and starts the core. Null when it worked, else what went wrong.
        public string Start(int debugTcpPort)
        {
            try
            {
                SteamNetworkingUtils.InitRelayNetworkAccess();
                _socket = SteamNetworkingSockets.CreateRelaySocket<HostSocket>(VirtualPort);
                if (_socket == null) return "Steam could not open the lobby to other players.";
                _socket.Host = this;
            }
            catch (Exception e)
            {
                return "Steam could not open the lobby to other players: " + e.Message;
            }
            if (debugTcpPort > 0)
            {
                try
                {
                    _debug = new TcpListener(IPAddress.Loopback, debugTcpPort);
                    _debug.Start();
                    new Thread(AcceptDebug) { IsBackground = true, Name = "WKSR lobby host test port" }.Start();
                    _log("lobby host: test connections on 127.0.0.1:" + debugTcpPort + " (Debug/HostTcpPort)");
                }
                catch (Exception e)
                {
                    _log("lobby host: no test port " + debugTcpPort + ": " + e.Message);
                    _debug = null;
                }
            }
            _thread = new Thread(Run) { IsBackground = true, Name = "WKSR lobby host" };
            _thread.Start();
            return null;
        }

        // The host's own WKSR.
        public IServerLink ConnectLocal(Func<ClientHello> hello)
        {
            return new LocalLink(this, hello);
        }

        // Closes the lobby (everyone in it is told why) and then the host itself. Returns at once.
        public void Stop(string reason)
        {
            _closeReason = reason ?? "The lobby closed.";
            // In the queue, so it comes before anything sent after it (the host's own WKSR letting go must not hand
            // the lobby to someone else first).
            Post(() => _core.CloseAllLobbies(_closeReason));
            _stop = true;
        }

        internal void Post(Action action)
        {
            try { _work.Add(action); }
            catch (InvalidOperationException) { }
        }

        private void Run()
        {
            long nextTick = NowMs();
            while (!_stop)
            {
                Action action;
                if (_work.TryTake(out action, 5)) RunSafely(action);
                while (_work.TryTake(out action)) RunSafely(action);
                try { _socket?.Receive(); }
                catch (Exception e) { _log("lobby host: receive: " + e.Message); }
                long now = NowMs();
                if (now >= nextTick)
                {
                    nextTick = now + TickMs;
                    RunSafely(_core.Tick);
                }
            }

            // Closing: the lobby's goodbye first (before the host's own WKSR leaving could hand the lobby to someone
            // else), then whatever is still queued, then the connections.
            RunSafely(() => _core.CloseAllLobbies(_closeReason));
            _work.CompleteAdding();
            Action rest;
            while (_work.TryTake(out rest)) RunSafely(rest);
            foreach (SteamPeer peer in new List<SteamPeer>(_peers.Values)) peer.CloseQuietly(true);
            // A listen socket that closes drops its connections at once; give the goodbyes a moment to arrive.
            Thread.Sleep(800);
            try { _socket?.Close(); } catch (Exception) { }
            try { _debug?.Stop(); } catch (Exception) { }
            _log("lobby host: closed");
        }

        private void RunSafely(Action action)
        {
            try { action(); }
            catch (Exception e) { _log("lobby host error: " + e); }
        }

        // ---- the core's side of the connections (host thread) --------------------------------------------------

        internal void Deliver(IConnection conn, Message message)
        {
            _core.OnMessage(conn, message);
        }

        internal void Gone(IConnection conn)
        {
            _core.OnDisconnected(conn);
        }

        // ---- Steam relay (status changes arrive on the game's thread, messages on the host thread) ----------------

        internal void OnPeerConnected(Connection connection, ulong steamId)
        {
            Post(() => PeerFor(connection, steamId));
        }

        internal void OnPeerDisconnected(Connection connection)
        {
            Post(() =>
            {
                SteamPeer peer;
                if (!_peers.TryGetValue(connection.Id, out peer)) return;
                _peers.Remove(connection.Id);
                peer.MarkGone();
                Gone(peer);
            });
        }

        // Host thread. A player's first message can be read before word of their connection has come through.
        private SteamPeer PeerFor(Connection connection, ulong steamId)
        {
            SteamPeer peer;
            if (_peers.TryGetValue(connection.Id, out peer)) return peer;
            peer = new SteamPeer(this, connection, steamId);
            _peers[connection.Id] = peer;
            _log("lobby host: a player connected through Steam");
            return peer;
        }

        internal void OnPeerMessage(Connection connection, ulong steamId, byte[] data)
        {
            if (steamId == 0) return;
            SteamPeer peer = PeerFor(connection, steamId);
            if (peer.IsGone) return;
            Message message;
            try { message = MessageCodec.Decode(data); }
            catch (ProtocolException e)
            {
                _log("lobby host: unreadable message from a player: " + e.Message);
                peer.Close("unreadable message");
                return;
            }
            if (!Allowed(message)) return;
            // Who a Steam player is comes from Steam (the relay proves the account), never from what they claim.
            var hello = message as ClientHello;
            if (hello != null)
            {
                hello.PlayerId = SteamPlayerId(peer.SteamId);
                hello.Token = SteamToken(peer.SteamId);
            }
            Deliver(peer, message);
        }

        // What another player may ask of this host: their lobby's business, not opening lobbies or queues of their own.
        private static bool Allowed(Message message)
        {
            if (message is LobbyCreate || message is QueueJoin || message is SoloStart || message is ModMark || message is SteamLinkStart || message is SteamIdentity) return false;
            var chatJoin = message as ChatJoin;
            return chatJoin == null || chatJoin.Room != ChatRooms.Global;
        }

        internal void ForgetPeer(SteamPeer peer)
        {
            _peers.Remove(peer.Connection.Id);
        }

        // A Steam account's player id on every host: the same account is the same player, so a dropped player who
        // comes back is let back into their match.
        public static Guid SteamPlayerId(ulong steamId)
        {
            byte[] hash = Hash("wksr-steam-player:" + steamId);
            var bytes = new byte[16];
            Buffer.BlockCopy(hash, 0, bytes, 0, 16);
            return new Guid(bytes);
        }

        public static byte[] SteamToken(ulong steamId)
        {
            byte[] hash = Hash("wksr-steam-token:" + steamId);
            var token = new byte[ProtocolConstants.TokenLength];
            Buffer.BlockCopy(hash, 0, token, 0, Math.Min(hash.Length, token.Length));
            return token;
        }

        private static byte[] Hash(string text)
        {
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        }

        // ---- the test port: the DevTools bot as another player -----------------------------------------------------

        private void AcceptDebug()
        {
            while (!_stop)
            {
                TcpClient client;
                try { client = _debug.AcceptTcpClient(); }
                catch (Exception) { return; }
                client.NoDelay = true;
                var conn = new TcpTestConnection(this, client);
                new Thread(conn.Run) { IsBackground = true, Name = "WKSR lobby host test client" }.Start();
            }
        }
    }

    // The relay's listening side: accepts every player who connects (the core decides what they may do).
    internal sealed class HostSocket : SocketManager
    {
        public LobbyHost Host;

        public override void OnConnecting(Connection connection, ConnectionInfo info)
        {
            if (info.Identity.IsSteamId) connection.Accept();
            else connection.Close();
        }

        public override void OnConnected(Connection connection, ConnectionInfo info)
        {
            base.OnConnected(connection, info);
            Host?.OnPeerConnected(connection, info.Identity.SteamId.Value);
        }

        public override void OnDisconnected(Connection connection, ConnectionInfo info)
        {
            connection.Close();
            Host?.OnPeerDisconnected(connection);
        }

        public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            if (size <= 0 || size > ProtocolConstants.MaxFrameSize) return;
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, size);
            Host?.OnPeerMessage(connection, identity.IsSteamId ? identity.SteamId.Value : 0, bytes);
        }
    }

    // A player connected through Steam, as the core sees them.
    internal sealed class SteamPeer : IConnection
    {
        private readonly LobbyHost _host;
        private bool _gone;

        public SteamPeer(LobbyHost host, Connection connection, ulong steamId)
        {
            _host = host;
            Connection = connection;
            SteamId = steamId;
        }

        public Connection Connection { get; private set; }
        public ulong SteamId { get; private set; }
        public bool IsGone { get { return _gone; } }
        public string RemoteAddress { get { return "steam"; } }

        public void Send(Message message)
        {
            if (_gone) return;
            byte[] data = MessageCodec.Encode(message);
            Connection.SendMessage(data, SendType.Reliable);
        }

        // The core closes a connection (a refused sign-in, a replaced session): gone for the core too.
        public void Close(string reason)
        {
            if (_gone) return;
            CloseQuietly(true);
            // Told to the core once it has finished with what it is doing.
            _host.Post(() =>
            {
                _host.ForgetPeer(this);
                _host.Gone(this);
            });
        }

        public void CloseQuietly(bool linger)
        {
            _gone = true;
            try { Connection.Close(linger, 0, "WK Race lobby"); }
            catch (Exception) { }
        }

        public void MarkGone()
        {
            _gone = true;
        }

        public void MarkAuthenticated()
        {
        }
    }

    // The host's own WKSR: messages go to and fro in memory (copied, so neither side can change the other's).
    internal sealed class LocalLink : IServerLink, IConnection
    {
        private readonly LobbyHost _host;
        private readonly Func<ClientHello> _hello;
        private volatile bool _stopped;
        private Timer _pinger;

        public LocalLink(LobbyHost host, Func<ClientHello> hello)
        {
            _host = host;
            _hello = hello;
        }

        public event Action<Message> Received;
        public event Action<LinkState, string> StateChanged;

        public string RemoteAddress { get { return "local"; } }

        public void Start()
        {
            ClientHello hello = _hello();
            ulong me = WKRace.Mod.GameBridge.SteamId();
            if (me != 0)
            {
                hello.PlayerId = LobbyHost.SteamPlayerId(me);
                hello.Token = LobbyHost.SteamToken(me);
            }
            StateChanged?.Invoke(LinkState.Connected, "");
            _host.Post(() => _host.Deliver(this, hello));
            // The client keeps its clock estimate fresh with pings, as it does with the match server.
            _pinger = new Timer(_ => Send(new Ping { ClientTimeMs = LobbyHost.NowMs() }), null, 500, 2000);
        }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            _pinger?.Dispose();
            _host.Post(() => _host.Gone(this));
            StateChanged?.Invoke(LinkState.Disconnected, "");
        }

        // From the client to the core.
        public void Send(Message message)
        {
            if (_stopped) return;
            Message copy = MessageCodec.Decode(MessageCodec.Encode(message));
            _host.Post(() => _host.Deliver(this, copy));
        }

        // From the core to the client (IConnection).
        void IConnection.Send(Message message)
        {
            if (_stopped) return;
            Received?.Invoke(MessageCodec.Decode(MessageCodec.Encode(message)));
        }

        public void Close(string reason)
        {
            if (_stopped) return;
            _stopped = true;
            _pinger?.Dispose();
            _host.Post(() => _host.Gone(this));
            StateChanged?.Invoke(LinkState.Disconnected, reason);
        }

        public void MarkAuthenticated()
        {
        }
    }

    // Tests only: a plain TCP client (the DevTools bot) as another player, framed as on the match server.
    internal sealed class TcpTestConnection : IConnection
    {
        private readonly LobbyHost _host;
        private readonly TcpClient _client;
        private readonly Stream _stream;
        private readonly object _write = new object();
        private volatile bool _closed;

        public TcpTestConnection(LobbyHost host, TcpClient client)
        {
            _host = host;
            _client = client;
            _stream = client.GetStream();
        }

        public string RemoteAddress { get { return "127.0.0.1"; } }

        public void Run()
        {
            try
            {
                byte[] frame;
                while (!_closed && (frame = FrameIO.ReadFrame(_stream)) != null)
                {
                    Message message = MessageCodec.Decode(frame);
                    _host.Post(() => _host.Deliver(this, message));
                }
            }
            catch (Exception)
            {
            }
            if (!_closed)
            {
                _closed = true;
                try { _client.Close(); } catch (Exception) { }
                _host.Post(() => _host.Gone(this));
            }
        }

        public void Send(Message message)
        {
            if (_closed) return;
            try
            {
                lock (_write) FrameIO.WriteFrame(_stream, MessageCodec.Encode(message));
            }
            catch (Exception)
            {
                Close("write failed");
            }
        }

        public void Close(string reason)
        {
            if (_closed) return;
            _closed = true;
            try { _client.Close(); } catch (Exception) { }
            _host.Post(() => _host.Gone(this));
        }

        public void MarkAuthenticated()
        {
        }
    }
}
