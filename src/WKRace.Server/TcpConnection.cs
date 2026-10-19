using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WKRace.Protocol;

namespace WKRace.Server
{
    internal sealed class TcpConnection : IConnection
    {
        // Room for a party race: up to 14 opponents at 15 states a second each.
        private const int SendQueueCapacity = 4096;
        private const double RateBurst = 200;
        private const double RatePerSecond = 100;

        private readonly RaceServer _server;
        private readonly TcpClient _client;
        private readonly Stream _stream;
        private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(SendQueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private int _closed;
        private volatile bool _authenticated;
        private long _lastReceiveTicks = Environment.TickCount64;
        private double _tokens = RateBurst;
        private long _lastRefillTicks = Environment.TickCount64;

        private readonly IPAddress _address;

        public TcpConnection(RaceServer server, TcpClient client, Stream stream, IPAddress address)
        {
            _server = server;
            _client = client;
            _stream = stream;
            _address = address;
            RemoteAddress = address.ToString();
            ConnectedTicks = Environment.TickCount64;
        }

        public string RemoteAddress { get; private set; }
        public long ConnectedTicks { get; private set; }
        public bool IsAuthenticated { get { return _authenticated; } }
        public long LastReceiveTicks { get { return Interlocked.Read(ref _lastReceiveTicks); } }

        public void MarkAuthenticated()
        {
            _authenticated = true;
        }

        public void Send(Message message)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            byte[] frame;
            try
            {
                frame = FrameIO.Pack(MessageCodec.Encode(message));
            }
            catch (ProtocolException)
            {
                return;
            }
            if (!_outbound.Writer.TryWrite(frame)) Close("send queue full");
        }

        // Pending frames (for example a final error message) are flushed before the socket is closed.
        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            if (reason == ServerCore.ViolationReason) _server.Strike(_address, "protocol violations");
            _outbound.Writer.TryComplete();
            Task.Delay(2000).ContinueWith(_ => Abort());
        }

        public async Task RunAsync()
        {
            Task writer = WriteLoopAsync();
            try
            {
                await ReadLoopAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Any read failure just ends the connection.
            }
            finally
            {
                Close("read ended");
                _server.Post(() => _server.Core.OnDisconnected(this));
            }
            await writer.ConfigureAwait(false);
            await DrainAsync().ConfigureAwait(false);
            Abort();
        }

        // Closing a socket that still has unread incoming data makes the OS send a reset, which can discard the last
        // messages (for example the rate-limit error) before the peer has read them. Swallow what is left first.
        private async Task DrainAsync()
        {
            if (_cts.IsCancellationRequested) return;
            try
            {
                var buffer = new byte[4096];
                while (await _stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token).ConfigureAwait(false) > 0) { }
            }
            catch (Exception)
            {
                // Reset, timeout or abort: nothing left to drain.
            }
        }

        private async Task ReadLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                byte[] frame = await FrameIO.ReadFrameAsync(_stream, _cts.Token).ConfigureAwait(false);
                if (frame == null) return;
                Interlocked.Exchange(ref _lastReceiveTicks, Environment.TickCount64);

                if (!TakeToken())
                {
                    Send(new ErrorMessage(ErrorCode.RateLimited, "Too many messages."));
                    _server.Strike(_address, "message flooding");
                    Close("rate limited");
                    return;
                }

                Message message;
                try
                {
                    message = MessageCodec.Decode(frame);
                }
                catch (ProtocolException e)
                {
                    Send(new ErrorMessage(ErrorCode.ProtocolViolation, e.Message));
                    _server.Strike(_address, "bad frames");
                    Close("bad frame");
                    return;
                }
                _server.Post(() => _server.Core.OnMessage(this, message));
            }
        }

        private async Task WriteLoopAsync()
        {
            bool flushed = false;
            try
            {
                await foreach (byte[] frame in _outbound.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    await _stream.WriteAsync(frame, 0, frame.Length, _cts.Token).ConfigureAwait(false);
                }
                await _stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                flushed = true;
            }
            catch (Exception)
            {
                // Peer went away or the connection was aborted.
            }
            finally
            {
                if (flushed)
                {
                    // Everything queued has been sent: tell the peer we are done and let RunAsync finish the close.
                    try { _client.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { }
                }
                else
                {
                    Abort();
                }
            }
        }

        private bool TakeToken()
        {
            long now = Environment.TickCount64;
            _tokens = Math.Min(RateBurst, _tokens + (now - _lastRefillTicks) / 1000.0 * RatePerSecond);
            _lastRefillTicks = now;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }

        private void Abort()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try { _client.Close(); } catch (Exception) { }
        }
    }
}
