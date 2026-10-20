using System;
using System.Collections.Generic;

namespace WKRace.Protocol
{
    // Spectating a match or a ranked solo run (ServerCore.Spectate). The spectator's game builds the same world from
    // the seed and follows the players in it: the server sends a SpectateStart, the roster, the splits so far, and
    // from then on what the players' opponents get (OpponentState, OpponentSplit, OpponentAttempt, OpponentFinished,
    // MatchRoster). Clients ask only after a ServerFeatures saying the server has it (Spectate).

    // Client -> server: watch something. One of: the player with this tag (online list), the match of this lobby
    // (lobby browser), or the match the player is in themselves (they are out of it and watch the rest).
    public sealed class SpectateRequest : Message
    {
        public uint Tag;
        public uint LobbyId;
        public Guid OwnMatch;

        public override MessageType Type { get { return MessageType.SpectateRequest; } }

        public override void Write(PacketWriter w)
        {
            w.WriteUInt32(Tag);
            w.WriteUInt32(LobbyId);
            w.WriteGuid(OwnMatch);
        }

        public static SpectateRequest Read(PacketReader r)
        {
            return new SpectateRequest { Tag = r.ReadUInt32(), LobbyId = r.ReadUInt32(), OwnMatch = r.ReadGuid() };
        }
    }

    // Server -> client: what is watched, enough to build its world. A ranked solo run that restarts sends a new one
    // (a new seed). GoServerTimeMs: the match's GO on the server clock, or 0 before it.
    public sealed class SpectateStart : Message
    {
        public Guid MatchId;
        public int Seed;
        public MatchMode Mode;
        public MatchRules Rules;
        public bool Ranked;
        public bool Competitive = true;
        public byte Segment;
        public long GoServerTimeMs;
        // The player to follow first.
        public byte WatchRacer;
        // The artifacts banned in the match (its world has them taken out).
        public List<string> Bans = new List<string>();
        // The spectator is one of the match's own players (out of it).
        public bool Own;

        public override MessageType Type { get { return MessageType.SpectateStart; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteInt32(Seed);
            w.WriteByte((byte)Mode);
            w.WriteByte((byte)Rules);
            w.WriteBool(Ranked);
            w.WriteBool(Competitive);
            w.WriteByte(Segment);
            w.WriteInt64(GoServerTimeMs);
            w.WriteByte(WatchRacer);
            BanLists.Write(w, Bans);
            w.WriteBool(Own);
        }

        public static SpectateStart Read(PacketReader r)
        {
            return new SpectateStart
            {
                MatchId = r.ReadGuid(),
                Seed = r.ReadInt32(),
                Mode = (MatchMode)r.ReadByte(),
                Rules = (MatchRules)(r.ReadByte() & (byte)MatchRules.All),
                Ranked = r.ReadBool(),
                Competitive = r.ReadBool(),
                Segment = r.ReadByte(),
                GoServerTimeMs = r.ReadInt64(),
                WatchRacer = r.ReadByte(),
                Bans = BanLists.Read(r),
                Own = r.ReadBool(),
            };
        }
    }

    // Client -> server: stop watching.
    public sealed class SpectateStop : Message
    {
        public override MessageType Type { get { return MessageType.SpectateStop; } }

        public override void Write(PacketWriter w)
        {
        }

        public static SpectateStop Read(PacketReader r)
        {
            return new SpectateStop();
        }
    }

    // Server -> client: the watching is over (the match ended, the player left, the request could not be met).
    public sealed class SpectateEnd : Message
    {
        public Guid MatchId;
        public string Reason = "";

        public override MessageType Type { get { return MessageType.SpectateEnd; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteString(Reason, ProtocolConstants.MaxTextLength);
        }

        public static SpectateEnd Read(PacketReader r)
        {
            return new SpectateEnd { MatchId = r.ReadGuid(), Reason = r.ReadString(ProtocolConstants.MaxTextLength) };
        }
    }

    // Server -> client: how many are watching the match or run the player is in (and the spectators).
    public sealed class SpectatorCount : Message
    {
        public Guid MatchId;
        public ushort Count;

        public override MessageType Type { get { return MessageType.SpectatorCount; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteUInt16(Count);
        }

        public static SpectatorCount Read(PacketReader r)
        {
            return new SpectatorCount { MatchId = r.ReadGuid(), Count = r.ReadUInt16() };
        }
    }
}
