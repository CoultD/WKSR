using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Input;
using WKRace.Protocol;

namespace WKRace.Client
{
    using Message = WKRace.Protocol.Message;

    // Which set of cards the console shows: the three main ones, or the choices under one of them.
    internal enum ConsoleMenu
    {
        Main,
        Ranked,
        Lobby,
        Unranked,
    }

    // The console's menus, the options window (hotkeys), the update notice and the launch check.
    internal sealed partial class MainViewModel
    {
        // ---- menus ----------------------------------------------------------------------------------------

        private ConsoleMenu _menu = ConsoleMenu.Main;

        public ICommand RankedMenuCommand { get; private set; }
        public ICommand LobbyMenuCommand { get; private set; }
        public ICommand UnrankedMenuCommand { get; private set; }
        public ICommand BackCommand { get; private set; }
        public ICommand RankedSoloCardCommand { get; private set; }
        public ICommand UnrankedSoloCardCommand { get; private set; }
        public ICommand ReplaysCardCommand { get; private set; }
        public ICommand RankedSoloCommand { get; private set; }
        public ICommand LeaderboardsCommand { get; private set; }
        public ICommand OptionsCommand { get; private set; }
        public ICommand ModsCommand { get; private set; }

        public event Action OpenOptions;

        private void InitConsole()
        {
            Func<bool> free = () => _phase != ClientPhase.InMatch;
            RankedMenuCommand = new RelayCommand(() => { Menu = ConsoleMenu.Ranked; Selected = ConsoleCard.Find; }, free);
            LobbyMenuCommand = new RelayCommand(() => { Menu = ConsoleMenu.Lobby; Selected = ConsoleCard.Lobby; }, free);
            UnrankedMenuCommand = new RelayCommand(() => { Menu = ConsoleMenu.Unranked; Selected = ConsoleCard.UnrankedSolo; }, free);
            BackCommand = new RelayCommand(() => Menu = ConsoleMenu.Main);
            RankedSoloCardCommand = new RelayCommand(() => Show(ConsoleCard.RankedSolo), free);
            UnrankedSoloCardCommand = new RelayCommand(() => Show(ConsoleCard.UnrankedSolo), free);
            RankedSoloCommand = new RelayCommand(StartRankedSolo, () => _link == LinkState.Connected && _phase != ClientPhase.InMatch && !_matchActive);
            OptionsCommand = new RelayCommand(() => OpenOptions?.Invoke());

            if (_settings.Keys != null && _settings.Keys.Length == 5)
            {
                // A chat key that types a character (T, the default before 0.9.5) becomes F8.
                if (KeyBindings.TypesText(_settings.Keys[4]))
                {
                    _settings.Keys[4] = KeyBindings.DefaultChat;
                    _settings.Save();
                }
                _keys = new KeyBindings { Ready = _settings.Keys[0], Forfeit = _settings.Keys[1], Splits = _settings.Keys[2], Ghost = _settings.Keys[3], Chat = _settings.Keys[4] };
            }
        }

        public ConsoleMenu Menu
        {
            get { return _menu; }
            set
            {
                if (!Set(ref _menu, value)) return;
                RaiseCards();
            }
        }

        // Asks the server for a ranked solo run. It answers with the run (a MatchFound) or says why not.
        private void StartRankedSolo()
        {
            StartRankedSolo(Segments.None);
        }

        // segment: a run of that segment (Segments), for its leaderboard.
        private void StartRankedSolo(byte segment)
        {
            if (_server == null || _matchActive || _phase == ClientPhase.InMatch) return;
            if (_steamRequired && !_steamLinked)
            {
                Log("Ranked solo runs need your Steam account linked: " + LinkSteamHint);
                return;
            }
            if (!RankedModsAllowed("Ranked solo runs")) return;
            if (_phase == ClientPhase.Searching)
            {
                _server.Send(new QueueLeave());
                _phase = ClientPhase.Idle;
                QueueText = "";
                UpdateFindTitle();
            }
            if (_hasLobby)
            {
                CancelLobby();
                Log("Left the lobby for a ranked solo run.");
            }
            _pendingJoin = 0;
            _server.Send(new SoloStart { GameBuild = MyBuildTag(), Segment = segment });
            Log(segment != Segments.None ? "Starting a ranked " + Segments.Find(segment).Title + " segment run..." : "Starting a ranked solo run...");
        }

