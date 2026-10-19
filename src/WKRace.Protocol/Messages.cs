using System;
using System.Collections.Generic;

namespace WKRace.Protocol
{
    public enum MessageType : byte
    {
        ClientHello = 1,
        ServerWelcome = 2,
        Error = 3,
        Ping = 4,
        Pong = 5,

        QueueJoin = 10,
        QueueLeave = 11,
        QueueStatus = 12,
        MatchFound = 13,
        LobbyCreate = 14,
        LobbyCreated = 15,
        LobbyJoin = 16,
        LobbyCancel = 17,
        LobbyClosed = 18,
        LobbyState = 19,

        MatchReady = 20,
        MatchStart = 21,
        StartCountdown = 22,
        LobbyStart = 23,
        PlayerReady = 24,
        MatchRoster = 25,
        ServerStatus = 26,
        SoloStart = 27,
        // The lobby browser (BrowserMessages.cs).
        ServerFeatures = 28,

        PlayerState = 30,
        OpponentState = 31,
        SplitReached = 32,
        OpponentSplit = 33,
        AttemptStarted = 34,
        OpponentAttempt = 35,
        PlayerFinished = 36,
        OpponentFinished = 37,
        PlayerForfeit = 38,
        MatchResult = 39,
        GameStatus = 40,
        MatchPaused = 41,
        MatchResumed = 42,
        SteamLinkStart = 43,
        SteamLinkUrl = 44,
        SteamLinkResult = 45,
        PlayerDied = 46,
        SteamIdentity = 47,

        BanReady = 50,
        BanPick = 51,
        BanState = 52,

        ModHello = 60,
        ClientStatus = 61,
        RacePaused = 62,
        RaceResumed = 63,
        PracticeMark = 64,
        PracticeRestart = 65,
        ModSettings = 66,
        ModKeys = 67,
        LoadedMods = 68,

        ChatSend = 70,
        ChatMessage = 71,
        ChatJoin = 72,
        LobbyVisibility = 73,
        LobbyListRequest = 74,
        LobbyList = 75,

        VoiceHello = 80,
        VoiceState = 81,
        VoicePeers = 82,
        VoiceActivity = 83,

        BoardRequest = 90,
        BoardPage = 91,
        ReplayRequest = 92,
        ReplayChunk = 93,
        BoardNote = 94,
        ModVerdict = 95,
        ModsRequest = 96,
        ModsPage = 97,
        ModMark = 98,
    }

    public enum ErrorCode : byte
    {
        Unknown = 0,
        VersionMismatch = 1,
        BadToken = 2,
        InvalidName = 3,
        RateLimited = 4,
        ServerFull = 5,
        ProtocolViolation = 6,
        NotInMatch = 7,
        AlreadyQueued = 8,
        AlreadyInMatch = 9,
        ServerShuttingDown = 10,
        LobbyNotFound = 11,
        LobbyUnavailable = 12,
        LobbyBuildMismatch = 13,
        // Too many new players from one address, or the server is not taking new players. Retrying will not help.
        RegistrationLimited = 14,
        // The lobby's settings (artifact bans on or off) differ from the joining player's.
        LobbySettingsMismatch = 15,
        // Ranked races need a linked Steam account on this server.
        SteamLinkRequired = 16,
        // The lobby has as many players as its mode allows.
        LobbyFull = 17,
        // The lobby's race is under way; joining waits for it to end.
        LobbyBusy = 18,
        // Only the lobby's host can do that.
        NotHost = 19,
        // The player's game has a mod loaded that is not approved for what they asked for.
        ModsNotApproved = 20,
    }

    // 1v1 (ranked or friendly), or a party race of up to ProtocolConstants.MaxPartyPlayers.
    public enum MatchMode : byte
    {
        Duel = 0,
        Party = 1,
        // One player, hosted by the server: a ranked solo run.
        Solo = 2,
    }

    // The modifiers a race is played with. Ranked races always use Ranked; a lobby's host picks them for everyone in
    // it, and practice runs pick their own. Identical worlds (same contents for every racer) are always on.
    [Flags]
    public enum MatchRules : byte
    {
        None = 0,
        ArtifactBans = 1,
        // Curated seeds, in three parts (RaceGuarantees in the mod).
        DeepStorageFood = 2,
        GuaranteedPerks = 4,
        GuaranteedItems = 8,
        CuratedSeeds = DeepStorageFood | GuaranteedPerks | GuaranteedItems,
        Ranked = ArtifactBans | CuratedSeeds,
        All = ArtifactBans | CuratedSeeds,
    }

    // Where a racer stands in a match, as the roster shows it.
    public enum RacerStatus : byte
    {
        Loading = 0,
        Loaded = 1,
        Ready = 2,
        Racing = 3,
        Finished = 4,
        // Died (one life).
        Out = 5,
        // Forfeited, disconnected for too long, or disqualified.
        Left = 6,
    }

