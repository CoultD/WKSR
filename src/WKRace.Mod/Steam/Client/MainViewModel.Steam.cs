using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Steamworks;
using Steamworks.Data;
using WKRace.Lobbies;
using WKRace.Protocol;

namespace WKRace.Client
{
    using Message = WKRace.Protocol.Message;

    // STEAM LOBBIES: instead of CONNECT (the official match server), players meet in Steam lobbies. Whoever opens a
    // lobby hosts it: the match server's own core runs in their game (LobbyHost), and their WKSR talks to it as it
    // would to the server. Everyone else joins the Steam lobby (from the browser, a code or a Steam invite) and
    // connects to the host through Steam's relay. The Steam lobby is how players find the host; the host's core decides
    // everything else, with the same rules as on the official server. Nothing is sent to the match server.
    internal sealed partial class MainViewModel
    {
        // What a Steam lobby of WK Race says about itself (Steam lobby data).
        private const string LobbyKeyMark = "wksr";
        private const string LobbyKeyVersion = "wksr_ver";
        private const string LobbyKeyCode = "wksr_code";
        private const string LobbyKeyHost = "wksr_host";
        private const string LobbyKeyName = "wksr_name";
        private const string LobbyKeyMode = "wksr_mode";
        private const string LobbyKeyRules = "wksr_rules";
        private const string LobbyKeyPlayers = "wksr_players";
        private const string LobbyKeyMax = "wksr_max";
        private const string LobbyKeyRacing = "wksr_racing";
        private const string LobbyKeyPublic = "wksr_public";
        // How long a lost host is waited for before the lobby is given up.
        private const int HostLostGiveUpSeconds = 60;
        // How long the others wait for a new host after the host left.
        private const int HandoverSeconds = 30;

        // Which players can share a Steam lobby: the same protocol (the host's core and the clients understand each other)
        // and the same seed rules (identical worlds). Not the mod version, so a release that changes neither leaves
        // Steam lobbies open to players on the version before.
        private static readonly string SteamCompat = "p" + ProtocolConstants.ProtocolVersion.ToString(CultureInfo.InvariantCulture)
            + "r" + WKRace.Mod.RaceGuarantees.RulesVersion.ToString(CultureInfo.InvariantCulture);

        // The lobby this player hosts (the core runs here), or null.
        private LobbyHost _host;
        // The host to connect to: another player's Steam account, or 0 for this player's own LobbyHost.
        private ulong _hostSteamId;
        private Lobby? _steamLobby;
        private bool _pendingCreate;
        private bool _creatingSteamLobby;
        private bool _steamBusy;
        private bool _leavingSteam;
        private DateTime _hostLostAt = DateTime.MinValue;
        // The host left: the host who went (0 when no handover is under way), and until when a new one is waited for.
        private ulong _handoverFrom;
        private DateTime _handoverUntil;
        // A lobby taken over after a handover: its mode and modifiers (else CREATE LOBBY's picker is used).
        private MatchMode? _takeoverMode;
        private MatchRules _takeoverRules;

        // STEAM LOBBIES chosen (instead of CONNECT) on the first page; kept for the next start.
        public bool SteamMode { get { return _settings.SteamLobbies; } }

        // STEAM LOBBIES: off the match server (if connected), into Steam lobbies.
        public void GoSteam()
        {
            if (_settings.SteamLobbies) return;
            if (_settings.Online) GoOffline();
            _settings.SteamLobbies = true;
            _settings.Save();
            Raise(nameof(SteamMode));
            Raise(nameof(HasLobbyBrowser));
            Raise(nameof(SteamLobbyStatus));
            PushStatus();
            Log("Steam lobbies: open a lobby or join one. Nothing is sent to the match server.");
        }

