using System;
using System.Collections.Generic;

namespace WKRace.Protocol
{
    // Artifact bans: before the world loads, the two racers take turns banning one artifact each from the pool the
    // game offers in its artifact rooms. The mod reports the pool, the server runs the turns and sends the state to
    // both (through the client), and the bans apply to every artifact room in the race world.

    // Mod -> client -> server: the game is up and these are the artifacts its artifact rooms can offer.
    public sealed class BanReady : Message
    {
        public Guid MatchId;
        public List<string> Pool = new List<string>();

        public override MessageType Type { get { return MessageType.BanReady; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            BanLists.Write(w, Pool);
        }

        public static BanReady Read(PacketReader r)
        {
            return new BanReady { MatchId = r.ReadGuid(), Pool = BanLists.Read(r) };
        }
    }

    // Mod -> client -> server: the player's ban. An empty item passes the turn without banning.
    public sealed class BanPick : Message
    {
        public Guid MatchId;
        public string Item = "";

        public override MessageType Type { get { return MessageType.BanPick; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteString(Item, ProtocolConstants.MaxItemNameLength);
        }

        public static BanPick Read(PacketReader r)
        {
            return new BanPick { MatchId = r.ReadGuid(), Item = r.ReadString(ProtocolConstants.MaxItemNameLength) };
        }
    }

    // Server -> client -> mod: where the ban phase stands, sent after every change and on reconnect.
    // An empty Pool while not Done means the server is still waiting for both games.
    public sealed class BanState : Message
    {
        public Guid MatchId;
        public List<string> Pool = new List<string>();
        // Banned items in order, and who banned each (same length as Banned).
        public List<string> Banned = new List<string>();
        public List<string> BannedBy = new List<string>();
        // Whose turn it is ("" when nobody's), whether that is the receiving player, and the time left for it.
        public string TurnName = "";
        public bool YourTurn;
        public int SecondsLeft;
        public bool Done;

        public override MessageType Type { get { return MessageType.BanState; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            BanLists.Write(w, Pool);
            BanLists.Write(w, Banned);
            BanLists.Write(w, BannedBy, ProtocolConstants.MaxNameLength);
            w.WriteString(TurnName, ProtocolConstants.MaxNameLength);
            w.WriteBool(YourTurn);
            w.WriteInt32(SecondsLeft);
            w.WriteBool(Done);
        }

        public static BanState Read(PacketReader r)
        {
            var m = new BanState();
            m.MatchId = r.ReadGuid();
            m.Pool = BanLists.Read(r);
            m.Banned = BanLists.Read(r);
            m.BannedBy = BanLists.Read(r, ProtocolConstants.MaxNameLength);
            m.TurnName = r.ReadString(ProtocolConstants.MaxNameLength);
            m.YourTurn = r.ReadBool();
            m.SecondsLeft = r.ReadInt32();
            m.Done = r.ReadBool();
            if (m.Banned.Count != m.BannedBy.Count) throw new ProtocolException("Ban lists differ in length");
            return m;
        }
    }

    internal static class BanLists
    {
        public static void Write(PacketWriter w, List<string> items, int maxChars = ProtocolConstants.MaxItemNameLength)
        {
            int count = items == null ? 0 : items.Count;
            if (count > ProtocolConstants.MaxBanPool) throw new ProtocolException("Too many items");
            w.WriteByte((byte)count);
            for (int i = 0; i < count; i++) w.WriteString(items[i], maxChars);
        }

        public static List<string> Read(PacketReader r, int maxChars = ProtocolConstants.MaxItemNameLength)
        {
            int count = r.ReadByte();
            if (count > ProtocolConstants.MaxBanPool) throw new ProtocolException("Too many items");
            var items = new List<string>(count);
            for (int i = 0; i < count; i++) items.Add(r.ReadString(maxChars));
            return items;
        }
    }
}
