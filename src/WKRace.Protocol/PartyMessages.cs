using System;
using System.Collections.Generic;

namespace WKRace.Protocol
{
    // Lobbies as rooms (1v1 or party), the ready check, the match roster, chat, and WKSR's settings for the mod.

    internal static class ProtocolChecks
    {
        public static MatchMode Mode(byte value)
        {
            if (value > (byte)MatchMode.Solo) throw new ProtocolException("Unknown match mode");
            return (MatchMode)value;
        }

        // A lobby is a 1v1 or a party.
        public static MatchMode LobbyMode(byte value)
        {
            if (value != (byte)MatchMode.Duel && value != (byte)MatchMode.Party) throw new ProtocolException("Unknown lobby mode");
            return (MatchMode)value;
        }

        public static RacerStatus Status(byte value)
        {
            if (value > (byte)RacerStatus.Left) throw new ProtocolException("Unknown player status");
            return (RacerStatus)value;
        }

        public static int Count(PacketReader r, int max)
        {
            int count = r.ReadByte();
            if (count > max) throw new ProtocolException("Too many entries");
            return count;
        }
    }

    // One player in a lobby, as LobbyState lists them.
    public sealed class LobbyMember
    {
        public string Name = "";
        public int Rating;
        public bool Host;
        // In the lobby's race right now.
        public bool Racing;
    }

    // Server -> each player in a lobby, after every change: who is in it, who hosts, the mode and the rules.
    public sealed class LobbyState : Message
    {
        public uint LobbyId;
        public MatchMode Mode;
        public MatchRules Rules;
        public int MaxPlayers;
        public bool YouAreHost;
        // The lobby's race is under way (nobody can join until it ends).
        public bool Racing;
        // When a lobby nobody has joined closes, or -1.
        public int ExpiresInSeconds = -1;
        // The host lets in players whose game has mods that are not approved.
        public bool AllowUnapproved;
        public List<LobbyMember> Members = new List<LobbyMember>();

        public override MessageType Type { get { return MessageType.LobbyState; } }

        public override void Write(PacketWriter w)
        {
            w.WriteUInt32(LobbyId);
            w.WriteByte((byte)Mode);
            w.WriteByte((byte)Rules);
            w.WriteByte((byte)MaxPlayers);
            w.WriteBool(YouAreHost);
            w.WriteBool(Racing);
            w.WriteInt32(ExpiresInSeconds);
            w.WriteBool(AllowUnapproved);
            if (Members.Count > ProtocolConstants.MaxPartyPlayers) throw new ProtocolException("Too many lobby members");
            w.WriteByte((byte)Members.Count);
            foreach (LobbyMember m in Members)
            {
                w.WriteString(m.Name, ProtocolConstants.MaxNameLength);
                w.WriteInt32(m.Rating);
                w.WriteBool(m.Host);
                w.WriteBool(m.Racing);
            }
        }

        public static LobbyState Read(PacketReader r)
        {
            var m = new LobbyState
            {
                LobbyId = r.ReadUInt32(),
                Mode = ProtocolChecks.LobbyMode(r.ReadByte()),
                Rules = (MatchRules)(r.ReadByte() & (byte)MatchRules.All),
                MaxPlayers = r.ReadByte(),
                YouAreHost = r.ReadBool(),
                Racing = r.ReadBool(),
                ExpiresInSeconds = r.ReadInt32(),
                AllowUnapproved = r.ReadBool(),
            };
            int count = ProtocolChecks.Count(r, ProtocolConstants.MaxPartyPlayers);
            for (int i = 0; i < count; i++)
            {
                m.Members.Add(new LobbyMember
                {
                    Name = r.ReadString(ProtocolConstants.MaxNameLength),
                    Rating = r.ReadInt32(),
                    Host = r.ReadBool(),
                    Racing = r.ReadBool(),
                });
            }
            return m;
        }
    }

    // Client -> server: the host starts the lobby's race (party: two players or more; 1v1: a rematch).
    public sealed class LobbyStart : Message
    {
        public override MessageType Type { get { return MessageType.LobbyStart; } }
        public override void Write(PacketWriter w) { }
        public static LobbyStart Read(PacketReader r) { return new LobbyStart(); }
    }

