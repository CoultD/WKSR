using System;
using System.Collections.Generic;
using System.Linq;
using WKRace.Protocol;

namespace WKRace.Server
{
    // Inside the game (the Steam lobby host) a global type of the game is also called Message.
    using Message = WKRace.Protocol.Message;

    // All members must be called from a single thread; the TCP layer funnels everything through one work queue.
    public sealed partial class ServerCore
    {
        private const int QueueStatusIntervalMs = 5000;
        // The player count goes out when it changes (checked this often), and at least once in the longer interval.
        private const int ServerStatusCheckMs = 3000;
        private const int ServerStatusRepeatMs = 30000;
        private const int StoreFlushIntervalMs = 30000;

        private readonly ServerOptions _options;
        private readonly PlayerStore _store;
        private readonly MatchLog _matchLog;
        private readonly Func<long> _now;
        private readonly Action<string> _log;

        private readonly Dictionary<IConnection, Player> _byConnection = new Dictionary<IConnection, Player>();
        private readonly Dictionary<Guid, Player> _players = new Dictionary<Guid, Player>();
        private readonly List<QueueEntry> _queue = new List<QueueEntry>();
        private readonly Dictionary<Guid, Match> _matches = new Dictionary<Guid, Match>();
        private long _lastQueueStatusMs;
        private long _lastStatusCheckMs;
        private long _lastStatusSentMs;
        private int _lastStatusOnline = -1;
        private int _lastStatusQueued = -1;
        // Connections that were turned away at the hello: whatever else they send is ignored, not counted against them.
        private readonly HashSet<IConnection> _refused = new HashSet<IConnection>();
        private long _lastFlushMs;

        public ServerCore(ServerOptions options, PlayerStore store, MatchLog matchLog, Func<long> nowMs, Action<string> log)
        {
            _options = options;
            _store = store;
            _matchLog = matchLog;
            _now = nowMs;
            _log = log ?? (s => { });
        }

        public int OnlineCount { get { return _byConnection.Count; } }
        public int QueuedCount { get { return _queue.Count; } }
        public int ActiveMatchCount { get { return _matches.Count; } }

        public void OnMessage(IConnection conn, Message message)
        {
            Player player;
            if (!_byConnection.TryGetValue(conn, out player))
            {
                var hello = message as ClientHello;
                if (hello == null)
                {
                    if (!_refused.Contains(conn)) Violation(conn, "Expected hello");
                    return;
                }
                HandleHello(conn, hello);
                return;
            }

            // A ranked solo run is hosted apart from the matches: its messages go to its own host.
            if (player.Solo != null && RouteSolo(player, message)) return;

            switch (message)
            {
                case Ping ping:
                    conn.Send(new Pong { ClientTimeMs = ping.ClientTimeMs, ServerTimeMs = _now() });
                    break;
                case QueueJoin join:
                    HandleQueueJoin(player, join);
                    break;
                case SoloStart soloStart:
                    HandleSoloStart(player, soloStart);
                    break;
                case BoardRequest boardRequest:
                    HandleBoardRequest(player, boardRequest);
                    break;
                case ReplayRequest replayRequest:
                    HandleReplayRequest(player, replayRequest);
                    break;
                case LoadedMods loadedMods:
                    HandleLoadedMods(player, loadedMods);
                    break;
                case ModsRequest modsRequest:
                    HandleModsRequest(player, modsRequest);
                    break;
                case ModMark modMark:
                    HandleModMark(player, modMark);
                    break;
                case PracticeRestart restartEnded:
                    RestartEndedSolo(player, restartEnded);   // a restart after the ranked solo run ended (died, finished)
                    break;
                case QueueLeave _:
                    RemoveFromQueue(player);
                    player.Send(new QueueStatus { Queued = false, PlayersQueued = _queue.Count });
                    break;
                case MatchReady ready:
                    HandleLoaded(player, ready);
                    break;
                case PlayerReady playerReady:
                    HandlePlayerReady(player, playerReady);
                    break;
                case LobbyStart _:
                    HandleLobbyStart(player);
                    break;
                case ChatSend chat:
                    HandleChat(player, chat);
                    break;
                case ChatJoin chatJoin:
                    HandleChatJoin(player, chatJoin);
                    break;
                case VoiceState voiceState:
                    HandleVoiceState(player, voiceState);
                    break;
                case PlayerState state:
                    HandleState(player, state);
                    break;
                case SplitReached split:
                    HandleSplit(player, split);
                    break;
                case AttemptStarted attempt:
                    HandleAttempt(player, attempt);
                    break;
                case PlayerFinished finished:
                    HandleFinished(player, finished);
                    break;
                case PlayerForfeit _:
                    HandleForfeit(player);
                    break;
                case PlayerDied died:
                    HandleDied(player, died);
                    break;
                case LobbyCreate create:
                    HandleLobbyCreate(player, create);
                    break;
                case LobbyJoin join:
                    HandleLobbyJoin(player, join);
                    break;
                case LobbyCancel _:
                    HandleLobbyCancel(player);
                    break;
                case LobbyVisibility visibility:
                    HandleLobbyVisibility(player, visibility);
                    break;
                case LobbyListRequest _:
                    HandleLobbyListRequest(player);
                    break;
                case BanReady banReady:
                    HandleBanReady(player, banReady);
                    break;
                case BanPick banPick:
                    HandleBanPick(player, banPick);
                    break;
                case SteamLinkStart _:
                    HandleSteamLinkStart(player);
                    break;
                case SteamIdentity identity:
                    HandleSteamIdentity(player, identity);
                    break;
                case GameStatus status:
                    player.GameConnected = status.Connected;
                    if (player.Match != null) UpdatePresence(player.Match);
                    break;
                default:
                    Violation(conn, "Unexpected message " + message.Type);
                    break;
            }
        }