        // Out of Steam lobbies (leaving the one the player is in).
        public void LeaveSteam()
        {
            if (!_settings.SteamLobbies) return;
            if (_hasLobby) CancelLobby();
            StopSteamSession(null);
            _settings.SteamLobbies = false;
            _settings.Save();
            Raise(nameof(SteamMode));
            Raise(nameof(HasLobbyBrowser));
            BrowseRows.Clear();
            BrowseStatus = "";
            LobbyText = "";
            PushStatus();
            Log("Left Steam lobbies.");
        }

        // The build tag of a Steam lobby player: the game build and the Steam compatibility (instead of the mod version).
        private string SteamBuildTag()
        {
            return BuildTag.Compose(GameLauncher.FindGameBuild(_settings.GamePath), "steam" + SteamCompat);
        }

        // The connection WKSR's logic uses: the player's own lobby host, or another player's through Steam.
        private IServerLink NewSteamLink(Func<ClientHello> hello)
        {
            if (_hostSteamId == 0) return _host == null ? null : _host.ConnectLocal(hello);
            return new SteamHostLink(_hostSteamId, hello);
        }

        partial void InitEdition()
        {
            // OPEN LOBBY and JOIN do the Steam lobby's version in STEAM LOBBIES.
            ICommand openOnServer = OpenLobbyCommand, joinOnServer = JoinLobbyCommand;
            OpenLobbyCommand = new RelayCommand(() => { if (SteamMode) CreateSteamLobby(); else openOnServer.Execute(null); },
                () => SteamMode ? _phase != ClientPhase.InMatch && !_hasLobby && !_steamBusy && _steamLobby == null : openOnServer.CanExecute(null));
            JoinLobbyCommand = new RelayCommand(() => { if (SteamMode) JoinSteamByCode(); else joinOnServer.Execute(null); },
                () => SteamMode ? _phase != ClientPhase.InMatch && !string.IsNullOrWhiteSpace(_joinCode) && !_steamBusy : joinOnServer.CanExecute(null));
            try
            {
                SteamMatchmaking.OnLobbyMemberLeave += (lobby, friend) => _ui.BeginInvoke(new Action(() => OnSteamMemberGone(lobby, friend.Id.Value)));
                SteamMatchmaking.OnLobbyMemberDisconnected += (lobby, friend) => _ui.BeginInvoke(new Action(() => OnSteamMemberGone(lobby, friend.Id.Value)));
                // A Steam invite accepted (or "Join game" from the friends list) while the game runs.
                SteamFriends.OnGameLobbyJoinRequested += (lobby, friend) => _ui.BeginInvoke(new Action(() =>
                {
                    // Accepting a Steam invite switches to Steam lobbies.
                    if (_phase == ClientPhase.InMatch) { Log("Finish your match before joining another lobby."); return; }
                    GoSteam();
                    JoinSteamLobby(lobby, 0);
                }));
                // A new host names themselves in the lobby's data.
                SteamMatchmaking.OnLobbyDataChanged += lobby => _ui.BeginInvoke(new Action(() => { if (_steamLobby.HasValue && _steamLobby.Value.Id == lobby.Id) CheckHandover(); }));
                SteamNetworkingUtils.InitRelayNetworkAccess();
            }
            catch (Exception e)
            {
                Log("Steam is not available: " + e.Message);
            }
        }

        // What the header says about the lobby.
        public string SteamLobbyStatus
        {
            get
            {
                if (_host != null) return _hasLobby ? "HOSTING A LOBBY" : "OPENING A LOBBY...";
                if (_handoverFrom != 0) return "WAITING FOR A NEW HOST...";
                if (_steamLobby != null) return _link == LinkState.Connected ? "IN A LOBBY" : "CONNECTING TO THE HOST...";
                return "NOT IN A LOBBY";
            }
        }

        private static bool SteamUp()
        {
            try { return SteamClient.IsValid && SteamClient.IsLoggedOn; }
            catch (Exception) { return false; }
        }

