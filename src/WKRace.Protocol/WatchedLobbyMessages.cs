using System.Collections.Generic;

namespace WKRace.Protocol
{
    // Server -> spectator of a lobby: the lobby as it is now (sent again whenever it changes). A public lobby can be
    // watched from the lobby browser whatever its state, full or not: the spectator sees who is in it, watches each
    // of its matches in their own world (SpectateStart, from GO), and can join when a spot is free and it is not in a
    // match (LobbyJoin). Closed: the lobby is gone (Reason says why) and the watching with it.
    public sealed class WatchedLobby : Message
    {
        public uint LobbyId;
        public string Host = "";
        public MatchMode Mode;
        public MatchRules Rules;
        public byte Segment;
        public byte MaxPlayers;
        public bool Racing;
        public ushort Spectators;
        public List<string> Members = new List<string>();
        public bool Closed;
        public string Reason = "";

        public bool CanJoin { get { return !Closed && !Racing && Members.Count < MaxPlayers; } }

        public override MessageType Type { get { return MessageType.WatchedLobby; } }

        public override void Write(PacketWriter w)
        {
            w.WriteUInt32(LobbyId);
            w.WriteString(Host, ProtocolConstants.MaxNameLength);
            w.WriteByte((byte)Mode);
            w.WriteByte((byte)Rules);
            w.WriteByte(Segment);
            w.WriteByte(MaxPlayers);
            w.WriteBool(Racing);
            w.WriteUInt16(Spectators);
            if (Members.Count > ProtocolConstants.MaxPartyPlayers) throw new ProtocolException("Too many lobby members");
            w.WriteByte((byte)Members.Count);
            foreach (string m in Members) w.WriteString(m, ProtocolConstants.MaxNameLength);
            w.WriteBool(Closed);
            w.WriteString(Reason, ProtocolConstants.MaxTextLength);
        }

        public static WatchedLobby Read(PacketReader r)
        {
            var m = new WatchedLobby
            {
                LobbyId = r.ReadUInt32(),
                Host = r.ReadString(ProtocolConstants.MaxNameLength),
                Mode = ProtocolChecks.LobbyMode(r.ReadByte()),
                Rules = (MatchRules)(r.ReadByte() & (byte)MatchRules.All),
                Segment = r.ReadByte(),
                MaxPlayers = r.ReadByte(),
                Racing = r.ReadBool(),
                Spectators = r.ReadUInt16(),
            };
            int count = ProtocolChecks.Count(r, ProtocolConstants.MaxPartyPlayers);
            for (int i = 0; i < count; i++) m.Members.Add(r.ReadString(ProtocolConstants.MaxNameLength));
            m.Closed = r.ReadBool();
            m.Reason = r.ReadString(ProtocolConstants.MaxTextLength);
            return m;
        }
    }
}