    public enum MatchOutcome : byte
    {
        Win = 1,
        Loss = 2,
        Aborted = 4,
    }

    // How a match ended. Finish and Death end the race for both players, and both keep their run for practice.
    public enum MatchEnd : byte
    {
        Other = 0,
        Finish = 1,
        Death = 2,
    }

    public enum ClientPhase : byte
    {
        Offline = 0,
        Connecting = 1,
        Idle = 2,
        Searching = 3,
        InMatch = 4,
    }

    [Flags]
    public enum StateFlags : byte
    {
        None = 0,
        Dead = 1,
        LeftHold = 2,
        RightHold = 4,
        Loading = 8,
    }

    public abstract class Message
    {
        public abstract MessageType Type { get; }
        public abstract void Write(PacketWriter w);
    }

    // ---- Session -------------------------------------------------------

    public sealed class ClientHello : Message
    {
        public int ProtocolVersion = ProtocolConstants.ProtocolVersion;
        public Guid PlayerId;
        public byte[] Token = new byte[ProtocolConstants.TokenLength];
        public string Name = "";
        public string ClientVersion = "";
        // Whether the game (with the mod) is connected to this client right now.
        public bool GameConnected = true;

        public override MessageType Type { get { return MessageType.ClientHello; } }

        public override void Write(PacketWriter w)
        {
            w.WriteInt32(ProtocolVersion);
            w.WriteGuid(PlayerId);
            w.WriteFixedBytes(Token, ProtocolConstants.TokenLength);
            w.WriteString(Name, ProtocolConstants.MaxNameLength);
            w.WriteString(ClientVersion, 32);
            w.WriteBool(GameConnected);
        }

        public static ClientHello Read(PacketReader r)
        {
            var m = new ClientHello();
            m.ProtocolVersion = r.ReadInt32();
            m.PlayerId = r.ReadGuid();
            m.Token = r.ReadFixedBytes(ProtocolConstants.TokenLength);
            m.Name = r.ReadString(ProtocolConstants.MaxNameLength);
            m.ClientVersion = r.ReadString(32);
            m.GameConnected = r.ReadBool();
            return m;
        }
    }

    public sealed class ServerWelcome : Message
    {
        public Guid PlayerId;
        public string Name = "";
        public int Rating;
        public int Wins;
        public int Losses;
        public long ServerTimeMs;
        public int PlayersOnline;
        public int PlayersQueued;
        // Steam sign-in: whether the server offers it, whether this player is linked, and whether ranked needs it.
        public bool SteamAvailable;
        public bool SteamLinked;
        public bool SteamRequiredForRanked;

        public override MessageType Type { get { return MessageType.ServerWelcome; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(PlayerId);
            w.WriteString(Name, ProtocolConstants.MaxNameLength);
            w.WriteInt32(Rating);
            w.WriteInt32(Wins);
            w.WriteInt32(Losses);
            w.WriteInt64(ServerTimeMs);
            w.WriteInt32(PlayersOnline);
            w.WriteInt32(PlayersQueued);
            w.WriteBool(SteamAvailable);
            w.WriteBool(SteamLinked);
            w.WriteBool(SteamRequiredForRanked);
        }

        public static ServerWelcome Read(PacketReader r)
        {
            var m = new ServerWelcome();
            m.PlayerId = r.ReadGuid();
            m.Name = r.ReadString(ProtocolConstants.MaxNameLength);
            m.Rating = r.ReadInt32();
            m.Wins = r.ReadInt32();
            m.Losses = r.ReadInt32();
            m.ServerTimeMs = r.ReadInt64();
            m.PlayersOnline = r.ReadInt32();
            m.PlayersQueued = r.ReadInt32();
            m.SteamAvailable = r.ReadBool();
            m.SteamLinked = r.ReadBool();
            m.SteamRequiredForRanked = r.ReadBool();
            return m;
        }
    }

    public sealed class ErrorMessage : Message
    {
        public ErrorCode Code;
        public string Text = "";

        public ErrorMessage() { }

        public ErrorMessage(ErrorCode code, string text)
        {
            Code = code;
            Text = text ?? "";
        }

        public override MessageType Type { get { return MessageType.Error; } }

        public override void Write(PacketWriter w)
        {
            w.WriteByte((byte)Code);
            w.WriteString(Text, ProtocolConstants.MaxTextLength);
        }

        public static ErrorMessage Read(PacketReader r)
        {
            var m = new ErrorMessage();
            m.Code = (ErrorCode)r.ReadByte();
            m.Text = r.ReadString(ProtocolConstants.MaxTextLength);
            return m;
        }
    }

    public sealed class Ping : Message
    {
        public long ClientTimeMs;

        public override MessageType Type { get { return MessageType.Ping; } }

        public override void Write(PacketWriter w)
        {
            w.WriteInt64(ClientTimeMs);
        }