        public void OnDisconnected(IConnection conn)
        {
            _refused.Remove(conn);
            Player player;
            if (!_byConnection.TryGetValue(conn, out player)) return;
            _byConnection.Remove(conn);
            if (player.Conn != conn) return;
            EndSolo(player, "disconnected");

            player.Conn = null;
            player.PendingMods = null;
            DropVoice(player);
            RemoveFromQueue(player);
            // Someone racing stays in their lobby until the race is over (ReturnToLobby lets them go then).
            if (player.Match == null) LeaveLobby(player, null);

            Match match = player.Match;
            if (match != null && match.Phase != MatchPhase.Completed)
            {
                UpdatePresence(match);
            }
            else if (player.PendingResult == null)
            {
                _players.Remove(player.Id);
            }
        }

        public void Tick()
        {
            long now = _now();
            TickMatches(now);
            TickSolo(now);
            ExpireLobbies(now);
            Matchmake();

            if (now - _lastQueueStatusMs >= QueueStatusIntervalMs)
            {
                _lastQueueStatusMs = now;
                foreach (QueueEntry entry in _queue) SendQueueStatus(entry, now);
            }
            if (now - _lastStatusCheckMs >= ServerStatusCheckMs)
            {
                _lastStatusCheckMs = now;
                if (_byConnection.Count != _lastStatusOnline || _queue.Count != _lastStatusQueued || now - _lastStatusSentMs >= ServerStatusRepeatMs) BroadcastStatus(now);
            }
            if (now - _lastFlushMs >= StoreFlushIntervalMs)
            {
                _lastFlushMs = now;
                _store.Flush();
            }
            if (now - _lastRegistrationPruneMs >= 3600000)
            {
                _lastRegistrationPruneMs = now;
                PruneRegistrations(now);
            }
        }

        private long _lastRegistrationPruneMs;

        // Saves changed players now (the backup snapshot wants the latest state).
        public void FlushStore()
        {
            _store.Flush();
        }

        public void Shutdown()
        {
            foreach (IConnection conn in _byConnection.Keys.ToArray())
            {
                conn.Send(new ErrorMessage(ErrorCode.ServerShuttingDown, "Server is shutting down"));
                conn.Close("shutdown");
            }
            _store.Flush();
        }

        // The close reason that marks a protocol violation (the TCP layer counts these towards a ban).
        public const string ViolationReason = "protocol violation";

        private void Violation(IConnection conn, string text)
        {
            _log("protocol violation from " + LogAddress.Short(conn.RemoteAddress) + ": " + text);
            conn.Send(new ErrorMessage(ErrorCode.ProtocolViolation, text));
            conn.Close(ViolationReason);
        }

        // New identities per address in the last 24 hours, kept in memory only (never written to disk).
        private readonly Dictionary<string, Queue<long>> _registrations = new Dictionary<string, Queue<long>>();
        private const long RegistrationWindowMs = 24L * 60 * 60 * 1000;

