using System;
using System.Collections.Generic;
using WKRace.Protocol;

namespace WKRace.Server
{
    // Inside the game (the Steam lobby host) a global type of the game is also called Message.
    using Message = WKRace.Protocol.Message;

    public interface IConnection
    {
        string RemoteAddress { get; }
        void Send(Message message);
        void Close(string reason);
        void MarkAuthenticated();
    }

    internal enum MatchPhase
    {
        Preparing,
        Countdown,
        Running,
        Settling,
        Completed,
    }

    internal sealed class Player
    {
        public PlayerRecord Record;
        public IConnection Conn;
        public Match Match;
        public QueueEntry Queue;
        public Lobby Lobby;
        // The ranked solo run this player is on, if any.
        public SoloRun Solo;
        // The ranked solo run that last ended with a result (a death, a finish), while a restart in the game can
        // still follow it with a new run.
        public SoloRun LastSolo;
        // The build tag the player's client last sent (game build and WKRace version), kept with a leaderboard time.
        public string GameBuild = "";
        public long LastBoardRequestMs = long.MinValue / 2;
        public long LastSteamIdentityMs = long.MinValue / 2;
        public long LastReplayRequestMs = long.MinValue / 2;
        // The mods loaded in the player's game besides WK Race, by id in lower case, as their client last reported
        // (ServerCore.Mods). ModsCut: the game has more than a list holds.
        public Dictionary<string, ModEntry> Mods = new Dictionary<string, ModEntry>(StringComparer.Ordinal);
        public bool ModsCut;
        // A list that is still arriving, part by part.
        public List<ModEntry> PendingMods;
        public int PendingModParts;
        public int PendingModNext;
        public long LastModsListMs = long.MinValue / 2;
        public long LastModsRequestMs = long.MinValue / 2;
        // When this player's lists added mods to the server's list (a limit per hour).
        public readonly Queue<long> NewModTimes = new Queue<long>();
        public MatchResult PendingResult;
        // The client's version, from its hello (what it can read beyond the protocol's basics).
        public string ClientVersion = "";
        public long LastLobbyListMs = long.MinValue / 2;
        // Reported by the client: is the game (with the mod) attached to it.
        public bool GameConnected = true;
        // Lobby creates and joins in the last minute.
        public readonly Queue<long> LobbyAttempts = new Queue<long>();
        // Chat lines sent recently (rate limit).
        public readonly Queue<long> ChatTimes = new Queue<long>();
        // Silenced for sending chat lines too fast, until this time.
        public long ChatSilencedUntilMs;
        // In the Global chat room (asked for by the client after each sign-in).
        public bool InGlobalChat;
        // Voice (ServerCore.Voice): this connection's key and token, the id others see, where its UDP packets come
        // from, whether voice is on, the packet counters (replay protection) and the frame rate limit.
        public byte[] VoiceKey;
        public ulong VoiceToken;
        public ushort VoiceId;
        public System.Net.IPEndPoint VoiceEndpoint;
        public bool VoiceEnabled;
        public ulong VoiceInCounter;
        public ulong VoiceOutCounter;
        public long VoiceWindowMs;
        public int VoiceFrames;

        public Guid Id { get { return Record.Id; } }

        public void Send(Message message)
        {
            if (Conn != null) Conn.Send(message);
        }
    }

    internal sealed class QueueEntry
    {
        public Player Player;
        public int Rating;
        public string GameBuild = "";
        public long EnqueuedMs;
        public long LastStatusMs;
    }

    // A friendly lobby: a room the host opens and others join with its code. A 1v1 lobby races as soon as the second
    // player joins; a party lobby races when the host starts it. Everyone goes back to the lobby after a race.
    internal sealed class Lobby
    {
        public uint Id;
        public Player Host;
        public MatchMode Mode;
        public MatchRules Rules;
        // The host lets in players whose game has mods that are not approved.
        public bool AllowUnapproved;
        // Listed in the lobby browser (the host's choice); otherwise joined with the code only.
        public bool Public;
        public string GameBuild = "";
        public long CreatedMs;
        // Since when the host has been on their own (the lobby expires after LobbyTimeoutSeconds of that), or -1.
        public long AloneSinceMs;
        public readonly List<Player> Members = new List<Player>();
        // What was said in the lobby (kept in memory, 24 hours at most): a player who joins later can read back.
        public readonly List<ChatMessage> Chat = new List<ChatMessage>();
        // The lobby's race while one is under way.
        public Match Match;

