using System;

namespace WKRace.Protocol
{
    // Linking a WKSR player to a Steam account (Steam's OpenID sign-in, in the player's browser). It is done once:
    // afterwards the player connects with their token as before. Signing in again restores the linked player on a new
    // PC, and always issues a fresh token.

    // Client -> server: start a sign-in for this player.
    public sealed class SteamLinkStart : Message
    {
        public override MessageType Type { get { return MessageType.SteamLinkStart; } }
        public override void Write(PacketWriter w) { }
        public static SteamLinkStart Read(PacketReader r) { return new SteamLinkStart(); }
    }

    // Server -> client: the Steam sign-in page to open in the browser (valid for a few minutes, one use).
    public sealed class SteamLinkUrl : Message
    {
        public string Url = "";

        public override MessageType Type { get { return MessageType.SteamLinkUrl; } }

        public override void Write(PacketWriter w)
        {
            w.WriteString(Url, ProtocolConstants.MaxUrlLength);
        }

        public static SteamLinkUrl Read(PacketReader r)
        {
            return new SteamLinkUrl { Url = r.ReadString(ProtocolConstants.MaxUrlLength) };
        }
    }

    // Server -> client: how the sign-in ended. On success the client stores PlayerId and Token (a new token, and on a
    // restore a different player) and reconnects if the player changed.
    public sealed class SteamLinkResult : Message
    {
        public bool Ok;
        public string Message = "";
        public Guid PlayerId;
        // Empty when unchanged.
        public byte[] Token = new byte[0];
        public string Name = "";
        public int Rating;
        public int Wins;
        public int Losses;

        public override MessageType Type { get { return MessageType.SteamLinkResult; } }

        public override void Write(PacketWriter w)
        {
            w.WriteBool(Ok);
            w.WriteString(Message, ProtocolConstants.MaxTextLength);
            w.WriteGuid(PlayerId);
            if (Token.Length != 0 && Token.Length != ProtocolConstants.TokenLength) throw new ProtocolException("Bad token length");
            w.WriteBool(Token.Length != 0);
            if (Token.Length != 0) w.WriteFixedBytes(Token, ProtocolConstants.TokenLength);
            w.WriteString(Name, ProtocolConstants.MaxNameLength);
            w.WriteInt32(Rating);
            w.WriteInt32(Wins);
            w.WriteInt32(Losses);
        }

        public static SteamLinkResult Read(PacketReader r)
        {
            var m = new SteamLinkResult();
            m.Ok = r.ReadBool();
            m.Message = r.ReadString(ProtocolConstants.MaxTextLength);
            m.PlayerId = r.ReadGuid();
            m.Token = r.ReadBool() ? r.ReadFixedBytes(ProtocolConstants.TokenLength) : new byte[0];
            m.Name = r.ReadString(ProtocolConstants.MaxNameLength);
            m.Rating = r.ReadInt32();
            m.Wins = r.ReadInt32();
            m.Losses = r.ReadInt32();
            return m;
        }
    }
}