        // Null when this connection may register a new player, else the reason to give it.
        private string RegistrationRefusal(IConnection conn, long now)
        {
            if (_options.MaxPlayers > 0 && _store.Count >= _options.MaxPlayers)
                return "The server is not accepting new players right now. Try again later.";
            if (_options.NewPlayersPerIpPerDay <= 0) return null;
            Queue<long> times;
            if (_registrations.TryGetValue(conn.RemoteAddress, out times))
            {
                while (times.Count > 0 && times.Peek() <= now - RegistrationWindowMs) times.Dequeue();
                if (times.Count >= _options.NewPlayersPerIpPerDay)
                    return "Too many new players were created from your network today. Use your existing player file, or try again tomorrow.";
            }
            return null;
        }

        private void NoteRegistration(IConnection conn, long now)
        {
            if (_options.NewPlayersPerIpPerDay <= 0) return;
            Queue<long> times;
            if (!_registrations.TryGetValue(conn.RemoteAddress, out times))
            {
                times = new Queue<long>();
                _registrations[conn.RemoteAddress] = times;
            }
            times.Enqueue(now);
        }

        private ServerStatus CurrentStatus()
        {
            return new ServerStatus { PlayersOnline = _byConnection.Count, LatestVersion = LatestVersion, PlayersQueued = _queue.Count };
        }

        private void BroadcastStatus(long now)
        {
            _lastStatusSentMs = now;
            _lastStatusOnline = _byConnection.Count;
            _lastStatusQueued = _queue.Count;
            ServerStatus status = CurrentStatus();
            foreach (Player player in _byConnection.Values) player.Send(status);
        }

        // What clients are told is the newest release: the configured one, else this server's own version.
        private string LatestVersion
        {
            get { return string.IsNullOrEmpty(_options.LatestVersion) ? BuildTag.ShortVersion(ServerVersion) : _options.LatestVersion; }
        }

        private static readonly string ServerVersion =
            (typeof(ServerCore).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);

        private void PruneRegistrations(long now)
        {
            var empty = new List<string>();
            foreach (KeyValuePair<string, Queue<long>> pair in _registrations)
            {
                while (pair.Value.Count > 0 && pair.Value.Peek() <= now - RegistrationWindowMs) pair.Value.Dequeue();
                if (pair.Value.Count == 0) empty.Add(pair.Key);
            }
            foreach (string address in empty) _registrations.Remove(address);
        }

