using System.Collections.Generic;

namespace WKRace.Protocol
{
    // One BepInEx plugin loaded in a player's game, or one row of the server's mods list.
    public sealed class ModEntry
    {
        public const int MaxIdLength = 64;
        public const int MaxNameLength = 40;
        public const int MaxVersionLength = 20;

        // The plugin's GUID ("com.someone.somemod"): what an approval is attached to, whatever the version.
        public string Id = "";
        public string Name = "";
        public string Version = "";

        public void Write(PacketWriter w)
        {
            w.WriteString(Id ?? "", MaxIdLength);
            w.WriteString(Name ?? "", MaxNameLength);
            w.WriteString(Version ?? "", MaxVersionLength);
        }

        public static ModEntry Read(PacketReader r)
        {
            return new ModEntry { Id = r.ReadString(MaxIdLength), Name = r.ReadString(MaxNameLength), Version = r.ReadString(MaxVersionLength) };
        }
    }

    // Mod -> WKSR after it connects, and WKSR -> server: the plugins loaded in the player's game besides WK Race
    // itself, a few per message (a frame is small). Part 0 starts a new list, part Parts - 1 completes it.
    // Parts == 0: there is no game, so no list.
    public sealed class LoadedMods : Message
    {
        public const int MaxPerPart = 4;
        public const int MaxParts = 32;
        public const int MaxMods = MaxPerPart * MaxParts;

        public byte Part;
        public byte Parts;
        // The game has more plugins than a list holds: the rest is missing.
        public bool Cut;
        public List<ModEntry> Mods = new List<ModEntry>();

        public override MessageType Type { get { return MessageType.LoadedMods; } }

        public override void Write(PacketWriter w)
        {
            if (Mods.Count > MaxPerPart) throw new ProtocolException("Too many mods in one part");
            w.WriteByte(Part);
            w.WriteByte(Parts);
            w.WriteBool(Cut);
            w.WriteByte((byte)Mods.Count);
            foreach (ModEntry mod in Mods) mod.Write(w);
        }

        public static LoadedMods Read(PacketReader r)
        {
            var m = new LoadedMods { Part = r.ReadByte(), Parts = r.ReadByte(), Cut = r.ReadBool() };
            if (m.Parts > MaxParts || (m.Parts > 0 && m.Part >= m.Parts)) throw new ProtocolException("Bad mods list part");
            int count = ProtocolChecks.Count(r, MaxPerPart);
            for (int i = 0; i < count; i++) m.Mods.Add(ModEntry.Read(r));
            return m;
        }

        // The whole list as the messages that carry it (one empty message when there are no mods).
        public static List<LoadedMods> Split(IList<ModEntry> mods)
        {
            var parts = new List<LoadedMods>();
            bool cut = mods.Count > MaxMods;
            int count = cut ? MaxMods : mods.Count;
            int total = count == 0 ? 1 : (count + MaxPerPart - 1) / MaxPerPart;
            for (int p = 0; p < total; p++)
            {
                var part = new LoadedMods { Part = (byte)p, Parts = (byte)total, Cut = cut };
                for (int i = p * MaxPerPart; i < count && i < (p + 1) * MaxPerPart; i++) part.Mods.Add(mods[i]);
                parts.Add(part);
            }
            return parts;
        }
    }

    // Server -> client, on connect and whenever it changes: what the server makes of the player's mods.
    public sealed class ModVerdict : Message
    {
        public const int MaxNames = 12;

        // A mod is marked "not approved" on this server. Until then every mod is approved, and WKSR shows neither the
        // mods list nor the "allow unapproved mods" boxes.
        public bool AnyMarked;
        // This player looks after the list (they can mark mods in WKSR).
        public bool Manager;
        // How many of the player's loaded mods are not approved, and the names of the first few.
        public int Unapproved;
        public List<string> Names = new List<string>();

        public override MessageType Type { get { return MessageType.ModVerdict; } }

