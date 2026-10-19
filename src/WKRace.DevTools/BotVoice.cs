using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Concentus;
using Concentus.Enums;
using WKRace.Protocol;

namespace WKRace.DevTools
{
    // A bot's "voice" for testing proximity voice without a second person: once the server's VoiceHello arrives, the
    // bot speaks a short warbling tone every few seconds through the real voice relay (Opus, encrypted, from where the
    // bot climbs), and counts the voice packets it receives from others.
    internal sealed class BotVoice : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly byte[] _key;
        private readonly ulong _token;
        private readonly double _pitch;
        private readonly IOpusEncoder _encoder;
        private readonly Timer _timer;
        private readonly Thread _receiver;
        private long _counter;
        private uint _seq;
        private long _frame;
        private volatile bool _stopped;

        public int Received;

        public BotVoice(VoiceHello hello, string host, double pitch)
        {
            OpusCodecFactory.AttemptToUseNativeLibrary = false;
            IPAddress address = Dns.GetHostAddresses(host).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First();
            _udp = new UdpClient(address.AddressFamily);
            if (OperatingSystem.IsWindows()) _udp.Client.IOControl(-1744830452, new byte[] { 0 }, null);
            _udp.Connect(new IPEndPoint(address, hello.Port));
            _key = hello.Key;
            _token = hello.Token;
            _pitch = pitch;
            _encoder = OpusCodecFactory.CreateEncoder(VoiceWire.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            _encoder.Bitrate = 24000;
            Send(VoiceWire.KindKeepAlive, 0, ReadOnlySpan<byte>.Empty);
            _receiver = new Thread(Receive) { IsBackground = true };
            _receiver.Start();
            _timer = new Timer(_ => Tick(), null, 20, 20);
        }

        // Every 20 ms: a frame of tone during the first 1.2 s of every 4 s, and a keepalive now and then.
        private readonly object _tickLock = new object();

        // Timer callbacks can overlap; the encoder must only be used by one at a time.
        private void Tick()
        {
            if (_stopped || !Monitor.TryEnter(_tickLock)) return;
            try { TickOnce(); }
            catch (Exception) { }
            finally { Monitor.Exit(_tickLock); }
        }

        private void TickOnce()
        {
            long frame = Interlocked.Increment(ref _frame);
            if (frame % 150 == 0) Send(VoiceWire.KindKeepAlive, 0, ReadOnlySpan<byte>.Empty);
            if (frame % 200 >= 60) return;
            var pcm = new short[VoiceWire.FrameSamples];
            for (int i = 0; i < pcm.Length; i++)
            {
                double t = (frame * VoiceWire.FrameSamples + i) / (double)VoiceWire.SampleRate;
                double f = _pitch * (1 + 0.08 * Math.Sin(2 * Math.PI * 5 * t));
                pcm[i] = (short)(9000 * Math.Sin(2 * Math.PI * f * t));
            }
            var opus = new byte[VoiceWire.MaxOpusBytes];
            int length = _encoder.Encode(pcm, VoiceWire.FrameSamples, opus, opus.Length);
            if (length > 0) Send(VoiceWire.KindAudio, _seq++, new ReadOnlySpan<byte>(opus, 0, length));
        }

        private void Send(byte kind, uint seq, ReadOnlySpan<byte> opus)
        {
            byte[] packet = VoiceWire.SealToServer(_key, _token, (ulong)Interlocked.Increment(ref _counter), kind, seq, opus);
            try { _udp.Send(packet, packet.Length); }
            catch (Exception) { }
        }

        private void Receive()
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (!_stopped)
            {
                byte[] packet;
                try { packet = _udp.Receive(ref from); }
                catch (Exception) { if (_stopped) return; continue; }
                byte[] body;
                if (VoiceWire.TryOpen(_key, packet, packet.Length, false, out body)) Interlocked.Increment(ref Received);
            }
        }

        public void Dispose()
        {
            _stopped = true;
            _timer.Dispose();
            _udp.Dispose();
        }
    }
}