        private void HandleHello(IConnection conn, ClientHello hello)
        {
            if (hello.ProtocolVersion != ProtocolConstants.ProtocolVersion)
            {
                conn.Send(new ErrorMessage(ErrorCode.VersionMismatch, hello.ProtocolVersion < ProtocolConstants.ProtocolVersion
                    ? "This server needs a newer WKSR. Update the WKRace mod in your mod manager (WKSR comes with it), then start the game again."
                    : "This WKSR is newer than the server (protocol " + hello.ProtocolVersion + ", the server speaks " + ProtocolConstants.ProtocolVersion + ")."));
                _refused.Add(conn);
                conn.Close("version mismatch");
                return;
            }
            if (hello.PlayerId == Guid.Empty)
            {
                Violation(conn, "Missing player id");
                return;
            }
            string name = NameSanitizer.CleanName(hello.Name);
            if (name == null)
            {
                conn.Send(new ErrorMessage(ErrorCode.InvalidName, "Pick a name with at least 2 letters or digits."));
                conn.Close("invalid name");
                return;
            }

            long now = _now();
            string tokenHash = PlayerStore.HashToken(hello.Token);
            PlayerRecord record = _store.Find(hello.PlayerId);
            if (record == null)
            {
                name = FreeName(name, null);
                string refusal = RegistrationRefusal(conn, now);
                if (refusal != null)
                {
                    _log("refused a new player from " + LogAddress.Short(conn.RemoteAddress) + ": registration limit");
                    conn.Send(new ErrorMessage(ErrorCode.RegistrationLimited, refusal));
                    conn.Close("registration limited");
                    return;
                }
                NoteRegistration(conn, now);
                record = _store.Add(new PlayerRecord { Id = hello.PlayerId, Name = name, TokenHash = tokenHash });
                _log("new player " + name + " (" + hello.PlayerId + ")");
            }
            else if (!PlayerStore.TokenMatches(record.TokenHash, tokenHash))
            {
                conn.Send(new ErrorMessage(ErrorCode.BadToken, "This player id belongs to someone else."));
                conn.Close("bad token");
                return;
            }
            else if (record.Banned)
            {
                conn.Send(new ErrorMessage(ErrorCode.BadToken, "This player is banned from the server" + (string.IsNullOrEmpty(record.BanReason) ? "." : ": " + record.BanReason)));
                conn.Close("banned");
                return;
            }
            else if (record.Name != name && !record.NameLocked)
            {
                // A rename: names are unique (ignoring case). A name set by an admin stays whatever the client sends.
                record.Name = FreeName(name, record);
            }
            record.LastSeenUnixMs = now;
            _store.MarkDirty(record);

            Player player;
            if (!_players.TryGetValue(record.Id, out player))
            {
                player = new Player { Record = record };
                _players[record.Id] = player;
            }
            if (player.Conn != null && player.Conn != conn)
            {
                IConnection old = player.Conn;
                _byConnection.Remove(old);
                old.Send(new ErrorMessage(ErrorCode.ProtocolViolation, "Signed in from another location."));
                old.Close("replaced");
            }
            player.Conn = conn;
            // A new session asks for its chat rooms again (and gets their history with that).
            player.InGlobalChat = false;
            player.GameConnected = hello.GameConnected;
            player.ClientVersion = hello.ClientVersion ?? "";
            _byConnection[conn] = player;
            conn.MarkAuthenticated();

            player.Send(new ServerWelcome
            {
                PlayerId = record.Id,
                Name = record.Name,
                Rating = record.Rating,
                Wins = record.Wins,
                Losses = record.Losses,
                ServerTimeMs = now,
                PlayersOnline = _byConnection.Count,
                PlayersQueued = _queue.Count,
                SteamAvailable = SteamEnabled,
                SteamLinked = record.SteamHash != null,
                SteamRequiredForRanked = SteamRequiredForRanked,
            });
            player.Send(CurrentStatus());
            SendFeatures(player);
            SendModVerdict(player);

            if (player.PendingResult != null)
            {
                player.Send(player.PendingResult);
                player.PendingResult = null;
            }
            GiveVoice(player);
            if (player.Match != null) ResumeMatch(player);
            if (player.Lobby != null)
            {
                SendLobbyState(player.Lobby, player, now);
                SendChatHistory(player, player.Lobby.Chat);
            }
            SendVoicePeers(player);
        }

