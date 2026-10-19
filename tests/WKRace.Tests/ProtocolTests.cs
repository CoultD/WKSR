using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using WKRace.Protocol;
using Xunit;

namespace WKRace.Tests
{
    public class ProtocolTests
    {
        private static Message RoundTrip(Message message)
        {
            byte[] payload = MessageCodec.Encode(message);
            Message decoded = MessageCodec.Decode(payload);
            Assert.Equal(message.Type, decoded.Type);
            Assert.Equal(payload, MessageCodec.Encode(decoded));
            return decoded;
        }

        [Fact]
        public void EveryMessageType_RoundTrips()
        {
            var id = Guid.NewGuid();
            var token = Enumerable.Range(0, ProtocolConstants.TokenLength).Select(i => (byte)i).ToArray();
            var messages = new Message[]
            {
                new ClientHello { PlayerId = id, Token = token, Name = "Climber", ClientVersion = "1.2.3", GameConnected = false },
                new ServerWelcome { PlayerId = id, Name = "Climber", Rating = 1034, Wins = 3, Losses = 2, ServerTimeMs = 1700000000000, PlayersOnline = 5, PlayersQueued = 1, SteamAvailable = true, SteamLinked = true, SteamRequiredForRanked = true },
                new ErrorMessage(ErrorCode.RateLimited, "slow down"),
                new Ping { ClientTimeMs = 42 },
                new Pong { ClientTimeMs = 42, ServerTimeMs = 99 },
                new QueueJoin { GameBuild = "steam-25612096" },
                new QueueLeave(),
                new QueueStatus { Queued = true, PlayersQueued = 4, WaitSeconds = 12 },
                new LobbyCreate { GameBuild = "steam-1" },
                new LobbyCreated { LobbyId = 0xDEADBEEF, PublicAddress = "203.0.113.7:47600", ExpiresInSeconds = 600 },
                new LobbyJoin { LobbyId = 42, GameBuild = "steam-1" },
                new LobbyCancel(),
                new LobbyClosed { LobbyId = 42, Reason = "expired" },
                new MatchFound { MatchId = id, Seed = 1234567, OpponentName = "Rival", OpponentRating = 1100, Competitive = true, TimeLimitSeconds = 5400, ResumeElapsedMs = 2500, GraceSeconds = 60, Ranked = false, ArtifactBans = true, Mode = MatchMode.Party, YourRacer = 3, PlayerCount = 9, ReadySeconds = 60 },
                new MatchReady { MatchId = id, WorldHash = 0xDEADBEEF, LevelCount = 31 },
                new MatchStart { MatchId = id, GoServerTimeMs = 1700000005000 },
                new StartCountdown { MatchId = id, MsUntilGo = -1500 },
                new PlayerState { Attempt = 2, RaceTimeMs = 12345, X = 1.5f, Y = 200.25f, Z = -3f, Yaw = 90f, Pitch = -10f, Flags = StateFlags.Dead },
                new OpponentState { Racer = 4, Attempt = 1, RaceTimeMs = 5, X = 0, Y = 1, Z = 2, Yaw = 3, Pitch = 4, Flags = StateFlags.LeftHold | StateFlags.RightHold, LeftX = 1, LeftY = 2, LeftZ = 3, RightX = 4, RightY = 5, RightZ = 6 },
                new SplitReached { Attempt = 1, Index = 7, Level = "S_Storage_04", Region = "silos", RaceTimeMs = 61234 },
                new OpponentSplit { Racer = 2, Attempt = 1, Index = 8, Level = "S_Air_01", Region = "silos", RaceTimeMs = 70000 },
                new AttemptStarted { Attempt = 3, RaceTimeMs = 90000 },
                new OpponentAttempt { Racer = 1, Attempt = 3, RaceTimeMs = 90000 },
                new PlayerFinished { Attempt = 1, RaceTimeMs = 600000, Cheated = false },
                new OpponentFinished { Racer = 14, Attempt = 1, RaceTimeMs = 601000, Cheated = false },
                new PlayerForfeit(),
                new MatchResult { MatchId = id, Outcome = MatchOutcome.Win, YourTimeMs = 600000, OpponentTimeMs = -1, OldRating = 1000, NewRating = 1016, Reason = "finished first", End = MatchEnd.Death, Place = 2,
                    Standings = new List<Standing> { new Standing { Racer = 1, Name = "Rival", Place = 1, Status = RacerStatus.Finished, TimeMs = 590000, Levels = 30 }, new Standing { Racer = 0, Name = "Climber", Place = 2, Status = RacerStatus.Out, TimeMs = 12000, Levels = 3, Note = "FELL TO THEIR DEATH" } } },
                new GameStatus { Connected = true },
                new MatchPaused { MatchId = id, AwayName = "Rival", SecondsLeft = 60, RaceTimeMs = 42000 },
                new MatchResumed { MatchId = id, GoServerTimeMs = 1700000009000, ResumeServerTimeMs = 1700000052000 },
                new RacePaused { MatchId = id, Reason = "Rival disconnected", SecondsLeft = 58, RaceTimeMs = -1 },
                new RaceResumed { MatchId = id, MsUntilGo = -42000, MsUntilResume = 3000 },
                new PracticeMark { MatchId = id, BestTimeMs = 1234567, BestFinished = false, BestLevels = 17 },
                new PracticeRestart { MatchId = id },
                new PlayerDied { Attempt = 1, RaceTimeMs = 345678, Cause = "deathfloor", Text = "FUSED INTO THE MASS" },
                new ModHello { ModVersion = "0.1.0", GameVersion = "white knuckle-0.61e" },
                new ClientStatus { Phase = ClientPhase.Searching, PlayerName = "Climber", Rating = 1000, Info = "Searching" },
                new BanReady { MatchId = id, Pool = new List<string> { "Item_Artifact_Rapier", "Item_Artifact_Remote" } },
                new BanPick { MatchId = id, Item = "Item_Artifact_Rapier" },
                new BanState { MatchId = id, Pool = new List<string> { "Item_Artifact_Rapier" }, Banned = new List<string> { "Item_Artifact_Rapier" }, BannedBy = new List<string> { "Rival" }, Done = true },
                new SteamLinkStart(),
                new SteamLinkUrl { Url = "https://steamcommunity.com/openid/login?openid.mode=checkid_setup" },
                new SteamLinkResult { Ok = true, Message = "Linked", PlayerId = id, Token = token, Name = "Climber", Rating = 1016, Wins = 1, Losses = 0 },
                new SteamLinkResult { Ok = false, Message = "Nope", PlayerId = id },
                new LobbyState { LobbyId = 7, Mode = MatchMode.Party, Rules = MatchRules.CuratedSeeds, MaxPlayers = 15, YouAreHost = true, Racing = false, ExpiresInSeconds = 500,
                    Members = new List<LobbyMember> { new LobbyMember { Name = "Climber", Rating = 1000, Host = true }, new LobbyMember { Name = "Rival", Rating = 1100, Racing = true } } },
                new LobbyStart(),
                new PlayerReady { MatchId = id },
                new MatchRoster { MatchId = id, YourRacer = 1, ReadySecondsLeft = 42,
                    Racers = new List<RosterEntry> { new RosterEntry { Racer = 0, Name = "Rival", Rating = 1100, Status = RacerStatus.Ready, TimeMs = -1 }, new RosterEntry { Racer = 1, Name = "Climber", Rating = 1000, Status = RacerStatus.Loading } } },
                new ModSettings { GhostOpacityPercent = 73 },
                new ModSettings { GhostOpacityPercent = 12, Keys = new KeyBindings { Ready = 282, Forfeit = 283, Splits = 284, Ghost = 285, Chat = 121 } },
                new ModKeys { Keys = new KeyBindings { Ready = 290, Forfeit = 291, Splits = 288, Ghost = 287, Chat = 116 } },
                new ServerStatus { PlayersOnline = 12, LatestVersion = "0.9.2" },
                new SoloStart { GameBuild = "steam-1|mod-0.9.1" },
                new BoardRequest { Board = BoardKind.DuelTime, Offset = 24, Count = 12, Search = "alp", FindMe = true },
                new BoardPage { Board = BoardKind.SoloTime, Offset = 12, Total = 40, YourRank = 7, Search = "a", Rows = new List<BoardRow> { new BoardRow { Rank = 13, Name = "Alpha", Value = 754321, Detail = "0.9.1", Replay = true, You = true } } },
                new ReplayRequest { Board = BoardKind.SoloTime, Rank = 3 },
                new ReplayChunk { Board = BoardKind.SoloTime, Rank = 3, Index = 1, Count = 4, Bytes = new byte[] { 1, 2, 3, 250 } },
                new BoardNote { Board = BoardKind.DuelTime, TimeMs = 600000, Rank = 4, BestMs = 590000, PersonalBest = false, Problem = "x" },
                new LoadedMods { Part = 1, Parts = 3, Cut = true, Mods = new List<ModEntry> { new ModEntry { Id = "com.someone.mod", Name = "Some Mod", Version = "1.2.3" } } },
                new ModVerdict { AnyMarked = true, Manager = true, Unapproved = 2, Names = new List<string> { "Some Mod", "Other Mod" } },
                new ModsRequest { Offset = 10, Count = 10 },
                new ModsPage { Offset = 10, Total = 11, NotApproved = 1, Rows = new List<ModRow> { new ModRow { Mod = new ModEntry { Id = "com.someone.mod", Name = "Some Mod", Version = "1.2.3" }, Approved = false, Yours = true, Players = 3 } } },
                new ModMark { Id = "com.someone.mod", Approved = false },
                new SteamIdentity { SteamId = 76561198000000042 },
                new ChatSend { Text = "hello", Room = ChatRooms.Global },
                new ChatMessage { From = "Alpha", Text = "hello", Room = ChatRooms.Global, SentUnixMs = 1791800000000, History = true },
                new ChatJoin { Room = ChatRooms.Global, Join = true },
                new ServerFeatures { LobbyBrowser = true },
                new LobbyVisibility { Public = true },
                new LobbyListRequest(),
                new LobbyList { Entries = { new LobbyListEntry { LobbyId = 7, Host = "Alpha", Mode = MatchMode.Party, Rules = MatchRules.CuratedSeeds, Members = 3, MaxPlayers = 15 } } },
                new ServerStatus { PlayersOnline = 12, LatestVersion = "0.9.5", PlayersQueued = 3 },
                new ChatSend { Text = "gl hf" },
                new ChatMessage { From = "Rival", Text = "you too", System = false },
                new ChatMessage { Text = "Rival joined.", System = true },
                new VoiceHello { Port = 47601, Key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), Token = 0xDEADBEEFCAFEul, RangeMeters = 50 },
                new VoiceState { Enabled = true },
                new VoicePeers { Peers = new List<VoicePeer> { new VoicePeer { VoiceId = 7, Name = "Rival", Racer = 2 }, new VoicePeer { VoiceId = 9, Name = "Pal" } } },
                new VoiceActivity { On = true, Me = true, Racers = new List<byte> { 1, 4 } },
            };