        // The card and the menu it belongs to.
        private void Show(ConsoleCard card)
        {
            Selected = card;
            Menu = card == ConsoleCard.Find || card == ConsoleCard.RankedSolo ? ConsoleMenu.Ranked
                : card == ConsoleCard.Join || card == ConsoleCard.Lobby ? ConsoleMenu.Lobby
                : ConsoleMenu.Unranked;
        }

        private void RaiseCards()
        {
            Raise(nameof(ShowCards));
            Raise(nameof(ShowMainCards));
            Raise(nameof(ShowSubCards));
            Raise(nameof(IsRankedMenu));
            Raise(nameof(IsLobbyMenu));
            Raise(nameof(IsUnrankedMenu));
        }

        public bool ShowMainCards { get { return ShowCards && _menu == ConsoleMenu.Main; } }
        public bool ShowSubCards { get { return ShowCards && _menu != ConsoleMenu.Main; } }
        public bool IsRankedMenu { get { return ShowCards && _menu == ConsoleMenu.Ranked; } }
        public bool IsLobbyMenu { get { return ShowCards && _menu == ConsoleMenu.Lobby; } }
        public bool IsUnrankedMenu { get { return ShowCards && _menu == ConsoleMenu.Unranked; } }
        public bool IsRankedSoloSelected { get { return _selected == ConsoleCard.RankedSolo; } }
        public bool IsUnrankedSoloSelected { get { return _selected == ConsoleCard.UnrankedSolo; } }
        public bool IsReplaysSelected { get { return _selected == ConsoleCard.Replays; } }
        public string RankedTitle { get { return _phase == ClientPhase.Searching ? "SEARCHING" : "RANKED"; } }

        // ---- what is new about the player's data ----------------------------------------------------------------

        // The version whose notice (public leaderboards) every player sees once.
        private const string NoticeVersion = "0.9.1";

        public bool NoticeDue { get { return _settings.NoticeSeen != NoticeVersion; } }

        public void MarkNoticeSeen()
        {
            if (_settings.NoticeSeen == NoticeVersion) return;
            _settings.NoticeSeen = NoticeVersion;
            _settings.Save();
        }

        // ---- hotkeys ----------------------------------------------------------------------------------------

        // The game's hotkeys: the mod reports them when it connects, and the options window changes them.
        private KeyBindings _keys = new KeyBindings();

        public string KeyReadyText { get { return KeyNames.Name(_keys.Ready); } }
        public string KeyForfeitText { get { return KeyNames.Name(_keys.Forfeit); } }
        public string KeySplitsText { get { return KeyNames.Name(_keys.Splits); } }
        public string KeyGhostText { get { return KeyNames.Name(_keys.Ghost); } }
        public string KeyChatText { get { return KeyNames.Name(_keys.Chat); } }
        public string KeySplitsGhostText { get { return KeyNames.Name(_keys.Splits) + " " + KeyNames.Name(_keys.Ghost); } }
        public string ChatHintText { get { return "Chat with the lobby here, or press " + KeyNames.Name(_keys.Chat) + " in the game."; } }
        public string MatchChatHintText { get { return "Match chat. In the game: " + KeyNames.Name(_keys.Chat) + ", then Enter."; } }
        public string ReadyTip { get { return "Same as " + KeyNames.Name(_keys.Ready) + " in the game: the countdown starts when everyone is ready"; } }