        public override void Write(PacketWriter w)
        {
            w.WriteByte((byte)((AnyMarked ? 1 : 0) | (Manager ? 2 : 0)));
            w.WriteInt32(Unapproved);
            int count = Names.Count > MaxNames ? MaxNames : Names.Count;
            w.WriteByte((byte)count);
            for (int i = 0; i < count; i++) w.WriteString(Names[i] ?? "", ModEntry.MaxNameLength);
        }

        public static ModVerdict Read(PacketReader r)
        {
            byte flags = r.ReadByte();
            var m = new ModVerdict { AnyMarked = (flags & 1) != 0, Manager = (flags & 2) != 0, Unapproved = r.ReadInt32() };
            int count = ProtocolChecks.Count(r, MaxNames);
            for (int i = 0; i < count; i++) m.Names.Add(r.ReadString(ModEntry.MaxNameLength));
            return m;
        }
    }

    // Client -> server: a page of the server's mods list (answered with ModsPage).
    public sealed class ModsRequest : Message
    {
        public const int MaxRows = 10;

        public int Offset;
        public byte Count = MaxRows;

        public override MessageType Type { get { return MessageType.ModsRequest; } }
        public override void Write(PacketWriter w) { w.WriteInt32(Offset); w.WriteByte(Count); }

        public static ModsRequest Read(PacketReader r)
        {
            var m = new ModsRequest { Offset = r.ReadInt32(), Count = r.ReadByte() };
            if (m.Offset < 0 || m.Count < 1 || m.Count > MaxRows) throw new ProtocolException("Bad mods list request");
            return m;
        }
    }

    public sealed class ModRow
    {
        public ModEntry Mod = new ModEntry();
        public bool Approved;
        // The asking player has this mod loaded right now.
        public bool Yours;
        // How many connected players have it loaded right now.
        public int Players;
    }

    // Server -> client: a page of the mods list. Not approved first, then by name.
    public sealed class ModsPage : Message
    {
        public int Offset;
        public int Total;
        public int NotApproved;
        public List<ModRow> Rows = new List<ModRow>();

        public override MessageType Type { get { return MessageType.ModsPage; } }

        public override void Write(PacketWriter w)
        {
            if (Rows.Count > ModsRequest.MaxRows) throw new ProtocolException("Too many mod rows");
            w.WriteInt32(Offset);
            w.WriteInt32(Total);
            w.WriteInt32(NotApproved);
            w.WriteByte((byte)Rows.Count);
            foreach (ModRow row in Rows)
            {
                row.Mod.Write(w);
                w.WriteByte((byte)((row.Approved ? 1 : 0) | (row.Yours ? 2 : 0)));
                w.WriteInt32(row.Players);
            }
        }

        public static ModsPage Read(PacketReader r)
        {
            var m = new ModsPage { Offset = r.ReadInt32(), Total = r.ReadInt32(), NotApproved = r.ReadInt32() };
            int count = ProtocolChecks.Count(r, ModsRequest.MaxRows);
            for (int i = 0; i < count; i++)
            {
                var row = new ModRow { Mod = ModEntry.Read(r) };
                byte flags = r.ReadByte();
                row.Approved = (flags & 1) != 0;
                row.Yours = (flags & 2) != 0;
                row.Players = r.ReadInt32();
                m.Rows.Add(row);
            }
            return m;
        }
    }

    // Client -> server, from the list's manager only: a mod (every version of it) is approved, or is not.
    public sealed class ModMark : Message
    {
        public string Id = "";
        public bool Approved;

        public override MessageType Type { get { return MessageType.ModMark; } }
        public override void Write(PacketWriter w) { w.WriteString(Id ?? "", ModEntry.MaxIdLength); w.WriteBool(Approved); }
        public static ModMark Read(PacketReader r) { return new ModMark { Id = r.ReadString(ModEntry.MaxIdLength), Approved = r.ReadBool() }; }
    }
}
