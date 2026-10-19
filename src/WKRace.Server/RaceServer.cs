using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WKRace.Protocol;

namespace WKRace.Server
{
    public sealed class RaceServer
    {
        private const int HandshakeTimeoutMs = 10000;
        // Clients ping every 2 s, so this much silence means the connection is dead even if the socket has not noticed.
        private const int IdleTimeoutMs = 10000;
        // How often the certificate file is checked for a renewal.
        private const int CertificateCheckMs = 60000;

        private readonly ServerOptions _options;
        private readonly Action<string> _log;
        private readonly TcpListener _listener;
        private readonly ServerDatabase _database;
        private readonly PlayerStore _store;
        private long _nextSnapshot;
        private SteamWeb _steamWeb;
        private AdminPort _adminPort;
        private ReportSpool _reports;
        private static readonly System.Net.Http.HttpClient SteamHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly Channel<Action> _work = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<TcpConnection, byte> _connections = new ConcurrentDictionary<TcpConnection, byte>();
        private readonly ConcurrentDictionary<IPAddress, int> _perAddress = new ConcurrentDictionary<IPAddress, int>();
        private readonly ConnectionGuard _guard;
        private long _nextGuardPrune;
        private volatile X509Certificate2 _certificate;
        private DateTime _certificateWrittenUtc;
        private long _nextCertificateCheck;
        private Task _coreLoop;
        private Task _acceptLoop;
        private Task _tickLoop;
        private Task _voiceLoop;
        private UdpClient _voice;

        public RaceServer(ServerOptions options, Action<string> log)
        {
            _options = options;
            _log = log ?? (s => { });
            _database = new ServerDatabase(options.DataDirectory, options.DatabaseKey, _log);
            _store = new PlayerStore(_database);
            Core = new ServerCore(options, _store, new MatchLog(_database), () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _log);
            _guard = new ConnectionGuard(options.ConnectAttemptsPerMinute, options.BanMinutes);
            _listener = new TcpListener(IPAddress.IPv6Any, options.Port);
            _listener.Server.DualMode = true;
            if (!string.IsNullOrEmpty(options.CertificatePath))
            {
                _certificateWrittenUtc = File.GetLastWriteTimeUtc(options.CertificatePath);
                _certificate = X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, options.CertificatePassword);
                _nextCertificateCheck = Environment.TickCount64 + CertificateCheckMs;
            }
        }

        internal ServerCore Core { get; private set; }

        public int Port
        {
            get { return ((IPEndPoint)_listener.LocalEndpoint).Port; }
        }

        public int VoicePort
        {
            get { return _voice == null ? 0 : ((IPEndPoint)_voice.Client.LocalEndPoint).Port; }
        }