        // Runs `then` on WKSR's thread with the task's result (default when it failed).
        private void Later<T>(Task<T> task, Action<T> then)
        {
            task.ContinueWith(t => _ui.BeginInvoke(new Action(() =>
            {
                T value = default(T);
                if (t.Status == TaskStatus.RanToCompletion) value = t.Result;
                else if (t.Exception != null) WKRace.Mod.Plugin.Log.LogWarning("Steam: " + t.Exception.GetBaseException().Message);
                then(value);
            })));
        }

        // ---- opening a lobby ---------------------------------------------------------------------------------------

        private void CreateSteamLobby()
        {
            if (_hasLobby || _phase == ClientPhase.InMatch || _steamBusy) return;
            if (!SteamUp())
            {
                LobbyText = "Steam is not running or not signed in: lobbies need it.";
                return;
            }
            StopSteamSession(null);
            _host = new LobbyHost(s => WKRace.Mod.Plugin.Log.LogInfo(s));
            string problem = _host.Start(WKRace.Mod.RaceConfig.HostTcpPort.Value);
            if (problem != null)
            {
                _host = null;
                LobbyText = problem;
                Log(problem);
                return;
            }
            _hostSteamId = 0;
            _pendingCreate = true;
            ConnectToServer();
            LobbyText = "Opening a lobby...";
            Raise(nameof(SteamLobbyStatus));
        }

        partial void OnWelcomedEdition()
        {
            if (!SteamMode) return;
            _hostLostAt = DateTime.MinValue;
            Raise(nameof(SteamLobbyStatus));
            if (!_pendingCreate) return;
            _pendingCreate = false;
            if (_takeoverMode.HasValue)
            {
                _server.Send(new LobbyCreate { GameBuild = MyBuildTag(), Mode = _takeoverMode.Value, Rules = _takeoverRules });
                _takeoverMode = null;
                return;
            }
            CreateLobby();
        }

        // The host's core opened the lobby or it changed: the Steam lobby (how others find it) follows.
        partial void OnLobbyStateEdition(LobbyState state)
        {
            if (!SteamMode) return;
            Raise(nameof(SteamLobbyStatus));
            if (_host == null || !state.YouAreHost) return;
            if (_steamLobby == null)
            {
                if (!_creatingSteamLobby) OpenSteamLobby(state);
                return;
            }
            DescribeSteamLobby(_steamLobby.Value, state);
        }

        private void OpenSteamLobby(LobbyState state)
        {
            _creatingSteamLobby = true;
            LobbyHost host = _host;
            Later(SteamMatchmaking.CreateLobbyAsync(state.MaxPlayers), created =>
            {
                _creatingSteamLobby = false;
                if (_host == null || _host != host)
                {
                    // Left again while Steam was making it.
                    if (created.HasValue) created.Value.Leave();
                    return;
                }
                if (!created.HasValue)
                {
                    Log("Steam could not create the lobby. Try again.");
                    CancelLobby();
                    LobbyText = "Steam could not create the lobby. Try again.";
                    return;
                }
                Lobby lobby = created.Value;
                _steamLobby = lobby;
                lobby.SetData(LobbyKeyMark, "1");
                lobby.SetData(LobbyKeyVersion, SteamCompat);
                lobby.SetData(LobbyKeyCode, _lobbyId.ToString(CultureInfo.InvariantCulture));
                lobby.SetData(LobbyKeyHost, SteamClient.SteamId.Value.ToString(CultureInfo.InvariantCulture));
                lobby.SetData(LobbyKeyName, _welcomeName ?? _playerName ?? "");
                lobby.SetData(LobbyKeyPublic, _settings.LobbyPublic ? "1" : "0");
                // Public lobbies are listed; private ones are joined with the code (Steam's "invisible" type: Steam's
                // own "private" type can only be joined from an invite).
                if (_settings.LobbyPublic) lobby.SetPublic();
                else lobby.SetInvisible();
                if (_lobbyState != null) DescribeSteamLobby(lobby, _lobbyState);
                Log(_settings.LobbyPublic ? "Your lobby is open and listed in the lobby browser. Friends can also join with the code." : "Your lobby is open. Send the code to your friends.");
                Raise(nameof(SteamLobbyStatus));
            });
        }

