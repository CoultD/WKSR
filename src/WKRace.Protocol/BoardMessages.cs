using System;
using System.Collections.Generic;

namespace WKRace.Protocol
{
    // The leaderboards. One row per player: their rating, or their fastest finished time (a faster run replaces it).
    public enum BoardKind : byte
    {
        // Ranked 1v1: the players' ratings.
        Rating = 0,
        // Ranked 1v1: fastest finished time.
        DuelTime = 1,
        // Ranked solo: fastest finished time.
        SoloTime = 2,
        // Each segment has a board of its own, numbered from 16 (Segments.Board): fastest time over the segment.
    }

    // Client -> server: one page of a board. Search: only the players whose name contains it. FindMe: the page
    // this player's own row is on (Offset is then ignored).
    public sealed class BoardRequest : Message
    {
        public const int MaxRows = 15;
        public const int MaxSearchLength = ProtocolConstants.MaxNameLength;

        public BoardKind Board;
        public int Offset;
        public byte Count = 12;
        public string Search = "";
        public bool FindMe;

        public override MessageType Type { get { return MessageType.BoardRequest; } }

        public override void Write(PacketWriter w)
        {
            w.WriteByte((byte)Board);
            w.WriteInt32(Offset);
            w.WriteByte(Count);
            w.WriteString(Search ?? "", MaxSearchLength);
            w.WriteBool(FindMe);
        }

        public static BoardRequest Read(PacketReader r)
        {
            return new BoardRequest { Board = BoardChecks.Kind(r.ReadByte()), Offset = r.ReadInt32(), Count = r.ReadByte(), Search = r.ReadString(MaxSearchLength), FindMe = r.ReadBool() };
        }
    }

    public sealed class BoardRow
    {
        public const int MaxDetailLength = 32;

        public int Rank;
        public string Name = "";
        // The rating, or the time in ms.
        public int Value;
        // Rating board: "12W 3L". Time boards: the WKRace version the time was set on.
        public string Detail = "";
        // A time board's row that has a public replay (the 20 fastest).
        public bool Replay;
        public bool You;
    }

    // Server -> client: a page of a board. Total: how many rows the board (or the search) has. YourRank: where the
    // player asking stands on the whole board, 0 when they are not on it.
    public sealed class BoardPage : Message
    {
        public BoardKind Board;
        public int Offset;
        public int Total;
        public int YourRank;
        public string Search = "";
        public List<BoardRow> Rows = new List<BoardRow>();

        public override MessageType Type { get { return MessageType.BoardPage; } }

        public override void Write(PacketWriter w)
        {
            if (Rows.Count > BoardRequest.MaxRows) throw new ProtocolException("Too many board rows");
            w.WriteByte((byte)Board);
            w.WriteInt32(Offset);
            w.WriteInt32(Total);
            w.WriteInt32(YourRank);
            w.WriteString(Search ?? "", BoardRequest.MaxSearchLength);
            w.WriteByte((byte)Rows.Count);
            foreach (BoardRow row in Rows)
            {
                w.WriteInt32(row.Rank);
                w.WriteString(row.Name ?? "", ProtocolConstants.MaxNameLength);
                w.WriteInt32(row.Value);
                w.WriteString(row.Detail ?? "", BoardRow.MaxDetailLength);
                w.WriteByte((byte)((row.Replay ? 1 : 0) | (row.You ? 2 : 0)));
            }
        }

        public static BoardPage Read(PacketReader r)
        {
            var m = new BoardPage { Board = BoardChecks.Kind(r.ReadByte()), Offset = r.ReadInt32(), Total = r.ReadInt32(), YourRank = r.ReadInt32(), Search = r.ReadString(BoardRequest.MaxSearchLength) };
            int count = r.ReadByte();
            if (count > BoardRequest.MaxRows) throw new ProtocolException("Too many board rows");
            for (int i = 0; i < count; i++)
            {
                var row = new BoardRow { Rank = r.ReadInt32(), Name = r.ReadString(ProtocolConstants.MaxNameLength), Value = r.ReadInt32(), Detail = r.ReadString(BoardRow.MaxDetailLength) };
                byte flags = r.ReadByte();
                row.Replay = (flags & 1) != 0;
                row.You = (flags & 2) != 0;
                m.Rows.Add(row);
            }
            return m;
        }
    }

    // Client -> server: the replay of the run that holds this rank on a time board (only the 20 fastest have one).
    public sealed class ReplayRequest : Message
    {
        public BoardKind Board;
        public int Rank;

        public override MessageType Type { get { return MessageType.ReplayRequest; } }
        public override void Write(PacketWriter w) { w.WriteByte((byte)Board); w.WriteInt32(Rank); }
        public static ReplayRequest Read(PacketReader r) { return new ReplayRequest { Board = BoardChecks.Kind(r.ReadByte()), Rank = r.ReadInt32() }; }
    }

    // Server -> client: one piece of a replay (a packed RunRecording), in order. Count 0: there is no replay for
    // that rank (any more).
    public sealed class ReplayChunk : Message
    {
        public const int MaxBytes = 1800;
        public const int MaxChunks = 4000;

        public BoardKind Board;
        public int Rank;
        public ushort Index;
        public ushort Count;
        public byte[] Bytes = new byte[0];

        public override MessageType Type { get { return MessageType.ReplayChunk; } }

        public override void Write(PacketWriter w)
        {
            if (Bytes.Length > MaxBytes) throw new ProtocolException("Replay piece too large");
            w.WriteByte((byte)Board);
            w.WriteInt32(Rank);
            w.WriteUInt16(Index);
            w.WriteUInt16(Count);
            w.WriteUInt16((ushort)Bytes.Length);
            w.WriteFixedBytes(Bytes, Bytes.Length);
        }

        public static ReplayChunk Read(PacketReader r)
        {
            var m = new ReplayChunk { Board = BoardChecks.Kind(r.ReadByte()), Rank = r.ReadInt32(), Index = r.ReadUInt16(), Count = r.ReadUInt16() };
            int length = r.ReadUInt16();
            if (length > MaxBytes) throw new ProtocolException("Replay piece too large");
            m.Bytes = r.ReadFixedBytes(length);
            return m;
        }
    }

    // Server -> client after a ranked run that reached the end: what it did to the player's row on its time board.
    // Problem: why the time was not taken, when it was not ("" otherwise).
    public sealed class BoardNote : Message
    {
        public const int MaxProblemLength = 120;

        public BoardKind Board;
        public int TimeMs;
        // The player's row after this run: its rank and time.
        public int Rank;
        public int BestMs;
        public bool PersonalBest;
        public string Problem = "";

        public override MessageType Type { get { return MessageType.BoardNote; } }

        public override void Write(PacketWriter w)
        {
            w.WriteByte((byte)Board);
            w.WriteInt32(TimeMs);
            w.WriteInt32(Rank);
            w.WriteInt32(BestMs);
            w.WriteBool(PersonalBest);
            w.WriteString(Problem ?? "", MaxProblemLength);
        }

        public static BoardNote Read(PacketReader r)
        {
            return new BoardNote { Board = BoardChecks.Kind(r.ReadByte()), TimeMs = r.ReadInt32(), Rank = r.ReadInt32(), BestMs = r.ReadInt32(), PersonalBest = r.ReadBool(), Problem = r.ReadString(MaxProblemLength) };
        }
    }

    internal static class BoardChecks
    {
        public static BoardKind Kind(byte value)
        {
            if (value > (byte)BoardKind.SoloTime && Segments.OfBoard((BoardKind)value) == Segments.None) throw new ProtocolException("Unknown leaderboard");
            return (BoardKind)value;
        }
    }
}