    // Mod or WKSR -> server: the player is ready to race (their world has loaded). When every racer is ready the
    // countdown starts.
    public sealed class PlayerReady : Message
    {
        public Guid MatchId;

        public override MessageType Type { get { return MessageType.PlayerReady; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
        }

        public static PlayerReady Read(PacketReader r)
        {
            return new PlayerReady { MatchId = r.ReadGuid() };
        }
    }

    public sealed class RosterEntry
    {
        public byte Racer;
        public string Name = "";
        public int Rating;
        public RacerStatus Status;
        // Race time of the finish (Finished) or of going out (Out, Left), else -1.
        public int TimeMs = -1;
    }

    // Server -> client -> mod: everyone in the match and where they stand, after every change. ReadySecondsLeft counts
    // down the ready check (-1 when none is running).
    public sealed class MatchRoster : Message
    {
        public Guid MatchId;
        public byte YourRacer;
        public int ReadySecondsLeft = -1;
        public List<RosterEntry> Racers = new List<RosterEntry>();

        public override MessageType Type { get { return MessageType.MatchRoster; } }

        public RosterEntry Find(int racer)
        {
            foreach (RosterEntry e in Racers) if (e.Racer == racer) return e;
            return null;
        }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteByte(YourRacer);
            w.WriteInt32(ReadySecondsLeft);
            if (Racers.Count > ProtocolConstants.MaxPartyPlayers) throw new ProtocolException("Too many players");
            w.WriteByte((byte)Racers.Count);
            foreach (RosterEntry e in Racers)
            {
                w.WriteByte(e.Racer);
                w.WriteString(e.Name, ProtocolConstants.MaxNameLength);
                w.WriteInt32(e.Rating);
                w.WriteByte((byte)e.Status);
                w.WriteInt32(e.TimeMs);
            }
        }

        public static MatchRoster Read(PacketReader r)
        {
            var m = new MatchRoster { MatchId = r.ReadGuid(), YourRacer = r.ReadByte(), ReadySecondsLeft = r.ReadInt32() };
            int count = ProtocolChecks.Count(r, ProtocolConstants.MaxPartyPlayers);
            for (int i = 0; i < count; i++)
            {
                m.Racers.Add(new RosterEntry
                {
                    Racer = r.ReadByte(),
                    Name = r.ReadString(ProtocolConstants.MaxNameLength),
                    Rating = r.ReadInt32(),
                    Status = ProtocolChecks.Status(r.ReadByte()),
                    TimeMs = r.ReadInt32(),
                });
            }
            return m;
        }
    }

    // A racer's place at the end of a party race (MatchResult.Standings).
    public sealed class Standing
    {
        public byte Racer;
        public string Name = "";
        public int Place;
        public RacerStatus Status;
        // Finish time, or when they went out; -1 when neither.
        public int TimeMs = -1;
        public int Levels;
        // How they went out, in the game's words ("FELL TO THEIR DEATH"), or "".
        public string Note = "";

        internal static void WriteList(PacketWriter w, List<Standing> list)
        {
            int count = list == null ? 0 : list.Count;
            if (count > ProtocolConstants.MaxPartyPlayers) throw new ProtocolException("Too many standings");
            w.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                Standing s = list[i];
                w.WriteByte(s.Racer);
                w.WriteString(s.Name, ProtocolConstants.MaxNameLength);
                w.WriteByte((byte)s.Place);
                w.WriteByte((byte)s.Status);
                w.WriteInt32(s.TimeMs);
                w.WriteInt32(s.Levels);
                w.WriteString(s.Note, ProtocolConstants.MaxDeathTextLength);
            }
        }

