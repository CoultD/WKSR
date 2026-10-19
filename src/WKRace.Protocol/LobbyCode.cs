using System;
using System.Globalization;
using System.Text;

namespace WKRace.Protocol
{
    // A lobby join code: "WKR-" followed by two groups of four base32 characters, for example "WKR-2A46-F92E".
    // It only names a lobby on the official server; it never carries an address, so a code cannot send a player
    // (and their identity token) to some other server.
    //
    // Payload: lobby id (4 bytes) and a check byte: 40 bits, exactly eight characters.
    public sealed class LobbyCode
    {
        private const string Prefix = "WKR";
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";   // Crockford base32: no I, L, O or U
        private const int PayloadLength = 5;

        public uint LobbyId;

        public static string Encode(uint lobbyId)
        {
            var w = new PacketWriter(PayloadLength);
            w.WriteUInt32(lobbyId);
            byte[] payload = w.ToArray();
            byte[] full = new byte[payload.Length + 1];
            Buffer.BlockCopy(payload, 0, full, 0, payload.Length);
            full[payload.Length] = Check(payload, payload.Length);

            string body = ToBase32(full);
            return Prefix + "-" + body.Substring(0, 4) + "-" + body.Substring(4);
        }

        // Accepts the code with or without the prefix, dashes, spaces or lower case, and the usual look-alike letters.
        public static bool TryDecode(string text, out LobbyCode code)
        {
            code = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
            if (s.StartsWith(Prefix, StringComparison.Ordinal)) s = s.Substring(Prefix.Length);
            s = s.Replace('O', '0').Replace('I', '1').Replace('L', '1');

            byte[] full;
            if (s.Length != 8 || !FromBase32(s, out full) || full.Length != PayloadLength) return false;
            if (full[PayloadLength - 1] != Check(full, PayloadLength - 1)) return false;

            var r = new PacketReader(full, 0, PayloadLength - 1);
            uint id = r.ReadUInt32();
            if (id == 0) return false;
            code = new LobbyCode { LobbyId = id };
            return true;
        }

        // "host:port" or "[v6]:port" -> parts. Port defaults to the standard server port.
        public static bool TryParseAddress(string text, out string host, out int port)
        {
            host = null;
            port = ProtocolConstants.DefaultServerPort;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (t.StartsWith("[", StringComparison.Ordinal))
            {
                int close = t.IndexOf(']');
                if (close < 0) return false;
                host = t.Substring(1, close - 1);
                string rest = t.Substring(close + 1);
                return rest.Length == 0 || (rest[0] == ':' && TryPort(rest.Substring(1), out port));
            }
            int colon = t.LastIndexOf(':');
            if (colon >= 0 && t.IndexOf(':') == colon)
            {
                host = t.Substring(0, colon);
                return host.Length > 0 && TryPort(t.Substring(colon + 1), out port);
            }
            host = t;
            return true;
        }

        private static bool TryPort(string text, out int port)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port > 0 && port < 65536;
        }

        private static byte Check(byte[] data, int length)
        {
            uint h = Fnv1a.Offset;
            for (int i = 0; i < length; i++)
            {
                h ^= data[i];
                h = unchecked(h * 16777619);
            }
            return (byte)(h ^ (h >> 8) ^ (h >> 16) ^ (h >> 24));
        }

        private static string ToBase32(byte[] data)
        {
            var sb = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = 0, bits = 0;
            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }
            if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            return sb.ToString();
        }

        private static bool FromBase32(string text, out byte[] data)
        {
            data = null;
            var bytes = new System.Collections.Generic.List<byte>(text.Length * 5 / 8);
            int buffer = 0, bits = 0;
            foreach (char c in text)
            {
                int v = Alphabet.IndexOf(c);
                if (v < 0) return false;
                buffer = ((buffer << 5) | v) & 0xFFFF;
                bits += 5;
                if (bits >= 8)
                {
                    bytes.Add((byte)(buffer >> (bits - 8)));
                    bits -= 8;
                }
            }
            data = bytes.ToArray();
            return true;
        }
    }
}