        private void DescribeSteamLobby(Lobby lobby, LobbyState state)
        {
            lobby.SetData(LobbyKeyCode, state.LobbyId.ToString(CultureInfo.InvariantCulture));
            lobby.SetData(LobbyKeyHost, SteamClient.SteamId.Value.ToString(CultureInfo.InvariantCulture));
            lobby.SetData(LobbyKeyName, _welcomeName ?? _playerName ?? "");
            lobby.SetData(LobbyKeyMode, ((int)state.Mode).ToString(CultureInfo.InvariantCulture));
            lobby.SetData(LobbyKeyRules, ((int)state.Rules).ToString(CultureInfo.InvariantCulture));
            lobby.SetData(LobbyKeyPlayers, state.Members.Count.ToString(CultureInfo.InvariantCulture));
            lobby.SetData(LobbyKeyMax, state.MaxPlayers.ToString(CultureInfo.InvariantCulture));
            lobby.SetData(LobbyKeyRacing, state.Racing ? "1" : "0");
            // Nobody can join a lobby that is racing or full; the code works again afterwards.
            lobby.SetJoinable(!state.Racing && state.Members.Count < state.MaxPlayers);
        }

        // ---- joining ----------------------------------------------------------------------------------------------

        private void JoinSteamByCode()
        {
            WKRace.Protocol.LobbyCode code;
            if (!WKRace.Protocol.LobbyCode.TryDecode(_joinCode, out code))
            {
                LobbyText = "That lobby code is not valid. Codes look like WKR-ABCD-1234; check it was copied completely.";
                return;
            }
            if (_phase == ClientPhase.InMatch || _steamBusy) return;
            if (_hasLobby && code.LobbyId == _lobbyId)
            {
                LobbyText = "That is your own lobby. Send the code to a friend instead.";
                return;
            }
            if (!SteamUp())
            {
                LobbyText = "Steam is not running or not signed in: lobbies need it.";
                return;
            }
            _steamBusy = true;
            LobbyText = "Looking for the lobby...";
            uint id = code.LobbyId;
            Later(SteamMatchmaking.LobbyList.WithKeyValue(LobbyKeyCode, id.ToString(CultureInfo.InvariantCulture)).FilterDistanceWorldwide().WithMaxResults(10).RequestAsync(), found =>
            {
                _steamBusy = false;
                Lobby? match = found == null ? (Lobby?)null : found.Where(l => l.GetData(LobbyKeyMark) == "1").Select(l => (Lobby?)l).FirstOrDefault();
                if (!match.HasValue)
                {
                    LobbyText = "No open lobby has that code. It may have closed, be full, or be in a match right now.";
                    return;
                }
                JoinSteamLobby(match.Value, id);
            });
        }

        // code: the lobby's code if already known (0: read it from the Steam lobby).
        private void JoinSteamLobby(Lobby lobby, uint code)
        {
            if (_phase == ClientPhase.InMatch)
            {
                Log("Finish your match before joining another lobby.");
                return;
            }
            if (_steamLobby.HasValue && _steamLobby.Value.Id == lobby.Id) return;
            _steamBusy = true;
            LobbyText = "Joining the lobby...";
            // A Steam invite names the lobby only: its details arrive as it is entered.
            Later(lobby.Join(), entered =>
            {
                _steamBusy = false;
                if (entered != RoomEnter.Success)
                {
                    LobbyText = EnterProblem(entered);
                    return;
                }
                string version = lobby.GetData(LobbyKeyVersion);
                uint id = code;
                ulong host;
                if (id == 0) uint.TryParse(lobby.GetData(LobbyKeyCode), NumberStyles.None, CultureInfo.InvariantCulture, out id);
                ulong.TryParse(lobby.GetData(LobbyKeyHost), NumberStyles.None, CultureInfo.InvariantCulture, out host);
                if (lobby.GetData(LobbyKeyMark) != "1" || id == 0 || host == 0)
                {
                    lobby.Leave();
                    LobbyText = "That is not a WK Race lobby, or it is still opening. Try again in a moment.";
                    return;
                }
                if (version != SteamCompat)
                {
                    lobby.Leave();
                    LobbyText = "That lobby's host is on a version of WK Race that cannot play with yours. Update the mod (one of you is behind).";
                    return;
                }
                if (host == SteamClient.SteamId.Value)
                {
                    lobby.Leave();
                    LobbyText = "That is your own lobby.";
                    return;
                }
                if (_hasLobby) CancelLobby();
                StopSteamSession(null);
                _steamLobby = lobby;
                _hostSteamId = host;
                _pendingJoin = id;
                ConnectToServer();
                LobbyText = "Connecting to " + (lobby.GetData(LobbyKeyName) ?? "the host") + "...";
                Raise(nameof(SteamLobbyStatus));
            });
        }

