using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace WKRace.Protocol
{
    // Proximity voice chat. WKSR records, encodes (Opus) and sends 20 ms frames over UDP to the race server, which
    // relays them to the players who can hear the speaker: everyone in the same lobby, or in a race the racers within
    // VoiceHello.RangeMeters. Every UDP packet is encrypted and authenticated (AES-GCM) with a key each player gets over
    // the TLS connection, so voice cannot be read, forged or replayed. Nothing is stored.

    // Server -> client after the welcome: how to reach the voice relay. Port 0 means the server has no voice.
    public sealed class VoiceHello : Message
    {
        public int Port;
        public byte[] Key = new byte[VoiceWire.KeyLength];
        public ulong Token;
        public int RangeMeters = 65;

        public override MessageType Type { get { return MessageType.VoiceHello; } }

        public override void Write(PacketWriter w)
        {
            w.WriteInt32(Port);
            w.WriteFixedBytes(Key, VoiceWire.KeyLength);
            w.WriteInt64((long)Token);
            w.WriteInt32(RangeMeters);
        }

        public static VoiceHello Read(PacketReader r)
        {
            return new VoiceHello { Port = r.ReadInt32(), Key = r.ReadFixedBytes(VoiceWire.KeyLength), Token = (ulong)r.ReadInt64(), RangeMeters = r.ReadInt32() };
        }
    }

    // Client -> server: whether this player has voice on. Off: nothing is sent to them (and they send nothing).
    public sealed class VoiceState : Message
    {
        public bool Enabled;

        public override MessageType Type { get { return MessageType.VoiceState; } }
        public override void Write(PacketWriter w) { w.WriteBool(Enabled); }
        public static VoiceState Read(PacketReader r) { return new VoiceState { Enabled = r.ReadBool() }; }
    }

    public sealed class VoicePeer
    {
        public ushort VoiceId;
        public string Name = "";
        // Their racer number in the current race (for their position), or NoRacer outside a race.
        public byte Racer = NoRacer;
        public const byte NoRacer = 255;
    }

    // Server -> client: the players this player can hear right now (same lobby or race), with the id their voice
    // packets carry. Sent whenever that changes.
    public sealed class VoicePeers : Message
    {
        public List<VoicePeer> Peers = new List<VoicePeer>();

        public override MessageType Type { get { return MessageType.VoicePeers; } }

        public override void Write(PacketWriter w)
        {
            if (Peers.Count > ProtocolConstants.MaxPartyPlayers) throw new ProtocolException("Too many voice peers");
            w.WriteByte((byte)Peers.Count);
            foreach (VoicePeer p in Peers)
            {
                w.WriteUInt16(p.VoiceId);
                w.WriteString(p.Name, ProtocolConstants.MaxNameLength);
                w.WriteByte(p.Racer);
            }
        }

        public static VoicePeers Read(PacketReader r)
        {
            var m = new VoicePeers();
            int count = ProtocolChecks.Count(r, ProtocolConstants.MaxPartyPlayers);
            for (int i = 0; i < count; i++)
                m.Peers.Add(new VoicePeer { VoiceId = r.ReadUInt16(), Name = r.ReadString(ProtocolConstants.MaxNameLength), Racer = r.ReadByte() });
            return m;
        }
    }

    // WKSR -> mod: whether voice is on, who is talking right now (racer numbers), and whether this player is, for
    // the speaker symbols.
    public sealed class VoiceActivity : Message
    {
        public bool On;
        public bool Me;
        public List<byte> Racers = new List<byte>();

        public override MessageType Type { get { return MessageType.VoiceActivity; } }

        public override void Write(PacketWriter w)
        {
            w.WriteBool(On);
            w.WriteBool(Me);
            int count = Math.Min(Racers.Count, ProtocolConstants.MaxPartyPlayers);
            w.WriteByte((byte)count);
            for (int i = 0; i < count; i++) w.WriteByte(Racers[i]);
        }

        public static VoiceActivity Read(PacketReader r)
        {
            var m = new VoiceActivity { On = r.ReadBool(), Me = r.ReadBool() };
            int count = ProtocolChecks.Count(r, ProtocolConstants.MaxPartyPlayers);
            for (int i = 0; i < count; i++) m.Racers.Add(r.ReadByte());
            return m;
        }
    }

    // The UDP packets. Every packet: magic, kind, an id, a counter, then the AES-GCM encrypted body and its tag.
    // The header is authenticated too. The nonce is the direction plus the counter, and counters only go up, so a
    // replayed packet is refused.
    //   client -> server  (KindAudio, KindKeepAlive): id = the player's token; body = [frame seq u32][opus] or empty
    //   server -> client  (KindRelay):                id = the speaker's voice id; body = [frame seq u32][opus]
    public static class VoiceWire
    {
        public const byte Magic = 0x57;
        public const byte KindAudio = 1;
        public const byte KindKeepAlive = 2;
        public const byte KindRelay = 3;
        public const int KeyLength = 16;
        public const int TagLength = 16;
        public const int HeaderLength = 1 + 1 + 8 + 8;
        public const int MaxOpusBytes = 400;
        public const int MaxPacket = HeaderLength + 4 + MaxOpusBytes + TagLength;
        // 20 ms frames, 48 kHz mono.
        public const int SampleRate = 48000;
        public const int FrameSamples = 960;

        private const byte ToServer = 1;
        private const byte ToClient = 2;

        public static byte[] NewKey()
        {
            var key = new byte[KeyLength];
            RandomNumberGenerator.Fill(key);
            return key;
        }

        // A client's packet to the server: audio (seq and opus) or a keepalive (registers the address).
        public static byte[] SealToServer(byte[] key, ulong token, ulong counter, byte kind, uint seq, ReadOnlySpan<byte> opus)
        {
            return Seal(key, kind, token, counter, ToServer, seq, opus, kind == KindAudio);
        }

        // The server's relay of one speaker's frame to one listener.
        public static byte[] SealToClient(byte[] listenerKey, ushort speaker, ulong counter, uint seq, ReadOnlySpan<byte> opus)
        {
            return Seal(listenerKey, KindRelay, speaker, counter, ToClient, seq, opus, true);
        }

        private static byte[] Seal(byte[] key, byte kind, ulong id, ulong counter, byte direction, uint seq, ReadOnlySpan<byte> opus, bool audio)
        {
            int bodyLength = audio ? 4 + opus.Length : 0;
            var packet = new byte[HeaderLength + bodyLength + TagLength];
            packet[0] = Magic;
            packet[1] = kind;
            WriteU64(packet, 2, id);
            WriteU64(packet, 10, counter);
            var body = new byte[bodyLength];
            if (audio)
            {
                body[0] = (byte)seq; body[1] = (byte)(seq >> 8); body[2] = (byte)(seq >> 16); body[3] = (byte)(seq >> 24);
                opus.CopyTo(body.AsSpan(4));
            }
            using (var gcm = new AesGcm(key))
            {
                gcm.Encrypt(Nonce(direction, counter), body, packet.AsSpan(HeaderLength, bodyLength), packet.AsSpan(HeaderLength + bodyLength, TagLength), packet.AsSpan(0, HeaderLength));
            }
            return packet;
        }

        // The header of any packet (not yet authenticated): false when it is not a voice packet at all.
        public static bool TryHeader(byte[] packet, int length, out byte kind, out ulong id, out ulong counter)
        {
            kind = 0; id = 0; counter = 0;
            if (packet == null || length < HeaderLength + TagLength || length > MaxPacket || packet[0] != Magic) return false;
            kind = packet[1];
            id = ReadU64(packet, 2);
            counter = ReadU64(packet, 10);
            return kind == KindAudio || kind == KindKeepAlive || kind == KindRelay;
        }

        // Decrypts and checks a packet. body is [seq][opus] for audio and relays, empty for a keepalive. False when the
        // packet was forged, damaged or meant for someone else.
        public static bool TryOpen(byte[] key, byte[] packet, int length, bool toServer, out byte[] body)
        {
            body = null;
            byte kind; ulong id, counter;
            if (!TryHeader(packet, length, out kind, out id, out counter)) return false;
            int bodyLength = length - HeaderLength - TagLength;
            if (bodyLength != 0 && bodyLength < 4) return false;
            var plain = new byte[bodyLength];
            try
            {
                using (var gcm = new AesGcm(key))
                {
                    gcm.Decrypt(Nonce(toServer ? ToServer : ToClient, counter), packet.AsSpan(HeaderLength, bodyLength),
                        packet.AsSpan(HeaderLength + bodyLength, TagLength), plain, packet.AsSpan(0, HeaderLength));
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
            body = plain;
            return true;
        }

        public static uint Seq(byte[] body)
        {
            return (uint)(body[0] | (body[1] << 8) | (body[2] << 16) | (body[3] << 24));
        }

        private static byte[] Nonce(byte direction, ulong counter)
        {
            var nonce = new byte[12];
            nonce[0] = direction;
            WriteU64(nonce, 4, counter);
            return nonce;
        }

        private static void WriteU64(byte[] b, int at, ulong v)
        {
            for (int i = 0; i < 8; i++) b[at + i] = (byte)(v >> (8 * i));
        }

        private static ulong ReadU64(byte[] b, int at)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)b[at + i] << (8 * i);
            return v;
        }
    }
}