        public static Ping Read(PacketReader r)
        {
            return new Ping { ClientTimeMs = r.ReadInt64() };
        }
    }

    public sealed class Pong : Message
    {
        public long ClientTimeMs;
        public long ServerTimeMs;

        public override MessageType Type { get { return MessageType.Pong; } }

        public override void Write(PacketWriter w)
        {
            w.WriteInt64(ClientTimeMs);
            w.WriteInt64(ServerTimeMs);
        }

        public static Pong Read(PacketReader r)
        {
            var m = new Pong();
            m.ClientTimeMs = r.ReadInt64();
            m.ServerTimeMs = r.ReadInt64();
            return m;
        }
    }

    // ---- Matchmaking ---------------------------------------------------

    public sealed class QueueJoin : Message
    {
        // Identifies the installed game build (Steam build id); players are only matched on the same build.
        // Empty when unknown, which matches anyone. Ranked races always use MatchRules.Ranked.
        public string GameBuild = "";

        public override MessageType Type { get { return MessageType.QueueJoin; } }

        public override void Write(PacketWriter w)
        {
            w.WriteString(GameBuild, 32);
        }

        public static QueueJoin Read(PacketReader r)
        {
            return new QueueJoin { GameBuild = r.ReadString(32) };
        }
    }

    public sealed class QueueLeave : Message
    {
        public override MessageType Type { get { return MessageType.QueueLeave; } }
        public override void Write(PacketWriter w) { }
        public static QueueLeave Read(PacketReader r) { return new QueueLeave(); }
    }

    public sealed class QueueStatus : Message
    {
        public bool Queued;
        public int PlayersQueued;
        public int WaitSeconds;

        public override MessageType Type { get { return MessageType.QueueStatus; } }

        public override void Write(PacketWriter w)
        {
            w.WriteBool(Queued);
            w.WriteInt32(PlayersQueued);
            w.WriteInt32(WaitSeconds);
        }

        public static QueueStatus Read(PacketReader r)
        {
            var m = new QueueStatus();
            m.Queued = r.ReadBool();
            m.PlayersQueued = r.ReadInt32();
            m.WaitSeconds = r.ReadInt32();
            return m;
        }
    }

    public sealed class MatchFound : Message
    {
        public Guid MatchId;
        public int Seed;
        public string OpponentName = "";
        public int OpponentRating;
        public bool Competitive = true;
        public int TimeLimitSeconds;
        // -1 for a fresh match, otherwise the race clock (ms) of a match being rejoined.
        public int ResumeElapsedMs = -1;
        // The attempt number the server currently has for this player.
        public int Attempt = 1;
        // How long a match stays paused for a missing player before they forfeit.
        public int GraceSeconds = 60;
        // False for friendly (lobby) races, which do not change ratings.
        public bool Ranked = true;
        // The modifiers. With ArtifactBans the match starts with an artifact ban phase (BanState messages follow).
        public MatchRules Rules;
        public MatchMode Mode;
        // This player's number in the match (the Racer of Opponent* messages and the roster), and how many race.
        public byte YourRacer;
        public int PlayerCount = 2;
        // Once every world has loaded, each racer has this long to press ready. 0: no ready check (practice).
        public int ReadySeconds;
        // A segment run (Segments): the run ends where the segment does. 0: the whole climb.
        public byte Segment;

        public bool ArtifactBans
        {
            get { return (Rules & MatchRules.ArtifactBans) != 0; }
            set { Rules = value ? Rules | MatchRules.ArtifactBans : Rules & ~MatchRules.ArtifactBans; }
        }

        public bool Party { get { return Mode == MatchMode.Party; } }

        public override MessageType Type { get { return MessageType.MatchFound; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteInt32(Seed);
            w.WriteString(OpponentName, ProtocolConstants.MaxNameLength);
            w.WriteInt32(OpponentRating);
            w.WriteBool(Competitive);
            w.WriteInt32(TimeLimitSeconds);
            w.WriteInt32(ResumeElapsedMs);
            w.WriteInt32(Attempt);
            w.WriteInt32(GraceSeconds);
            w.WriteBool(Ranked);
            w.WriteByte((byte)Rules);
            w.WriteByte((byte)Mode);
            w.WriteByte(YourRacer);
            w.WriteInt32(PlayerCount);
            w.WriteInt32(ReadySeconds);
            w.WriteByte(Segment);
        }

        public static MatchFound Read(PacketReader r)
        {
            var m = new MatchFound();
            m.MatchId = r.ReadGuid();
            m.Seed = r.ReadInt32();
            m.OpponentName = r.ReadString(ProtocolConstants.MaxNameLength);
            m.OpponentRating = r.ReadInt32();
            m.Competitive = r.ReadBool();
            m.TimeLimitSeconds = r.ReadInt32();
            m.ResumeElapsedMs = r.ReadInt32();
            m.Attempt = r.ReadInt32();
            m.GraceSeconds = r.ReadInt32();
            m.Ranked = r.ReadBool();
            m.Rules = (MatchRules)(r.ReadByte() & (byte)MatchRules.All);
            m.Mode = ProtocolChecks.Mode(r.ReadByte());
            m.YourRacer = r.ReadByte();
            m.PlayerCount = r.ReadInt32();
            m.ReadySeconds = r.ReadInt32();
            m.Segment = r.ReadByte();
            return m;
        }
    }