        private static string EnterProblem(RoomEnter result)
        {
            switch (result)
            {
                case RoomEnter.DoesntExist: return "That lobby has closed.";
                case RoomEnter.Full: return "That lobby is full.";
                case RoomEnter.NotAllowed: return "That lobby cannot be joined right now (it may be in a match).";
                case RoomEnter.Banned: return "You cannot join that lobby.";
                case RoomEnter.MemberBlockedYou:
                case RoomEnter.YouBlockedMember: return "You cannot join that lobby: one of you has blocked the other on Steam.";
                case RoomEnter.RatelimitExceeded: return "Too many tries: wait a moment and try again.";
                default: return "Steam could not join that lobby (" + result + ").";
            }
        }

        // ---- the lobby browser -------------------------------------------------------------------------------------

        private void RequestSteamLobbyList()
        {
            if (_browseBusy) return;
            if (!SteamUp())
            {
                BrowseStatus = "Steam is not running or not signed in: lobbies need it.";
                return;
            }
            _browseBusy = true;
            BrowseStatus = "Looking for lobbies...";
            Later(SteamMatchmaking.LobbyList.WithKeyValue(LobbyKeyMark, "1").WithKeyValue(LobbyKeyPublic, "1").WithKeyValue(LobbyKeyVersion, SteamCompat)
                .FilterDistanceWorldwide().WithMaxResults(50).RequestAsync(), found =>
            {
                _browseBusy = false;
                BrowseRows.Clear();
                foreach (Lobby lobby in found ?? new Lobby[0])
                {
                    Lobby captured = lobby;
                    int mode, rules, players, max;
                    int.TryParse(lobby.GetData(LobbyKeyMode), NumberStyles.None, CultureInfo.InvariantCulture, out mode);
                    int.TryParse(lobby.GetData(LobbyKeyRules), NumberStyles.None, CultureInfo.InvariantCulture, out rules);
                    if (!int.TryParse(lobby.GetData(LobbyKeyPlayers), NumberStyles.None, CultureInfo.InvariantCulture, out players)) players = lobby.MemberCount;
                    if (!int.TryParse(lobby.GetData(LobbyKeyMax), NumberStyles.None, CultureInfo.InvariantCulture, out max)) max = lobby.MaxMembers;
                    bool racing = lobby.GetData(LobbyKeyRacing) == "1";
                    BrowseRows.Add(new BrowseRowModel
                    {
                        Host = lobby.GetData(LobbyKeyName) ?? "",
                        Mode = (MatchMode)mode == MatchMode.Party ? "PARTY" : "1V1",
                        Rules = RulesText.Describe((MatchRules)rules),
                        Players = players + "/" + max,
                        State = racing ? "IN A MATCH" : players >= max ? "FULL" : "OPEN",
                        Brush = racing || players >= max ? Theme.Muted : Theme.Good,
                        JoinCommand = new RelayCommand(() => JoinSteamLobby(captured, 0), () => !_steamBusy && _phase != ClientPhase.InMatch && !racing && players < max),
                    });
                }
                BrowseStatus = BrowseRows.Count == 0 ? "No public lobbies right now. Open one with CREATE LOBBY." : BrowseRows.Count + (BrowseRows.Count == 1 ? " lobby" : " lobbies") + ".";
            });
        }