            foreach (MessageType type in Enum.GetValues(typeof(MessageType)))
            {
                Assert.Contains(messages, m => m.Type == type);
            }
            foreach (Message m in messages) RoundTrip(m);
        }

        [Fact]
        public void State_KeepsHandPositionsOnlyWhenHolding()
        {
            var state = new PlayerState { Flags = StateFlags.LeftHold, LeftX = 1, LeftY = 2, LeftZ = 3, RightX = 9, RightY = 9, RightZ = 9 };
            var decoded = (PlayerState)RoundTrip(state);
            Assert.Equal(1f, decoded.LeftX);
            Assert.Equal(3f, decoded.LeftZ);
            Assert.Equal(0f, decoded.RightX);
        }

        [Fact]
        public void Decode_RejectsBadInput()
        {
            Assert.Throws<ProtocolException>(() => MessageCodec.Decode(new byte[0]));
            Assert.Throws<ProtocolException>(() => MessageCodec.Decode(new byte[] { 250 }));

            byte[] truncated = MessageCodec.Encode(new Ping { ClientTimeMs = 1 });
            Assert.Throws<ProtocolException>(() => MessageCodec.Decode(truncated.Take(truncated.Length - 1).ToArray()));
            Assert.Throws<ProtocolException>(() => MessageCodec.Decode(truncated.Concat(new byte[] { 0 }).ToArray()));
        }

