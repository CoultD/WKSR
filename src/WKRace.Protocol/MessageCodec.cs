using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WKRace.Protocol
{
    public static class MessageCodec
    {
        public static byte[] Encode(Message message)
        {
            var w = new PacketWriter(64);
            w.WriteByte((byte)message.Type);
            message.Write(w);
            if (w.Length > ProtocolConstants.MaxFrameSize) throw new ProtocolException("Message too large");
            return w.ToArray();
        }

        public static Message Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 1) throw new ProtocolException("Empty payload");
            var r = new PacketReader(payload, 1, payload.Length - 1);
            Message m;
            switch ((MessageType)payload[0])
            {
                case MessageType.ClientHello: m = ClientHello.Read(r); break;
                case MessageType.ServerWelcome: m = ServerWelcome.Read(r); break;
                case MessageType.Error: m = ErrorMessage.Read(r); break;
                case MessageType.Ping: m = Ping.Read(r); break;
                case MessageType.Pong: m = Pong.Read(r); break;
                case MessageType.QueueJoin: m = QueueJoin.Read(r); break;
                case MessageType.QueueLeave: m = QueueLeave.Read(r); break;
                case MessageType.QueueStatus: m = QueueStatus.Read(r); break;
                case MessageType.MatchFound: m = MatchFound.Read(r); break;
                case MessageType.LobbyCreate: m = LobbyCreate.Read(r); break;
                case MessageType.LobbyCreated: m = LobbyCreated.Read(r); break;
                case MessageType.LobbyJoin: m = LobbyJoin.Read(r); break;
                case MessageType.LobbyCancel: m = LobbyCancel.Read(r); break;
                case MessageType.LobbyClosed: m = LobbyClosed.Read(r); break;
                case MessageType.MatchReady: m = MatchReady.Read(r); break;
                case MessageType.MatchStart: m = MatchStart.Read(r); break;
                case MessageType.StartCountdown: m = StartCountdown.Read(r); break;
                case MessageType.PlayerState: m = PlayerState.Read(r); break;
                case MessageType.OpponentState: m = OpponentState.Read(r); break;
                case MessageType.SplitReached: m = SplitReached.Read(r); break;
                case MessageType.OpponentSplit: m = OpponentSplit.Read(r); break;
                case MessageType.AttemptStarted: m = AttemptStarted.Read(r); break;
                case MessageType.OpponentAttempt: m = OpponentAttempt.Read(r); break;
                case MessageType.PlayerFinished: m = PlayerFinished.Read(r); break;
                case MessageType.OpponentFinished: m = OpponentFinished.Read(r); break;
                case MessageType.PlayerForfeit: m = PlayerForfeit.Read(r); break;
                case MessageType.MatchResult: m = MatchResult.Read(r); break;
                case MessageType.GameStatus: m = GameStatus.Read(r); break;
                case MessageType.MatchPaused: m = MatchPaused.Read(r); break;
                case MessageType.MatchResumed: m = MatchResumed.Read(r); break;
                case MessageType.ModHello: m = ModHello.Read(r); break;
                case MessageType.ClientStatus: m = ClientStatus.Read(r); break;
                case MessageType.RacePaused: m = RacePaused.Read(r); break;
                case MessageType.RaceResumed: m = RaceResumed.Read(r); break;
                case MessageType.PracticeMark: m = PracticeMark.Read(r); break;
                case MessageType.PracticeRestart: m = PracticeRestart.Read(r); break;
                case MessageType.PlayerDied: m = PlayerDied.Read(r); break;
                case MessageType.BanReady: m = BanReady.Read(r); break;
                case MessageType.BanPick: m = BanPick.Read(r); break;
                case MessageType.BanState: m = BanState.Read(r); break;
                case MessageType.SteamLinkStart: m = SteamLinkStart.Read(r); break;
                case MessageType.SteamIdentity: m = SteamIdentity.Read(r); break;
                case MessageType.SteamLinkUrl: m = SteamLinkUrl.Read(r); break;
                case MessageType.SteamLinkResult: m = SteamLinkResult.Read(r); break;
                case MessageType.LobbyState: m = LobbyState.Read(r); break;
                case MessageType.LobbyStart: m = LobbyStart.Read(r); break;
                case MessageType.PlayerReady: m = PlayerReady.Read(r); break;
                case MessageType.MatchRoster: m = MatchRoster.Read(r); break;
                case MessageType.ModSettings: m = ModSettings.Read(r); break;
                case MessageType.ModKeys: m = ModKeys.Read(r); break;
                case MessageType.LoadedMods: m = LoadedMods.Read(r); break;
                case MessageType.ServerStatus: m = ServerStatus.Read(r); break;
                case MessageType.SoloStart: m = SoloStart.Read(r); break;
                case MessageType.ServerFeatures: m = ServerFeatures.Read(r); break;
                case MessageType.BoardRequest: m = BoardRequest.Read(r); break;
                case MessageType.BoardPage: m = BoardPage.Read(r); break;
                case MessageType.ReplayRequest: m = ReplayRequest.Read(r); break;
                case MessageType.ReplayChunk: m = ReplayChunk.Read(r); break;
                case MessageType.BoardNote: m = BoardNote.Read(r); break;
                case MessageType.ModVerdict: m = ModVerdict.Read(r); break;
                case MessageType.ModsRequest: m = ModsRequest.Read(r); break;
                case MessageType.ModsPage: m = ModsPage.Read(r); break;
                case MessageType.ModMark: m = ModMark.Read(r); break;
                case MessageType.ChatSend: m = ChatSend.Read(r); break;
                case MessageType.ChatMessage: m = ChatMessage.Read(r); break;
                case MessageType.ChatJoin: m = ChatJoin.Read(r); break;
                case MessageType.LobbyVisibility: m = LobbyVisibility.Read(r); break;
                case MessageType.LobbyListRequest: m = LobbyListRequest.Read(r); break;
                case MessageType.LobbyList: m = LobbyList.Read(r); break;
                case MessageType.VoiceHello: m = VoiceHello.Read(r); break;
                case MessageType.VoiceState: m = VoiceState.Read(r); break;
                case MessageType.VoicePeers: m = VoicePeers.Read(r); break;
                case MessageType.VoiceActivity: m = VoiceActivity.Read(r); break;
                default: throw new ProtocolException("Unknown message type " + payload[0]);
            }
            if (r.Remaining != 0) throw new ProtocolException("Trailing bytes in message " + m.Type);
            return m;
        }
    }

    public static class FrameIO
    {
        // Returns null when the stream ends cleanly between frames.
        public static byte[] ReadFrame(Stream stream)
        {
            var header = new byte[2];
            if (!ReadExactly(stream, header, 2)) return null;
            int length = header[0] | (header[1] << 8);
            if (length < 1 || length > ProtocolConstants.MaxFrameSize) throw new ProtocolException("Invalid frame length " + length);
            var payload = new byte[length];
            if (!ReadExactly(stream, payload, length)) throw new EndOfStreamException();
            return payload;
        }

        public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            var header = new byte[2];
            if (!await ReadExactlyAsync(stream, header, 2, cancellationToken).ConfigureAwait(false)) return null;
            int length = header[0] | (header[1] << 8);
            if (length < 1 || length > ProtocolConstants.MaxFrameSize) throw new ProtocolException("Invalid frame length " + length);
            var payload = new byte[length];
            if (!await ReadExactlyAsync(stream, payload, length, cancellationToken).ConfigureAwait(false)) throw new EndOfStreamException();
            return payload;
        }

        public static byte[] Pack(byte[] payload)
        {
            if (payload.Length < 1 || payload.Length > ProtocolConstants.MaxFrameSize) throw new ProtocolException("Invalid payload length");
            var frame = new byte[payload.Length + 2];
            frame[0] = (byte)payload.Length;
            frame[1] = (byte)(payload.Length >> 8);
            Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
            return frame;
        }

        public static void WriteFrame(Stream stream, byte[] payload)
        {
            byte[] frame = Pack(payload);
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }

        public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
        {
            byte[] frame = Pack(payload);
            await stream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0)
                {
                    if (read == 0) return false;
                    throw new EndOfStreamException();
                }
                read += n;
            }
            return true;
        }

        private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer, read, count - read, cancellationToken).ConfigureAwait(false);
                if (n <= 0)
                {
                    if (read == 0) return false;
                    throw new EndOfStreamException();
                }
                read += n;
            }
            return true;
        }
    }
}