    // ---- Lobbies (friendly, unranked races with a join code) ---------------

    public sealed class LobbyCreate : Message
    {
        public string GameBuild = "";
        public MatchMode Mode;
        // Set by the host for everyone in the lobby. Artifact bans are a 1v1 modifier only.
        public MatchRules Rules = MatchRules.Ranked;
        // Players whose game has mods that are not approved may join (and the host may have them).
        public bool AllowUnapproved;

        public override MessageType Type { get { return MessageType.LobbyCreate; } }

        public override void Write(PacketWriter w)
        {
            w.WriteString(GameBuild, 32);
            w.WriteByte((byte)Mode);
            w.WriteByte((byte)Rules);
            w.WriteBool(AllowUnapproved);
        }

        public static LobbyCreate Read(PacketReader r)
        {
            return new LobbyCreate
            {
                GameBuild = r.ReadString(32),
                Mode = ProtocolChecks.LobbyMode(r.ReadByte()),
                Rules = (MatchRules)(r.ReadByte() & (byte)MatchRules.All),
                AllowUnapproved = r.ReadBool(),
            };
        }
    }

    public sealed class LobbyCreated : Message
    {
        public uint LobbyId;
        // How players outside this machine reach the server ("host:port"), or "" when the server was not told.
        public string PublicAddress = "";
        public int ExpiresInSeconds;

        public override MessageType Type { get { return MessageType.LobbyCreated; } }

        public override void Write(PacketWriter w)
        {
            w.WriteUInt32(LobbyId);
            w.WriteString(PublicAddress, ProtocolConstants.MaxAddressLength);
            w.WriteInt32(ExpiresInSeconds);
        }

        public static LobbyCreated Read(PacketReader r)
        {
            var m = new LobbyCreated();
            m.LobbyId = r.ReadUInt32();
            m.PublicAddress = r.ReadString(ProtocolConstants.MaxAddressLength);
            m.ExpiresInSeconds = r.ReadInt32();
            return m;
        }
    }

    public sealed class LobbyJoin : Message
    {
        public uint LobbyId;
        public string GameBuild = "";

        public override MessageType Type { get { return MessageType.LobbyJoin; } }

        public override void Write(PacketWriter w)
        {
            w.WriteUInt32(LobbyId);
            w.WriteString(GameBuild, 32);
        }

        public static LobbyJoin Read(PacketReader r)
        {
            var m = new LobbyJoin();
            m.LobbyId = r.ReadUInt32();
            m.GameBuild = r.ReadString(32);
            return m;
        }
    }

    // Client -> server: leave the lobby. A host who leaves hands the lobby to the next player (or closes it when alone).
    public sealed class LobbyCancel : Message
    {
        public override MessageType Type { get { return MessageType.LobbyCancel; } }
        public override void Write(PacketWriter w) { }
        public static LobbyCancel Read(PacketReader r) { return new LobbyCancel(); }
    }

    // Server -> a lobby's player: the lobby is gone for them (closed, expired, or they left it).
    public sealed class LobbyClosed : Message
    {
        public uint LobbyId;
        public string Reason = "";

        public override MessageType Type { get { return MessageType.LobbyClosed; } }

        public override void Write(PacketWriter w)
        {
            w.WriteUInt32(LobbyId);
            w.WriteString(Reason, ProtocolConstants.MaxTextLength);
        }

        public static LobbyClosed Read(PacketReader r)
        {
            var m = new LobbyClosed();
            m.LobbyId = r.ReadUInt32();
            m.Reason = r.ReadString(ProtocolConstants.MaxTextLength);
            return m;
        }
    }

    // ---- Match lifecycle -----------------------------------------------

    public sealed class MatchReady : Message
    {
        public Guid MatchId;
        public uint WorldHash;
        public int LevelCount;

        public override MessageType Type { get { return MessageType.MatchReady; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteUInt32(WorldHash);
            w.WriteInt32(LevelCount);
        }

        public static MatchReady Read(PacketReader r)
        {
            var m = new MatchReady();
            m.MatchId = r.ReadGuid();
            m.WorldHash = r.ReadUInt32();
            m.LevelCount = r.ReadInt32();
            return m;
        }
    }

    public sealed class MatchStart : Message
    {
        public Guid MatchId;
        public long GoServerTimeMs;