        [Fact]
        public void Decode_RejectsNonFiniteAndOutOfRangeCoordinates()
        {
            byte[] nan = MessageCodec.Encode(new PlayerState { X = float.NaN });
            Assert.Throws<ProtocolException>(() => MessageCodec.Decode(nan));

            byte[] far = MessageCodec.Encode(new PlayerState { Y = 5_000_000f });
            Assert.Throws<ProtocolException>(() => MessageCodec.Decode(far));
        }

        [Fact]
        public void Strings_AreTruncatedOnWrite_AndBoundedOnRead()
        {
            var hello = new ClientHello { PlayerId = Guid.NewGuid(), Name = new string('a', 100) };
            var decoded = (ClientHello)RoundTrip(hello);
            Assert.Equal(ProtocolConstants.MaxNameLength, decoded.Name.Length);

            var w = new PacketWriter();
            w.WriteUInt16(60000);
            Assert.Throws<ProtocolException>(() => new PacketReader(w.ToArray(), 0, w.Length).ReadString(24));
        }

        [Fact]
        public void Frames_RoundTripThroughAStream()
        {
            var stream = new MemoryStream();
            FrameIO.WriteFrame(stream, MessageCodec.Encode(new Ping { ClientTimeMs = 7 }));
            FrameIO.WriteFrame(stream, MessageCodec.Encode(new QueueJoin()));
            stream.Position = 0;

            Assert.Equal(7, ((Ping)MessageCodec.Decode(FrameIO.ReadFrame(stream))).ClientTimeMs);
            Assert.IsType<QueueJoin>(MessageCodec.Decode(FrameIO.ReadFrame(stream)));
            Assert.Null(FrameIO.ReadFrame(stream));
        }

