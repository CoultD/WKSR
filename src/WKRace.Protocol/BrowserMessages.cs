using System.Collections.Generic;

namespace WKRace.Protocol
{
    // The lobby browser on the match server. All three are message types of their own, so neither side ever meets them
    // unless the other said it knows them: the server sends ServerFeatures only to clients new enough to read it, and a
    // client only asks for the lobby list (or makes its lobby public) after a ServerFeatures saying the server lists
    // lobbies. Older clients and servers keep working with newer ones.

    // Server -> client, right after the welcome: what this server can do beyond the protocol's basics. Each later
    // feature is one more byte at the end, written only for clients that know it; a reader
    // skips the bytes it does not know, so an older client can read a newer server's list.
    public sealed class ServerFeatures : Message
    {
        public bool LobbyBrowser;
        // "X is typing..." (ChatTyping / ChatTypingNote).
        public bool ChatTyping;
        // Lobbies can play a segment (LobbyCreate.Segment).
        public bool LobbySegments;
        // Keep running after a win, and the lobby option for it (LobbyOptions, EndRun).
        public bool KeepRunning;

        public override MessageType Type { get { return MessageType.ServerFeatures; } }

        public override void Write(PacketWriter w)
        {
            w.WriteBool(LobbyBrowser);
            // The flags after the first byte, all or none (a reader skips the ones it does not know).
            if (ChatTyping || LobbySegments || KeepRunning)
            {
                w.WriteBool(ChatTyping);
                w.WriteBool(LobbySegments);
                w.WriteBool(KeepRunning);
            }
        }

        public static ServerFeatures Read(PacketReader r)
        {
            var m = new ServerFeatures { LobbyBrowser = r.ReadBool() };
            if (r.Remaining > 0) m.ChatTyping = r.ReadBool();
            if (r.Remaining > 0) m.LobbySegments = r.ReadBool();
            if (r.Remaining > 0) m.KeepRunning = r.ReadBool();
            // Features of later versions.
            while (r.Remaining > 0) r.ReadByte();
            return m;
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
        public byte Segment;
    }

    // Server -> client: the public lobbies (at most MaxEntries; Cut when there were more).
    public sealed class LobbyList : Message
    {
        public const int MaxEntries = 40;

        public readonly List<LobbyListEntry> Entries = new List<LobbyListEntry>();
        public bool Cut;
        // Each lobby's segment, after the entries (for clients from 0.9.6 on; an older one would not read them).
        public bool WithSegments;

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
            if (WithSegments)
                for (int i = 0; i < count; i++) w.WriteByte(Entries[i].Segment);
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
            if (r.Remaining >= count && count > 0)
            {
                m.WithSegments = true;
                foreach (LobbyListEntry e in m.Entries) e.Segment = r.ReadByte();
            }
            return m;
        }
    }
}