        public override MessageType Type { get { return MessageType.MatchStart; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteInt64(GoServerTimeMs);
        }

        public static MatchStart Read(PacketReader r)
        {
            var m = new MatchStart();
            m.MatchId = r.ReadGuid();
            m.GoServerTimeMs = r.ReadInt64();
            return m;
        }
    }

    // Client -> mod. Negative MsUntilGo means the race started that long ago.
    public sealed class StartCountdown : Message
    {
        public Guid MatchId;
        public int MsUntilGo;

        public override MessageType Type { get { return MessageType.StartCountdown; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteInt32(MsUntilGo);
        }

        public static StartCountdown Read(PacketReader r)
        {
            var m = new StartCountdown();
            m.MatchId = r.ReadGuid();
            m.MsUntilGo = r.ReadInt32();
            return m;
        }
    }

    public abstract class StateMessage : Message
    {
        public const float MaxCoordinate = 1000000f;

        public int Attempt;
        public int RaceTimeMs;
        public float X, Y, Z;
        public float Yaw, Pitch;
        public StateFlags Flags;
        public float LeftX, LeftY, LeftZ;
        public float RightX, RightY, RightZ;

        public override void Write(PacketWriter w)
        {
            w.WriteInt32(Attempt);
            w.WriteInt32(RaceTimeMs);
            w.WriteFloat(X); w.WriteFloat(Y); w.WriteFloat(Z);
            w.WriteFloat(Yaw); w.WriteFloat(Pitch);
            w.WriteByte((byte)Flags);
            if ((Flags & StateFlags.LeftHold) != 0) { w.WriteFloat(LeftX); w.WriteFloat(LeftY); w.WriteFloat(LeftZ); }
            if ((Flags & StateFlags.RightHold) != 0) { w.WriteFloat(RightX); w.WriteFloat(RightY); w.WriteFloat(RightZ); }
        }

        protected void ReadBody(PacketReader r)
        {
            Attempt = r.ReadInt32();
            RaceTimeMs = r.ReadInt32();
            X = ReadCoordinate(r); Y = ReadCoordinate(r); Z = ReadCoordinate(r);
            Yaw = r.ReadFiniteFloat(); Pitch = r.ReadFiniteFloat();
            Flags = (StateFlags)r.ReadByte();
            if ((Flags & StateFlags.LeftHold) != 0) { LeftX = ReadCoordinate(r); LeftY = ReadCoordinate(r); LeftZ = ReadCoordinate(r); }
            if ((Flags & StateFlags.RightHold) != 0) { RightX = ReadCoordinate(r); RightY = ReadCoordinate(r); RightZ = ReadCoordinate(r); }
        }

        private static float ReadCoordinate(PacketReader r)
        {
            float v = r.ReadFiniteFloat();
            if (v > MaxCoordinate || v < -MaxCoordinate) throw new ProtocolException("Coordinate out of range");
            return v;
        }

        public void CopyFrom(StateMessage other)
        {
            Attempt = other.Attempt; RaceTimeMs = other.RaceTimeMs;
            X = other.X; Y = other.Y; Z = other.Z;
            Yaw = other.Yaw; Pitch = other.Pitch; Flags = other.Flags;
            LeftX = other.LeftX; LeftY = other.LeftY; LeftZ = other.LeftZ;
            RightX = other.RightX; RightY = other.RightY; RightZ = other.RightZ;
        }
    }

    public sealed class PlayerState : StateMessage
    {
        public override MessageType Type { get { return MessageType.PlayerState; } }

        public static PlayerState Read(PacketReader r)
        {
            var m = new PlayerState();
            m.ReadBody(r);
            return m;
        }
    }

    // Server -> client -> mod. Racer says whose state it is; it goes last and is optional (0 when missing), so the
    // runs WKSR recorded before party races existed still read.
    public sealed class OpponentState : StateMessage
    {
        public byte Racer;

        public override MessageType Type { get { return MessageType.OpponentState; } }

        public override void Write(PacketWriter w)
        {
            base.Write(w);
            w.WriteByte(Racer);
        }

        public static OpponentState Read(PacketReader r)
        {
            var m = new OpponentState();
            m.ReadBody(r);
            m.Racer = r.Remaining > 0 ? r.ReadByte() : (byte)0;
            return m;
        }
    }

    public abstract class SplitMessage : Message
    {
        public int Attempt;
        public int Index;
        public string Level = "";
        public string Region = "";
        public int RaceTimeMs;

        public override void Write(PacketWriter w)
        {
            w.WriteInt32(Attempt);
            w.WriteInt32(Index);
            w.WriteString(Level, ProtocolConstants.MaxLevelNameLength);
            w.WriteString(Region, ProtocolConstants.MaxLevelNameLength);
            w.WriteInt32(RaceTimeMs);
        }

