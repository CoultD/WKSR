namespace WKRace.Protocol
{
    // What happens in a player's world that a spectator's copy of it cannot know by itself: an item used (shown as a
    // line, "Alex used Syringe"), a button pressed, a hold button finished, a trigger zone walked into, a shortcut
    // branch generated. The spectator's game finds the same object (by name, near the same place: both worlds come
    // from the same seed) and sets it off too. Sent by racers in a match or ranked solo run; passed on to spectators.
    public static class RunEventKind
    {
        public const byte ItemUsed = 1;
        public const byte Button = 2;
        public const byte HoldFinished = 3;
        public const byte TriggerZone = 4;
        public const byte Branch = 5;
        public const byte EventZone = 6;

        public const byte Max = EventZone;
    }

    // Client -> server: something happened in this player's world. Name: the item, or the object's name; X/Y/Z: where
    // (shared coordinates, the object's position).
    public sealed class RunEvent : Message
    {
        public const int MaxNameLength = ProtocolConstants.MaxItemNameLength;

        public byte Kind;
        public string Name = "";
        public float X, Y, Z;
        public int RaceTimeMs;

        public override MessageType Type { get { return MessageType.RunEvent; } }

        public override void Write(PacketWriter w)
        {
            w.WriteByte(Kind);
            w.WriteString(Name, MaxNameLength);
            w.WriteFloat(X);
            w.WriteFloat(Y);
            w.WriteFloat(Z);
            w.WriteInt32(RaceTimeMs);
        }

        public static RunEvent Read(PacketReader r)
        {
            return new RunEvent
            {
                Kind = r.ReadByte(),
                Name = r.ReadString(MaxNameLength),
                X = r.ReadFiniteFloat(),
                Y = r.ReadFiniteFloat(),
                Z = r.ReadFiniteFloat(),
                RaceTimeMs = r.ReadInt32(),
            };
        }
    }

    // Server -> spectator: a player's RunEvent. History: one from before the spectator came (the world catches up;
    // no line is shown for it).
    public sealed class OpponentEvent : Message
    {
        public byte Racer;
        public byte Kind;
        public string Name = "";
        public float X, Y, Z;
        public int RaceTimeMs;
        public bool History;

        public override MessageType Type { get { return MessageType.OpponentEvent; } }

        public override void Write(PacketWriter w)
        {
            w.WriteByte(Racer);
            w.WriteByte(Kind);
            w.WriteString(Name, RunEvent.MaxNameLength);
            w.WriteFloat(X);
            w.WriteFloat(Y);
            w.WriteFloat(Z);
            w.WriteInt32(RaceTimeMs);
            w.WriteBool(History);
        }

        public static OpponentEvent Read(PacketReader r)
        {
            return new OpponentEvent
            {
                Racer = r.ReadByte(),
                Kind = r.ReadByte(),
                Name = r.ReadString(RunEvent.MaxNameLength),
                X = r.ReadFiniteFloat(),
                Y = r.ReadFiniteFloat(),
                Z = r.ReadFiniteFloat(),
                RaceTimeMs = r.ReadInt32(),
                History = r.ReadBool(),
            };
        }

        public static OpponentEvent From(RunEvent e, byte racer)
        {
            return new OpponentEvent { Racer = racer, Kind = e.Kind, Name = e.Name, X = e.X, Y = e.Y, Z = e.Z, RaceTimeMs = e.RaceTimeMs };
        }
    }
}