        // Sets one hotkey ("Ready", "Forfeit", "Splits", "Ghost" or "Chat"). Returns why not, or null when it is set.
        public string SetKey(string action, int code)
        {
            if (code <= 0) return "That key cannot be used.";
            if (action == "Chat" && KeyBindings.TypesText(code)) return "The chat key opens and closes the chat, so it cannot be a key that types a character. Pick a key like F8.";
            KeyBindings next = _keys.Clone();
            switch (action)
            {
                case "Ready": next.Ready = code; break;
                case "Forfeit": next.Forfeit = code; break;
                case "Splits": next.Splits = code; break;
                case "Ghost": next.Ghost = code; break;
                case "Chat": next.Chat = code; break;
                default: return "Unknown hotkey.";
            }
            int[] all = { next.Ready, next.Forfeit, next.Splits, next.Ghost, next.Chat };
            for (int i = 0; i < all.Length; i++)
                for (int j = i + 1; j < all.Length; j++)
                    if (all[i] == all[j]) return KeyNames.Name(code) + " is already used by another hotkey.";

            _keys = next;
            _settings.Keys = all;
            _settings.Save();
            RaiseKeys();
            ToMod(new ModSettings { GhostOpacityPercent = _settings.GhostOpacity, Keys = _keys });
            return null;
        }

        // The game says which hotkeys it uses. Keys picked in WKSR while the game was off win; otherwise WKSR shows
        // the game's (a player may have set them in the mod's own config file).
        private void OnModKeys(KeyBindings fromGame)
        {
            if (_settings.Keys != null && _settings.Keys.Length == 5)
            {
                if (!_keys.Same(fromGame)) ToMod(new ModSettings { GhostOpacityPercent = _settings.GhostOpacity, Keys = _keys });
                return;
            }
            _keys = fromGame.Clone();
            RaiseKeys();
        }

        private void RaiseKeys()
        {
            Raise(nameof(KeyReadyText));
            Raise(nameof(KeyForfeitText));
            Raise(nameof(KeySplitsText));
            Raise(nameof(KeyGhostText));
            Raise(nameof(KeyChatText));
            Raise(nameof(KeySplitsGhostText));
            Raise(nameof(ChatHintText));
            Raise(nameof(MatchChatHintText));
            Raise(nameof(ReadyTip));
        }

        // ---- the update notice ------------------------------------------------------------------------------

        private const string ThunderstorePackage = "https://thunderstore.io/api/experimental/package/WKSR/WKRace/";
        private static readonly TimeSpan OfflineBeforeAsking = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(10);

        private int _playersOnline = -1;
        private string _updateText = "";
        private string _announcedVersion;
        private DateTime _offlineSince = DateTime.UtcNow;
        private DateTime _lastStoreCheck = DateTime.MinValue;
        private bool _storeCheckRunning;

        // "UPDATE AVAILABLE: 0.9.2" while a newer WKSR is out, else empty.
        public string UpdateText { get { return _updateText; } private set { if (Set(ref _updateText, value)) Raise(nameof(HasUpdate)); } }
        public bool HasUpdate { get { return _updateText.Length > 0; } }

        private void OnServerStatus(ServerStatus status)
        {
            _playersOnline = status.PlayersOnline;
            _playersQueued = status.PlayersQueued;
            if (_link == LinkState.Connected) Status = OnlineText();
            NoteLatestVersion(status.LatestVersion, "The match server");
        }

        private string OnlineText()
        {
            return _playersOnline < 0 ? "ONLINE" : "ONLINE  -  " + _playersOnline + (_playersOnline == 1 ? " PLAYER" : " PLAYERS");
        }

        private void NoteLatestVersion(string latest, string source)
        {
            Version theirs, mine;
            if (!Version.TryParse(BuildTag.ShortVersion(latest ?? ""), out theirs) || !Version.TryParse(BuildTag.ShortVersion(_version), out mine)) return;
            if (theirs <= mine) return;
            UpdateText = "UPDATE AVAILABLE: " + theirs;
            if (_announcedVersion == theirs.ToString()) return;
            _announcedVersion = theirs.ToString();
            Log(source + " reports WKSR " + theirs + " (you have " + _version + "). Update the WKRace mod in your mod manager, then start the game again.");
        }