        public int MaxPlayers { get { return Mode == MatchMode.Party ? ProtocolConstants.MaxPartyPlayers : 2; } }
    }

    internal sealed class Slot
    {
        public Player Player;
        public byte Racer;
        public string Name;
        public int StartRating;
        // The player's world has loaded (MatchReady), and they pressed ready (PlayerReady).
        public bool Loaded;
        public bool Ready;
        public uint WorldHash;
        public int Attempt = 1;
        public bool Finished;
        // The racer's time: the game's own run timer when they finished (what is shown, ranked in a party and kept).
        public int FinishMs = -1;
        // The match's real-time clock when they finished (their own reading, kept within what the server saw). It
        // decides who finished first in a 1v1, where the game timer cannot (it runs faster on shortcuts).
        public int FinishRealMs = -1;
        public long FinishArrivalMs;
        // The racer's game timer as last reported with their position.
        public int GameMs;
        // Ranked 1v1 only: the server's own recording of the run (a leaderboard replay if the time is among the
        // fastest), and its check that the timer never stood still while the player moved.
        public RunRecorder Recorder;
        public TimerWatch Watch;
        // Party races: 1 for the first finish to reach the server, 2 for the next, ...
        public int FinishOrder;
        public bool Cheated;
        public bool Forfeited;
        public bool Died;
        // The game's line for how they died, as others read it ("FELL TO THEIR DEATH"), or "".
        public string DeathText = "";
        // Party races: out of the race (died, left, disqualified or gone too long), and the race time it happened.
        public bool Out;
        public int OutMs = -1;
        // When the player stopped being reachable (client or game gone), or -1.
        public long AwaySinceMs = -1;
        // The last reported position (shared coordinates) and when it arrived, for the voice range.
        public float PosX, PosY, PosZ;
        public long PosAtMs;
        public readonly Dictionary<int, SplitReached> Splits = new Dictionary<int, SplitReached>();
        // The artifacts this player's game offers, once reported (ban phase).
        public List<string> BanPool;

        // Still racing: neither finished nor out.
        public bool Active { get { return !Finished && !Out && !Forfeited && !Died && !Cheated; } }
    }

    internal sealed class Match
    {
        public Guid Id;
        public int Seed;
        public bool Ranked = true;
        public MatchMode Mode;
        public MatchRules Rules;
        // The lobby the race was started from (friendly races), or null (ranked).
        public Lobby Lobby;
        public long CreatedMs;
        public MatchPhase Phase;
        public long GoMs;
        public long SettleDeadlineMs;
        // Set while the race clock is stopped because a racer is away; -1 otherwise. Party races never pause.
        public long PausedAtMs = -1;
        // When play continues after the last pause.
        public long ResumeAtMs;
        public readonly List<Slot> Slots = new List<Slot>();

        // The ready check: when it ends, or -1 while it has not started.
        public long ReadyDeadlineMs = -1;
        // Party races: the race ends this long after the first finish (-1 before anyone has finished).
        public long PartyDeadlineMs = -1;

        // Artifact bans (ServerCore.Bans, 1v1 only). BansDone is true from the start for a match without bans.
        public bool BansDone = true;
        public List<string> BanPool;
        public readonly List<string> Banned = new List<string>();
        public readonly List<string> BannedBy = new List<string>();
        public Slot BanTurn;
        public int BanTurnsTaken;
        public long BanDeadlineMs;
        // When the wait for every world to load began: match creation, or the end of the ban phase.
        public long PrepareSinceMs;

        public bool Party { get { return Mode == MatchMode.Party; } }
        public bool ArtifactBans { get { return (Rules & MatchRules.ArtifactBans) != 0; } }

        // The two racers of a 1v1.
        public Slot A { get { return Slots[0]; } }
        public Slot B { get { return Slots[1]; } }

        public Slot SlotOf(Player p)
        {
            foreach (Slot s in Slots) if (s.Player == p) return s;
            return null;
        }

        // The other racer of a 1v1.
        public Slot Other(Slot s)
        {
            return s == A ? B : A;
        }

        public bool Paused { get { return PausedAtMs >= 0; } }

        public bool ClockStarted
        {
            get { return Phase == MatchPhase.Countdown || Phase == MatchPhase.Running || Phase == MatchPhase.Settling; }
        }

        // The race clock (ms since GO) as the server sees it: stopped while paused and during the resume countdown.
        public long RaceElapsed(long now)
        {
            long clock = Paused ? PausedAtMs : Math.Max(now, ResumeAtMs);
            return clock - GoMs;
        }
    }
}