        // ---- leaving ----------------------------------------------------------------------------------------------

        partial void OnLobbyLeftEdition()
        {
            if (!SteamMode) return;
            StopSteamSession(_host != null ? "The host left." : null);
        }

        // Only a host that goes away closes a lobby here: the lobby moves on to a new host if anyone is left.
        partial void OnLobbyClosedEdition()
        {
            if (!SteamMode) return;
            if (_host == null && _steamLobby.HasValue && _hostSteamId != 0) AwaitNewHost(_hostSteamId);
            else StopSteamSession(null);
        }

        // Leaves the Steam lobby, the connection and (when hosting) closes the host. reasonForOthers: what everyone
        // else in a hosted lobby is told.
        private void StopSteamSession(string reasonForOthers)
        {
            _leavingSteam = true;
            try
            {
                _pendingCreate = false;
                _takeoverMode = null;
                _handoverFrom = 0;
                _hostLostAt = DateTime.MinValue;
                if (_steamLobby.HasValue)
                {
                    try { _steamLobby.Value.Leave(); } catch (Exception) { }
                    _steamLobby = null;
                }
                // The host closes first, so the others hear why before the host's own WKSR lets go of it.
                if (_host != null)
                {
                    _host.Stop(reasonForOthers ?? "The host left, so the lobby closed.");
                    _host = null;
                }
                if (_server != null) Disconnect();
                _hostSteamId = 0;
            }
            finally
            {
                _leavingSteam = false;
            }
            Raise(nameof(SteamLobbyStatus));
        }

        // The connection to the host dropped. It keeps trying by itself; the lobby is rejoined when it is back (a
        // match keeps the player's place on the host for a while).
        partial void OnLinkLostEdition()
        {
            if (!SteamMode) return;
            Raise(nameof(SteamLobbyStatus));
            if (_leavingSteam || !_steamLobby.HasValue || _host != null || _handoverFrom != 0) return;
            if (_lobbyId != 0 && !_matchActive) _pendingJoin = _lobbyId;
            LobbyText = "Lost the connection to the lobby's host. Reconnecting...";
            if (_hostLostAt == DateTime.MinValue) _hostLostAt = DateTime.UtcNow;
        }

        partial void TickEdition()
        {
            if (!SteamMode) return;
            // The player's name is their Steam name (the hosts are told it when joining, too).
            string steam = _steamName;
            if (!string.IsNullOrEmpty(steam) && steam.Trim().Length >= 2 && _playerName != steam && !_hasLobby)
            {
                PlayerName = steam;
                PushStatus();
            }
            if (_handoverFrom != 0)
            {
                CheckHandover();
                return;
            }
            if (_hostLostAt == DateTime.MinValue || _link == LinkState.Connected) return;
            if ((DateTime.UtcNow - _hostLostAt).TotalSeconds < HostLostGiveUpSeconds) return;
            HostGone("The lobby's host could not be reached for a minute, so you left the lobby.");
        }

        // Someone left the Steam lobby: when it is the host, the lobby moves to a new host.
        private void OnSteamMemberGone(Lobby lobby, ulong member)
        {
            if (!_steamLobby.HasValue || _steamLobby.Value.Id != lobby.Id || _host != null) return;
            if (member != _hostSteamId || _handoverFrom != 0) return;
            AwaitNewHost(member);
        }