        protected void ReadBody(PacketReader r)
        {
            Attempt = r.ReadInt32();
            Index = r.ReadInt32();
            Level = r.ReadString(ProtocolConstants.MaxLevelNameLength);
            Region = r.ReadString(ProtocolConstants.MaxLevelNameLength);
            RaceTimeMs = r.ReadInt32();
        }
    }

    public sealed class SplitReached : SplitMessage
    {
        public override MessageType Type { get { return MessageType.SplitReached; } }

        public static SplitReached Read(PacketReader r)
        {
            var m = new SplitReached();
            m.ReadBody(r);
            return m;
        }
    }

    public sealed class OpponentSplit : SplitMessage
    {
        public byte Racer;

        public override MessageType Type { get { return MessageType.OpponentSplit; } }

        public override void Write(PacketWriter w)
        {
            base.Write(w);
            w.WriteByte(Racer);
        }

        public static OpponentSplit Read(PacketReader r)
        {
            var m = new OpponentSplit();
            m.ReadBody(r);
            m.Racer = r.Remaining > 0 ? r.ReadByte() : (byte)0;
            return m;
        }
    }

    public abstract class AttemptMessage : Message
    {
        public int Attempt;
        public int RaceTimeMs;

        public override void Write(PacketWriter w)
        {
            w.WriteInt32(Attempt);
            w.WriteInt32(RaceTimeMs);
        }

        protected void ReadBody(PacketReader r)
        {
            Attempt = r.ReadInt32();
            RaceTimeMs = r.ReadInt32();
        }
    }

    public sealed class AttemptStarted : AttemptMessage
    {
        public override MessageType Type { get { return MessageType.AttemptStarted; } }

        public static AttemptStarted Read(PacketReader r)
        {
            var m = new AttemptStarted();
            m.ReadBody(r);
            return m;
        }
    }

    public sealed class OpponentAttempt : AttemptMessage
    {
        public byte Racer;

        public override MessageType Type { get { return MessageType.OpponentAttempt; } }

        public override void Write(PacketWriter w)
        {
            base.Write(w);
            w.WriteByte(Racer);
        }

        public static OpponentAttempt Read(PacketReader r)
        {
            var m = new OpponentAttempt();
            m.ReadBody(r);
            m.Racer = r.Remaining > 0 ? r.ReadByte() : (byte)0;
            return m;
        }
    }

    public abstract class FinishMessage : Message
    {
        public int Attempt;
        public int RaceTimeMs;
        public bool Cheated;

        public override void Write(PacketWriter w)
        {
            w.WriteInt32(Attempt);
            w.WriteInt32(RaceTimeMs);
            w.WriteBool(Cheated);
        }

        protected void ReadBody(PacketReader r)
        {
            Attempt = r.ReadInt32();
            RaceTimeMs = r.ReadInt32();
            Cheated = r.ReadBool();
        }
    }

    public sealed class PlayerFinished : FinishMessage
    {
        // The match's shared real-time clock at the finish, or -1 when the sender has none. RaceTimeMs is the game's
        // own run timer: it counts the shortcut sections three times over and stands still while the game loads, so
        // it is the time that is shown and kept, but it cannot say who reached the end first.
        public int RealTimeMs = -1;

        public override MessageType Type { get { return MessageType.PlayerFinished; } }

        public override void Write(PacketWriter w)
        {
            base.Write(w);
            w.WriteInt32(RealTimeMs);
        }

        public static PlayerFinished Read(PacketReader r)
        {
            var m = new PlayerFinished();
            m.ReadBody(r);
            m.RealTimeMs = r.Remaining >= 4 ? r.ReadInt32() : -1;
            return m;
        }
    }

    public sealed class OpponentFinished : FinishMessage
    {
        public byte Racer;

        public override MessageType Type { get { return MessageType.OpponentFinished; } }

        public override void Write(PacketWriter w)
        {
            base.Write(w);
            w.WriteByte(Racer);
        }

        public static OpponentFinished Read(PacketReader r)
        {
            var m = new OpponentFinished();
            m.ReadBody(r);
            m.Racer = r.Remaining > 0 ? r.ReadByte() : (byte)0;
            return m;
        }
    }

    // Game -> client -> server: the player died. In a race that ends the race: whoever is still alive wins.
    public sealed class PlayerDied : Message
    {
        public int Attempt;
        public int RaceTimeMs;
        // What killed the player, as the game names it ("deathfloor", "falling", "teeth", ...), and the game's own
        // death-screen line for it ("FUSED INTO THE MASS"). Either may be empty.
        public string Cause = "";
        public string Text = "";

        public override MessageType Type { get { return MessageType.PlayerDied; } }

        public override void Write(PacketWriter w)
        {
            w.WriteInt32(Attempt);
            w.WriteInt32(RaceTimeMs);
            w.WriteString(Cause, ProtocolConstants.MaxDeathCauseLength);
            w.WriteString(Text, ProtocolConstants.MaxDeathTextLength);
        }