        [Fact]
        public void Frames_RejectBadLengthsAndTruncation()
        {
            Assert.Throws<ProtocolException>(() => FrameIO.ReadFrame(new MemoryStream(new byte[] { 0, 0 })));
            Assert.Throws<ProtocolException>(() => FrameIO.ReadFrame(new MemoryStream(new byte[] { 0xFF, 0xFF })));
            Assert.Throws<EndOfStreamException>(() => FrameIO.ReadFrame(new MemoryStream(new byte[] { 5, 0, 1, 2 })));
        }

        [Fact]
        public void RaceFormat_FormatsTimesAndDeltas()
        {
            Assert.Equal("0:00.00", RaceFormat.Time(0));
            Assert.Equal("1:05.43", RaceFormat.Time(65432));
            Assert.Equal("1:01:01.00", RaceFormat.Time(3661000));
            Assert.Equal("+1.50", RaceFormat.Delta(1500));
            Assert.Equal("-0.25", RaceFormat.Delta(-250));
            Assert.Equal("+1:05.43", RaceFormat.Delta(65432));
            Assert.Equal("+3", RaceFormat.Delta(3000, 0));
        }

        [Fact]
        public void RaceFormat_PrettifiesLevelNames()
        {
            Assert.Equal("Safe Area 01", RaceFormat.LevelLabel("M1_Silos_SafeArea_01"));
            Assert.Equal("Intro 01", RaceFormat.LevelLabel("M1_Intro_01"));
            Assert.Equal("H Lab Ending", RaceFormat.LevelLabel("H_Lab_Ending"));
            Assert.Equal("", RaceFormat.LevelLabel(""));
            Assert.Equal("", RaceFormat.LevelLabel(null));
        }

        [Fact]
        public void Fnv1a_IsStableAndOrderSensitive()
        {
            uint a = Fnv1a.Add(Fnv1a.Add(Fnv1a.Offset, "level"), 5);
            uint b = Fnv1a.Add(Fnv1a.Add(Fnv1a.Offset, "level"), 5);
            uint c = Fnv1a.Add(Fnv1a.Add(Fnv1a.Offset, 5), "level");
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }
    }
}
