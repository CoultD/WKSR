using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using WKRace.Protocol;

namespace WKRace.Client
{
    // One public lobby in the lobby browser.
    internal sealed class BrowseRowModel
    {
        public string Host { get; set; }
        public string Mode { get; set; }
        public string Rules { get; set; }
        public string Players { get; set; }
        public string State { get; set; }
        public Brush Brush { get; set; }
        public ICommand JoinCommand { get; set; }
    }

    // The lobby browser: the public lobbies anyone can join. On the official server it is the server's list (when the
    // server says it has one: ServerFeatures); in STEAM LOBBIES it is Steam's lobby search (MainViewModel.Steam.cs).
    internal sealed partial class MainViewModel
    {
        private string _browseStatus = "";
        private bool _browseBusy;
        private ICommand _refreshBrowseCommand;
        // The match server lists lobbies (it said so after the welcome).
        private bool _lobbyBrowser;
        private DateTime _browseAskedAt;

        public ObservableCollection<BrowseRowModel> BrowseRows { get; } = new ObservableCollection<BrowseRowModel>();
        public string BrowseStatus { get { return _browseStatus; } private set { Set(ref _browseStatus, value); } }

        public ICommand RefreshBrowseCommand
        {
            get { return _refreshBrowseCommand ?? (_refreshBrowseCommand = new RelayCommand(() => RequestLobbyList(), () => !_browseBusy && _phase != ClientPhase.InMatch)); }
        }

        // Whether CREATE LOBBY lists the new lobby in the browser (else it is joined with its code only).
        public bool LobbyPublic
        {
            get { return _settings.LobbyPublic; }
            set
            {
                if (_settings.LobbyPublic == value) return;
                _settings.LobbyPublic = value;
                _settings.Save();
                Raise();
            }
        }

        // Whether the lobby page offers the browser (and the PUBLIC box).
        public bool HasLobbyBrowser
        {
            get { return SteamMode || _lobbyBrowser && _link == LinkState.Connected; }
        }

        // The public lobbies: Steam's lobby search in STEAM LOBBIES, else the match server's list.
        private void RequestLobbyList()
        {
            if (SteamMode) RequestSteamLobbyList();
            else RequestServerLobbyList();
        }

        private void OnServerFeatures(ServerFeatures features)
        {
            _lobbyBrowser = features.LobbyBrowser;
            Raise(nameof(HasLobbyBrowser));
        }

        private void RequestServerLobbyList()
        {
            // A request that was never answered does not hold the button forever.
            if (_browseBusy && DateTime.UtcNow - _browseAskedAt < TimeSpan.FromSeconds(4)) return;
            if (_server == null || _link != LinkState.Connected)
            {
                BrowseStatus = "Not connected to the match server.";
                return;
            }
            if (!_lobbyBrowser)
            {
                BrowseStatus = "The match server does not list lobbies yet. Join with a code instead.";
                return;
            }
            _browseBusy = true;
            _browseAskedAt = DateTime.UtcNow;
            BrowseStatus = "Looking for lobbies...";
            _server.Send(new LobbyListRequest());
        }

        private void OnLobbyList(LobbyList list)
        {
            _browseBusy = false;
            BrowseRows.Clear();
            foreach (LobbyListEntry entry in list.Entries)
            {
                uint id = entry.LobbyId;
                bool open = !entry.Racing && entry.Members < entry.MaxPlayers;
                BrowseRows.Add(new BrowseRowModel
                {
                    Host = entry.Host,
                    Mode = entry.Mode == MatchMode.Party ? "PARTY" : "1V1",
                    Rules = RulesText.Describe(entry.Rules),
                    Players = entry.Members + "/" + entry.MaxPlayers,
                    State = entry.Racing ? "IN A MATCH" : open ? "OPEN" : "FULL",
                    Brush = open ? Theme.Good : Theme.Muted,
                    JoinCommand = new RelayCommand(() => JoinListedLobby(id), () => open && _phase != ClientPhase.InMatch && !(_hasLobby && _lobbyId == id)),
                });
            }
            BrowseStatus = BrowseRows.Count == 0 ? "No public lobbies right now. Open one with CREATE LOBBY."
                : BrowseRows.Count + (BrowseRows.Count == 1 ? " lobby" : " lobbies") + (list.Cut ? " (the first " + BrowseRows.Count + ")." : ".");
        }

        private void JoinListedLobby(uint id)
        {
            JoinCode = WKRace.Protocol.LobbyCode.Encode(id);
            JoinLobby();
        }

        // A new lobby goes into the browser when the host ticked PUBLIC.
        private void ListNewLobby()
        {
            if (_lobbyBrowser && _settings.LobbyPublic) _server?.Send(new LobbyVisibility { Public = true });
        }
    }
}