        public static PlayerDied Read(PacketReader r)
        {
            return new PlayerDied
            {
                Attempt = r.ReadInt32(),
                RaceTimeMs = r.ReadInt32(),
                Cause = r.ReadString(ProtocolConstants.MaxDeathCauseLength),
                Text = r.ReadString(ProtocolConstants.MaxDeathTextLength),
            };
        }
    }

    public sealed class PlayerForfeit : Message
    {
        public override MessageType Type { get { return MessageType.PlayerForfeit; } }
        public override void Write(PacketWriter w) { }
        public static PlayerForfeit Read(PacketReader r) { return new PlayerForfeit(); }
    }

    public sealed class MatchResult : Message
    {
        public Guid MatchId;
        public MatchOutcome Outcome;
        public int YourTimeMs = -1;
        public int OpponentTimeMs = -1;
        public int OldRating;
        public int NewRating;
        public string Reason = "";
        public MatchEnd End;
        // Party races: this player's place (1 = first; 0 in a 1v1) and everyone's, in order.
        public int Place;
        public List<Standing> Standings = new List<Standing>();

        public override MessageType Type { get { return MessageType.MatchResult; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteByte((byte)Outcome);
            w.WriteInt32(YourTimeMs);
            w.WriteInt32(OpponentTimeMs);
            w.WriteInt32(OldRating);
            w.WriteInt32(NewRating);
            w.WriteString(Reason, ProtocolConstants.MaxTextLength);
            w.WriteByte((byte)End);
            w.WriteByte((byte)Place);
            Standing.WriteList(w, Standings);
        }

        public static MatchResult Read(PacketReader r)
        {
            var m = new MatchResult();
            m.MatchId = r.ReadGuid();
            m.Outcome = (MatchOutcome)r.ReadByte();
            m.YourTimeMs = r.ReadInt32();
            m.OpponentTimeMs = r.ReadInt32();
            m.OldRating = r.ReadInt32();
            m.NewRating = r.ReadInt32();
            m.Reason = r.ReadString(ProtocolConstants.MaxTextLength);
            m.End = (MatchEnd)r.ReadByte();
            m.Place = r.ReadByte();
            m.Standings = Standing.ReadList(r);
            return m;
        }
    }

    // Client -> server: the game connected to or disconnected from this client.
    public sealed class GameStatus : Message
    {
        public bool Connected;

        public override MessageType Type { get { return MessageType.GameStatus; } }

        public override void Write(PacketWriter w)
        {
            w.WriteBool(Connected);
        }

        public static GameStatus Read(PacketReader r)
        {
            return new GameStatus { Connected = r.ReadBool() };
        }
    }

    // Server -> client: the race clock is stopped because a player is away. AwayName forfeits if not back in SecondsLeft.
    public sealed class MatchPaused : Message
    {
        public Guid MatchId;
        public string AwayName = "";
        public int SecondsLeft;
        // Race clock at the moment of the pause; negative during the countdown.
        public int RaceTimeMs;

        public override MessageType Type { get { return MessageType.MatchPaused; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteString(AwayName, ProtocolConstants.MaxNameLength * 2 + 8);
            w.WriteInt32(SecondsLeft);
            w.WriteInt32(RaceTimeMs);
        }

        public static MatchPaused Read(PacketReader r)
        {
            var m = new MatchPaused();
            m.MatchId = r.ReadGuid();
            m.AwayName = r.ReadString(ProtocolConstants.MaxNameLength * 2 + 8);
            m.SecondsLeft = r.ReadInt32();
            m.RaceTimeMs = r.ReadInt32();
            return m;
        }
    }

    // Server -> client: play continues at ResumeServerTimeMs. GoServerTimeMs is the new effective GO time,
    // moved later by the length of the pause so the race clock carries on where it stopped.
    public sealed class MatchResumed : Message
    {
        public Guid MatchId;
        public long GoServerTimeMs;
        public long ResumeServerTimeMs;

        public override MessageType Type { get { return MessageType.MatchResumed; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteInt64(GoServerTimeMs);
            w.WriteInt64(ResumeServerTimeMs);
        }

        public static MatchResumed Read(PacketReader r)
        {
            var m = new MatchResumed();
            m.MatchId = r.ReadGuid();
            m.GoServerTimeMs = r.ReadInt64();
            m.ResumeServerTimeMs = r.ReadInt64();
            return m;
        }
    }

    // ---- Local mod <-> client link -------------------------------------

    public sealed class ModHello : Message
    {
        public int ProtocolVersion = ProtocolConstants.ProtocolVersion;
        public string ModVersion = "";
        public string GameVersion = "";

        public override MessageType Type { get { return MessageType.ModHello; } }

        public override void Write(PacketWriter w)
        {
            w.WriteInt32(ProtocolVersion);
            w.WriteString(ModVersion, 32);
            w.WriteString(GameVersion, 64);
        }

