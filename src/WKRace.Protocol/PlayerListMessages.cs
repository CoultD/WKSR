using System.Collections.Generic;

namespace WKRace.Protocol
{
    // The online player list and profiles, on the official match server only (STEAM LOBBIES have neither). A client asks
    // for them only after a ServerFeatures saying the server has them (PlayerList), so older servers never meet these.

    // What a player is doing right now, as the list shows it.
    public enum PlayerActivity : byte
    {
        Online = 0,
        RankedSearching = 1,
        RankedInGame = 2,
        RankedSolo = 3,
        Lobby = 4,
        LobbyInGame = 5,
        Spectating = 6,
    }

    // Client -> server: one page of the players online, from Offset.
    public sealed class OnlineListRequest : Message
    {
        public ushort Offset;

        public override MessageType Type { get { return MessageType.OnlineListRequest; } }

        public override void Write(PacketWriter w)
        {
            w.WriteUInt16(Offset);
        }

        public static OnlineListRequest Read(PacketReader r)
        {
            return new OnlineListRequest { Offset = r.ReadUInt16() };
        }
    }

    public sealed class OnlineEntry
    {
        // The player's number on the server for as long as they stay signed in (asks for their profile).
        public uint Tag;
        public string Name = "";
        public PlayerActivity Activity;
        // Their lobby, when it is public and can be joined right now by the player who asked (else 0).
        public uint LobbyId;
        // In a match or run that can be watched.
        public bool Watchable;
        // The player who asked.
        public bool You;
    }

    // Server -> client: one page of the players online (Total in all), from Offset.
    public sealed class OnlineList : Message
    {
        // A page fits a frame even with the longest names.
        public const int PageSize = 20;

        public ushort Total;
        public ushort Offset;
        public readonly List<OnlineEntry> Entries = new List<OnlineEntry>();

        public override MessageType Type { get { return MessageType.OnlineList; } }

        public override void Write(PacketWriter w)
        {
            int count = System.Math.Min(Entries.Count, PageSize);
            w.WriteUInt16(Total);
            w.WriteUInt16(Offset);
            w.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                OnlineEntry e = Entries[i];
                w.WriteUInt32(e.Tag);
                w.WriteString(e.Name, ProtocolConstants.MaxNameLength);
                w.WriteByte((byte)e.Activity);
                w.WriteUInt32(e.LobbyId);
                w.WriteBool(e.Watchable);
                w.WriteBool(e.You);
            }
        }

        public static OnlineList Read(PacketReader r)
        {
            var m = new OnlineList { Total = r.ReadUInt16(), Offset = r.ReadUInt16() };
            int count = r.ReadByte();
            if (count > PageSize) throw new ProtocolException("Too many players in a list page");
            for (int i = 0; i < count; i++)
            {
                m.Entries.Add(new OnlineEntry
                {
                    Tag = r.ReadUInt32(),
                    Name = r.ReadString(ProtocolConstants.MaxNameLength),
                    // An activity of a later version reads as plain online.
                    Activity = ActivityOf(r.ReadByte()),
                    LobbyId = r.ReadUInt32(),
                    Watchable = r.ReadBool(),
                    You = r.ReadBool(),
                });
            }
            return m;
        }

        private static PlayerActivity ActivityOf(byte value)
        {
            return value <= (byte)PlayerActivity.Spectating ? (PlayerActivity)value : PlayerActivity.Online;
        }
    }

    // Client -> server: the profile of the player with this tag (from the online list).
    public sealed class ProfileRequest : Message
    {
        public uint Tag;

        public override MessageType Type { get { return MessageType.ProfileRequest; } }

        public override void Write(PacketWriter w)
        {
            w.WriteUInt32(Tag);
        }

        public static ProfileRequest Read(PacketReader r)
        {
            return new ProfileRequest { Tag = r.ReadUInt32() };
        }
    }

    // A player's place on one leaderboard, and their value there (a rating, or a time in ms).
    public sealed class ProfilePlace
    {
        public BoardKind Board;
        public int Rank;
        public int Value;
    }

    // Server -> client: a player's profile. Found is false when the tag no longer names anyone (they signed out).
    public sealed class Profile : Message
    {
        public const int MaxPlaces = 16;

        public uint Tag;
        public bool Found;
        public string Name = "";
        public int Rating;
        // Ranked 1v1.
        public int Wins;
        public int Losses;
        // Runs finished to the end of the climb, ranked and unranked apart (a 1v1 won without finishing does not count).
        public int RankedRuns;
        public int UnrankedRuns;
        // Restarts, in every mode.
        public int Resets;
        // The leaderboards the player is on.
        public readonly List<ProfilePlace> Places = new List<ProfilePlace>();

        public override MessageType Type { get { return MessageType.Profile; } }

        public override void Write(PacketWriter w)
        {
            w.WriteUInt32(Tag);
            w.WriteBool(Found);
            w.WriteString(Name, ProtocolConstants.MaxNameLength);
            w.WriteInt32(Rating);
            w.WriteInt32(Wins);
            w.WriteInt32(Losses);
            w.WriteInt32(RankedRuns);
            w.WriteInt32(UnrankedRuns);
            w.WriteInt32(Resets);
            int count = System.Math.Min(Places.Count, MaxPlaces);
            w.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                w.WriteByte((byte)Places[i].Board);
                w.WriteInt32(Places[i].Rank);
                w.WriteInt32(Places[i].Value);
            }
        }

        public static Profile Read(PacketReader r)
        {
            var m = new Profile
            {
                Tag = r.ReadUInt32(),
                Found = r.ReadBool(),
                Name = r.ReadString(ProtocolConstants.MaxNameLength),
                Rating = r.ReadInt32(),
                Wins = r.ReadInt32(),
                Losses = r.ReadInt32(),
                RankedRuns = r.ReadInt32(),
                UnrankedRuns = r.ReadInt32(),
                Resets = r.ReadInt32(),
            };
            int count = r.ReadByte();
            if (count > MaxPlaces) throw new ProtocolException("Too many places in a profile");
            for (int i = 0; i < count; i++)
                m.Places.Add(new ProfilePlace { Board = (BoardKind)r.ReadByte(), Rank = r.ReadInt32(), Value = r.ReadInt32() });
            return m;
        }
    }

    // Client -> server: an unranked solo run on the player's own PC finished or was restarted (for the profile's
    // counts; the server hosts every other kind of run and counts those itself). Segment: the run's segment, 0 for the
    // whole climb.
    public sealed class RunNote : Message
    {
        public const byte Finished = 1;
        public const byte Restarted = 2;

        public byte Kind;
        public byte Segment;

        public override MessageType Type { get { return MessageType.RunNote; } }

        public override void Write(PacketWriter w)
        {
            w.WriteByte(Kind);
            w.WriteByte(Segment);
        }

        public static RunNote Read(PacketReader r)
        {
            return new RunNote { Kind = r.ReadByte(), Segment = r.ReadByte() };
        }
    }
}
