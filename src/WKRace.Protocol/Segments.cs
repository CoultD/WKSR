using System;

namespace WKRace.Protocol
{
    // A segment: a solo run of one part of the climb. It starts where the game starts and ends at a set point, where
    // the clock stops. A segment is played like a solo run (ranked on the match server, or unranked on the player's
    // own PC) and has a leaderboard of its own.
    public sealed class SegmentInfo
    {
        public byte Id;
        // "SILOS" for buttons and headers, "Silos" in a sentence.
        public string Name = "";
        public string Title = "";
        public string About = "";
        // Where it ends: the press of a button in a level whose name starts with EndLevel, when that press acts on an
        // object whose name starts with EndTarget.
        public string EndLevel = "";
        public string EndTarget = "";
        // The artifact room lies within the segment. Without it there is nothing to ban, so no ban phase.
        public bool Artifacts;
    }

    public static class Segments
    {
        public const byte None = 0;
        public const byte Silos = 1;

        // A segment's leaderboard is the board numbered FirstBoard + its id.
        private const int FirstBoard = 16;

        public static readonly SegmentInfo[] All =
        {
            new SegmentInfo
            {
                Id = Silos,
                Name = "SILOS",
                Title = "Silos",
                About = "From the start of the game to the button that seals the shaft at the end of the Silos, before the elevator.",
                EndLevel = "Campaign_Interlude_Silo_To_Pipeworks",
                // The seal button stops the goo and closes the shaft door (and unlocks the gate to the elevator).
                EndTarget = "Goop Controller - Stop And Close Door",
            },
        };

        public static SegmentInfo Find(byte id)
        {
            if (id == None) return null;
            foreach (SegmentInfo segment in All)
                if (segment.Id == id) return segment;
            return null;
        }

        // "SILOS", or "" when there is no such segment.
        public static string Name(byte id)
        {
            SegmentInfo segment = Find(id);
            return segment != null ? segment.Name : "";
        }

        public static BoardKind Board(byte id)
        {
            return (BoardKind)(FirstBoard + id);
        }

        // The segment a board is of, or None for the other boards.
        public static byte OfBoard(BoardKind kind)
        {
            int id = (int)kind - FirstBoard;
            return id > 0 && id <= byte.MaxValue && Find((byte)id) != null ? (byte)id : None;
        }

        // The modifiers as the segment is played with them: no artifact bans when the artifact room is not in it.
        public static MatchRules Rules(byte id, MatchRules rules)
        {
            SegmentInfo segment = Find(id);
            return segment != null && !segment.Artifacts ? rules & ~MatchRules.ArtifactBans : rules;
        }
    }
}