        // Voice datagrams go to the core thread one by one; anything that is not a voice packet is dropped there.
        private async Task VoiceLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await _voice.ReceiveAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }
                byte[] data = received.Buffer;
                IPEndPoint from = received.RemoteEndPoint;
                if (data.Length < VoiceWire.HeaderLength + VoiceWire.TagLength || data.Length > VoiceWire.MaxPacket) continue;
                Post(() => Core.OnVoicePacket(from, data, data.Length));
            }
        }

        // Misbehaviour by a connection from this address; enough of it earns a temporary ban.
        internal void Strike(IPAddress address, string why)
        {
            if (_guard.Strike(address, Environment.TickCount64))
                _log("banned " + LogAddress.Short(address) + " for " + _options.BanMinutes + " min: repeated " + why);
        }

        private void WriteSnapshot()
        {
            try
            {
                Core.FlushStore();
                _database.WriteSnapshot();
            }
            catch (Exception e)
            {
                _log("could not write the database snapshot: " + e.Message);
            }
        }

        // ipban acts on the connection guard; everything else on the game state, on the core thread.
        private Task<string> RunAdminAsync(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("ipban", StringComparison.OrdinalIgnoreCase))
            {
                IPAddress address;
                int minutes;
                if (args.Length < 3 || !IPAddress.TryParse(args[1], out address) || !int.TryParse(args[2], out minutes) || minutes < 1)
                    return Task.FromResult("Error: usage: ipban <ip> <minutes>\n");
                if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
                _guard.BanFor(address, minutes * 60000L, Environment.TickCount64);
                foreach (TcpConnection c in _connections.Keys) if (c.RemoteAddress == address.ToString()) c.Close("ip banned");
                _log("admin blocked " + LogAddress.Short(address) + " for " + minutes + " min");
                return Task.FromResult("Blocked " + address + " for " + minutes + " minutes.\n");
            }
            return OnCore(() => Core.RunAdmin(args));
        }

        // A bug report posted to /report: checked on the core thread (known player, limits), then stored for the mailer.
        private async Task<WebReply> AcceptReportAsync(byte[] body)
        {
            BugReport report;
            try { report = BugReport.FromBytes(body); }
            catch (ProtocolException e) { return new WebReply { Status = 400, Text = "The report could not be read: " + e.Message }; }
            if (report.Title.Trim().Length == 0 || report.Description.Trim().Length == 0)
                return new WebReply { Status = 400, Text = "A report needs a title and a description." };

            ReportCheck check = await OnCore(() => Core.CheckReport(report.PlayerId, report.Token)).ConfigureAwait(false);
            if (!check.Ok) return new WebReply { Status = check.Status, Text = check.Message };

            string id;
            try { id = _reports.Write(report, check.PlayerName, DateTime.UtcNow); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                _log("could not store a bug report: " + e.Message);
                return new WebReply { Status = 503, Text = "The server could not store the report. Try again later." };
            }
            if (id == null)
            {
                _log("bug report refused: " + _reports.Directory + " is full (is the mailer running?)");
                return new WebReply { Status = 503, Text = "The server cannot take more reports right now. Try again later." };
            }
            _log("bug report " + id + " from " + check.PlayerName + " (" + report.Attachments.Count + " file(s), "
                + ((report.AttachmentBytes() + report.Log.Length) / 1024) + " KB)");
            return new WebReply { Status = 200, Text = "Report sent. Thank you!" };
        }

        // Runs work on the core thread (where all game state lives) and hands back its result.
        private Task<T> OnCore<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try { done.SetResult(work()); }
                catch (Exception e) { done.SetException(e); }
            });
            return done.Task;
        }

        internal void Post(Action action)
        {
            _work.Writer.TryWrite(action);
        }

        public void Start()
        {
            _listener.Start();
            _coreLoop = Task.Run(CoreLoopAsync);
            _acceptLoop = Task.Run(AcceptLoopAsync);
            _tickLoop = Task.Run(TickLoopAsync);
            if (_options.VoicePort > 0)
            {
                _voice = new UdpClient(AddressFamily.InterNetworkV6);
                _voice.Client.DualMode = true;
                // A client that went away makes Windows report "connection reset" on the next receive; ignore it.
                if (OperatingSystem.IsWindows()) _voice.Client.IOControl(-1744830452, new byte[] { 0 }, null);
                _voice.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, _options.VoicePort));
                UdpClient socket = _voice;
                Core.VoiceSend = (to, packet) =>
                {
                    try { socket.Send(packet, packet.Length, to); }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                };
                _voiceLoop = Task.Run(VoiceLoopAsync);
            }
            if (!string.IsNullOrEmpty(_options.SteamPublicUrl))
            {
                _steamWeb = new SteamWeb(_options.SteamHttpPort, _options.SteamPublicUrl,
                    nonce => OnCore(() => Core.IsSteamLinkPending(nonce)),
                    (nonce, steamId) => OnCore(() => Core.CompleteSteamLink(nonce, steamId)),
                    form => SteamWeb.CheckWithSteam(SteamHttp, form),
                    _log,
                    PrivacyNotice.Html(_options.PrivacyContact, !_options.SteamOptional),
                    _options.Reports ? AcceptReportAsync : (Func<byte[], Task<WebReply>>)null);
                if (_options.Reports) _reports = new ReportSpool(_options.DataDirectory);
                _steamWeb.Start(_cts.Token);
                _log("Steam sign-in on 127.0.0.1:" + _steamWeb.Port + " for " + _options.SteamPublicUrl + "/steam/"
                    + (_options.SteamOptional ? " (optional)" : " (required for ranked)"));
            }
            if (!string.IsNullOrEmpty(_options.AdminKey))
            {
                _adminPort = new AdminPort(_options.AdminPort, _options.AdminKey, RunAdminAsync, _log);
                _adminPort.Start(_cts.Token);
                _log("admin commands on 127.0.0.1:" + _adminPort.Port);
            }
            _log("listening on port " + Port + (_certificate != null ? " (TLS)" : "") + (_voice != null ? ", voice on UDP " + VoicePort : "") + ", database " + (_database.Encrypted ? "encrypted" : "NOT encrypted (no key)")
                + ", " + _store.Count + " players");
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            Post(() => Core.Shutdown());
            _work.Writer.TryComplete();
            try { await Task.WhenAll(_acceptLoop, _tickLoop, _coreLoop).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (_voice != null)
            {
                _voice.Dispose();
                try { if (_voiceLoop != null) await _voiceLoop.ConfigureAwait(false); }
                catch (Exception) { }
            }
            foreach (TcpConnection c in _connections.Keys) c.Close("shutdown");
            _database.Dispose();
        }

        // Certificates are renewed in place (Let's Encrypt), so a new file is picked up without a restart that would
        // drop every live race. A file that cannot be read yet (half written) is tried again at the next check.
        private X509Certificate2 CurrentCertificate()
        {
            X509Certificate2 current = _certificate;
            if (current == null) return null;
            long now = Environment.TickCount64;
            long due = Interlocked.Read(ref _nextCertificateCheck);
            if (now < due || Interlocked.CompareExchange(ref _nextCertificateCheck, now + CertificateCheckMs, due) != due) return current;
            try
            {
                DateTime written = File.GetLastWriteTimeUtc(_options.CertificatePath);
                if (written == _certificateWrittenUtc) return current;
                var renewed = X509CertificateLoader.LoadPkcs12FromFile(_options.CertificatePath, _options.CertificatePassword);
                _certificateWrittenUtc = written;
                _certificate = renewed;
                _log("loaded the renewed TLS certificate (valid until " + renewed.NotAfter.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ")");
                return renewed;
            }
            catch (Exception e)
            {
                _log("could not load the renewed TLS certificate, keeping the current one: " + e.Message);
                return current;
            }
        }

        private async Task CoreLoopAsync()
        {
            await foreach (Action action in _work.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    _log("core error: " + e);
                }
            }
        }

        private async Task TickLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(250, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                Post(() => Core.Tick());

                // A consistent copy of the database for the backup job, made on the core thread between messages.
                if (_options.SnapshotMinutes > 0 && Environment.TickCount64 >= _nextSnapshot)
                {
                    _nextSnapshot = Environment.TickCount64 + _options.SnapshotMinutes * 60000L;
                    Post(WriteSnapshot);
                }

                long now = Environment.TickCount64;
                if (now >= _nextGuardPrune)
                {
                    _nextGuardPrune = now + 60000;
                    _guard.Prune(now);
                }
                foreach (TcpConnection c in _connections.Keys)
                {
                    if (!c.IsAuthenticated && now - c.ConnectedTicks > HandshakeTimeoutMs) c.Close("handshake timeout");
                    else if (now - c.LastReceiveTicks > IdleTimeoutMs) c.Close("idle timeout");
                }
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }

                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            IPAddress address = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

            int perAddress = _perAddress.AddOrUpdate(address, 1, (_, n) => n + 1);
            TcpConnection connection = null;
            try
            {
                ConnectionGuard.Verdict verdict = _guard.Admit(address, Environment.TickCount64);
                if (verdict == ConnectionGuard.Verdict.NewlyBanned)
                    _log("banned " + LogAddress.Short(address) + " for " + _options.BanMinutes + " min: too many connection attempts");
                if (verdict != ConnectionGuard.Verdict.Allowed || _connections.Count >= _options.MaxConnections || perAddress > _options.MaxConnectionsPerIp)
                {
                    client.Close();
                    return;
                }
                client.NoDelay = true;

                Stream stream = client.GetStream();
                X509Certificate2 certificate = CurrentCertificate();
                if (certificate != null)
                {
                    var ssl = new SslStream(stream, false);
                    await ssl.AuthenticateAsServerAsync(certificate).WaitAsync(TimeSpan.FromMilliseconds(HandshakeTimeoutMs)).ConfigureAwait(false);
                    stream = ssl;
                }

                connection = new TcpConnection(this, client, stream, address);
                _connections[connection] = 0;
                await connection.RunAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log("connection error from " + LogAddress.Short(address) + ": " + e.Message);
            }
            finally
            {
                if (connection != null)
                {
                    byte ignored;
                    _connections.TryRemove(connection, out ignored);
                }
                client.Close();
                if (_perAddress.AddOrUpdate(address, 0, (_, n) => n - 1) <= 0)
                {
                    int removed;
                    _perAddress.TryRemove(address, out removed);
                }
            }
        }
    }
}