        public static ModHello Read(PacketReader r)
        {
            var m = new ModHello();
            m.ProtocolVersion = r.ReadInt32();
            m.ModVersion = r.ReadString(32);
            m.GameVersion = r.ReadString(64);
            return m;
        }
    }

    public sealed class ClientStatus : Message
    {
        // Info starting with this tells the mod's HUD that WKSR and the mod do not match. ClientStatus has kept the
        // same layout since the first release, so even a much older or newer mod can read it.
        public const string MismatchPrefix = "VERSION MISMATCH";

        public ClientPhase Phase;
        public string PlayerName = "";
        public int Rating;
        public string Info = "";

        public override MessageType Type { get { return MessageType.ClientStatus; } }

        public override void Write(PacketWriter w)
        {
            w.WriteByte((byte)Phase);
            w.WriteString(PlayerName, ProtocolConstants.MaxNameLength);
            w.WriteInt32(Rating);
            w.WriteString(Info, ProtocolConstants.MaxTextLength);
        }

        public static ClientStatus Read(PacketReader r)
        {
            var m = new ClientStatus();
            m.Phase = (ClientPhase)r.ReadByte();
            m.PlayerName = r.ReadString(ProtocolConstants.MaxNameLength);
            m.Rating = r.ReadInt32();
            m.Info = r.ReadString(ProtocolConstants.MaxTextLength);
            return m;
        }
    }

    // Client -> mod: freeze the game. RaceTimeMs is the stopped race clock, or -1 when the client does not know it.
    public sealed class RacePaused : Message
    {
        public Guid MatchId;
        public string Reason = "";
        public int SecondsLeft;
        public int RaceTimeMs = -1;

        public override MessageType Type { get { return MessageType.RacePaused; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteString(Reason, ProtocolConstants.MaxTextLength);
            w.WriteInt32(SecondsLeft);
            w.WriteInt32(RaceTimeMs);
        }

        public static RacePaused Read(PacketReader r)
        {
            var m = new RacePaused();
            m.MatchId = r.ReadGuid();
            m.Reason = r.ReadString(ProtocolConstants.MaxTextLength);
            m.SecondsLeft = r.ReadInt32();
            m.RaceTimeMs = r.ReadInt32();
            return m;
        }
    }

    // Client -> mod, just before the MatchFound of a practice run (WKSR stands in for the server on this PC): the run
    // with this id is practice. The "opponent" is the player's best run on the seed being replayed, if there is one.
    // An older mod skips it and shows the run as a friendly race.
    public sealed class PracticeMark : Message
    {
        public Guid MatchId;
        // The best run being replayed: how long it lasted (-1 when there is none), whether it finished or died, and
        // how many levels it reached.
        public int BestTimeMs = -1;
        public bool BestFinished;
        public int BestLevels;
        // A ranked solo run (hosted by the match server), not an unranked one on this PC.
        public bool Ranked;

        public override MessageType Type { get { return MessageType.PracticeMark; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteInt32(BestTimeMs);
            w.WriteBool(BestFinished);
            w.WriteInt32(BestLevels);
            w.WriteBool(Ranked);
        }

        public static PracticeMark Read(PacketReader r)
        {
            return new PracticeMark { MatchId = r.ReadGuid(), BestTimeMs = r.ReadInt32(), BestFinished = r.ReadBool(), BestLevels = r.ReadInt32(), Ranked = r.ReadBool() };
        }
    }

    // Game -> client: the player restarted a solo run (any restart: a bind, the pause menu, F9). WKSR starts a fresh
    // run: clock, splits and the best's ghost all from the start. Unranked with a typed seed that is the same seed
    // again; otherwise a new one. For a ranked solo run WKSR passes this on to the server, which hosts those.
    public sealed class PracticeRestart : Message
    {
        public Guid MatchId;

        public override MessageType Type { get { return MessageType.PracticeRestart; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
        }

        public static PracticeRestart Read(PacketReader r)
        {
            return new PracticeRestart { MatchId = r.ReadGuid() };
        }
    }

    // Client -> mod: unfreeze in MsUntilResume. MsUntilGo is the (moved) GO time, negative once the race is under way.
    public sealed class RaceResumed : Message
    {
        public Guid MatchId;
        public int MsUntilGo;
        public int MsUntilResume;

        public override MessageType Type { get { return MessageType.RaceResumed; } }

        public override void Write(PacketWriter w)
        {
            w.WriteGuid(MatchId);
            w.WriteInt32(MsUntilGo);
            w.WriteInt32(MsUntilResume);
        }

        public static RaceResumed Read(PacketReader r)
        {
            var m = new RaceResumed();
            m.MatchId = r.ReadGuid();
            m.MsUntilGo = r.ReadInt32();
            m.MsUntilResume = r.ReadInt32();
            return m;
        }
    }
}
