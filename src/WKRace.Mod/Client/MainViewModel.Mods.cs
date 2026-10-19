using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using WKRace.Protocol;

namespace WKRace.Client
{
    using Message = WKRace.Protocol.Message;

    // One row of the mods list.
    internal sealed class ModRowModel
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string Version { get; set; }
        public string Status { get; set; }
        public Brush Brush { get; set; }
        // "IN YOUR GAME", and for the list's manager how many connected players have it.
        public string Where { get; set; }
        public bool CanMark { get; set; }
        public string MarkText { get; set; }
        public ICommand MarkCommand { get; set; }
    }

    // The mods in the player's game and what the match server makes of them. The game reports its plugins, WKSR
    // passes them on, and the server answers which are not approved. With one of those loaded there is no ranked
    // play, and lobbies and unranked solo runs need their "allow unapproved mods" box ticked.
    internal sealed partial class MainViewModel
    {
        private const int ModPageRows = ModsRequest.MaxRows;

        // The game's list (null while no game is connected), kept to send again when the server comes back.
        private List<ModEntry> _gameMods;
        private List<ModEntry> _pendingMods;
        private int _pendingModParts;
        private int _pendingModNext;

        // The server's verdict: something is marked "not approved" on it, this player manages the list, and which of
        // the player's mods are not approved.
        private bool _modsMarked;
        private bool _modsManager;
        private int _unapprovedCount;
        private List<string> _unapproved = new List<string>();
        // The next verdict is the one sent with the sign-in, while this game's list is still on its way.
        private bool _verdictBeforeList;

        private int _modsOffset;
        private int _modsTotal;
        private string _modsStatus = "";

        public ObservableCollection<ModRowModel> ModRows { get; } = new ObservableCollection<ModRowModel>();
        public ICommand ModsUpCommand { get; private set; }
        public ICommand ModsDownCommand { get; private set; }

        public event Action OpenMods;

        private void InitMods()
        {
            Func<bool> online = () => _link == LinkState.Connected;
            ModsCommand = new RelayCommand(() => { OpenMods?.Invoke(); RequestMods(0); });
            ModsUpCommand = new RelayCommand(() => RequestMods(Math.Max(0, _modsOffset - ModPageRows)), () => online() && _modsOffset > 0);
            ModsDownCommand = new RelayCommand(() => RequestMods(_modsOffset + ModPageRows), () => online() && _modsOffset + ModPageRows < _modsTotal);
        }

        // ---- the game's list, on its way to the server ---------------------------------------------------------

        // A part of the list from the game. The parts come in order; the last one completes it.
        private void OnGameMods(LoadedMods part)
        {
            if (part.Parts == 0) return;
            if (part.Part == 0)
            {
                _pendingMods = new List<ModEntry>();
                _pendingModParts = part.Parts;
                _pendingModNext = 0;
            }
            if (_pendingMods == null || part.Parts != _pendingModParts || part.Part != _pendingModNext)
            {
                _pendingMods = null;
                return;
            }
            _pendingMods.AddRange(part.Mods);
            _pendingModNext++;
            if (_pendingModNext < part.Parts) return;

            _gameMods = _pendingMods;
            _pendingMods = null;
            SendModsToServer();
        }

        // The game is gone, and its mods with it.
        private void OnGameModsGone()
        {
            _pendingMods = null;
            if (_gameMods == null) return;
            _gameMods = null;
            SendModsToServer();
        }

        private void SendModsToServer()
        {
            IServerLink server = _server;
            if (server == null || _link != LinkState.Connected) return;
            if (_gameMods == null)
            {
                server.Send(new LoadedMods());   // no parts: no game
                return;
            }
            foreach (LoadedMods part in LoadedMods.Split(_gameMods)) server.Send(part);
        }

        // ---- the server's verdict --------------------------------------------------------------------------------

        private void OnModVerdict(ModVerdict verdict)
        {
            bool hadUnapproved = _unapprovedCount > 0;
            string before = string.Join(", ", _unapproved);
            _modsMarked = verdict.AnyMarked;
            _modsManager = verdict.Manager;
            // The verdict that comes with the sign-in was made before the server had this game's list (which went
            // out when the sign-in was answered): what it says about the player's own mods is not news.
            bool beforeTheList = _verdictBeforeList;
            _verdictBeforeList = false;
            if (!beforeTheList)
            {
                _unapprovedCount = verdict.Unapproved;
                _unapproved = new List<string>(verdict.Names);
            }
            RaiseMods();

            if (_unapprovedCount > 0 && (!hadUnapproved || before != string.Join(", ", _unapproved)))
                Log("Not approved on this server: " + UnapprovedNames() + ". With " + (_unapprovedCount == 1 ? "it" : "them") + " in your game there is no ranked play, and lobbies and unranked solo runs need ALLOW UNAPPROVED MODS ticked.");
            else if (_unapprovedCount == 0 && hadUnapproved)
            {
                if (_phase != ClientPhase.Searching) QueueText = "";
                if (_gameMods != null) Log("Every mod in your game is approved.");
            }

            // An unranked solo run that started before the game (and its mods) showed up.
            if (_practice != null && _matchActive && !SoloModsAllowed())
            {
                Log("The run ended: your game has mods that are not approved, and ALLOW UNAPPROVED MODS is not ticked.");
                _practice.OnMessage(new PlayerForfeit());
            }
        }

        private void RaiseMods()
        {
            Raise(nameof(ShowModsButton));
            Raise(nameof(ShowUnapprovedBox));
            Raise(nameof(HasUnapprovedMods));
            Raise(nameof(ModsWarningText));
            Raise(nameof(ModsIntroText));
            Raise(nameof(IsModsManager));
            CommandManager.InvalidateRequerySuggested();
        }

        private string UnapprovedNames()
        {
            const int shown = 4;
            int more = _unapprovedCount - Math.Min(shown, _unapproved.Count);
            return string.Join(", ", _unapproved.Take(shown)) + (more > 0 ? " and " + more + " more" : "");
        }

        // Hidden until the first mod is marked as not approved: until then every mod is allowed and neither would do
        // anything. The list's manager always has the button, to mark that first mod.
        public bool ShowModsButton { get { return _modsMarked || _modsManager; } }
        public bool ShowUnapprovedBox { get { return _modsMarked; } }
        public bool IsModsManager { get { return _modsManager; } }
        public bool HasUnapprovedMods { get { return _unapprovedCount > 0; } }
        public string ModsWarningText { get { return _unapprovedCount == 0 ? "" : "NOT APPROVED IN YOUR GAME: " + UnapprovedNames().ToUpperInvariant() + ". NO RANKED PLAY WITH " + (_unapprovedCount == 1 ? "IT." : "THEM."); } }

        public bool LobbyAllowUnapproved
        {
            get { return _settings.LobbyUnapproved; }
            set
            {
                if (_settings.LobbyUnapproved == value) return;
                _settings.LobbyUnapproved = value;
                _settings.Save();
                Raise();
            }
        }

        public bool SoloAllowUnapproved
        {
            get { return _settings.SoloUnapproved; }
            set
            {
                if (_settings.SoloUnapproved == value) return;
                _settings.SoloUnapproved = value;
                _settings.Save();
                Raise();
            }
        }

        // Ranked takes approved mods only. The server checks it too; this says so before asking.
        private bool RankedModsAllowed(string what)
        {
            if (_unapprovedCount == 0) return true;
            string text = what + " only take approved mods. Not approved in your game: " + UnapprovedNames() + ". Remove them and start the game again.";
            Log(text);
            QueueText = text;
            return false;
        }

        private bool SoloModsAllowed()
        {
            return _unapprovedCount == 0 || !_modsMarked || _settings.SoloUnapproved;
        }

        // ---- the list ---------------------------------------------------------------------------------------------

        public string ModsStatus { get { return _modsStatus; } private set { Set(ref _modsStatus, value); } }

        public string ModsIntroText
        {
            get
            {
                string rule = "Approved mods work everywhere. With a mod that is not approved in your game there is no ranked 1v1 and no ranked solo; lobbies and unranked solo runs need ALLOW UNAPPROVED MODS ticked, and stay unranked.";
                return _modsManager
                    ? "Every mod seen in a player's game. A mark covers every version of the mod, and applies at once to everyone connected. " + rule
                    : "The mods that are not approved, and the mods in your game. " + rule;
            }
        }

        public string ModsRangeText
        {
            get
            {
                if (_modsTotal == 0) return "NO MODS LISTED";
                return (_modsOffset + 1) + "-" + Math.Min(_modsTotal, _modsOffset + ModPageRows) + " OF " + _modsTotal;
            }
        }

        private void RequestMods(int offset)
        {
            if (_server == null || _link != LinkState.Connected)
            {
                ModRows.Clear();
                _modsTotal = 0;
                ModsStatus = "Not connected to the match server.";
                Raise(nameof(ModsRangeText));
                return;
            }
            ModsStatus = "";
            _server.Send(new ModsRequest { Offset = Math.Max(0, offset), Count = ModPageRows });
        }

        private void OnModsPage(ModsPage page)
        {
            _modsOffset = page.Offset;
            _modsTotal = page.Total;
            ModRows.Clear();
            foreach (ModRow row in page.Rows)
            {
                ModRow captured = row;
                string where = row.Yours ? "IN YOUR GAME" : "";
                if (_modsManager && row.Players > 0) where += (where.Length > 0 ? "  -  " : "") + row.Players + " ONLINE";
                ModRows.Add(new ModRowModel
                {
                    Name = row.Mod.Name,
                    Id = row.Mod.Id,
                    Version = row.Mod.Version,
                    Status = row.Approved ? "APPROVED" : "NOT APPROVED",
                    Brush = row.Approved ? Theme.Good : Theme.Bad,
                    Where = where,
                    CanMark = _modsManager,
                    MarkText = row.Approved ? "UNAPPROVE" : "APPROVE",
                    MarkCommand = new RelayCommand(() => MarkMod(captured), () => _modsManager && _link == LinkState.Connected),
                });
            }
            ModsStatus = page.Total == 0 ? (_modsManager ? "No mod has been seen in a player's game yet." : "Nothing to list: every mod is approved, and your game has no other mods.") : "";
            Raise(nameof(ModsRangeText));
            CommandManager.InvalidateRequerySuggested();
        }

        // The manager marks a mod; the page comes back with the new state.
        private void MarkMod(ModRow row)
        {
            if (_server == null || !_modsManager) return;
            _server.Send(new ModMark { Id = row.Mod.Id, Approved = !row.Approved });
            Log((row.Approved ? "Marked as not approved: " : "Marked as approved: ") + row.Mod.Name + " (every version).");
            _server.Send(new ModsRequest { Offset = _modsOffset, Count = ModPageRows });
        }
    }
}