        // From 0.9.3 a player's name is their Steam name, and Steam names are not unique: a name someone else has
        // gets a number ("Alex 2"). A player who already carries a numbered form of the name keeps it.
        private string FreeName(string name, PlayerRecord self)
        {
            if (_store.FindByName(name, self) == null) return name;
            if (self != null && self.Name.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
            {
                int kept;
                if (int.TryParse(self.Name.Substring(name.Length + 1), out kept)) return self.Name;
            }
            for (int n = 2; n < 100000; n++)
            {
                string suffix = " " + n;
                string candidate = (name.Length + suffix.Length > ProtocolConstants.MaxNameLength ? name.Substring(0, ProtocolConstants.MaxNameLength - suffix.Length).TrimEnd() : name) + suffix;
                if (_store.FindByName(candidate, self) == null) return candidate;
            }
            return name;
        }

        private void HandleQueueJoin(Player player, QueueJoin join)
        {
            if (player.Match != null)
            {
                player.Send(new ErrorMessage(ErrorCode.AlreadyInMatch, "Finish or forfeit your current match first."));
                return;
            }
            if (player.Queue != null) return;
            if (SteamRequiredForRanked && player.Record.SteamHash == null)
            {
                player.Send(new ErrorMessage(ErrorCode.SteamLinkRequired, "Ranked matches need your Steam account. Start the game through Steam: it links by itself."));
                return;
            }
            string modsRefusal = RankedModsRefusal(player, "Ranked matches");
            if (modsRefusal != null)
            {
                player.Send(new QueueStatus { Queued = false, PlayersQueued = _queue.Count });
                player.Send(new ErrorMessage(ErrorCode.ModsNotApproved, modsRefusal));
                return;
            }
            LeaveLobby(player, null);
            EndSolo(player, "queued for a 1v1");
            player.GameBuild = join.GameBuild ?? "";

            long now = _now();
            var entry = new QueueEntry { Player = player, Rating = player.Record.Rating, GameBuild = join.GameBuild ?? "", EnqueuedMs = now, LastStatusMs = now };
            player.Queue = entry;
            _queue.Add(entry);
            _log(player.Record.Name + " queued" + (entry.GameBuild.Length > 0 ? " (" + entry.GameBuild + ")" : ""));
            SendQueueStatus(entry, now);
            Matchmake();
        }

        private void RemoveFromQueue(Player player)
        {
            if (player.Queue == null) return;
            _queue.Remove(player.Queue);
            player.Queue = null;
        }

        private void SendQueueStatus(QueueEntry entry, long now)
        {
            entry.Player.Send(new QueueStatus
            {
                Queued = true,
                PlayersQueued = _queue.Count,
                WaitSeconds = (int)((now - entry.EnqueuedMs) / 1000),
            });
        }

        // Players are paired when they run the same WK Race version. A different build of the game (the current
        // release and the public test release) is accepted: whether two builds make the same world from a seed is not
        // known in advance, so the world fingerprint decides. A part one side does not know matches anything.
        private bool SameBuild(QueueEntry a, QueueEntry b)
        {
            return BuildsPlayTogether(a.GameBuild, b.GameBuild);
        }

        // Pairs of game builds ("123|456") and what matches between them showed: how often the worlds were the same
        // and how often they differed. Two builds that only ever differed (twice or more) are kept apart from then on,
        // so their players do not keep meeting in matches that cannot be played. Kept in memory: after a restart the
        // server finds out again.
        private readonly Dictionary<string, int[]> _buildPairs = new Dictionary<string, int[]>();
        private const int DifferentWorldsBeforeSplit = 2;

        private static string BuildPair(string a, string b)
        {
            return string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;
        }

        private bool BuildsPlayTogether(string tagA, string tagB)
        {
            if (!BuildTag.CompatibleBesidesGame(tagA, tagB)) return false;
            string a = BuildTag.GameBuild(tagA), b = BuildTag.GameBuild(tagB);
            if (a.Length == 0 || b.Length == 0 || a == b) return true;
            int[] seen;
            return !_buildPairs.TryGetValue(BuildPair(a, b), out seen) || seen[0] > 0 || seen[1] < DifferentWorldsBeforeSplit;
        }

        // Two players on different game builds loaded the same seed: their worlds were the same, or were not.
        private void NoteWorlds(string tagA, string tagB, bool same)
        {
            string a = BuildTag.GameBuild(tagA), b = BuildTag.GameBuild(tagB);
            if (a.Length == 0 || b.Length == 0 || a == b) return;
            string key = BuildPair(a, b);
            int[] seen;
            if (!_buildPairs.TryGetValue(key, out seen)) _buildPairs[key] = seen = new int[2];
            bool together = seen[0] > 0 || seen[1] < DifferentWorldsBeforeSplit;
            seen[same ? 0 : 1]++;
            if (same && seen[0] == 1) _log("game builds " + a + " and " + b + " make the same world: their players play together");
            else if (!same && together && seen[0] == 0 && seen[1] >= DifferentWorldsBeforeSplit)
                _log("game builds " + a + " and " + b + " made different worlds " + seen[1] + " times and never the same: their players are no longer paired");
        }

        private static int RatingWindow(long waitedMs)
        {
            return 150 + (int)(waitedMs / 1000) * 25;
        }

        private void Matchmake()
        {
            bool paired;
            do
            {
                paired = false;
                long now = _now();
                for (int i = 0; i < _queue.Count && !paired; i++)
                {
                    for (int j = i + 1; j < _queue.Count; j++)
                    {
                        QueueEntry a = _queue[i];
                        QueueEntry b = _queue[j];
                        int allowed = Math.Max(RatingWindow(now - a.EnqueuedMs), RatingWindow(now - b.EnqueuedMs));
                        if (Math.Abs(a.Rating - b.Rating) > allowed || !SameBuild(a, b)) continue;

                        _queue.RemoveAt(j);
                        _queue.RemoveAt(i);
                        a.Player.Queue = null;
                        b.Player.Queue = null;
                        // Ranked races always have artifact bans and fully curated seeds.
                        CreateMatch(new[] { a.Player, b.Player }, true, MatchMode.Duel, _options.RankedRules, null);
                        paired = true;
                        break;
                    }
                }
            } while (paired);
        }
    }
}
