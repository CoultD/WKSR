using System;

namespace WKRace.Protocol
{
    // Server -> client, on connect and then every few seconds: how many players are connected right now, and the
    // newest WKSR release the server knows of (so an open client learns about an update without a restart).
    public sealed class ServerStatus : Message
    {
        public const int MaxVersionLength = 32;

        public int PlayersOnline;
        public string LatestVersion = "";
        // How many players are searching for a ranked 1v1 right now.
        public int PlayersQueued;

        public override MessageType Type { get { return MessageType.ServerStatus; } }

        public override void Write(PacketWriter w)
        {
            w.WriteInt32(PlayersOnline);
            w.WriteString(LatestVersion ?? "", MaxVersionLength);
            w.WriteInt32(PlayersQueued);
        }

        public static ServerStatus Read(PacketReader r)
        {
            return new ServerStatus { PlayersOnline = r.ReadInt32(), LatestVersion = r.ReadString(MaxVersionLength), PlayersQueued = r.ReadInt32() };
        }
    }

    // Client -> server: start a ranked solo run. The server answers with a MatchFound (mode Solo) and hosts the run
    // from there on, with the messages of a match. A restart in the game is PracticeRestart with the run's id.
    // Segment: a run of that segment (Segments) for its leaderboard, or 0 for the whole climb.
    public sealed class SoloStart : Message
    {
        public string GameBuild = "";
        public byte Segment;

        public override MessageType Type { get { return MessageType.SoloStart; } }
        public override void Write(PacketWriter w) { w.WriteString(GameBuild ?? "", 32); w.WriteByte(Segment); }
        public static SoloStart Read(PacketReader r) { return new SoloStart { GameBuild = r.ReadString(32), Segment = r.ReadByte() }; }
    }

    // The mod's hotkeys, as Unity KeyCode numbers. WKSR's options window edits them; the mod keeps them in its
    // config file.
    public sealed class KeyBindings
    {
        public int Ready = 290;     // F9
        public int Forfeit = 291;   // F10
        public int Splits = 288;    // F7
        public int Ghost = 287;     // F6
        public int Chat = DefaultChat;

        // F8. The chat is opened and closed with the same key, so it cannot be a key that types a character.
        public const int DefaultChat = 289;

        // Letters, digits, punctuation, space and the number pad (as Unity KeyCode numbers).
        public static bool TypesText(int key)
        {
            return (key >= 32 && key <= 126) || (key >= 256 && key <= 271);
        }

        public void Write(PacketWriter w)
        {
            w.WriteInt32(Ready);
            w.WriteInt32(Forfeit);
            w.WriteInt32(Splits);
            w.WriteInt32(Ghost);
            w.WriteInt32(Chat);
        }

        public static KeyBindings Read(PacketReader r)
        {
            return new KeyBindings { Ready = r.ReadInt32(), Forfeit = r.ReadInt32(), Splits = r.ReadInt32(), Ghost = r.ReadInt32(), Chat = r.ReadInt32() };
        }

        public bool Same(KeyBindings other)
        {
            return other != null && Ready == other.Ready && Forfeit == other.Forfeit && Splits == other.Splits && Ghost == other.Ghost && Chat == other.Chat;
        }

        public KeyBindings Clone()
        {
            return (KeyBindings)MemberwiseClone();
        }
    }

    // Client -> server: the Steam account the player's game is running under, as the game's own Steamworks reports
    // it. The server links the player to that account, or gives them the player the account already has. There is no
    // button and no browser: starting the game through Steam is the sign-in. The number is taken on the client's
    // word (the operator's choice); the browser sign-in remains for a server that wants it proven.
    public sealed class SteamIdentity : Message
    {
        public ulong SteamId;

        public override MessageType Type { get { return MessageType.SteamIdentity; } }
        public override void Write(PacketWriter w) { w.WriteInt64(unchecked((long)SteamId)); }
        public static SteamIdentity Read(PacketReader r) { return new SteamIdentity { SteamId = unchecked((ulong)r.ReadInt64()) }; }
    }

    // Mod -> WKSR after it connects: the hotkeys the mod uses now.
    public sealed class ModKeys : Message
    {
        public KeyBindings Keys = new KeyBindings();

        public override MessageType Type { get { return MessageType.ModKeys; } }
        public override void Write(PacketWriter w) { Keys.Write(w); }
        public static ModKeys Read(PacketReader r) { return new ModKeys { Keys = KeyBindings.Read(r) }; }
    }
}
