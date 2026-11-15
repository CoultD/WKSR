using System;
using System.Collections.Generic;

namespace WKRace.Protocol
{
    // Diagnostics for game updates (0.9.8). A client sends these only after a ServerFeatures with Diagnostics set, so an
    // older server never meets them.
    //
    // WorldLevels: the levels a player's game built for a match, in climb order, sent just before MatchReady. When two
    // worlds do not match, the server logs the first level where they differ ("worlds split at level 9: ... vs ..."),
    // so the cause is visible in the server log without anyone's save files.
    //
    // ClientIssues: the parts of WKSR that did not load in a player's game (a game update changed what they hook).

    public struct WorldLevel
    {
        public const int MaxName = 48;

        // The level's id (its asset name).
        public string Name;
        // The rest of what the world fingerprint covers for this level: its position (to 0.1 m) and whether it is
        // mirrored. Equal names with different hashes mean the level sits somewhere else.
        public uint Hash;

        public WorldLevel(string name, uint hash)
        {
            Name = name ?? "";
            Hash = hash;
        }

        public void Write(PacketWriter w)
        {
            w.WriteString(Name, MaxName);
            w.WriteUInt32(Hash);
        }

        public static WorldLevel Read(PacketReader r)
        {
            return new WorldLevel(r.ReadString(MaxName), r.ReadUInt32());
        }

        public override string ToString()
        {
            return Name + " #" + Hash.ToString("X8");
        }
    }

    public sealed class WorldLevels : Message
    {
        public const int MaxPerPart = 16;
        public const int MaxLevels = 192;

        public Guid MatchId;
        // How many levels the whole list has, and where this part's levels start in it.
        public ushort Total;
        public ushort First;
        public List<WorldLevel> Levels = new List<WorldLevel>();

        public override MessageType Type { get { return MessageType.WorldLevels; } }

        public override void Write(PacketWriter w)
        {
            if (Levels.Count > MaxPerPart) throw new ProtocolException("Too many levels in one part");
            w.WriteGuid(MatchId);
            w.WriteUInt16(Total);
            w.WriteUInt16(First);
            w.WriteByte((byte)Levels.Count);
            foreach (WorldLevel level in Levels) level.Write(w);
        }

        public static WorldLevels Read(PacketReader r)
        {
            var m = new WorldLevels { MatchId = r.ReadGuid(), Total = r.ReadUInt16(), First = r.ReadUInt16() };
            int count = ProtocolChecks.Count(r, MaxPerPart);
            if (m.Total > MaxLevels || m.First + count > m.Total) throw new ProtocolException("Bad world levels part");
            for (int i = 0; i < count; i++) m.Levels.Add(WorldLevel.Read(r));
            return m;
        }

        // The whole list as the messages that carry it (at most MaxLevels levels).
        public static List<WorldLevels> Split(Guid matchId, IList<WorldLevel> levels)
        {
            var parts = new List<WorldLevels>();
            int total = Math.Min(levels.Count, MaxLevels);
            for (int first = 0; first < total; first += MaxPerPart)
            {
                var part = new WorldLevels { MatchId = matchId, Total = (ushort)total, First = (ushort)first };
                for (int i = first; i < total && i < first + MaxPerPart; i++) part.Levels.Add(levels[i]);
                parts.Add(part);
            }
            return parts;
        }

        // Where two level lists first differ: -1 when they are the same.
        public static int FirstDifference(IList<WorldLevel> a, IList<WorldLevel> b)
        {
            int n = Math.Min(a.Count, b.Count);
            for (int i = 0; i < n; i++)
                if (a[i].Name != b[i].Name || a[i].Hash != b[i].Hash) return i;
            return a.Count == b.Count ? -1 : n;
        }
    }

    public sealed class ClientIssues : Message
    {
        public const int MaxItems = 16;
        public const int MaxItemLength = 120;

        public List<string> Items = new List<string>();

        public override MessageType Type { get { return MessageType.ClientIssues; } }

        public override void Write(PacketWriter w)
        {
            int count = Math.Min(Items.Count, MaxItems);
            w.WriteByte((byte)count);
            for (int i = 0; i < count; i++) w.WriteString(Items[i], MaxItemLength);
        }

        public static ClientIssues Read(PacketReader r)
        {
            var m = new ClientIssues();
            int count = ProtocolChecks.Count(r, MaxItems);
            for (int i = 0; i < count; i++) m.Items.Add(r.ReadString(MaxItemLength));
            return m;
        }
    }
}
