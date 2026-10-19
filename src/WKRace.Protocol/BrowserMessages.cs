using System.Collections.Generic;

namespace WKRace.Protocol
{
    // The lobby browser on the match server. All three are message types of their own, so neither side ever meets them
    // unless the other said it knows them: the server sends ServerFeatures only to clients new enough to read it, and a
    // client only asks for the lobby list (or makes its lobby public) after a ServerFeatures saying the server lists
    // lobbies. Older clients and servers keep working with newer ones.

    // Server -> client, right after the welcome: what this server can do beyond the protocol's basics.
    public sealed class ServerFeatures : Message
    {
        public bool LobbyBrowser;

        public override MessageType Type { get { return MessageType.ServerFeatures; } }

        public override void Write(PacketWriter w)
        {
            w.WriteBool(LobbyBrowser);
        }

        public static ServerFeatures Read(PacketReader r)
        {
            return new ServerFeatures { LobbyBrowser = r.ReadBool() };
        }
    }

    // Client -> server: the host's lobby is listed in the browser (or no longer).
    public sealed class LobbyVisibility : Message
    {
        public bool Public;

        public override MessageType Type { get { return MessageType.LobbyVisibility; } }

        public override void Write(PacketWriter w)
        {
            w.WriteBool(Public);
        }

        public static LobbyVisibility Read(PacketReader r)
        {
            return new LobbyVisibility { Public = r.ReadBool() };
        }
    }

    // Client -> server: the public lobbies, please.
    public sealed class LobbyListRequest : Message
    {
        public override MessageType Type { get { return MessageType.LobbyListRequest; } }

        public override void Write(PacketWriter w)
        {
        }

        public static LobbyListRequest Read(PacketReader r)
        {
            return new LobbyListRequest();
        }
    }

    public sealed class LobbyListEntry
    {
        public uint LobbyId;
        public string Host = "";
        public MatchMode Mode;
        public MatchRules Rules;
        public byte Members;
        public byte MaxPlayers;
        public bool Racing;
    }

    // Server -> client: the public lobbies (at most MaxEntries; Cut when there were more).
    public sealed class LobbyList : Message
    {
        public const int MaxEntries = 40;

        public readonly List<LobbyListEntry> Entries = new List<LobbyListEntry>();
        public bool Cut;

        public override MessageType Type { get { return MessageType.LobbyList; } }

        public override void Write(PacketWriter w)
        {
            int count = System.Math.Min(Entries.Count, MaxEntries);
            w.WriteBool(Cut || Entries.Count > MaxEntries);
            w.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                LobbyListEntry e = Entries[i];
                w.WriteUInt32(e.LobbyId);
                w.WriteString(e.Host, ProtocolConstants.MaxNameLength);
                w.WriteByte((byte)e.Mode);
                w.WriteByte((byte)e.Rules);
                w.WriteByte(e.Members);
                w.WriteByte(e.MaxPlayers);
                w.WriteBool(e.Racing);
            }
        }

        public static LobbyList Read(PacketReader r)
        {
            var m = new LobbyList { Cut = r.ReadBool() };
            int count = r.ReadByte();
            if (count > MaxEntries) throw new ProtocolException("Too many lobbies in a list");
            for (int i = 0; i < count; i++)
            {
                m.Entries.Add(new LobbyListEntry
                {
                    LobbyId = r.ReadUInt32(),
                    Host = r.ReadString(ProtocolConstants.MaxNameLength),
                    Mode = ProtocolChecks.LobbyMode(r.ReadByte()),
                    Rules = (MatchRules)(r.ReadByte() & (byte)MatchRules.All),
                    Members = r.ReadByte(),
                    MaxPlayers = r.ReadByte(),
                    Racing = r.ReadBool(),
                });
            }
            return m;
        }
    }
}