        // The host left. A match in progress ends without a result; the lobby itself goes on: Steam makes another
        // member the lobby's owner, whose game becomes the new host (with the same mode and modifiers), and everyone
        // else connects to them.
        private void AwaitNewHost(ulong oldHost)
        {
            if (_matchActive && _match != null && _practice == null)
            {
                OnMatchResult(new MatchResult
                {
                    MatchId = _match.MatchId, Outcome = MatchOutcome.Aborted, YourTimeMs = -1, OpponentTimeMs = -1,
                    OldRating = _rating, NewRating = _rating, Reason = "The host left. The match has no result.", End = MatchEnd.Other,
                });
            }
            _handoverFrom = oldHost;
            _handoverUntil = DateTime.UtcNow.AddSeconds(HandoverSeconds);
            _hostLostAt = DateTime.MinValue;
            _pendingJoin = 0;
            HasLobby = false;
            if (_server != null)
            {
                _leavingSteam = true;
                try { Disconnect(); }
                finally { _leavingSteam = false; }
            }
            _hostSteamId = 0;
            LobbyText = "The host left. The lobby moves to a new host...";
            Log("The host left. The lobby moves to a new host.");
            Raise(nameof(SteamLobbyStatus));
            CheckHandover();
        }

        // During a handover: this player is the lobby's new owner (they host it now), or another player has taken it
        // over (connect to them).
        private void CheckHandover()
        {
            if (_handoverFrom == 0 || !_steamLobby.HasValue) return;
            Lobby lobby = _steamLobby.Value;
            ulong me = SteamClient.SteamId.Value;
            ulong named;
            ulong.TryParse(lobby.GetData(LobbyKeyHost), NumberStyles.None, CultureInfo.InvariantCulture, out named);
            if (named != 0 && named != _handoverFrom && named != me)
            {
                uint code;
                if (uint.TryParse(lobby.GetData(LobbyKeyCode), NumberStyles.None, CultureInfo.InvariantCulture, out code) && code != 0)
                {
                    _handoverFrom = 0;
                    _hostSteamId = named;
                    _pendingJoin = code;
                    ConnectToServer();
                    LobbyText = (lobby.GetData(LobbyKeyName) ?? "Another player") + " hosts the lobby now. Connecting...";
                    Raise(nameof(SteamLobbyStatus));
                    return;
                }
            }
            if (lobby.Owner.Id.Value == me && (named == _handoverFrom || named == 0))
            {
                TakeOverLobby(lobby);
                return;
            }
            if (DateTime.UtcNow > _handoverUntil) HostGone("No new host took the lobby over, so you left it.");
        }

        // This player is the lobby's owner now: their game hosts it, with the lobby's mode and modifiers.
        private void TakeOverLobby(Lobby lobby)
        {
            int mode, rules;
            int.TryParse(lobby.GetData(LobbyKeyMode), NumberStyles.None, CultureInfo.InvariantCulture, out mode);
            int.TryParse(lobby.GetData(LobbyKeyRules), NumberStyles.None, CultureInfo.InvariantCulture, out rules);
            _handoverFrom = 0;
            _host = new LobbyHost(s => WKRace.Mod.Plugin.Log.LogInfo(s));
            string problem = _host.Start(WKRace.Mod.RaceConfig.HostTcpPort.Value);
            if (problem != null)
            {
                _host = null;
                HostGone(problem);
                return;
            }
            _hostSteamId = 0;
            _takeoverMode = (MatchMode)mode;
            _takeoverRules = (MatchRules)rules;
            _pendingCreate = true;
            ConnectToServer();
            LobbyText = "You host the lobby now.";
            Log("The host left: you host the lobby now.");
            Raise(nameof(SteamLobbyStatus));
        }

        private void HostGone(string reason)
        {
            if (_matchActive && _match != null && _practice == null)
            {
                OnMatchResult(new MatchResult
                {
                    MatchId = _match.MatchId, Outcome = MatchOutcome.Aborted, YourTimeMs = -1, OpponentTimeMs = -1,
                    OldRating = _rating, NewRating = _rating, Reason = reason + " The match has no result.", End = MatchEnd.Other,
                });
            }
            HasLobby = false;
            StopSteamSession(null);
            LobbyText = reason;
            Log(reason);
        }
    }
}
