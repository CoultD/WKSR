using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WKRace.Protocol;

namespace WKRace.Client
{
    using Message = WKRace.Protocol.Message;

    internal class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }

        protected void Raise([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    internal sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) { return _canExecute == null || _canExecute(); }
        public void Execute(object parameter) { _execute(); }
    }

    internal sealed class SplitRow : ObservableObject
    {
        private int _index;
        // Position in the table (rows are in the order the levels were first reached).
        public int Index { get { return _index; } set { Set(ref _index, value); } }
        // The level this row is for (see SplitKeys): both racers' times for the same level share a row.
        public string Key;
        public int YouMs = -1;
        public int OppMs = -1;
        private string _level = "";
        private string _you = "-";
        private string _opp = "-";
        private string _delta = "";
        private Brush _deltaBrush = Theme.Muted;

        public string Level { get { return _level; } set { Set(ref _level, value); } }
        public string You { get { return _you; } private set { Set(ref _you, value); } }
        public string Opp { get { return _opp; } private set { Set(ref _opp, value); } }
        public string Delta { get { return _delta; } private set { Set(ref _delta, value); } }
        public Brush DeltaBrush { get { return _deltaBrush; } private set { Set(ref _deltaBrush, value); } }

        // When either racer first reached this level, for ordering the rows.
        public int FirstMs
        {
            get { return YouMs < 0 ? OppMs : OppMs < 0 ? YouMs : Math.Min(YouMs, OppMs); }
        }

        public void Refresh()
        {
            You = YouMs >= 0 ? RaceFormat.Time(YouMs) : "-";
            Opp = OppMs >= 0 ? RaceFormat.Time(OppMs) : "-";
            if (YouMs >= 0 && OppMs >= 0)
            {
                Delta = RaceFormat.Delta(YouMs - OppMs);
                DeltaBrush = YouMs <= OppMs ? Theme.Good : Theme.Bad;
            }
            else
            {
                Delta = "";
            }
        }
    }

    internal static class Theme
    {
        // Matches the console palette in App.xaml.
        public static readonly Brush Good = Freeze(0xA8, 0xC2, 0x5C);
        public static readonly Brush Bad = Freeze(0xD9, 0x57, 0x3B);
        public static readonly Brush Warn = Freeze(0xEA, 0xA1, 0x42);
        public static readonly Brush Muted = Freeze(0x8A, 0x67, 0x48);
        public static readonly Brush Accent = Freeze(0xBE, 0x82, 0x4D);

        private static Brush Freeze(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    // One finished race, kept for the recent list and the match data export.
    internal sealed class RaceRecord
    {
        public DateTime When;
        public string Opponent;
        public int OpponentRating;
        public int Seed;
        public bool Ranked;
        public bool Competitive;
        public MatchOutcome Outcome;
        public string Reason;
        public int YourTimeMs;
        public int OpponentTimeMs;
        public int OldRating;
        public int NewRating;
        public List<SplitRow> Splits;
        // "Rapier (Alpha), Remote (Bravo)", "none", or null for a match without bans.
        public string Bans;
        // A practice run (WKSR on its own, no server); NewBest when it beat the stored best on its seed.
        public bool Practice;
        // A ranked solo run (hosted by the server). Practice is set too: it reads like a solo run.
        public bool RankedSolo;
        public bool NewBest;
        // The race or practice run ended with this player's death.
        public bool Died;
        // A party race: this player's place and how many raced (Place 0 otherwise).
        public int Place;
        public int Racers;
        public string Rules;

        public string Title
        {
            get
            {
                if (Place > 0) return Standings.Ordinal(Place).ToUpperInvariant() + " OF " + Racers;
                if (RankedSolo) return Died ? "DIED" : Outcome == MatchOutcome.Win ? "RANKED SOLO" : "RUN ENDED";
                if (Practice && Died) return NewBest ? "DIED, BEST" : "DIED";
                if (Practice) return Outcome == MatchOutcome.Win ? (NewBest ? "NEW BEST" : "UNRANKED") : "RUN ENDED";
                return Outcome == MatchOutcome.Win ? "WIN" : Outcome == MatchOutcome.Loss ? "LOSS" : "ABORTED";
            }
        }

        public string Summary { get { return Practice ? Title + "  seed " + Seed : Place > 0 ? Title + "  party" : Title + "  vs " + Opponent; } }

        public string Detail
        {
            get
            {
                string time = YourTimeMs >= 0 ? RaceFormat.Time(YourTimeMs, 1) : "no finish";
                if (Practice) return time + (OpponentTimeMs >= 0 ? "  best " + RaceFormat.Time(OpponentTimeMs, 1) : "");
                if (!Ranked) return time + (Place > 0 ? "" : "  friendly");
                int change = NewRating - OldRating;
                return time + "  " + (change >= 0 ? "+" : "") + change;
            }
        }

        public Brush Brush
        {
            get
            {
                if (Place > 0) return Place == 1 ? Theme.Good : Place <= 3 ? Theme.Warn : Theme.Muted;
                return Outcome == MatchOutcome.Win ? Theme.Good : Outcome == MatchOutcome.Loss ? Theme.Bad : Theme.Warn;
            }
        }
    }

    internal enum ConsoleCard
    {
        Find,
        Join,
        Lobby,
        RankedSolo,
        UnrankedSolo,
        Replays,
    }

    // Owns the server connection and the bridge to the mod, and relays a match between them.
    internal sealed partial class MainViewModel : ObservableObject
    {
        private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
        private readonly ClientSettings _settings;
        private readonly ModBridge _mod = new ModBridge();
        // Proximity voice (VoiceEngine): who was talking at the last update, to tell the game only about changes.
        private readonly VoiceEngine _voice;
        private string _lastActivity = "";
        private DateTime _nextActivity;
        private bool _voiceAvailable;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly List<KeyValuePair<long, long>> _clockSamples = new List<KeyValuePair<long, long>>();
        // Inside the game, WKSR and the mod are one thing with one version.
        private readonly string _version = BuildTag.ShortVersion(WKRace.Mod.PluginInfo.Version);

        private IServerLink _server;
        private LinkState _link = LinkState.Disconnected;
        private ClientPhase _phase = ClientPhase.Offline;
        private long _offsetMs;
        private MatchFound _match;
        private volatile bool _matchActive;
        private bool _started;
        private long _goServerMs;
        // The run's time: the game's own timer, as the game reports it about 15 times a second (-1 before the first
        // report). WKSR's timer shows this and nothing else, so it stops when the game's does. Written on the link's
        // thread.
        private volatile int _gameMs = -1;
        private int _attempt = 1;
        // What the game needs to catch up on if it (re)connects mid-match: opponent progress, pause state and the result.
        // Opponent splits by racer and index (racer << 32 | index).
        private readonly SortedDictionary<long, OpponentSplit> _oppSplits = new SortedDictionary<long, OpponentSplit>();
        // The race's roster (names, ready check, who is out) and this player's own splits with their regions.
        private MatchRoster _roster;
        private DateTime _readyDeadline;
        private readonly SortedDictionary<int, SplitReached> _mySplits = new SortedDictionary<int, SplitReached>();
        private bool _myDied;
        private DateTime _nextStandings;
        // Each racer's levels by split index in the current attempt, to work out which row a split belongs to.
        private readonly SortedDictionary<int, string> _myLevels = new SortedDictionary<int, string>();
        private readonly SortedDictionary<int, string> _oppLevels = new SortedDictionary<int, string>();
        private readonly Dictionary<byte, OpponentFinished> _oppFinished = new Dictionary<byte, OpponentFinished>();
        private MatchPaused _paused;
        // Steam sign-in, as the server reported it in its welcome.
        private bool _steamAvailable;
        private bool _steamLinked;
        private bool _steamRequired;
        private string _steamStatus = "";

        // The latest artifact-ban state of the current match, passed on to the game (again after it reconnects).
        private BanState _banState;
        private string _bansText = "";
        private bool _serverLost;
        private DateTime _serverLostAt;
        private long _resumeServerMs;
        private MatchResult _lastResult;
        private string _welcomeName;

        // An unranked solo run: WKSR hosts the run itself (SoloHost) instead of the server.
        private SoloHost _practice;
        // A ranked solo run: the server hosts it (the same SoloHost there). For the game and on screen it is a solo
        // run like the unranked one; its messages go to the server.
        private bool _rankedSolo;
        // The run that last ended was a ranked solo run: a restart in the game then asks for another ranked one.
        private bool _lastWasRankedSolo;
        // The unranked solo run is on a seed the player picked (typed, or RUN THIS SEED): a restart repeats that seed.
        // Otherwise every restart is a new random seed.
        private bool _soloSeedTyped;
        private bool Solo { get { return _practice != null || _rankedSolo; } }
        // The current match's run as the game reports it, kept as a best for replaying in practice.
        private RunRecorder _recorder;
        private readonly PracticeStore _practiceStore;
        private readonly ReplayStore _replayStore;
        private string _practiceSeed = "";
        private bool _lastWasPractice;

        // Where the client connects: the built-in official server, unless a test build was started with --server.
        private readonly ServerEndpoint _endpoint;
        private uint _pendingJoin;
        private uint _lobbyId;
        private DateTime _lobbyExpires;
        private string _lobbyCode = "";
        private string _lobbyText = "";
        private string _joinCode = "";
        private bool _hasLobby;
        // The lobby room as the server last described it (members, host, mode, modifiers).
        private LobbyState _lobbyState;
        private string _chatDraft = "";
        private bool _chatHasLines;
        private DateTime _copiedUntil;
        private DateTime _searchStarted;
        private DateTime _nextGameCheck;
        private string _modVersion;
        // From the mod's hello: its protocol differs from ours, so only ClientStatus (readable by every version) is sent.
        private bool _modIncompatible;
        private string _gameVersion;
        private int _rating;
        private int _wins;
        private int _losses;
        private bool _autoSearch;

        private string _playerName;
        private ConsoleCard _selected = ConsoleCard.Find;
        private bool _canRename = true;
        private string _lastEvent = "";
        private string _findTitle = "1V1";
        private string _status = "Offline";
        private Brush _statusBrush = Theme.Muted;
        private string _queueText = "";
        private bool _hasMatch;
        private string _opponentText = "";
        private string _seedText = "";
        private string _matchState = "";
        private string _timerText = "";
        private bool _hasResult;
        private string _resultText = "";
        private Brush _resultBrush = Theme.Muted;
        private string _gameText = "";

        public MainViewModel(ClientSettings settings, ServerEndpoint endpoint, bool autoSearch)
        {
            _settings = settings;
            _endpoint = endpoint;
            _playerName = settings.Name;
            _autoSearch = autoSearch;
            _practiceStore = PracticeStore.For(settings.FilePath);
            _replayStore = ReplayStore.For(settings.FilePath);

            FindCardCommand = new RelayCommand(() => { Show(ConsoleCard.Find); ToggleSearch(); }, () => _link == LinkState.Connected && _phase != ClientPhase.InMatch);
            JoinCardCommand = new RelayCommand(SelectJoin, () => _phase != ClientPhase.InMatch);
            LobbyCardCommand = new RelayCommand(() => Show(ConsoleCard.Lobby), () => _phase != ClientPhase.InMatch);
            InitConsole();
            InitBoards();
            InitMods();
            OpenLobbyCommand = new RelayCommand(CreateLobby, () => _phase != ClientPhase.InMatch && !_hasLobby && _link == LinkState.Connected);
            StartLobbyCommand = new RelayCommand(StartLobbyRace, () => _hasLobby && _lobbyState != null && _lobbyState.YouAreHost && !_lobbyState.Racing && _lobbyState.Members.Count >= 2 && !_matchActive);
            ReadyCommand = new RelayCommand(PressReady, () => ShowReady);
            SendChatCommand = new RelayCommand(SendChatFromBox, () => !string.IsNullOrWhiteSpace(_chatDraft));
            LobbyRules = new RulesModel((MatchRules)settings.LobbyRules, (MatchMode)settings.LobbyMode, true, r =>
            {
                _settings.LobbyRules = (int)r.Stored;
                _settings.LobbyMode = (int)r.Mode;
                _settings.Save();
            });
            PracticeRules = new RulesModel((MatchRules)settings.PracticeRules, MatchMode.Duel, false, r =>
            {
                _settings.PracticeRules = (int)r.Stored;
                _settings.Save();
            });
            LaunchCommand = new RelayCommand(LaunchGame);
            ForfeitCommand = new RelayCommand(() => { if (_matchActive) { ToHost(new PlayerForfeit()); Log(Solo ? "Run ended." : "Forfeit requested."); } }, () => _matchActive);
            DismissCommand = new RelayCommand(DismissMatch, () => HasMatch && !_matchActive);
            JoinLobbyCommand = new RelayCommand(JoinLobby, () => _phase != ClientPhase.InMatch && !string.IsNullOrWhiteSpace(_joinCode));
            CopyCodeCommand = new RelayCommand(CopyCode, () => _hasLobby);
            CancelLobbyCommand = new RelayCommand(CancelLobby, () => _hasLobby);
            ExportCommand = new RelayCommand(ExportMatchData);
            RetryCommand = new RelayCommand(ConnectToServer, () => _server == null);
            PrivacyCommand = new RelayCommand(OpenPrivacy);
            PracticeCommand = new RelayCommand(() => StartPractice(null, null, null, true), () => _phase != ClientPhase.InMatch && !_matchActive);
            PracticeSeedCommand = new RelayCommand(() => { _soloAgainst = null; StartPractice(_match.Seed, LastBans(), _match.Rules, false, _match.Segment); }, () => !_matchActive && _match != null && HasMatch);
            ReportBugCommand = new RelayCommand(ReportBug, () => _endpoint.ReportUrl != null);
            LinkSteamCommand = new RelayCommand(StartSteamLink, () => _link == LinkState.Connected && _steamAvailable && _phase != ClientPhase.InMatch);

            _voice = new VoiceEngine(Log);
            _voice.Device = settings.VoiceDevice ?? "";
            _voice.MicVolume = settings.VoiceMicVolume / 100f;
            _voice.OutputVolume = settings.VoiceOutVolume / 100f;
            _voice.PushToTalk = settings.VoicePushToTalk;
            _voice.PushToTalkKey = settings.VoiceKey;
            _voice.SetMuted(settings.VoiceMuted);
            _voice.Enabled = settings.VoiceEnabled;
            VoiceCommand = new RelayCommand(() => OpenVoice?.Invoke());

            _mod.Received += OnModThread;
            _mod.ConnectionChanged += connected => _ui.BeginInvoke(new Action(() => OnModConnection(connected)));
            try
            {
                _mod.Start(settings.ModPort);
            }
            catch (Exception e)
            {
                Log("Could not listen for the game on port " + settings.ModPort + ": " + e.Message + " (is another client running?)");
            }

            _timer.Tick += (s, e) => OnTimer();
            _timer.Start();
            UpdateGameText();
            // Only a player who pressed CONNECT (kept from the last time) is signed in. Signing in waits a moment
            // for Steam, so the very first sign-in already carries the Steam name.
            if (_settings.Online) _connectDue = DateTime.UtcNow.AddSeconds(4);
            InitEdition();
        }

        // What STEAM LOBBIES does on top (MainViewModel.Steam.cs); each does nothing outside Steam mode.
        partial void InitEdition();
        partial void OnWelcomedEdition();
        partial void OnLobbyStateEdition(LobbyState state);
        partial void OnLobbyClosedEdition();
        partial void OnLobbyLeftEdition();
        partial void OnLinkLostEdition();
        partial void TickEdition();

        // Asks the window to put the cursor in the lobby code box.
        public event Action FocusJoinCode;

        public ICommand FindCardCommand { get; private set; }
        public ICommand JoinCardCommand { get; private set; }
        public ICommand LobbyCardCommand { get; private set; }
        public ICommand LaunchCommand { get; private set; }
        public ICommand ForfeitCommand { get; private set; }
        public ICommand DismissCommand { get; private set; }
        public ICommand JoinLobbyCommand { get; private set; }
        public ICommand CopyCodeCommand { get; private set; }
        public ICommand CancelLobbyCommand { get; private set; }
        public ICommand ExportCommand { get; private set; }
        public ICommand RetryCommand { get; private set; }
        public ICommand LinkSteamCommand { get; private set; }
        public ICommand PrivacyCommand { get; private set; }
        public ICommand PracticeCommand { get; private set; }
        public ICommand PracticeSeedCommand { get; private set; }
        public ICommand OpenLobbyCommand { get; private set; }
        public ICommand StartLobbyCommand { get; private set; }
        public ICommand ReadyCommand { get; private set; }
        public ICommand SendChatCommand { get; private set; }

        // The CREATE LOBBY picker and the SOLO picker.
        public RulesModel LobbyRules { get; private set; }
        public RulesModel PracticeRules { get; private set; }
        public string PracticeRulesText { get { return "MODIFIERS"; } }

        // The lobby room, shown in place of the cards while in a lobby (and not racing).
        public ObservableCollection<LobbyMemberRow> LobbyMembers { get; } = new ObservableCollection<LobbyMemberRow>();
        public bool ShowLobbyRoom { get { return _hasLobby && !_hasMatch; } }
        public string LobbyTitle
        {
            get
            {
                if (_lobbyState == null) return "LOBBY";
                return (_lobbyState.Mode == MatchMode.Party ? "PARTY LOBBY" : "1V1 LOBBY") + "  " + _lobbyState.Members.Count + "/" + _lobbyState.MaxPlayers;
            }
        }
        public string LobbyRulesText
        {
            get
            {
                if (_lobbyState == null) return "";
                return RulesText.Describe(_lobbyState.Rules) + (_lobbyState.Mode == MatchMode.Party ? ". One life, no restarts, unranked." : ". Unranked.")
                    + (_lobbyState.AllowUnapproved ? " Unapproved mods allowed." : "");
            }
        }
        public bool IsLobbyHost { get { return _lobbyState != null && _lobbyState.YouAreHost; } }
        public string StartText { get { return _lobbyState != null && _lobbyState.Mode == MatchMode.Duel ? "REMATCH" : "START MATCH"; } }

        // The party standings in the race view (in place of the split table).
        public ObservableCollection<StandingRow> StandingsRows { get; } = new ObservableCollection<StandingRow>();
        public bool IsPartyMatch { get { return _match != null && _match.Party; } }
        public bool ShowSplitTable { get { return !IsPartyMatch; } }

        // The ready check: shown while this player's world is loaded and they have not pressed ready.
        public bool ShowReady
        {
            get
            {
                if (_match == null || !_matchActive || _practice != null || _started || _roster == null) return false;
                RosterEntry me = _roster.Find(_roster.YourRacer);
                return me != null && me.Status == RacerStatus.Loaded;
            }
        }

        // Chat (lobby and race).
        public ObservableCollection<ChatLine> ChatLines { get; } = new ObservableCollection<ChatLine>();
        public string ChatDraft { get { return _chatDraft; } set { Set(ref _chatDraft, value); } }
        public bool ChatHasLines { get { return _chatHasLines; } private set { Set(ref _chatHasLines, value); } }

        // The ghost's opacity in the game (0-100), sent to the mod.
        public double GhostOpacity
        {
            get { return _settings.GhostOpacity; }
            set
            {
                int v = (int)Math.Round(Math.Max(0, Math.Min(100, value)));
                if (_settings.GhostOpacity == v) return;
                _settings.GhostOpacity = v;
                _settings.Save();
                Raise();
                Raise(nameof(GhostOpacityText));
                ToMod(new ModSettings { GhostOpacityPercent = v });
            }
        }
        public string GhostOpacityText { get { return _settings.GhostOpacity + "%"; } }

        // The seed box of the practice panel: blank for a random seed.
        public string PracticeSeed
        {
            get { return _practiceSeed; }
            set { Set(ref _practiceSeed, value); Raise(nameof(PracticeSeedEmpty)); }
        }

        public bool PracticeSeedEmpty { get { return string.IsNullOrEmpty(_practiceSeed); } }
        public string OppColumnText { get { return _lastWasPractice ? "BEST" : "OPPONENT"; } }
        public string ForfeitText { get { return Solo ? "END RUN" : "FORFEIT"; } }
        public ICommand ReportBugCommand { get; private set; }
        // Asks the window to open the bug report window.
        internal event Action<BugReportContext> OpenBugReport;
        // The privacy notice lives on the official server's web page.
        public bool HasPrivacyPage { get { return !_endpoint.Custom; } }
        public ObservableCollection<SplitRow> Splits { get; } = new ObservableCollection<SplitRow>();
        public ObservableCollection<RaceRecord> Recent { get; } = new ObservableCollection<RaceRecord>();
        private readonly List<RaceRecord> _records = new List<RaceRecord>();
        // Kept for the match data export; the window only shows the latest line.
        public List<string> LogLines { get; } = new List<string>();

        public string PlayerName { get { return _playerName; } set { Set(ref _playerName, value); } }
        public bool CanRename { get { return _canRename; } private set { Set(ref _canRename, value); } }
        public string LastEvent { get { return _lastEvent; } private set { Set(ref _lastEvent, value); } }
        public string FindTitle { get { return _findTitle; } private set { Set(ref _findTitle, value); } }
        public string SessionText
        {
            get
            {
                return "TOTAL " + _records.Count;
            }
        }
        public bool HasRecent { get { return _records.Count > 0; } }
        public bool IsOffline { get { return _server == null; } }
        public string VersionText { get { return "WKSR " + _version + (_endpoint.Custom ? " TEST" : ""); } }
        public string VersionNumber { get { return _version; } }

        public ConsoleCard Selected
        {
            get { return _selected; }
            set
            {
                if (!Set(ref _selected, value)) return;
                Raise(nameof(IsFindSelected));
                Raise(nameof(IsJoinSelected));
                Raise(nameof(IsLobbySelected));
                Raise(nameof(IsRankedSoloSelected));
                Raise(nameof(IsUnrankedSoloSelected));
                Raise(nameof(IsReplaysSelected));
            }
        }
        public bool IsFindSelected { get { return _selected == ConsoleCard.Find; } }
        public bool IsJoinSelected { get { return _selected == ConsoleCard.Join; } }
        public bool IsLobbySelected { get { return _selected == ConsoleCard.Lobby; } }
        public string BansText { get { return _bansText; } private set { Set(ref _bansText, value); } }
        public bool SteamAvailable { get { return _steamAvailable; } private set { Set(ref _steamAvailable, value); } }
        public bool SteamLinked
        {
            get { return _steamLinked; }
            private set { if (Set(ref _steamLinked, value)) Raise(nameof(SteamButtonText)); }
        }
        public string SteamButtonText { get { return _steamLinked ? "STEAM: LINKED" : "LINK STEAM"; } }
        public string SteamStatus { get { return _steamStatus; } private set { Set(ref _steamStatus, (value ?? "").ToUpperInvariant()); } }
        public string Status { get { return _status; } private set { Set(ref _status, (value ?? "").ToUpperInvariant()); } }
        public Brush StatusBrush { get { return _statusBrush; } private set { Set(ref _statusBrush, value); } }
        public string QueueText { get { return _queueText; } private set { Set(ref _queueText, value); } }
        public bool HasMatch { get { return _hasMatch; } private set { if (Set(ref _hasMatch, value)) { RaiseCards(); Raise(nameof(ShowLobbyRoom)); } } }
        public bool ShowCards { get { return !_hasMatch && !_hasLobby; } }
        public string OpponentText { get { return _opponentText; } private set { Set(ref _opponentText, value); } }
        public string SeedText { get { return _seedText; } private set { Set(ref _seedText, value); } }
        public string MatchState { get { return _matchState; } private set { Set(ref _matchState, value); } }
        public string TimerText { get { return _timerText; } private set { Set(ref _timerText, value); } }
        public bool HasResult { get { return _hasResult; } private set { Set(ref _hasResult, value); } }
        public string ResultText { get { return _resultText; } private set { Set(ref _resultText, value); } }
        public Brush ResultBrush { get { return _resultBrush; } private set { Set(ref _resultBrush, value); } }
        public string GameText { get { return _gameText; } private set { Set(ref _gameText, value); } }
        public bool HasLobby
        {
            get { return _hasLobby; }
            private set
            {
                if (!Set(ref _hasLobby, value)) return;
                Raise(nameof(JoinHint));
                RaiseCards();
                Raise(nameof(ShowLobbyRoom));
                if (!value)
                {
                    _lobbyState = null;
                    LobbyMembers.Clear();
                    RaiseLobby();
                }
            }
        }
        public string LobbyCode { get { return _lobbyCode; } private set { Set(ref _lobbyCode, value); } }
        public string LobbyText { get { return _lobbyText; } private set { if (Set(ref _lobbyText, value)) Raise(nameof(JoinHint)); } }
        public string JoinHint
        {
            get { return string.IsNullOrEmpty(_lobbyText) || _hasLobby ? "Paste the code your friend sent you and press JOIN. Friendly matches do not change ratings." : _lobbyText; }
        }
        public string JoinCode { get { return _joinCode; } set { Set(ref _joinCode, value); } }
        public string CopyText { get { return DateTime.UtcNow < _copiedUntil ? "COPIED!" : "COPY CODE"; } }

        public string RatingText { get { return _rating == 0 ? "----" : _rating.ToString(CultureInfo.InvariantCulture); } }
        // New players' ratings move twice as fast for their first 10 ranked races (the server's provisional K).
        public string RecordText { get { return _rating == 0 ? "" : _wins + "W  " + _losses + "L" + (_wins + _losses < ProvisionalGames ? "  PROVISIONAL" : ""); } }
        private const int ProvisionalGames = 10;

        public void Shutdown()
        {
            _timer.Stop();
            _voice.Dispose();
            if (_server != null) _server.Stop();
            _mod.Stop();
        }

        // ---- connection ---------------------------------------------------

        private const string LinkSteamHint = "start the game through Steam and it links by itself.";

        // What the status says after the server refused the sign-in ("Update needed"...), until the next try: the
        // connection closing right after the refusal must not replace it, nor its reason the server's own words.
        private string _refusedStatus;

        private void ConnectToServer()
        {
            if (_server != null) return;
            _refusedStatus = null;
            _settings.Name = _playerName;
            _settings.Save();
            _server = NewLink(() => new ClientHello
            {
                PlayerId = _settings.PlayerId,
                Token = _settings.TokenBytes(),
                Name = HelloName(),
                ClientVersion = _version,
                GameConnected = _mod.Connected,
            });
            if (_server == null) return;
            _server.Received += OnServerThread;
            _server.StateChanged += (state, reason) => _ui.BeginInvoke(new Action(() => OnLinkState(state, reason)));
            _server.Start();
            Raise(nameof(IsOffline));
        }

        // The match server; in STEAM LOBBIES the lobby's host (MainViewModel.Steam.cs).
        private IServerLink NewLink(Func<ClientHello> hello)
        {
            if (SteamMode) return NewSteamLink(hello);
            return new ServerConnection(_endpoint.Host, _endpoint.Port, _endpoint.Tls, hello);
        }

        private void Disconnect()
        {
            if (_server == null) return;
            _server.Stop();
            _server = null;
            OnLinkState(LinkState.Disconnected, "");
            Raise(nameof(IsOffline));
        }

        // The name is sent when connecting, so a new name means a new connection. Only allowed while idle.
        public void CommitName()
        {
            string name = (_playerName ?? "").Trim();
            if (name != _playerName) PlayerName = name;
            if (!_canRename || name == _settings.Name && _server != null) return;
            if (name.Length < 2)
            {
                Log("Names need at least 2 characters.");
                PlayerName = _settings.Name;
                return;
            }
            _settings.Name = name;
            _settings.Save();
            Log("Name changed to " + name + ". Reconnecting...");
            Disconnect();
            ConnectToServer();
        }

        private void OnLinkState(LinkState state, string reason)
        {
            _link = state;
            switch (state)
            {
                case LinkState.Connecting:
                    Status = "Connecting...";
                    StatusBrush = Theme.Warn;
                    _phase = ClientPhase.Connecting;
                    break;
                case LinkState.Connected:
                    Status = OnlineText();
                    StatusBrush = Theme.Good;
                    if (_phase != ClientPhase.InMatch) _phase = ClientPhase.Idle;
                    break;
                default:
                    Status = _refusedStatus ?? (_server == null ? "Offline" : "Offline - retrying");
                    StatusBrush = Theme.Bad;
                    if (_phase != ClientPhase.InMatch) _phase = ClientPhase.Offline;
                    QueueText = "";
                    if (!string.IsNullOrEmpty(reason) && _refusedStatus == null) Log(reason);
                    OnServerLost();
                    _voice.Disconnect();
                    _voiceAvailable = false;
                    RaiseVoice();
                    if (_hasLobby)
                    {
                        HasLobby = false;
                        LobbyText = "Your lobby closed because the connection to the server was lost.";
                    }
                    if (_pendingJoin != 0) LobbyText = "Waiting for the server connection to join the lobby...";
                    OnLinkLostEdition();
                    break;
            }
            UpdateFindTitle();
            PushStatus();
        }

        // ---- server messages ----------------------------------------------

        private void OnServerThread(Message message)
        {
            if (message is OpponentState)
            {
                if (_practice == null)
                {
                    _voice.SetSource((OpponentState)message);
                    ToMod(message);
                }
                return;
            }
            _ui.BeginInvoke(new Action(() => OnServerMessage(message)));
        }

        private void OnServerMessage(Message message)
        {
            // During a practice run WKSR runs the match itself: nothing the server says about matches applies.
            if (_practice != null && IsMatchMessage(message)) return;
            Dispatch(message);
        }

        private static bool IsMatchMessage(Message message)
        {
            return message is MatchFound || message is MatchStart || message is OpponentSplit || message is OpponentAttempt
                || message is OpponentFinished || message is MatchPaused || message is MatchResumed || message is BanState
                || message is MatchResult || message is OpponentState || message is MatchRoster;
        }

        // Server messages, and the practice host's (which stand in for them).
        private void Dispatch(Message message)
        {
            switch (message)
            {
                case ServerWelcome welcome:
                    _welcomeName = welcome.Name;
                    _lobbyBrowser = false;
                    // The server's name wins: it refuses a taken name and an admin can set one.
                    if (welcome.Name.Length > 0 && welcome.Name != _settings.Name)
                    {
                        _settings.Name = welcome.Name;
                        _settings.Save();
                        PlayerName = welcome.Name;
                    }
                    _rating = welcome.Rating;
                    _wins = welcome.Wins;
                    _losses = welcome.Losses;
                    SteamAvailable = welcome.SteamAvailable;
                    SteamLinked = welcome.SteamLinked;
                    _steamRequired = welcome.SteamRequiredForRanked;
                    SteamStatus = !welcome.SteamAvailable ? "" : welcome.SteamLinked ? "" : welcome.SteamRequiredForRanked ? "Needed for ranked" : "Optional";
                    _playersOnline = welcome.PlayersOnline;
                    Status = OnlineText();
                    Raise(nameof(RatingText));
                    Raise(nameof(RecordText));
                    PushStatus();
                    if (!SteamMode) Log("Connected to the match server as " + welcome.Name + ".");
                    else Log(_host != null ? "Your lobby is starting..." : "Connected to the lobby's host as " + welcome.Name + ".");
                    _steamIdentitySent = false;
                    _playersQueued = welcome.PlayersQueued;
                    OnWelcomeChat();
                    _verdictBeforeList = _gameMods != null;
                    SendModsToServer();
                    if (_autoSearch && _phase == ClientPhase.Idle) { _autoSearch = false; ToggleSearch(); }
                    if (_pendingJoin != 0 && _phase != ClientPhase.InMatch) SendLobbyJoin(_pendingJoin);
                    _pendingJoin = 0;
                    OnWelcomedEdition();
                    break;

                case LobbyCreated created: OnLobbyCreated(created); break;
                case ServerFeatures features: OnServerFeatures(features); break;
                case LobbyList lobbyList: OnLobbyList(lobbyList); break;
                case LobbyState lobby: OnLobbyState(lobby); break;
                case LobbyClosed closed:
                    if (closed.LobbyId != _lobbyId) break;
                    HasLobby = false;
                    LobbyText = closed.Reason;
                    Log(closed.Reason);
                    OnLobbyClosedEdition();
                    break;
                case MatchRoster roster: OnRoster(roster); break;
                case ChatMessage chat: OnChat(chat); break;
                case VoiceHello hello:
                    _voiceAvailable = hello.Port > 0;
                    _voice.Connect(hello, _endpoint.Host);
                    _server?.Send(new VoiceState { Enabled = _settings.VoiceEnabled });
                    RaiseVoice();
                    break;
                case VoicePeers peers:
                    _voice.SetPeers(peers);
                    break;

                case ServerStatus serverStatus:
                    OnServerStatus(serverStatus);
                    break;

                case BoardPage boardPage:
                    OnBoardPage(boardPage);
                    break;

                case ReplayChunk replayChunk:
                    OnReplayChunk(replayChunk);
                    break;

                case BoardNote boardNote:
                    OnBoardNote(boardNote);
                    break;

                case ModVerdict modVerdict:
                    OnModVerdict(modVerdict);
                    break;

                case ModsPage modsPage:
                    OnModsPage(modsPage);
                    break;

                case QueueStatus queue:
                    if (_phase != ClientPhase.InMatch)
                    {
                        if (queue.Queued && _phase != ClientPhase.Searching) _searchStarted = DateTime.UtcNow - TimeSpan.FromSeconds(queue.WaitSeconds);
                        _phase = queue.Queued ? ClientPhase.Searching : ClientPhase.Idle;
                        UpdateFindTitle();
                        if (!queue.Queued) QueueText = "";
                    }
                    break;

                case MatchFound found: OnMatchFound(found); break;
                case MatchStart start: OnMatchStart(start); break;
                case OpponentState state: ToMod(state); break;
                case OpponentSplit split: OnOpponentSplit(split); break;
                case OpponentAttempt attempt: OnOpponentAttempt(attempt); break;
                case OpponentFinished finished: OnOpponentFinished(finished); break;
                case MatchPaused paused: OnMatchPaused(paused); break;
                case BanState bans: OnBanState(bans); break;
                case SteamLinkUrl link: OnSteamLinkUrl(link); break;
                case SteamLinkResult linked: OnSteamLinkResult(linked); break;
                case MatchResumed resumed: OnMatchResumed(resumed); break;
                case MatchResult result: OnMatchResult(result); break;
                case Pong pong: OnPong(pong); break;
                case ErrorMessage error: OnServerError(error); break;
            }
        }

        // "SILOS SEGMENT  -  " in front of what a segment run is called.
        private static string SegmentTag(MatchFound found)
        {
            string name = Segments.Name(found.Segment);
            return name.Length > 0 ? name + " SEGMENT  -  " : "";
        }

        private void OnMatchFound(MatchFound found)
        {
            bool resume = _match != null && _match.MatchId == found.MatchId;
            _match = found;
            _matchActive = true;
            _phase = ClientPhase.InMatch;
            QueueText = "";
            _rankedSolo = _practice == null && found.Mode == MatchMode.Solo;
            _lastWasRankedSolo = false;
            if (!resume)
            {
                Splits.Clear();
                _myLevels.Clear();
                _oppLevels.Clear();
                _oppSplits.Clear();
                _oppFinished.Clear();
                _mySplits.Clear();
                _myDied = false;
                _roster = null;
                StandingsRows.Clear();
                _paused = null;
                _serverLost = false;
                _resumeServerMs = 0;
                _lastResult = null;
                _started = false;
                _goServerMs = 0;
                _gameMs = -1;
                _attempt = Math.Max(1, found.Attempt);
                _banState = null;
                _recorder = new RunRecorder();
                OfferReplay(null);
                BoardNoteText = "";
                _lastWasPractice = Solo;
                Raise(nameof(OppColumnText));
                Raise(nameof(ForfeitText));
                BansText = found.ArtifactBans ? "Artifact bans: waiting for both games" : "";
                HasResult = false;
                MatchState = found.ArtifactBans ? "Artifact bans (in the game)" : "Start your run in the game (F9 on the menu)";
                Raise(nameof(IsPartyMatch));
                Raise(nameof(ShowSplitTable));
            }
            HasMatch = true;
            LobbyText = "";
            if (_practice != null)
            {
                ISoloBest best = _practice.Best;
                bool others = best != null && found.OpponentName != SoloHost.BestName;
                OpponentText = SegmentTag(found) + (best == null ? "UNRANKED RUN" : others ? "UNRANKED VS " + found.OpponentName.ToUpperInvariant() : "UNRANKED VS YOUR BEST");
                SeedText = "Seed " + found.Seed + "  -  unranked" + (best != null ? (others ? "  -  their run " : "  -  best ") + best.Describe() : _soloSeedTyped ? "  -  no best yet" : "  -  new seed on restart");
                if (!resume && !others) Log("Unranked run on seed " + found.Seed + (best != null ? ", against your best (" + best.Describe() + ")" : "") + ".");
            }
            else if (_rankedSolo)
            {
                OpponentText = found.Segment != Segments.None ? SegmentTag(found) + "RANKED" : "RANKED SOLO";
                SeedText = "Seed " + found.Seed + "  -  " + RulesText.Describe(found.Rules).ToLowerInvariant() + "  -  ranked solo";
                if (!resume) Log((found.Segment != Segments.None ? "Ranked " + Segments.Find(found.Segment).Title + " segment run" : "Ranked solo run") + " on seed " + found.Seed + ". A restart in the game gives a new seed.");
            }
            else if (found.Party)
            {
                OpponentText = "PARTY MATCH  " + found.PlayerCount + " PLAYERS";
                SeedText = "Seed " + found.Seed + "  -  " + RulesText.Describe(found.Rules).ToLowerInvariant() + "  -  unranked";
                if (!resume) Log("Party match with " + found.PlayerCount + " players started. One life each, no restarts.");
            }
            else
            {
                // Steam lobbies have no ratings.
                OpponentText = "VS " + found.OpponentName + (SteamMode ? "" : "  (" + found.OpponentRating + ")");
                SeedText = "Seed " + found.Seed + "  -  " + RulesText.Describe(found.Rules).ToLowerInvariant() + (found.Ranked ? "  -  ranked" : "  -  friendly (unranked)");
                Log("Match found against " + found.OpponentName + ".");
            }
            UpdateFindTitle();
            PushStatus();

            if (_mod.Connected)
            {
                MarkPractice();
                ToMod(found);
                if (_started) SendCountdown();
            }
            else if (!GameLauncher.IsRunning())
            {
                // WKSR never starts the game by itself: the player does (LAUNCH GAME, or the mod manager).
                Log("Start White Knuckle to play: LAUNCH GAME, or Start modded in your mod manager.");
            }
        }

        private void OnMatchStart(MatchStart start)
        {
            if (_match == null || _match.MatchId != start.MatchId) return;
            _goServerMs = start.GoServerTimeMs;
            _started = true;
            MatchState = "Countdown";
            SendCountdown();
        }

        private void SendCountdown()
        {
            if (_match == null) return;
            long until = _goServerMs - ServerNow();
            ToMod(new StartCountdown
            {
                MatchId = _match.MatchId,
                MsUntilGo = (int)Math.Max(int.MinValue / 2, Math.Min(int.MaxValue / 2, until)),
            });
        }

        private void OnMatchResult(MatchResult result)
        {
            _voice.ClearPositions();
            // A ranked solo run reads like a solo run here too (no opponent, no rating change).
            bool rankedSolo = _rankedSolo;
            bool practice = _practice != null || rankedSolo;
            bool newBest = KeepRun(result, practice);
            _practice = null;
            _rankedSolo = false;
            _lastWasRankedSolo = rankedSolo;
            _recorder = null;
            Raise(nameof(ForfeitText));
            ToMod(result);
            _lastResult = result;
            _paused = null;
            _serverLost = false;
            _matchActive = false;
            TimerText = result.YourTimeMs >= 0 ? RaceFormat.Time(result.YourTimeMs) : "";
            _phase = _link == LinkState.Connected ? ClientPhase.Idle : ClientPhase.Offline;
            _started = false;
            bool ranked = !practice && (_match == null || _match.Ranked);
            _rating = result.NewRating;
            if (ranked && result.Outcome == MatchOutcome.Win) _wins++;
            else if (ranked && result.Outcome == MatchOutcome.Loss) _losses++;

            string title;
            bool died = result.End == MatchEnd.Death && result.YourTimeMs < 0 && result.Outcome == MatchOutcome.Loss;
            if (result.Place > 0)
            {
                title = result.Place == 1 ? "1ST PLACE" : Standings.Ordinal(result.Place).ToUpperInvariant() + " OF " + result.Standings.Count;
                ResultBrush = result.Place == 1 ? Theme.Good : result.Place <= 3 ? Theme.Warn : Theme.Muted;
                ShowFinalStandings(result);
            }
            else switch (result.Outcome)
            {
                case MatchOutcome.Win: title = rankedSolo ? "FINISHED" : practice ? (newBest ? "NEW BEST" : "FINISHED") : "VICTORY"; ResultBrush = Theme.Good; break;
                case MatchOutcome.Loss: title = practice ? (died ? (newBest && !rankedSolo ? "DIED - FURTHEST YET" : "DIED") : "NOT COUNTED") : "DEFEAT"; ResultBrush = Theme.Bad; break;
                default: title = practice ? "RUN ENDED" : "MATCH ABORTED"; ResultBrush = Theme.Warn; break;
            }
            int change = result.NewRating - result.OldRating;
            string rating = result.Outcome == MatchOutcome.Aborted || practice ? ""
                : result.Place > 0 ? "   Party match: no rating change"
                : !ranked ? "   Friendly match: no rating change"
                : "   Rating " + result.OldRating + " > " + result.NewRating + " (" + (change >= 0 ? "+" : "") + change + ")";
            ResultText = title + " - " + result.Reason + rating;
            HasResult = true;
            RaiseSaveReplay();
            MatchState = "Finished";
            Log(title + ": " + result.Reason);
            Record(result, ranked, practice, newBest, died, rankedSolo);
            Raise(nameof(RatingText));
            Raise(nameof(RecordText));
            UpdateFindTitle();
            PushStatus();
        }

        // Keeps this match's run as the best on its seed when it is one (RunRecording.IsBetterThan): a run the
        // player finished, or one that ended with a death (theirs, or in a race the other racer's). Returns whether it
        // was kept.
        private bool KeepRun(MatchResult result, bool practice)
        {
            RunRecorder recorder = _recorder;
            if (recorder == null || _match == null || recorder.Cheated) return false;
            bool finished;
            int endMs;
            if (result.YourTimeMs >= 0 && result.Outcome != MatchOutcome.Aborted)
            {
                finished = true;
                endMs = result.YourTimeMs;
            }
            else if (result.End == MatchEnd.Death)
            {
                finished = false;
                endMs = recorder.DiedMs >= 0 ? recorder.DiedMs : Math.Max(0, _gameMs);
            }
            else
            {
                return false;
            }
            try
            {
                RunRecording run = recorder.Finish(_match.Seed, endMs, finished, !practice);
                run.Rules = _match.Rules;
                run.Bans = LastBans() ?? new List<string>();
                run.Player = _welcomeName ?? _playerName ?? "";
                run.Version = BuildTag.ShortVersion(_version);
                run.Kind = _rankedSolo ? RunKind.RankedSolo : _practice != null ? RunKind.UnrankedSolo : _match.Ranked ? RunKind.Ranked1v1 : RunKind.Friendly;
                run.Segment = _match.Segment;
                OfferReplay(run);
                bool kept = _practiceStore.SaveIfBest(run);
                if (kept && !practice) Log("Your run on seed " + _match.Seed + " is kept: RUN THIS SEED plays against it.");
                return kept;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log("Could not keep the run for practice: " + e.Message);
                return false;
            }
        }

        private void Record(MatchResult result, bool ranked, bool practice, bool newBest, bool died, bool rankedSolo = false)
        {
            if (_match == null) return;
            var record = new RaceRecord
            {
                When = DateTime.Now,
                Opponent = _match.OpponentName,
                OpponentRating = _match.OpponentRating,
                Seed = _match.Seed,
                Ranked = ranked,
                Competitive = _match.Competitive,
                Outcome = result.Outcome,
                Reason = result.Reason,
                YourTimeMs = result.YourTimeMs,
                OpponentTimeMs = result.OpponentTimeMs,
                OldRating = result.OldRating,
                NewRating = result.NewRating,
                Splits = Splits.ToList(),
                Bans = _match.ArtifactBans ? BansSummary(_banState, "none") : null,
                Practice = practice,
                RankedSolo = rankedSolo,
                NewBest = newBest,
                Died = died,
                Place = result.Place,
                Racers = result.Standings.Count,
                Rules = RulesText.Describe(_match.Rules),
            };
            _records.Add(record);
            Recent.Insert(0, record);
            while (Recent.Count > 6) Recent.RemoveAt(Recent.Count - 1);
            Raise(nameof(SessionText));
            Raise(nameof(HasRecent));
        }

        private void DismissMatch()
        {
            if (_matchActive) return;
            HasMatch = false;
            HasResult = false;
            Splits.Clear();
            _myLevels.Clear();
            _oppLevels.Clear();
            MatchState = "";
            TimerText = "";
        }

        private void OnServerError(ErrorMessage error)
        {
            Log("Server: " + error.Text);
            if (error.Code == ErrorCode.LobbyNotFound || error.Code == ErrorCode.LobbyUnavailable || error.Code == ErrorCode.LobbyBuildMismatch
                || error.Code == ErrorCode.LobbySettingsMismatch
                || error.Code == ErrorCode.RateLimited && error.Text.Contains("lobby"))
            {
                LobbyText = error.Text;
            }
            if (error.Code == ErrorCode.SteamLinkRequired)
            {
                _phase = _link == LinkState.Connected ? ClientPhase.Idle : ClientPhase.Offline;
                QueueText = "Ranked needs your Steam account linked: " + LinkSteamHint;
                UpdateFindTitle();
                PushStatus();
            }
            if (error.Code == ErrorCode.ModsNotApproved)
            {
                if (_phase == ClientPhase.Searching) _phase = _link == LinkState.Connected ? ClientPhase.Idle : ClientPhase.Offline;
                QueueText = error.Text;
                if (error.Text.Contains("lobby")) LobbyText = error.Text;
                UpdateFindTitle();
                PushStatus();
            }
            if (error.Code == ErrorCode.BadToken && StartOverForSteam()) return;
            if (error.Code == ErrorCode.VersionMismatch || error.Code == ErrorCode.BadToken || error.Code == ErrorCode.InvalidName || error.Code == ErrorCode.RegistrationLimited)
            {
                if (_server != null) _server.Stop();
                _server = null;
                Status = error.Code == ErrorCode.InvalidName ? "Name refused"
                    : error.Code == ErrorCode.VersionMismatch ? "Update needed"
                    : error.Code == ErrorCode.RegistrationLimited ? "Sign-up limit"
                    : "Refused by the server";
                _refusedStatus = Status;
                StatusBrush = Theme.Bad;
                Raise(nameof(IsOffline));
                // A refused rename: go back to the name this player already has and reconnect with it.
                if (error.Code == ErrorCode.InvalidName && !string.IsNullOrEmpty(_welcomeName) && _welcomeName != _settings.Name)
                {
                    _settings.Name = _welcomeName;
                    _settings.Save();
                    PlayerName = _welcomeName;
                    Log("Keeping the name " + _welcomeName + ".");
                    ConnectToServer();
                }
            }
        }

        // The estimate with the lowest round trip is the most trustworthy one.
        private void OnPong(Pong pong)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long rtt = now - pong.ClientTimeMs;
            if (rtt < 0 || rtt > 5000) return;
            _clockSamples.Add(new KeyValuePair<long, long>(rtt, pong.ServerTimeMs + rtt / 2 - now));
            if (_clockSamples.Count > 8) _clockSamples.RemoveAt(0);
            KeyValuePair<long, long> best = _clockSamples[0];
            foreach (KeyValuePair<long, long> sample in _clockSamples) if (sample.Key < best.Key) best = sample;
            _offsetMs = best.Value;
        }

        private long ServerNow()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _offsetMs;
        }

        // ---- mod messages ---------------------------------------------------

        private void OnModThread(Message message)
        {
            if (message is PlayerState)
            {
                if (!_matchActive) return;
                _gameMs = ((PlayerState)message).RaceTimeMs;
                _practice?.NoteGameTime(_gameMs);
                if (_practice == null) _voice.SetListener((PlayerState)message);
                _recorder?.Add(message);
                IServerLink server = _server;
                if (_practice == null && server != null) server.Send(message);
                return;
            }
            _ui.BeginInvoke(new Action(() => OnModMessage(message)));
        }

        private void OnModConnection(bool connected)
        {
            if (!connected)
            {
                _modVersion = null;
                _gameVersion = null;
                if (_matchActive) Log("The game disconnected. The match is paused until it is back.");
                OnGameLinkLost();
                OnGameModsGone();
            }
            // The server pauses a running race while a racer's game is gone.
            _server?.Send(new GameStatus { Connected = connected });
            UpdateGameText();
        }

        // What the game reports goes to the race server, or to the practice host during a practice run.
        private void ToHost(Message message)
        {
            if (_practice != null) _practice.OnMessage(message);
            else _server?.Send(message);
        }

        private void OnModMessage(Message message)
        {
            switch (message)
            {
                case ModHello hello:
                    _modVersion = hello.ModVersion;
                    _gameVersion = hello.GameVersion;
                    _modIncompatible = hello.ProtocolVersion != ProtocolConstants.ProtocolVersion;
                    if (_settings.LastModVersion != hello.ModVersion)
                    {
                        _settings.LastModVersion = hello.ModVersion;
                        _settings.Save();
                    }
                    if (BuildTag.ShortVersion(hello.ModVersion) != BuildTag.ShortVersion(_version))
                        Log("The WK Race mod in your game is version " + hello.ModVersion + " but WKSR is " + _version + ". Install the matching mod (Mod folder of the client package).");
                    UpdateGameText();
                    Log("Game connected (mod " + hello.ModVersion + ").");
                    PushStatus();
                    ToMod(new ModSettings { GhostOpacityPercent = _settings.GhostOpacity });
                    if (_match != null && _matchActive) SendMatchToMod();
                    else if (_lastResult != null) ToMod(_lastResult);
                    break;

                case ModKeys keys:
                    OnModKeys(keys.Keys);
                    break;

                case LoadedMods mods:
                    OnGameMods(mods);
                    break;

                case MatchReady ready:
                    if (_matchActive) { MatchState = Solo ? "Starting" : "Waiting for opponent"; ToHost(ready); }
                    break;

                case SplitReached split:
                    if (_matchActive) { _mySplits[split.Index] = split; SetMySplit(split); _recorder?.Add(split); ToHost(split); }
                    break;

                case PlayerReady ready:
                    if (_matchActive && _practice == null) _server?.Send(ready);
                    break;

                case ChatSend chat:
                    SendChat(chat.Text);
                    break;

                case AttemptStarted attempt:
                    if (_matchActive)
                    {
                        _attempt = attempt.Attempt;
                        ClearMyTimes();
                        _recorder?.Add(attempt);
                        ToHost(attempt);
                    }
                    break;

                case PlayerFinished finished:
                    if (_matchActive)
                    {
                        MatchState = "Finished - waiting for the result";
                        _gameMs = finished.RaceTimeMs;
                        _recorder?.Add(finished);
                        ToHost(finished);
                    }
                    break;

                case PlayerDied died:
                    if (_matchActive)
                    {
                        _myDied = true;
                        MatchState = Solo ? "You died" : "You died - waiting for the result";
                        _gameMs = died.RaceTimeMs;
                        _recorder?.Add(died);
                        ToHost(died);
                    }
                    break;

                case PracticeRestart restart:
                    OnPracticeRestart(restart);
                    break;

                case PlayerForfeit _:
                case BanReady _:
                case BanPick _:
                    if (_matchActive) ToHost(message);
                    break;
            }
        }

        // A mod that connects mid-match (game restarted) is told how far along the race is.
        private void SendMatchToMod()
        {
            var resume = new MatchFound
            {
                MatchId = _match.MatchId,
                Seed = _match.Seed,
                OpponentName = _match.OpponentName,
                OpponentRating = _match.OpponentRating,
                Competitive = _match.Competitive,
                TimeLimitSeconds = _match.TimeLimitSeconds,
                GraceSeconds = _match.GraceSeconds,
                Ranked = _match.Ranked,
                Attempt = _attempt,
                ResumeElapsedMs = _started && ServerNow() >= _goServerMs ? (int)(ServerNow() - _goServerMs) : -1,
                Rules = _match.Rules,
                Mode = _match.Mode,
                YourRacer = _match.YourRacer,
                PlayerCount = _match.PlayerCount,
                ReadySeconds = _match.ReadySeconds,
                Segment = _match.Segment,
            };
            MarkPractice();
            ToMod(resume);
            if (_banState != null) ToMod(_banState);
            if (_roster != null) ToMod(_roster);
            if (_started) SendCountdown();
            foreach (OpponentSplit split in _oppSplits.Values) ToMod(split);
            foreach (OpponentFinished finished in _oppFinished.Values) ToMod(finished);
            SendPauseState();
        }

        // Tells the game the match it is about to get is a practice run (sent just before its MatchFound).
        private void MarkPractice()
        {
            if (_practice != null) ToMod(_practice.Mark());
            else if (_rankedSolo && _match != null) ToMod(new PracticeMark { MatchId = _match.MatchId, Ranked = true });
        }

        // ---- practice -----------------------------------------------------------------

        // Any restart in the game during practice (or right after a practice run ended): a fresh run on the same seed.
        // The abandoned run is not kept; only a finish or a death keeps a run.
        private void OnPracticeRestart(PracticeRestart restart)
        {
            if (_match == null || restart.MatchId != _match.MatchId || !_lastWasPractice) return;
            // The run is over and the player has since gone on to something else in WKSR: a restart pressed in the
            // game (still standing in the old world) must not pull them out of the queue or their lobby.
            if (!_matchActive && (_phase == ClientPhase.Searching || _hasLobby))
            {
                Log("Restart ignored: you are " + (_hasLobby ? "in a lobby" : "searching for a 1v1") + ". Leave it first to start another solo run.");
                return;
            }
            if (_rankedSolo || _lastWasRankedSolo)
            {
                // The server hosts ranked solo runs: it drops this one (or follows the one that just ended, after a
                // death or a finish) and answers with a new ranked run on a new seed. A restart never turns a
                // ranked solo session into an unranked one.
                if (_rankedSolo && !_matchActive) return;
                if (_server == null || _link != LinkState.Connected)
                {
                    Log("Not connected to the match server: press START under RANKED, SOLO once it is back.");
                    return;
                }
                if (!_rankedSolo && !RankedModsAllowed("Ranked solo runs")) return;
                Log(_rankedSolo ? "Ranked solo run restarted: a new seed is on its way." : "Ranked solo: a new run on a new seed is on its way.");
                _recorder = null;
                _server.Send(restart);
                return;
            }
            // A seed the player picked is run again. Otherwise the restart is a new run on a new random seed.
            int? seed = _soloSeedTyped ? _match.Seed : (int?)null;
            List<string> bans = LastBans();
            MatchRules rules = _match.Rules;
            if (_practice != null) Log(seed != null ? "Unranked run restarted on the same seed." : "Unranked run restarted on a new seed.");
            _practice = null;
            _recorder = null;
            _matchActive = false;
            _started = false;
            _phase = _link == LinkState.Connected ? ClientPhase.Idle : ClientPhase.Offline;
            StartPractice(seed, bans, rules, false, _match.Segment);
        }

        // The artifact bans of the last match, to repeat its world exactly (null when it had none).
        private List<string> LastBans()
        {
            return _match != null && _match.ArtifactBans && _banState != null && _banState.Done ? new List<string>(_banState.Banned) : null;
        }

        // A solo run on `seed`, or on the seed box's (random when blank), with the SOLO picker's modifiers or the given
        // ones (practising a race's seed, or restarting a practice run). Leaves the queue or lobby first.
        // seed: the seed to run, or null for a random one. fromBox: the seed box decides (a typed seed, or blank for
        // random). segment: a run of that segment (Segments) instead of the whole climb.
        private void StartPractice(int? seed, List<string> presetBans = null, MatchRules? rules = null, bool fromBox = false, byte segment = Segments.None)
        {
            if (_phase == ClientPhase.InMatch || _matchActive) return;
            if (!SoloModsAllowed())
            {
                Log("Your game has mods that are not approved (" + UnapprovedNames() + "). Tick ALLOW UNAPPROVED MODS under UNRANKED, SOLO UNRANKED to run with them.");
                return;
            }
            if (fromBox) _soloAgainst = null;
            if (seed == null && fromBox)
            {
                string text = (_practiceSeed ?? "").Trim();
                int typed;
                if (text.Length > 0)
                {
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out typed))
                    {
                        Log("A seed is a whole number (or leave the box blank for a random one).");
                        return;
                    }
                    seed = typed;
                }
            }
            _soloSeedTyped = seed != null;
            int chosen = seed ?? new Random().Next(1, 100000000);
            if (_phase == ClientPhase.Searching)
            {
                _server?.Send(new QueueLeave());
                QueueText = "";
                Log("Stopped searching for a match.");
            }
            // A lobby race could start while practising, and this player would stand in it unseen: leave first.
            if (_hasLobby)
            {
                CancelLobby();
                Log("Left the lobby for a solo run.");
            }
            _pendingJoin = 0;

            // The ghost: a replay the player picked to run against (from a leaderboard or their saved ones), else their
            // own best on this seed.
            RunRecording against = _soloAgainst != null && _soloAgainst.Seed == chosen && _soloAgainst.Segment == segment ? _soloAgainst : null;
            if (against == null) _soloAgainst = null;
            string me = _welcomeName ?? _playerName ?? "";
            string whose = against != null && !string.IsNullOrEmpty(against.Player) && !string.Equals(against.Player, me, StringComparison.OrdinalIgnoreCase) ? against.Player : null;
            RunRecording best = against ?? _practiceStore.Load(chosen, segment);
            _practice = new SoloHost(chosen, rules ?? PracticeRules.Rules, me, _rating, best, ServerNow, Dispatch, presetBans, false, whose, segment);
            Raise(nameof(ForfeitText));
            _practice.Start();
        }

        private void ReportBug()
        {
            byte[] token = null;
            try { if (!string.IsNullOrEmpty(_settings.Token)) token = Convert.FromBase64String(_settings.Token); }
            catch (FormatException) { }
            if (token != null && token.Length != ProtocolConstants.TokenLength) token = null;
            OpenBugReport?.Invoke(new BugReportContext
            {
                Url = _endpoint.ReportUrl,
                PlayerId = _settings.PlayerId,
                Token = token,
                Info = string.Join("\n",
                    "WKSR " + _version,
                    "Build " + MyBuildTag(),
                    "Mod " + (_modVersion ?? "not connected"),
                    "Game " + (GameLauncher.IsRunning() ? "running" : "not running"),
                    "Windows " + Environment.OSVersion.Version),
                SessionLog = BuildExport,
            });
        }

        private void OpenPrivacy()
        {
            if (_endpoint.Custom) return;
            try
            {
                UnityEngine.Application.OpenURL("https://" + _endpoint.Host + "/privacy");
            }
            catch (Exception e)
            {
                Log("Could not open your browser for the privacy notice: " + e.Message);
            }
        }

        // ---- Steam sign-in --------------------------------------------------------

        private void StartSteamLink()
        {
            if (_server == null || _link != LinkState.Connected) return;
            _server.Send(new SteamLinkStart());
            SteamStatus = "Opening Steam...";
        }

        private void OnSteamLinkUrl(SteamLinkUrl link)
        {
            // Only Steam's own OpenID page is opened.
            if (!SteamLinks.IsSteamOpenIdPage(link.Url))
            {
                Log("Ignored a link that is not Steam's.");
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.Url) { UseShellExecute = true });
                SteamStatus = "Continue in your browser...";
                Log("Steam opened in your browser to link your account.");
            }
            catch (Exception e)
            {
                SteamStatus = "Could not open the browser";
                Log("Could not open your browser to link Steam: " + e.Message);
            }
        }

        private void OnSteamLinkResult(SteamLinkResult result)
        {
            Log(result.Message);
            if (!result.Ok)
            {
                SteamStatus = "Not linked: see the last event";
                return;
            }
            bool otherPlayer = result.PlayerId != _settings.PlayerId;
            if (otherPlayer) _settings.Backup("before-steam");
            _settings.PlayerId = result.PlayerId;
            if (result.Token.Length == ProtocolConstants.TokenLength) _settings.Token = Convert.ToBase64String(result.Token);
            if (otherPlayer)
            {
                // The restored player: the server closes this connection and the client signs in again as them.
                _settings.Name = result.Name;
                PlayerName = result.Name;
                _rating = result.Rating;
                _wins = result.Wins;
                _losses = result.Losses;
                Raise(nameof(RatingText));
                Raise(nameof(RecordText));
            }
            _settings.Save();
            SteamLinked = true;
            SteamStatus = otherPlayer ? "Player restored" : "";
            if (_steamRequired) QueueText = "";
        }

        // ---- artifact bans --------------------------------------------------------

        // The bans themselves happen in the game; the client relays them and shows where they stand.
        private void OnBanState(BanState state)
        {
            if (_match == null || state.MatchId != _match.MatchId) return;
            _banState = state;
            ToMod(state);
            if (state.Done)
            {
                BansText = "Artifact bans: " + BansSummary(state, "none");
                if (MatchState.StartsWith("Artifact bans", StringComparison.Ordinal)) MatchState = "Waiting for your game";
                Log(BansText + ".");
            }
            else if (state.Pool.Count == 0)
            {
                BansText = "Artifact bans: waiting for both games";
            }
            else
            {
                string turn = state.YourTurn ? "your turn (ban in the game)" : "waiting for " + state.TurnName;
                BansText = "Artifact bans: " + turn + (state.Banned.Count > 0 ? "  -  banned " + BansSummary(state, "") : "");
                MatchState = state.YourTurn ? "Artifact bans: your turn (in the game)" : "Artifact bans: waiting for " + state.TurnName;
            }
        }

        private static string BansSummary(BanState state, string none)
        {
            if (state == null || state.Banned.Count == 0) return none;
            var parts = new List<string>();
            for (int i = 0; i < state.Banned.Count; i++) parts.Add(RaceFormat.ItemLabel(state.Banned[i]) + " (" + state.BannedBy[i] + ")");
            return string.Join(", ", parts);
        }

        // ---- pausing ------------------------------------------------------------

        private static long SplitKey(byte racer, int index)
        {
            return ((long)racer << 32) | (uint)index;
        }

        private void DropSplitsOf(byte racer, int belowAttempt)
        {
            foreach (long key in _oppSplits.Where(p => p.Value.Racer == racer && p.Value.Attempt < belowAttempt).Select(p => p.Key).ToList()) _oppSplits.Remove(key);
        }

        private void OnOpponentSplit(OpponentSplit split)
        {
            DropSplitsOf(split.Racer, split.Attempt);
            _oppSplits[SplitKey(split.Racer, split.Index)] = split;
            if (!IsPartyMatch) SetOpponentSplit(split);
            ToMod(split);
        }

        private void OnOpponentAttempt(OpponentAttempt attempt)
        {
            DropSplitsOf(attempt.Racer, attempt.Attempt + 1);
            if (!IsPartyMatch) ClearOpponentTimes();
            ToMod(attempt);
            Log(RacerName(attempt.Racer) + " reset.");
        }

        private void OnOpponentFinished(OpponentFinished finished)
        {
            _oppFinished[finished.Racer] = finished;
            if (_practice != null) MatchState = "Your best finished in " + RaceFormat.Time(finished.RaceTimeMs);
            else if (IsPartyMatch) MatchState = RacerName(finished.Racer) + " finished in " + RaceFormat.Time(finished.RaceTimeMs);
            else MatchState = "Opponent finished in " + RaceFormat.Time(finished.RaceTimeMs);
            ToMod(finished);
        }

        private string RacerName(byte racer)
        {
            RosterEntry e = _roster != null ? _roster.Find(racer) : null;
            if (e != null) return e.Name;
            return _match != null && !_match.Party ? _match.OpponentName : "Player " + (racer + 1);
        }

        private void OnMatchPaused(MatchPaused paused)
        {
            if (_match == null || paused.MatchId != _match.MatchId) return;
            bool first = _paused == null;
            _paused = paused;
            _serverLost = false;
            MatchState = "Paused - " + PauseReason(paused);
            if (first) Log("Match paused: " + PauseReason(paused) + ".");
            SendPauseState();
        }

        private void OnMatchResumed(MatchResumed resumed)
        {
            if (_match == null || resumed.MatchId != _match.MatchId) return;
            if (_paused != null || _serverLost) Log("Match resumes.");
            _paused = null;
            _serverLost = false;
            _goServerMs = resumed.GoServerTimeMs;
            _resumeServerMs = resumed.ResumeServerTimeMs;
            _started = true;
            MatchState = "In progress";
            SendPauseState();
        }

        // Losing the server mid-race freezes the game right away; the server pauses the opponent from its side.
        private void OnServerLost()
        {
            if (_rankedSolo && _matchActive && _match != null)
            {
                // A ranked solo run is the server's to keep: without the server it cannot count, so it ends here.
                Log("The connection to the match server was lost, so this ranked solo run has ended.");
                OnMatchResult(new MatchResult
                {
                    MatchId = _match.MatchId, Outcome = MatchOutcome.Aborted, YourTimeMs = -1, OpponentTimeMs = -1,
                    OldRating = _rating, NewRating = _rating, Reason = "The connection to the match server was lost.", End = MatchEnd.Other,
                });
                return;
            }
            if (!_matchActive || !_started || _serverLost || _practice != null) return;
            _serverLost = true;
            _serverLostAt = DateTime.UtcNow;
            MatchState = "Paused - reconnecting to the server";
            SendPauseState();
        }

        private string PauseReason(MatchPaused paused)
        {
            if (string.IsNullOrEmpty(paused.AwayName)) return "a player is disconnected";
            if (paused.AwayName == _welcomeName) return "you are disconnected";
            return paused.AwayName + " disconnected";
        }

        // Tells the game whether to be frozen right now, and if not, when play (re)starts.
        private void SendPauseState()
        {
            if (_match == null || !_matchActive || !_started) return;
            if (_serverLost)
            {
                int left = _match.GraceSeconds - (int)(DateTime.UtcNow - _serverLostAt).TotalSeconds;
                ToMod(new RacePaused { MatchId = _match.MatchId, Reason = "Lost connection to the match server. Reconnecting...", SecondsLeft = Math.Max(0, left), RaceTimeMs = -1 });
            }
            else if (_paused != null)
            {
                ToMod(new RacePaused { MatchId = _match.MatchId, Reason = PauseReason(_paused), SecondsLeft = _paused.SecondsLeft, RaceTimeMs = _paused.RaceTimeMs });
            }
            else
            {
                long now = ServerNow();
                ToMod(new RaceResumed
                {
                    MatchId = _match.MatchId,
                    MsUntilGo = Clamp(_goServerMs - now),
                    MsUntilResume = Clamp(Math.Max(0, _resumeServerMs - now)),
                });
            }
        }

        private static int Clamp(long ms)
        {
            return (int)Math.Max(int.MinValue / 2, Math.Min(int.MaxValue / 2, ms));
        }

        // ---- splits -----------------------------------------------------------

        // Rows are matched by level, not by split number: racers on different routes (different numbers of levels)
        // only share rows for levels both went through.
        private SplitRow RowFor(string key, string level, int timeMs)
        {
            foreach (SplitRow existing in Splits) if (existing.Key == key) return existing;
            var row = new SplitRow { Key = key, Level = RaceFormat.LevelLabel(level) };
            int at = Splits.Count;
            while (at > 0 && Splits[at - 1].FirstMs > timeMs) at--;
            Splits.Insert(at, row);
            Renumber();
            return row;
        }

        private void Renumber()
        {
            for (int i = 0; i < Splits.Count; i++) Splits[i].Index = i + 1;
        }

        private static string KeyAt(SortedDictionary<int, string> levels, int index)
        {
            List<string> keys = SplitKeys.Build(levels.Values);
            int position = 0;
            foreach (int i in levels.Keys)
            {
                if (i == index) return keys[position];
                position++;
            }
            return null;
        }

        private void SetMySplit(SplitReached split)
        {
            _myLevels[split.Index] = split.Level ?? "";
            SplitRow row = RowFor(KeyAt(_myLevels, split.Index), split.Level, split.RaceTimeMs);
            row.YouMs = split.RaceTimeMs;
            row.Refresh();
        }

        private void SetOpponentSplit(OpponentSplit split)
        {
            _oppLevels[split.Index] = split.Level ?? "";
            SplitRow row = RowFor(KeyAt(_oppLevels, split.Index), split.Level, split.RaceTimeMs);
            row.OppMs = split.RaceTimeMs;
            row.Refresh();
        }

        private void ClearMyTimes()
        {
            _myLevels.Clear();
            for (int i = Splits.Count - 1; i >= 0; i--)
            {
                Splits[i].YouMs = -1;
                if (Splits[i].OppMs < 0) Splits.RemoveAt(i);
                else Splits[i].Refresh();
            }
            Renumber();
        }

        private void ClearOpponentTimes()
        {
            _oppLevels.Clear();
            for (int i = Splits.Count - 1; i >= 0; i--)
            {
                Splits[i].OppMs = -1;
                if (Splits[i].YouMs < 0) Splits.RemoveAt(i);
                else Splits[i].Refresh();
            }
            Renumber();
        }

        // ---- proximity voice -------------------------------------------------------------

        public ICommand VoiceCommand { get; private set; }
        // Asks the window to open the voice settings.
        public event Action OpenVoice;

        public bool VoiceEnabled
        {
            get { return _settings.VoiceEnabled; }
            set
            {
                if (_settings.VoiceEnabled == value) return;
                _settings.VoiceEnabled = value;
                _settings.Save();
                _voice.Enabled = value;
                _server?.Send(new VoiceState { Enabled = value });
                RaiseVoice();
            }
        }

        public const string DefaultMicrophone = "Default microphone";

        public List<string> VoiceDevices
        {
            get
            {
                var list = new List<string> { DefaultMicrophone };
                list.AddRange(VoiceEngine.Devices());
                if (!string.IsNullOrEmpty(_settings.VoiceDevice) && !list.Contains(_settings.VoiceDevice)) list.Add(_settings.VoiceDevice);
                return list;
            }
        }

        public string VoiceDevice
        {
            get { return string.IsNullOrEmpty(_settings.VoiceDevice) ? DefaultMicrophone : _settings.VoiceDevice; }
            set
            {
                string device = value == DefaultMicrophone ? "" : (value ?? "");
                if (_settings.VoiceDevice == device) return;
                _settings.VoiceDevice = device;
                _settings.Save();
                _voice.Device = device;
                Raise();
            }
        }

        public double VoiceMicVolume
        {
            get { return _settings.VoiceMicVolume; }
            set
            {
                int v = (int)Math.Round(Math.Max(0, Math.Min(200, value)));
                if (_settings.VoiceMicVolume == v) return;
                _settings.VoiceMicVolume = v;
                _settings.Save();
                _voice.MicVolume = v / 100f;
                Raise();
                Raise(nameof(VoiceMicVolumeText));
            }
        }

        public double VoiceOutVolume
        {
            get { return _settings.VoiceOutVolume; }
            set
            {
                int v = (int)Math.Round(Math.Max(0, Math.Min(200, value)));
                if (_settings.VoiceOutVolume == v) return;
                _settings.VoiceOutVolume = v;
                _settings.Save();
                _voice.OutputVolume = v / 100f;
                Raise();
                Raise(nameof(VoiceOutVolumeText));
            }
        }

        public string VoiceMicVolumeText { get { return _settings.VoiceMicVolume + "%"; } }
        public string VoiceOutVolumeText { get { return _settings.VoiceOutVolume + "%"; } }

        // Push to talk and open mic act as one choice.
        public bool VoicePushToTalk
        {
            get { return _settings.VoicePushToTalk; }
            set { SetPushToTalk(value || _settings.VoicePushToTalk); }
        }

        public bool VoiceOpenMic
        {
            get { return !_settings.VoicePushToTalk; }
            set { SetPushToTalk(!value && _settings.VoicePushToTalk); }
        }

        private void SetPushToTalk(bool on)
        {
            if (_settings.VoicePushToTalk != on)
            {
                _settings.VoicePushToTalk = on;
                _settings.Save();
                _voice.PushToTalk = on;
            }
            // A check box that was clicked off but stays the choice is ticked again after the binding has written.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(RaiseVoice));
        }

        public void SetVoiceKey(int vk)
        {
            if (vk <= 0) return;
            _settings.VoiceKey = vk;
            _settings.Save();
            _voice.PushToTalkKey = vk;
            RaiseVoice();
        }

        public string VoiceKeyText { get { return KeyName(_settings.VoiceKey); } }

        public static string KeyName(int vk)
        {
            switch (vk)
            {
                case 0x04: return "MIDDLE MOUSE";
                case 0x05: return "MOUSE 4";
                case 0x06: return "MOUSE 5";
            }
            return "KEY " + vk;
        }

        // "VOICE: PUSH TO TALK (V)", "VOICE: OPEN MIC", "VOICE: OFF", ...
        public string VoiceStatusText
        {
            get
            {
                if (!_settings.VoiceEnabled) return "OFF";
                if (!_voiceAvailable) return "NOT CONNECTED";
                return _settings.VoicePushToTalk ? "PUSH TO TALK (" + VoiceKeyText + ")" : "OPEN MIC";
            }
        }

        public string VoiceHelpText
        {
            get
            {
                return "Your voice goes to the players in your lobby, and in a match to the players within " + _voice.RangeMeters
                    + " m of you: they hear it from where you are. Nothing is recorded. Headphones keep others from hearing themselves.";
            }
        }

        public double VoiceMeterWidth { get { return 512 * Math.Max(0, Math.Min(1, _voice.MicLevel)); } }
        public Brush VoiceMeterBrush { get { return _voice.Transmitting ? Theme.Good : Theme.Muted; } }
        public bool VoiceTalking { get { return _voice.Transmitting; } }

        private void RaiseVoice()
        {
            Raise(nameof(VoiceEnabled));
            Raise(nameof(VoicePushToTalk));
            Raise(nameof(VoiceOpenMic));
            Raise(nameof(VoiceKeyText));
            Raise(nameof(VoiceStatusText));
            Raise(nameof(VoiceHelpText));
        }

        private bool IsVoiceMuted(string name)
        {
            return _settings.VoiceMuted.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }

        private void ToggleVoiceMute(string name)
        {
            bool muted = IsVoiceMuted(name);
            _settings.VoiceMuted.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (!muted) _settings.VoiceMuted.Add(name);
            _settings.Save();
            _voice.SetMuted(_settings.VoiceMuted);
            foreach (LobbyMemberRow row in LobbyMembers) if (string.Equals(row.PlainName, name, StringComparison.OrdinalIgnoreCase)) row.Muted = !muted;
            Log((muted ? "Unmuted " : "Muted ") + name + "'s voice.");
        }

        // Every timer tick: the meters, who is talking (lobby rows), and the speaking marks in the game.
        private void UpdateVoice(DateTime now)
        {
            Raise(nameof(VoiceMeterWidth));
            Raise(nameof(VoiceMeterBrush));
            Raise(nameof(VoiceTalking));
            List<ushort> speaking = _voice.Speaking();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var racers = new List<byte>();
            foreach (ushort id in speaking)
            {
                VoicePeer peer = _voice.Peer(id);
                if (peer == null) continue;
                names.Add(peer.Name);
                if (peer.Racer != VoicePeer.NoRacer) racers.Add(peer.Racer);
            }
            foreach (LobbyMemberRow row in LobbyMembers) row.Speaking = names.Contains(row.PlainName ?? "") || (row.PlainName == _welcomeName && _voice.Transmitting);

            racers.Sort();
            bool on = _settings.VoiceEnabled && _voice.Connected;
            string activity = (on ? "on;" : "off;") + (_voice.Transmitting ? "me;" : ";") + string.Join(",", racers);
            if (activity != _lastActivity || now >= _nextActivity)
            {
                _lastActivity = activity;
                _nextActivity = now.AddSeconds(1);
                ToMod(new VoiceActivity { On = on, Me = _voice.Transmitting, Racers = racers });
            }
        }

        // ---- the roster and the ready check ----------------------------------------

        private void OnRoster(MatchRoster roster)
        {
            if (_match == null || roster.MatchId != _match.MatchId) return;
            _roster = roster;
            if (roster.ReadySecondsLeft >= 0) _readyDeadline = DateTime.UtcNow.AddSeconds(roster.ReadySecondsLeft);
            ToMod(roster);
            UpdateReadyState();
            if (IsPartyMatch) UpdateStandings();
        }

        private void PressReady()
        {
            if (_match == null || _server == null || !ShowReady) return;
            _server.Send(new PlayerReady { MatchId = _match.MatchId });
            Log("Ready.");
        }

        // What the race view says before the countdown: load the world, then press ready.
        private void UpdateReadyState()
        {
            Raise(nameof(ShowReady));
            if (_match == null || !_matchActive || _started || _practice != null || _roster == null) return;
            if (_banState != null && !_banState.Done) return;
            RosterEntry me = _roster.Find(_roster.YourRacer);
            if (me == null) return;
            int ready = _roster.Racers.Count(r => r.Status == RacerStatus.Ready);
            int total = _roster.Racers.Count(r => r.Status != RacerStatus.Left);
            int seconds = (int)Math.Ceiling((_readyDeadline - DateTime.UtcNow).TotalSeconds);
            string clock = _roster.ReadySecondsLeft >= 0 ? "  " + Math.Max(0, seconds) + " s" : "";
            switch (me.Status)
            {
                case RacerStatus.Loading:
                    MatchState = "Start your run in the game (F9 on the menu) to load the match world";
                    break;
                case RacerStatus.Loaded:
                    MatchState = _roster.ReadySecondsLeft >= 0 ? "Ready check: press F9 in the game or READY here" + clock : "Loaded. Press F9 or READY when you are ready";
                    break;
                case RacerStatus.Ready:
                    MatchState = "Ready (" + ready + " of " + total + ")" + (_roster.ReadySecondsLeft >= 0 ? clock : ", waiting for the others to load");
                    break;
            }
        }

        // The party standings: the same order the game's HUD shows (Standings.Rank).
        private void UpdateStandings()
        {
            if (_roster == null || _match == null) return;
            int now = Math.Max(0, _gameMs);
            var all = new List<RacerProgress>();
            foreach (RosterEntry e in _roster.Racers)
            {
                var p = new RacerProgress { Racer = e.Racer, Name = e.Name };
                if (e.Racer == _roster.YourRacer)
                {
                    foreach (SplitReached s in _mySplits.Values) p.AddSplit(s.Level, s.Region, s.RaceTimeMs);
                }
                else
                {
                    foreach (OpponentSplit s in _oppSplits.Values.Where(x => x.Racer == e.Racer).OrderBy(x => x.Index)) p.AddSplit(s.Level, s.Region, s.RaceTimeMs);
                }
                p.Finished = e.Status == RacerStatus.Finished;
                p.FinishMs = e.Status == RacerStatus.Finished ? e.TimeMs : -1;
                p.Out = e.Status == RacerStatus.Out || e.Status == RacerStatus.Left;
                p.OutMs = e.TimeMs;
                all.Add(p);
            }
            List<RacerProgress> ranked = Standings.Rank(all, now);
            StandingsRows.Clear();
            for (int i = 0; i < ranked.Count; i++)
            {
                RacerProgress p = ranked[i];
                RosterEntry e = _roster.Find(p.Racer);
                bool me = p.Racer == _roster.YourRacer;
                string detail = p.Finished ? RaceFormat.Time(p.FinishMs, 1)
                    : e.Status == RacerStatus.Out ? "died  " + p.Levels + " lv"
                    : e.Status == RacerStatus.Left ? "left  " + p.Levels + " lv"
                    : !_started ? e.Status.ToString().ToLowerInvariant()
                    : p.OnShortcut ? "shortcut  " + p.Levels + " lv" : p.Levels + " lv";
                StandingsRows.Add(new StandingRow
                {
                    Place = !_started ? "" : p.OnShortcut ? "?" : Standings.Ordinal(i + 1),
                    Name = p.Name + (me ? " (you)" : ""),
                    Detail = detail,
                    Brush = me ? Theme.Good : p.Out ? Theme.Muted : Theme.Accent,
                });
            }
        }

        private void ShowFinalStandings(MatchResult result)
        {
            StandingsRows.Clear();
            foreach (Standing s in result.Standings)
            {
                bool me = _roster != null && s.Racer == _roster.YourRacer;
                string detail = s.Status == RacerStatus.Finished ? RaceFormat.Time(s.TimeMs, 1)
                    : s.Status == RacerStatus.Out ? (string.IsNullOrEmpty(s.Note) ? "died" : s.Note) + "  " + s.Levels + " lv"
                    : s.Status == RacerStatus.Left ? "left  " + s.Levels + " lv" : "dnf  " + s.Levels + " lv";
                StandingsRows.Add(new StandingRow { Place = Standings.Ordinal(s.Place), Name = s.Name + (me ? " (you)" : ""), Detail = detail, Brush = me ? Theme.Good : Theme.Accent });
            }
        }

        // ---- chat ----------------------------------------------------------------

        private void SendChatFromBox()
        {
            string text = _chatDraft;
            ChatDraft = "";
            SendChat(text);
        }

        // A line from WKSR's chat box or the game's. /mute and /unmute are handled here.
        private void SendChat(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.StartsWith("/mute ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/unmute ", StringComparison.OrdinalIgnoreCase))
            {
                bool mute = text.StartsWith("/mute ", StringComparison.OrdinalIgnoreCase);
                string name = text.Substring(mute ? 6 : 8).Trim();
                _settings.Muted.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                if (mute && name.Length > 0) _settings.Muted.Add(name);
                _settings.Save();
                LocalChat(name.Length == 0 ? "Usage: /mute name, /unmute name" : (mute ? "Muted " : "Unmuted ") + name + ".");
                return;
            }
            if (text.StartsWith("/", StringComparison.Ordinal))
            {
                LocalChat("Commands: /mute name, /unmute name");
                return;
            }
            if (_server == null || _link != LinkState.Connected)
            {
                LocalChat("Not connected to the match server.");
                return;
            }
            if (text.Length > ProtocolConstants.MaxChatLength) text = text.Substring(0, ProtocolConstants.MaxChatLength);
            // (The Match room opens with the lobby or match; make sure of it before picking the room.)
            UpdateMatchRoom();
            ChatRoomModel sayIn = Room(_selectedRoom);
            if (sayIn == null || !sayIn.Joined)
            {
                Log("No chat room is open: add one with the + next to the chat tabs.");
                return;
            }
            _server.Send(new ChatSend { Text = text, Room = _selectedRoom });
        }

        private void LocalChat(string text)
        {
            OnChat(new ChatMessage { System = true, Text = text });
        }

        private void OnChat(ChatMessage chat)
        {
            if (!chat.System && _settings.Muted.Any(n => string.Equals(n, chat.From, StringComparison.OrdinalIgnoreCase))) return;
            OnRoomChat(chat);
            ChatLines.Add(new ChatLine
            {
                Text = chat.System ? "* " + chat.Text : chat.From + ": " + chat.Text,
                Brush = chat.System ? Theme.Warn : chat.From == _welcomeName ? Theme.Good : Theme.Accent,
            });
            while (ChatLines.Count > 60) ChatLines.RemoveAt(0);
            ChatHasLines = true;
            ToMod(chat);
        }

        // ---- actions ------------------------------------------------------------

        private void ToggleSearch()
        {
            if (_server == null || _link != LinkState.Connected || _phase == ClientPhase.InMatch) return;
            if (_phase == ClientPhase.Searching)
            {
                _server.Send(new QueueLeave());
                _phase = ClientPhase.Idle;
                QueueText = "";
            }
            else
            {
                if (!RankedModsAllowed("Ranked matches")) return;
                HasResult = false;
                _searchStarted = DateTime.UtcNow;
                _phase = ClientPhase.Searching;
                if (_hasLobby)
                {
                    HasLobby = false;
                    LobbyText = "You left the lobby to search for a ranked match.";
                }
                _server.Send(new QueueJoin { GameBuild = MyBuildTag() });
            }
            UpdateFindTitle();
            PushStatus();
        }

        private void UpdateFindTitle()
        {
            FindTitle = _phase == ClientPhase.Searching ? "SEARCHING" : "1V1";
            Raise(nameof(RankedTitle));
        }

        // ---- lobbies ------------------------------------------------------------

        private void CreateLobby()
        {
            if (_server == null || _link != LinkState.Connected || _phase == ClientPhase.InMatch) return;
            if (_phase == ClientPhase.Searching)
            {
                _server.Send(new QueueLeave());
                _phase = ClientPhase.Idle;
                QueueText = "";
                UpdateFindTitle();
            }
            // The box only exists once a mod is marked on the server; before that it changes nothing.
            _server.Send(new LobbyCreate { GameBuild = MyBuildTag(), Mode = LobbyRules.Mode, Rules = LobbyRules.Rules, AllowUnapproved = _modsMarked && _settings.LobbyUnapproved });
            LobbyText = "Opening a lobby...";
        }

        private void StartLobbyRace()
        {
            if (_server == null || !_hasLobby) return;
            _server.Send(new LobbyStart());
        }

        // The lobby room: who is in it, who hosts, the mode and modifiers.
        private void OnLobbyState(LobbyState state)
        {
            bool joined = !_hasLobby || _lobbyId != state.LobbyId;
            _lobbyId = state.LobbyId;
            _lobbyState = state;
            LobbyCode = WKRace.Protocol.LobbyCode.Encode(state.LobbyId);
            if (state.ExpiresInSeconds >= 0) _lobbyExpires = DateTime.UtcNow.AddSeconds(state.ExpiresInSeconds);
            HasLobby = true;
            if (joined)
            {
                Show(ConsoleCard.Lobby);
                if (!state.YouAreHost) Log("Joined the " + (state.Mode == MatchMode.Party ? "party" : "1v1") + " lobby of " + state.Members.Where(x => x.Host).Select(x => x.Name).FirstOrDefault() + ". " + RulesText.Describe(state.Rules) + ".");
            }
            LobbyMembers.Clear();
            foreach (LobbyMember member in state.Members)
            {
                string plain = member.Name;
                LobbyMembers.Add(new LobbyMemberRow
                {
                    Name = member.Name + (member.Host ? "  (host)" : ""),
                    PlainName = plain,
                    Detail = member.Racing ? "in a match" : SteamMode ? "" : member.Rating.ToString(CultureInfo.InvariantCulture),
                    Brush = member.Name == _welcomeName ? Theme.Good : member.Host ? Theme.Warn : Theme.Accent,
                    CanMute = member.Name != _welcomeName,
                    Muted = IsVoiceMuted(plain),
                    MuteCommand = new RelayCommand(() => ToggleVoiceMute(plain)),
                });
            }
            RaiseLobby();
            UpdateLobbyText();
            PushStatus();
            OnLobbyStateEdition(state);
        }

        private void RaiseLobby()
        {
            Raise(nameof(LobbyTitle));
            Raise(nameof(LobbyRulesText));
            Raise(nameof(IsLobbyHost));
            Raise(nameof(StartText));
        }

        private void OnLobbyCreated(LobbyCreated created)
        {
            _lobbyId = created.LobbyId;
            _lobbyExpires = DateTime.UtcNow.AddSeconds(created.ExpiresInSeconds);
            LobbyCode = WKRace.Protocol.LobbyCode.Encode(created.LobbyId);
            Show(ConsoleCard.Lobby);
            if (SteamMode) return;   // the Steam lobby says it when Steam has made it (OpenSteamLobby)
            ListNewLobby();
            Log(_lobbyBrowser && _settings.LobbyPublic ? "Lobby open and listed in the lobby browser. Friends can also join with the code." : "Lobby open. Send the code to your friends.");
        }

        private void UpdateLobbyText()
        {
            if (!_hasLobby || _lobbyState == null) return;
            LobbyState s = _lobbyState;
            if (s.Racing)
            {
                LobbyText = "The lobby is in a match. Late joiners wait for the next one.";
                return;
            }
            if (s.Members.Count < 2)
            {
                // A Steam lobby does not expire.
                if (SteamMode)
                {
                    LobbyText = _settings.LobbyPublic
                        ? "Waiting for players: the lobby is in the lobby browser, and friends can also join with the code (LOBBY, then JOIN)."
                        : "Waiting for players: send the code to your friends. They press LOBBY, paste it under JOIN LOBBY and press JOIN.";
                    return;
                }
                TimeSpan left = _lobbyExpires - DateTime.UtcNow;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                LobbyText = "Send the code to your friends: they press LOBBY, then JOIN LOBBY, and paste it. Closes in "
                    + (int)left.TotalMinutes + ":" + left.Seconds.ToString("00", CultureInfo.InvariantCulture) + " if nobody joins.";
                return;
            }
            string host = s.Members.Where(x => x.Host).Select(x => x.Name).FirstOrDefault() ?? "the host";
            if (s.Mode == MatchMode.Duel) LobbyText = s.YouAreHost ? "Press REMATCH to play again." : "Waiting for " + host + " to start a rematch.";
            else LobbyText = s.YouAreHost ? "Press START MATCH when everyone is in (up to " + s.MaxPlayers + ")." : "Waiting for " + host + " to start the match.";
        }

        private void SelectJoin()
        {
            Show(ConsoleCard.Join);
            if (string.IsNullOrWhiteSpace(_joinCode))
            {
                // A code waiting on the clipboard is almost certainly the one the player wants.
                try
                {
                    string text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                    WKRace.Protocol.LobbyCode code;
                    if (text != null && text.Length < 40 && WKRace.Protocol.LobbyCode.TryDecode(text, out code)) JoinCode = text.Trim();
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                }
            }
            FocusJoinCode?.Invoke();
        }

        private void CopyCode()
        {
            if (!_hasLobby) return;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetText(_lobbyCode);
                    _copiedUntil = DateTime.UtcNow.AddSeconds(2);
                    Raise(nameof(CopyText));
                    return;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Another program has the clipboard open; try again in a moment.
                    System.Threading.Thread.Sleep(30);
                }
            }
            Log("Could not copy to the clipboard. Select the code and copy it by hand.");
        }

        private void CancelLobby()
        {
            if (!_hasLobby) return;
            // A Steam lobby's host who leaves closes it (OnLobbyLeftEdition): the core runs in their game and goes with it.
            if (!(SteamMode && _host != null)) _server?.Send(new LobbyCancel());
            HasLobby = false;
            LobbyText = "";
            OnLobbyLeftEdition();
        }

        public void JoinFromCommandLine(string code)
        {
            JoinCode = code;
            JoinLobby();
        }

        private void JoinLobby()
        {
            WKRace.Protocol.LobbyCode code;
            if (!WKRace.Protocol.LobbyCode.TryDecode(_joinCode, out code))
            {
                LobbyText = "That lobby code is not valid. Codes look like WKR-ABCD-1234; check it was copied completely.";
                return;
            }
            if (_phase == ClientPhase.InMatch) return;
            if (code.LobbyId == _lobbyId && _hasLobby)
            {
                LobbyText = "That is your own lobby. Send the code to a friend instead.";
                return;
            }
            if (_server == null || _link != LinkState.Connected)
            {
                _pendingJoin = code.LobbyId;
                LobbyText = "Waiting for the server connection to join the lobby...";
                if (_server == null) ConnectToServer();
                return;
            }
            SendLobbyJoin(code.LobbyId);
        }

        private void SendLobbyJoin(uint lobbyId)
        {
            if (_phase == ClientPhase.Searching)
            {
                _server.Send(new QueueLeave());
                _phase = ClientPhase.Idle;
                QueueText = "";
                UpdateFindTitle();
            }
            if (_hasLobby) CancelLobby();
            _server.Send(new LobbyJoin { LobbyId = lobbyId, GameBuild = MyBuildTag() });
            LobbyText = "Joining the lobby...";
        }

        // Game build and mod version, so the server only pairs players whose worlds will match.
        private string MyBuildTag()
        {
            if (SteamMode) return SteamBuildTag();
            string mod = _modVersion ?? _settings.LastModVersion;
            return BuildTag.Compose(GameLauncher.FindGameBuild(_settings.GamePath), mod);
        }

        private void LaunchGame()
        {
            if (GameLauncher.IsRunning())
            {
                Log("White Knuckle is already running.");
                return;
            }
            string note;
            string error = GameLauncher.Launch(_settings.GamePath, _settings.BepInExRoot, out note);
            if (note != null) Log(note);
            Log(error ?? "Launching White Knuckle through Steam...");
            if (error == null) WatchLaunch();
        }

        private void PushStatus()
        {
            string info = MismatchText() ?? (_phase == ClientPhase.Searching ? "Searching for a match"
                : _hasLobby && _lobbyState != null && !_matchActive ? (_lobbyState.Mode == MatchMode.Party ? "In a party lobby (" : "In a 1v1 lobby (") + _lobbyState.Members.Count + "/" + _lobbyState.MaxPlayers + ")"
                : "");
            if (SteamMode && info.Length == 0 && !_hasLobby && !_matchActive) info = "Steam lobbies";
            ToMod(new ClientStatus { Phase = _phase, PlayerName = _playerName ?? "", Rating = _rating, Info = info });
        }

        // For the game's HUD when the mod and WKSR are different versions, else null.
        private string MismatchText()
        {
            if (_modVersion == null || BuildTag.ShortVersion(_modVersion) == BuildTag.ShortVersion(_version)) return null;
            return ClientStatus.MismatchPrefix + ": WK Race mod " + BuildTag.ShortVersion(_modVersion) + ", WKSR " + _version
                + (_modIncompatible ? ". Matches cannot start until both are the same version." : ". You are only matched with players on mod " + BuildTag.ShortVersion(_modVersion) + ".");
        }

        // Everything for the game goes through here. A mod on another protocol could not read race messages (older
        // mods even dropped the link on them), so it only gets the status, which tells its HUD what is wrong.
        private void ToMod(Message message)
        {
            if (_modIncompatible && !(message is ClientStatus)) return;
            _mod.Send(message);
        }

        private void OnTimer()
        {
            DateTime now = DateTime.UtcNow;
            _practice?.Tick();
            TickEdition();
            ConnectWhenSteamIsUp();
            UpdateMatchRoom();
            SendSteamIdentityIfDue();
            CheckForUpdateWhileOffline();
            CheckLaunch();
            CheckGameGone();
            CheckReplayDownload();
            if (_phase == ClientPhase.Searching)
            {
                QueueText = "Searching for an opponent...  " + RaceFormat.Time((long)(now - _searchStarted).TotalMilliseconds, 0);
            }
            if (_matchActive && _started && (_paused != null || _serverLost))
            {
                TimerText = "Paused";
            }
            else if (_matchActive && _started && ServerNow() < _resumeServerMs)
            {
                TimerText = "Resumes in " + ((_resumeServerMs - ServerNow()) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
            }
            else if (_matchActive && _started)
            {
                long ms = ServerNow() - _goServerMs;
                if (ms < 0)
                {
                    TimerText = "Starts in " + (-ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
                }
                else
                {
                    TimerText = RaceFormat.Time(Math.Max(0, _gameMs));
                    if (MatchState == "Countdown") MatchState = "In progress";
                }
            }
            else if (!_matchActive && !HasMatch)
            {
                TimerText = "";
            }
            if (_hasLobby) UpdateLobbyText();
            UpdateReadyState();
            UpdateVoice(now);
            if (IsPartyMatch && _matchActive && now >= _nextStandings)
            {
                _nextStandings = now.AddMilliseconds(500);
                UpdateStandings();
            }
            CanRename = _phase != ClientPhase.InMatch && _phase != ClientPhase.Searching && !_hasLobby && _pendingJoin == 0;
            Raise(nameof(CopyText));
            if (now >= _nextGameCheck)
            {
                _nextGameCheck = now.AddSeconds(2);
                UpdateGameText();
            }
            CommandManager.InvalidateRequerySuggested();
        }

        private void UpdateGameText()
        {
            if (_mod.Connected && _modVersion != null)
            {
                GameText = BuildTag.ShortVersion(_modVersion) != BuildTag.ShortVersion(_version)
                    ? "Connected, but its WK Race mod is " + _modVersion + " and WKSR is " + _version + ". Update the mod so you can be matched."
                    : "Connected. Mod " + _modVersion + (string.IsNullOrEmpty(_gameVersion) ? "" : ", game " + _gameVersion) + ".";
            }
            else if (GameLauncher.IsRunning())
            {
                GameText = "Running, but the WK Race mod has not connected. Start it with LAUNCH GAME.";
            }
            else
            {
                GameText = "Not running.";
            }
        }

        // Writes this session's runs (with splits) and the event log to a text file the player picks.
        private void ExportMatchData()
        {
            // Inside the game there is no file dialog: the file goes to the Documents folder under its usual name.
            string exportFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WKSR-matches-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".txt");
            try
            {
                File.WriteAllText(exportFile, BuildExport());
                Log("Match data saved to your Documents folder as " + Path.GetFileName(exportFile) + ".");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log("Could not save the match data: " + e.Message);
            }
        }

        private string BuildExport()
        {
            var b = new StringBuilder();
            b.AppendLine("WKSR match data, exported " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            b.AppendLine("Player: " + (_welcomeName ?? _playerName) + (_rating == 0 ? "" : "   rating " + _rating + "   " + _wins + "W " + _losses + "L"));
            b.AppendLine("WKSR " + _version + "   build " + MyBuildTag());
            b.AppendLine();

            if (_records.Count == 0) b.AppendLine("No runs finished this session.");
            for (int i = 0; i < _records.Count; i++)
            {
                RaceRecord r = _records[i];
                b.AppendLine("=== Run " + (i + 1) + "  " + r.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " ===");
                if (r.RankedSolo) b.AppendLine("Ranked solo run");
                else if (r.Practice) b.AppendLine("Unranked run" + (r.OpponentTimeMs >= 0 ? " against your best of " + RaceFormat.Time(r.OpponentTimeMs) : "") + (r.NewBest ? "   new best" : ""));
                else if (r.Place == 0) b.AppendLine("Opponent: " + r.Opponent + " (" + r.OpponentRating + ")");
                if (r.Place > 0) b.AppendLine("Party match: " + Standings.Ordinal(r.Place) + " of " + r.Racers);
                b.AppendLine("Seed: " + r.Seed + (r.Competitive ? "   competitive rules" : "") + (r.RankedSolo ? "   ranked solo" : r.Practice ? "   unranked solo" : r.Ranked ? "   ranked" : "   friendly (unranked)"));
                if (r.Rules != null) b.AppendLine("Modifiers: " + r.Rules);
                if (r.Bans != null) b.AppendLine("Artifact bans: " + r.Bans);
                b.AppendLine("Result: " + r.Title + " - " + r.Reason);
                b.AppendLine("Your time: " + (r.YourTimeMs >= 0 ? RaceFormat.Time(r.YourTimeMs) : "-") + (r.Practice ? "   best: " : "   opponent: ") + (r.OpponentTimeMs >= 0 ? RaceFormat.Time(r.OpponentTimeMs) : "-"));
                if (r.Ranked && r.Outcome != MatchOutcome.Aborted)
                    b.AppendLine("Rating: " + r.OldRating + " > " + r.NewRating);
                if (r.Splits.Count > 0)
                {
                    b.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,-3} {1,-34} {2,11} {3,11} {4,10}", "#", "Split", "You", r.Practice ? "Best" : "Opponent", "Diff"));
                    foreach (SplitRow split in r.Splits)
                        b.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,-3} {1,-34} {2,11} {3,11} {4,10}", split.Index, split.Level, split.You, split.Opp, split.Delta));
                }
                b.AppendLine();
            }

            b.AppendLine("=== Event log ===");
            foreach (string line in LogLines) b.AppendLine(line);
            return b.ToString();
        }

        public void Log(string line)
        {
            if (!_ui.CheckAccess())
            {
                _ui.BeginInvoke(new Action(() => Log(line)));
                return;
            }
            LogLines.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
            while (LogLines.Count > 1000) LogLines.RemoveAt(0);
            LastEvent = LogLines[LogLines.Count - 1];
            string logFile = Environment.GetEnvironmentVariable("WKRACE_CLIENT_LOG");
            if (!string.IsNullOrEmpty(logFile))
            {
                try { System.IO.File.AppendAllText(logFile, LogLines[LogLines.Count - 1] + Environment.NewLine); }
                catch (System.IO.IOException) { }
            }
        }
    }
}