        // Called on the timer. While the match server cannot be reached nothing tells this client about an update,
        // so it asks Thunderstore for the newest version of the package instead (the version number is all it reads).
        private void CheckForUpdateWhileOffline()
        {
            DateTime now = DateTime.UtcNow;
            if (_link == LinkState.Connected) { _offlineSince = now; return; }
            // Offline because the player wants to be: nothing is asked of anyone.
            if (!_settings.Online) { _offlineSince = now; return; }
            if (_endpoint.Custom || _storeCheckRunning) return;
            if (now - _offlineSince < OfflineBeforeAsking || now - _lastStoreCheck < AskAgainAfter) return;
            _lastStoreCheck = now;
            _storeCheckRunning = true;
            _ = Task.Run(async () =>
            {
                string latest = null;
                try
                {
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                    {
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("WKSR/" + _version);
                        string json = await http.GetStringAsync(ThunderstorePackage).ConfigureAwait(false);
                        Newtonsoft.Json.Linq.JToken number = Newtonsoft.Json.Linq.JObject.Parse(json).SelectToken("latest.version_number");
                        if (number != null) latest = (string)number;
                    }
                }
                catch (Exception) { }   // no network, or Thunderstore is down: nothing to say
                _ = _ui.BeginInvoke(new Action(() =>
                {
                    _storeCheckRunning = false;
                    if (latest != null) NoteLatestVersion(latest, "Thunderstore");
                }));
            });
        }

        // ---- the game closed or crashed during a run ------------------------------------------------------

        // A run cannot go on without the game, and a match allows no restarts, so when the game is gone the run ends
        // (in a match: a forfeit). A dropped link to a game that is still running is not that: it pauses as before.
        // A crashing game can linger for a moment, so the process is looked for a little while.
        private static readonly TimeSpan WatchForClosedGame = TimeSpan.FromSeconds(20);
        private DateTime _gameGoneUntil = DateTime.MinValue;
        private DateTime _nextGameProbe = DateTime.MinValue;

        private void OnGameLinkLost()
        {
            if (_matchActive && _started) _gameGoneUntil = DateTime.UtcNow + WatchForClosedGame;
        }

        private void CheckGameGone()
        {
            if (_gameGoneUntil == DateTime.MinValue) return;
            DateTime now = DateTime.UtcNow;
            if (_modVersion != null || !_matchActive || now > _gameGoneUntil)
            {
                _gameGoneUntil = DateTime.MinValue;   // the game is back, the run is over, or it is still running
                return;
            }
            if (now < _nextGameProbe) return;
            _nextGameProbe = now.AddMilliseconds(500);
            if (GameLauncher.IsRunning()) return;

            _gameGoneUntil = DateTime.MinValue;
            Log(_practice != null ? "White Knuckle closed, so the run has ended."
                : "White Knuckle closed during the match, so your run has ended. In a match that counts as a forfeit.");
            if (_practice != null) _practice.GameClosed();
            else _server?.Send(new PlayerForfeit());
        }

        // ---- did LAUNCH GAME work? --------------------------------------------------------------------------

        private DateTime _launchCheckAt = DateTime.MaxValue;

        private void WatchLaunch()
        {
            _launchCheckAt = DateTime.UtcNow.AddSeconds(30);
        }

        private void CheckLaunch()
        {
            if (DateTime.UtcNow < _launchCheckAt) return;
            _launchCheckAt = DateTime.MaxValue;
            if (_modVersion != null || GameLauncher.IsRunning()) return;
            Log("Steam did not start White Knuckle. If Steam shows the game as running or asks a question, answer it there. "
                + "If the game opened this WKSR window earlier, close WKSR, open it again and press LAUNCH GAME.");
        }
    }
}