        internal static List<Standing> ReadList(PacketReader r)
        {
            int count = ProtocolChecks.Count(r, ProtocolConstants.MaxPartyPlayers);
            var list = new List<Standing>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new Standing
                {
                    Racer = r.ReadByte(),
                    Name = r.ReadString(ProtocolConstants.MaxNameLength),
                    Place = r.ReadByte(),
                    Status = ProtocolChecks.Status(r.ReadByte()),
                    TimeMs = r.ReadInt32(),
                    Levels = r.ReadInt32(),
                    Note = r.ReadString(ProtocolConstants.MaxDeathTextLength),
                });
            }
            return list;
        }
    }

    // WKSR -> mod: settings the player changes in WKSR.
    public sealed class ModSettings : Message
    {
        // 0-100.
        public int GhostOpacityPercent = 45;
        // The hotkeys to use from now on, or null to leave them as they are.
        public KeyBindings Keys;

        public override MessageType Type { get { return MessageType.ModSettings; } }

        public override void Write(PacketWriter w)
        {
            w.WriteByte((byte)Math.Max(0, Math.Min(100, GhostOpacityPercent)));
            w.WriteBool(Keys != null);
            if (Keys != null) Keys.Write(w);
        }

        public static ModSettings Read(PacketReader r)
        {
            var m = new ModSettings { GhostOpacityPercent = Math.Min(100, (int)r.ReadByte()) };
            if (r.ReadBool()) m.Keys = KeyBindings.Read(r);
            return m;
        }
    }

    // Mod -> WKSR -> server: a chat line from this player, to their lobby or match.
    public sealed class ChatSend : Message
    {
        public string Text = "";
        // Which chat room the line is for (ChatRooms).
        public byte Room;

        public override MessageType Type { get { return MessageType.ChatSend; } }

        public override void Write(PacketWriter w)
        {
            w.WriteString(Text, ProtocolConstants.MaxChatLength);
            w.WriteByte(Room);
        }

        public static ChatSend Read(PacketReader r)
        {
            return new ChatSend { Text = r.ReadString(ProtocolConstants.MaxChatLength), Room = r.ReadByte() };
        }
    }

    // The chat rooms. More can be added later; a client shows the ones it knows.
    public static class ChatRooms
    {
        // The player's lobby or match: joined and left with it.
        public const byte Match = 0;
        // Everyone who is connected and has it open.
        public const byte Global = 1;
    }

    // Server -> WKSR: a chat line. System lines (someone joined, left, ...) have no sender.
    public sealed class ChatMessage : Message
    {
        public string From = "";
        public string Text = "";
        public bool System;
        public byte Room;
        // When the server passed the line on (0 for a line it only made up for this player).
        public long SentUnixMs;
        // An older line, sent to a player who just joined the room.
        public bool History;
        // On a note to a player who sent lines too fast: how much longer they are silenced, in ms (0 otherwise).
        public int SilencedMs;

        public override MessageType Type { get { return MessageType.ChatMessage; } }

        public override void Write(PacketWriter w)
        {
            w.WriteString(From, ProtocolConstants.MaxNameLength);
            w.WriteString(Text, ProtocolConstants.MaxChatLength);
            w.WriteBool(System);
            w.WriteByte(Room);
            w.WriteInt64(SentUnixMs);
            w.WriteBool(History);
            w.WriteInt32(SilencedMs);
        }

        public static ChatMessage Read(PacketReader r)
        {
            return new ChatMessage
            {
                From = r.ReadString(ProtocolConstants.MaxNameLength),
                Text = r.ReadString(ProtocolConstants.MaxChatLength),
                System = r.ReadBool(),
                Room = r.ReadByte(),
                SentUnixMs = r.ReadInt64(),
                History = r.ReadBool(),
                SilencedMs = r.ReadInt32(),
            };
        }
    }

    // WKSR -> server: join or leave a chat room that players opt in to (Global). Joining brings the room's lines of
    // the last 24 hours.
    public sealed class ChatJoin : Message
    {
        public byte Room;
        public bool Join;

        public override MessageType Type { get { return MessageType.ChatJoin; } }

        public override void Write(PacketWriter w)
        {
            w.WriteByte(Room);
            w.WriteBool(Join);
        }

        public static ChatJoin Read(PacketReader r)
        {
            return new ChatJoin { Room = r.ReadByte(), Join = r.ReadBool() };
        }
    }
}
