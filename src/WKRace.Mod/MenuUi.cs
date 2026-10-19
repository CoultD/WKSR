using System;
using System.Windows.Input;
using Imui.Controls;
using Imui.Core;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using WKLib.API;
using WKLib.API.UI;
using WKRace.Client;

namespace WKRace.Mod
{
    // WKSR inside the game: a button on the side of the main menu opens a window. Until the player presses CONNECT
    // there is only UNRANKED (solo runs need no server); connected, there are the three modes (RANKED, LOBBY,
    // UNRANKED), each leading to its own page, LEADERBOARDS and OPTIONS, DISCONNECT, and BACK on every page. The pause menu of a
    // run has the same button (the match, its splits and FORFEIT are there).
    //
    // The window is drawn with Imui through WKLib (both are separate mods this one depends on): WKLib owns the canvas
    // and calls the window every frame. The button on the main menu is a copy of one of the game's own side buttons,
    // so it looks and behaves like its neighbours. What the window shows and does is WKSR's logic (EmbeddedClient.Vm).
    internal static class MenuUi
    {
        // The main menu's column of side buttons, and the one that is copied.
        private const string SideButtonPath = "Canvas - Main Menu/Main Menu/Support Menu/Update Info";
        private const string MainMenuScene = "Main-Menu";
        // The pause menu's buttons in a run, the one that is copied and the gap between two of them.
        // (Looked up from the root: the pause menu is not active while the run goes on, and only active objects are
        // found by their whole path.)
        private const string PauseRoot = "GameManager";
        private const string PauseButtonsPath = "Canvas/Pause/Pause Menu/Pause Buttons/Pause Layout";
        private const string PauseButton = "Settings";
        private const string PauseGap = "Gap.01";

        private static WKLibAPI _api;
        private static WksrWindow _window;

        internal static WksrWindow Window { get { return _window; } }
        public static bool IsOpen { get { return _window != null && _window.isOpen; } }
        // A text box of the window has the keyboard (or a slider is held): the mod's hotkeys keep out of it.
        public static bool Typing { get; internal set; }

        public static void Init()
        {
            try
            {
                _api = WKLibAPI.Create("WKSR", PluginInfo.Guid);
                _window = new WksrWindow();
                _api.AddWindow(_window);
                SceneManager.sceneLoaded += OnSceneLoaded;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("The WKSR menu could not be set up (are WKLib and ImuiBepInEx installed?): " + e.Message);
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_window == null) return;
            if (scene.name != MainMenuScene)
            {
                _window.isOpen = false;
                try
                {
                    AddPauseButton();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("The WKSR button could not be added to the pause menu: " + e.Message);
                }
                return;
            }
            try
            {
                AddMainMenuButton();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("The WKSR button could not be added to the main menu: " + e.Message);
            }
            // Back from a match with something to look at (the result, or the lobby and its chat): the window is
            // there without pressing anything.
            MainViewModel vm = EmbeddedClient.Vm;
            if (vm != null && (vm.ShowLobbyRoom || vm.HasMatch)) Open();
            if (RaceConfig.DebugOpenMenu.Value) MenuWalk.Start();
        }

        // Called every frame by the controller.
        public static void Update()
        {
            MenuWalk.Update();
            // In a run the window belongs to the pause menu: resuming (Esc or RESUME) closes it too.
            if (IsOpen && SceneManager.GetActiveScene().name != MainMenuScene)
            {
                CL_GameManager game = CL_GameManager.gMan;
                if (game != null && !game.isPaused) _window.isOpen = false;
            }
        }

        public static void Close()
        {
            if (_window != null) _window.isOpen = false;
        }

        public static void Open()
        {
            if (_window == null) return;
            _window.Show(WksrWindow.Page.Main);
            _window.isOpen = true;
        }

        // The same button in the pause menu of a run, under the game's own.
        private static void AddPauseButton()
        {
            Transform layout = PauseLayout();
            if (layout == null) return;
            Transform gap = layout.transform.Find(PauseGap);
            Transform original = layout.transform.Find(PauseButton);
            if (original == null || layout.transform.Find("WKSR") != null) return;

            if (gap != null)
            {
                GameObject gapCopy = UnityEngine.Object.Instantiate(gap.gameObject, layout.transform);
                gapCopy.name = "Gap.WKSR";
                gapCopy.transform.SetSiblingIndex(layout.transform.childCount - 1);
            }
            GameObject copy = UnityEngine.Object.Instantiate(original.gameObject, layout.transform);
            copy.name = "WKSR";
            copy.transform.SetSiblingIndex(layout.transform.childCount - 1);
            UI_MenuButton menuButton = copy.GetComponent<UI_MenuButton>();
            if (menuButton != null) UnityEngine.Object.DestroyImmediate(menuButton);
            TextMeshProUGUI label = copy.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = "WKSR";
            Button button = copy.GetComponent<Button>();
            if (button == null) return;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() =>
            {
                if (IsOpen) _window.isOpen = false;
                else Open();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            });
            Plugin.Log.LogInfo("WKSR button added to the pause menu.");
        }

        private static Transform PauseLayout()
        {
            GameObject root = GameObject.Find(PauseRoot);
            return root == null ? null : root.transform.Find(PauseButtonsPath);
        }

        // Testing: the pause menu's WKSR button, pressed as a click would.
        internal static bool PressPauseButton()
        {
            Transform layout = PauseLayout();
            Transform ours = layout == null ? null : layout.Find("WKSR");
            Button button = ours == null ? null : ours.GetComponent<Button>();
            if (button == null) return false;
            button.onClick.Invoke();
            return true;
        }

        private static void AddMainMenuButton()
        {
            GameObject original = GameObject.Find(SideButtonPath);
            if (original == null)
            {
                Plugin.Log.LogWarning("The main menu's side buttons were not found (" + SideButtonPath + "): no WKSR button.");
                return;
            }
            if (original.transform.parent.Find("WKSR") != null) return;

            GameObject copy = UnityEngine.Object.Instantiate(original, original.transform.parent);
            copy.name = "WKSR";
            copy.transform.SetSiblingIndex(0);
            // The copy must not open the screen of the button it was copied from.
            UI_MenuButton menuButton = copy.GetComponent<UI_MenuButton>();
            if (menuButton != null) UnityEngine.Object.DestroyImmediate(menuButton);
            TextMeshProUGUI label = copy.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = "WKSR";
            Button button = copy.GetComponent<Button>();
            if (button == null) return;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() =>
            {
                if (IsOpen) _window.isOpen = false;
                else Open();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            });
            Plugin.Log.LogInfo("WKSR button added to the main menu.");
        }
    }

    internal sealed partial class WksrWindow : WKLibWindow
    {
        internal enum Page { Main, Ranked, Lobby, Unranked, Boards, Options, Replays, Segments, Browse }

        private const float Width = 760f;
        private const float Height = 700f;
        private const float Gap = 12f;
        private static readonly Color32 Gold = new Color32(0xEA, 0xA1, 0x42, 255);
        private static readonly Color32 Dim = new Color32(0xA0, 0xA0, 0xA0, 255);
        private static readonly Color32 Red = new Color32(0xD9, 0x57, 0x3B, 255);

        private Page _page = Page.Main;

        public void Show(Page page)
        {
            _page = page;
            // The leaderboards are asked for when their page opens.
            MainViewModel vm = EmbeddedClient.Vm;
            if (page == Page.Boards && vm != null) vm.LeaderboardsCommand.Execute(null);
            // The saved replays are read from their folder when their page opens.
            if (page == Page.Replays && vm != null && vm.ReplaysCardCommand.CanExecute(null)) vm.ReplaysCardCommand.Execute(null);
            // The lobby browser looks for lobbies when it opens.
            if (page == Page.Browse && vm != null && vm.RefreshBrowseCommand.CanExecute(null)) vm.RefreshBrowseCommand.Execute(null);
            _keyProblem = null;
        }

        public override void HandleInput(ImGui gui)
        {
        }

        private bool _chatOpen;

        // A text box. While the chat is open it cannot be typed in: the keyboard is the chat's.
        private static bool TextBox(ImGui gui, ref string text, ImSize size, int limit)
        {
            gui.BeginReadOnly(ChatBox.AnyOpen);
            bool changed = gui.TextEdit(ref text, size, false, limit);
            gui.EndReadOnly();
            return changed;
        }

        public override void Draw(ImGui gui, bool isRootPanelOpen)
        {
            MenuUi.Typing = false;
            if (!isOpen) return;
            // The chat has the keyboard while its panel is open: a text box of this window lets go of it.
            if (ChatBox.AnyOpen)
            {
                if (!_chatOpen && gui.GetActiveControl() != 0) gui.ResetActiveControl();
            }
            else MenuUi.Typing = gui.GetActiveControl() != 0;
            _chatOpen = ChatBox.AnyOpen;
            if (!gui.BeginWindow("WKSR", ref isOpen, new ImSize(Width, Height), ImWindowFlag.NoResizing)) return;
            try
            {
                MainViewModel vm = EmbeddedClient.Vm;
                if (vm == null) gui.Text(EmbeddedClient.StartProblem ?? "WKSR is starting...", true);
                else DrawPages(gui, vm);
            }
            catch (Exception e)
            {
                // A drawing mistake must not take the whole overlay down with it, frame after frame.
                Plugin.Log.LogError("WKSR menu: " + e);
            }
            gui.EndWindow();
        }

        private void DrawPages(ImGui gui, MainViewModel vm)
        {
            // STEAM LOBBIES: no match server; lobbies are hosted by the players.
            bool steam = vm.SteamMode;
            // Offline (neither CONNECT nor STEAM LOBBIES pressed): only what needs no server.
            bool offline = !vm.WantsOnline && !steam;
            if (steam && (_page == Page.Ranked || _page == Page.Boards)) _page = Page.Main;
            if (offline && _page != Page.Main && _page != Page.Unranked && _page != Page.Replays && _page != Page.Segments) _page = Page.Main;
            if (steam) SteamHeader(gui, vm);
            else Header(gui, vm, offline);

            // A match or run (or its result) and the lobby room take the window over, whatever page was open.
            if (vm.HasMatch) Match(gui, vm);
            else if (vm.ShowLobbyRoom) LobbyRoom(gui, vm);
            else
            {
                switch (_page)
                {
                    case Page.Ranked: Ranked(gui, vm); break;
                    case Page.Lobby: Lobby(gui, vm); break;
                    case Page.Unranked: Unranked(gui, vm); break;
                    case Page.Boards: Boards(gui, vm); break;
                    case Page.Options: Options(gui, vm); break;
                    case Page.Replays: Replays(gui, vm); break;
                    case Page.Segments: SegmentRuns(gui, vm, offline); break;
                    case Page.Browse: Browse(gui, vm); break;
                    default:
                        if (steam) SteamMain(gui);
                        else if (offline) OfflineMain(gui);
                        else Main(gui);
                        break;
                }
                gui.AddSpacing(16f);
                gui.BeginHorizontal();
                // BACK: from a mode's page to the three modes, and from there to the game's main menu.
                bool main = _page == Page.Main;
                if (gui.Button("BACK", new ImSize(160f, 40f)))
                {
                    if (main) isOpen = false;
                    else _page = _page == Page.Replays ? Page.Unranked : _page == Page.Browse ? Page.Lobby : Page.Main;
                }
                // At the bottom of the first page: the match server (CONNECT) or Steam lobbies, and back offline.
                if (main)
                {
                    gui.AddSpacing(Gap);
                    if (offline)
                    {
                        if (gui.Button("CONNECT", new ImSize(200f, 40f))) vm.GoOnline();
                        gui.AddSpacing(Gap);
                        if (gui.Button("STEAM LOBBIES", new ImSize(240f, 40f))) vm.GoSteam();
                    }
                    else if (steam)
                    {
                        if (gui.Button("LEAVE STEAM LOBBIES", new ImSize(280f, 40f))) vm.LeaveSteam();
                    }
                    else if (gui.Button("DISCONNECT", new ImSize(200f, 40f))) vm.GoOffline();
                }
                gui.EndHorizontal();
            }

            gui.AddSpacing(10f);
            gui.Separator();
            if (!string.IsNullOrEmpty(vm.LastEvent)) gui.Text(vm.LastEvent, Dim, true);
        }

        // Who the player is and whether the match server is reachable.
        private static void Header(ImGui gui, MainViewModel vm, bool offline)
        {
            if (offline)
            {
                gui.Text("OFFLINE", Dim);
                if (EmbeddedClient.StartProblem != null) gui.Text(EmbeddedClient.StartProblem, Red, true);
                gui.Separator();
                return;
            }
            gui.Text(vm.PlayerName + "     RATING " + vm.RatingText + "   " + vm.RecordText);
            gui.Text(vm.Status, ToColor(vm.StatusBrush));
            // How many players are searching for a ranked 1v1 right now.
            string queued = vm.QueuedText;
            if (queued.Length > 0) gui.Text(queued, Dim);
            if (vm.HasUpdate) gui.Text(vm.UpdateText + ". Update the mod in your mod manager.", Gold, true);
            if (EmbeddedClient.StartProblem != null) gui.Text(EmbeddedClient.StartProblem, Red, true);
            gui.Separator();
        }

        // The first page before CONNECT: what needs no server (unranked solo runs, of the whole climb or a segment).
        private void OfflineMain(ImGui gui)
        {
            Title(gui, "WHITE KNUCKLE SPEEDRUN", "UNRANKED is solo, with your own rules. SEGMENTS is a solo run of one part of the climb. Both work offline. CONNECT signs in to the match server for RANKED, LOBBY, the LEADERBOARDS and segment runs that count for them. STEAM LOBBIES plays with others in lobbies the players host through Steam, without the match server: nothing is ranked there.");
            gui.BeginHorizontal();
            float half = (gui.GetLayoutWidth() - Gap - 8f) / 2f;
            if (gui.Button("UNRANKED", new ImSize(half, 150f))) _page = Page.Unranked;
            gui.AddSpacing(Gap);
            if (gui.Button("SEGMENTS", new ImSize(half, 150f))) _page = Page.Segments;
            gui.EndHorizontal();
            gui.AddSpacing(12f);
            gui.Text(OtherModsNote, Gold, true);
        }

        // The user's own wording: keep it as written.
        private const string OtherModsNote = "Cosmetic mods SHOULD work with this mod just make sure they're updated. This mod might have issues with other mods that tamper with item generating, new items, physics, custom levels and world generating etc.";

        private void Main(ImGui gui)
        {
            Title(gui, "WHITE KNUCKLE SPEEDRUN", "RANKED counts for your rating and the leaderboards. LOBBY is for playing with friends. UNRANKED is solo, with your own rules. SEGMENTS is a solo run of one part of the climb, with a leaderboard per segment.");
            // The layout adds its own spacing between controls on top of the gaps: leave room for it.
            float half = (gui.GetLayoutWidth() - Gap - 8f) / 2f;
            gui.BeginHorizontal();
            if (gui.Button("RANKED", new ImSize(half, 96f))) _page = Page.Ranked;
            gui.AddSpacing(Gap);
            if (gui.Button("LOBBY", new ImSize(half, 96f))) _page = Page.Lobby;
            gui.EndHorizontal();
            gui.AddSpacing(Gap);
            gui.BeginHorizontal();
            if (gui.Button("UNRANKED", new ImSize(half, 96f))) _page = Page.Unranked;
            gui.AddSpacing(Gap);
            if (gui.Button("SEGMENTS", new ImSize(half, 96f))) _page = Page.Segments;
            gui.EndHorizontal();
            gui.AddSpacing(Gap);
            gui.BeginHorizontal();
            if (gui.Button("LEADERBOARDS", new ImSize(half, 56f))) Show(Page.Boards);
            gui.AddSpacing(Gap);
            if (gui.Button("OPTIONS", new ImSize(half, 56f))) Show(Page.Options);
            gui.EndHorizontal();
        }

        // ---- leaderboards ---------------------------------------------------------------------------------------

        private static void Boards(ImGui gui, MainViewModel vm)
        {
            gui.BeginHorizontal();
            Tab(gui, "RATING", vm.IsRatingBoard, vm.BoardRatingCommand);
            gui.AddSpacing(Gap);
            Tab(gui, "1V1 TIMES", vm.IsDuelTimeBoard, vm.BoardDuelCommand);
            gui.AddSpacing(Gap);
            Tab(gui, "SOLO TIMES", vm.IsSoloTimeBoard, vm.BoardSoloCommand);
            gui.AddSpacing(Gap);
            // The segments' boards: this tab opens the one last looked at (the first segment's to begin with).
            gui.BeginReadOnly(!vm.ServerConnected);
            if (gui.Button(vm.IsSegmentBoard ? "[ SEGMENTS ]" : "SEGMENTS", new ImSize(TabWidth, 36f)) && !vm.IsSegmentBoard) vm.ShowSegmentBoard(_boardSegment);
            gui.EndReadOnly();
            gui.EndHorizontal();
            if (vm.IsSegmentBoard)
            {
                // Which segment's board is shown.
                _boardSegment = vm.BoardSegment;
                gui.BeginHorizontal();
                gui.Text("SEGMENT ", Dim);
                foreach (WKRace.Protocol.SegmentInfo segment in WKRace.Protocol.Segments.All)
                {
                    bool shown = segment.Id == _boardSegment;
                    if (gui.Button(shown ? "[ " + segment.Name + " ]" : segment.Name, new ImSize(150f, 30f)) && !shown) vm.ShowSegmentBoard(segment.Id);
                    gui.AddSpacing(8f);
                }
                gui.EndHorizontal();
            }
            gui.AddSpacing(6f);
            gui.Text(vm.BoardTitle, Gold);

            gui.BeginHorizontal();
            string search = vm.BoardSearch;
            if (TextBox(gui, ref search, new ImSize(220f, 32f), WKRace.Protocol.ProtocolConstants.MaxNameLength)) vm.BoardSearch = search;
            gui.AddSpacing(8f);
            Command(gui, "SEARCH", vm.BoardSearchCommand, new ImSize(110f, 32f));
            gui.AddSpacing(8f);
            Command(gui, "CLEAR", vm.BoardClearCommand, new ImSize(90f, 32f));
            gui.AddSpacing(8f);
            Command(gui, "FIND ME", vm.BoardFindMeCommand, new ImSize(110f, 32f));
            gui.EndHorizontal();

            float width = gui.GetLayoutWidth();
            bool times = vm.IsTimeBoard;
            BoardLine(gui, "#", "PLAYER", vm.BoardValueHeader, vm.BoardDetailHeader, Dim, width);
            gui.BeginList(new ImSize(width, vm.IsSegmentBoard ? 214f : 250f));
            foreach (BoardRowModel row in vm.BoardRows)
            {
                gui.BeginHorizontal();
                BoardLine(gui, row.Rank, row.Name, row.Value, row.Detail, ToColor(row.Brush), width);
                // One of the 20 fastest: its replay can be run against, unranked.
                if (times && row.CanPlay && gui.Button("PLAY", new ImSize(70f, gui.GetRowHeight())) && row.PlayCommand.CanExecute(null)) row.PlayCommand.Execute(null);
                gui.EndHorizontal();
            }
            gui.EndList();
            gui.Text(vm.BoardRangeText + "     " + vm.BoardYouText, Dim);
            if (!string.IsNullOrEmpty(vm.BoardStatus)) gui.Text(vm.BoardStatus, Gold, true);

            gui.BeginHorizontal();
            Command(gui, "TOP", vm.BoardTopCommand, new ImSize(90f, 34f));
            gui.AddSpacing(8f);
            Command(gui, "UP", vm.BoardUpCommand, new ImSize(90f, 34f));
            gui.AddSpacing(8f);
            Command(gui, "DOWN", vm.BoardDownCommand, new ImSize(90f, 34f));
            gui.EndHorizontal();
        }

        private const float TabWidth = 166f;
        private static byte _boardSegment = WKRace.Protocol.Segments.Silos;

        private static void Tab(ImGui gui, string label, bool selected, ICommand command)
        {
            Command(gui, selected ? "[ " + label + " ]" : label, command, new ImSize(TabWidth, 36f));
        }

        // One line of a board, in columns.
        private static void BoardLine(ImGui gui, string rank, string name, string value, string detail, Color32 color, float width)
        {
            float h = gui.GetRowHeight();
            bool own = false;
            // The header is not in a row of its own yet.
            if (rank == "#") { gui.BeginHorizontal(); own = true; }
            gui.Text(rank ?? "", color, gui.AddLayoutRect(60f, h));
            gui.Text(name ?? "", color, gui.AddLayoutRect(width * 0.36f, h));
            gui.Text(value ?? "", color, gui.AddLayoutRect(130f, h));
            gui.Text(detail ?? "", color, gui.AddLayoutRect(150f, h));
            if (own) gui.EndHorizontal();
        }

        // ---- options --------------------------------------------------------------------------------------------

        private static string _keyProblem;

        private static void Options(ImGui gui, MainViewModel vm)
        {
            Title(gui, "OPTIONS", "Hotkeys in the game: press a key's button, then the new key.");
            gui.BeginVertical(420f);
            Key(gui, vm, "READY", "Ready");
            Key(gui, vm, "FORFEIT", "Forfeit");
            Key(gui, vm, "SPLITS ON / OFF", "Splits");
            Key(gui, vm, "GHOSTS ON / OFF", "Ghost");
            Key(gui, vm, "OPEN / CLOSE CHAT", "Chat");
            gui.EndVertical();
            if (_keyProblem != null) gui.Text(_keyProblem, Red, true);

            gui.AddSpacing(14f);
            gui.Text("GHOST OPACITY   " + vm.GhostOpacityText, Gold);
            float opacity = (float)vm.GhostOpacity;
            if (gui.Slider(ref opacity, 0f, 100f, new ImSize(420f, 28f), 5f)) vm.GhostOpacity = opacity;

            gui.AddSpacing(14f);
            gui.Text("MATCH DATA", Gold);
            gui.Text("Saves what this session's matches and runs sent and received as a file in your Documents folder, for a bug report on Discord.", Dim, true);
            Command(gui, "EXPORT MATCH DATA", vm.ExportCommand, new ImSize(240f, 36f));

            if (vm.HasPrivacyPage)
            {
                gui.AddSpacing(14f);
                gui.Text("YOUR DATA", Gold);
                gui.Text("What the match server keeps about you, and how to get a copy or have it deleted. Opens in your browser.", Dim, true);
                Command(gui, "PRIVACY NOTICE", vm.PrivacyCommand, new ImSize(240f, 36f));
            }

            if (!vm.SteamMode)
            {
                gui.AddSpacing(14f);
                gui.Text("CHAT", Gold);
                bool autoGlobal = vm.ChatAutoGlobal;
                if (gui.Checkbox(ref autoGlobal, "JOIN THE GLOBAL CHAT WHEN I CONNECT")) vm.ChatAutoGlobal = autoGlobal;
            }
        }

        private static void Key(ImGui gui, MainViewModel vm, string label, string action)
        {
            KeyCode code = (KeyCode)vm.KeyCodeOf(action);
            if (gui.Keybind(label, ref code)) _keyProblem = vm.SetKey(action, (int)code);
        }

        private static void Ranked(ImGui gui, MainViewModel vm)
        {
            Title(gui, "RANKED", "Counts for your rating and the leaderboards. All modifiers on.");
            if (vm.SteamAvailable && !vm.SteamLinked)
            {
                // Nothing to press: starting the game through Steam is the sign-in, and it is linked by itself.
                gui.Text("Ranked needs your Steam account. It links by itself when the game runs through Steam. " + vm.SteamStatus, Gold, true);
                gui.AddSpacing(10f);
            }
            float half = (gui.GetLayoutWidth() - Gap - 8f) / 2f;
            gui.BeginHorizontal();
            gui.BeginVertical(half);
            Command(gui, vm.FindTitle == "SEARCHING" ? "STOP SEARCHING" : "1V1", vm.FindCardCommand, new ImSize(half, 90f));
            gui.Text("A rated match against one other player on the same mod version.", true);
            gui.EndVertical();
            gui.AddSpacing(Gap);
            gui.BeginVertical(half);
            Command(gui, "SOLO", vm.RankedSoloCommand, new ImSize(half, 90f));
            gui.Text("A rated solo run for the leaderboard. Every restart gives a new seed.", true);
            gui.EndVertical();
            gui.EndHorizontal();
            if (!string.IsNullOrEmpty(vm.QueueText)) gui.Text(vm.QueueText, Gold, true);
        }

        private string _joinCode = "";

        private void Lobby(ImGui gui, MainViewModel vm)
        {
            Title(gui, "LOBBY", "Play with friends. Unranked.");
            float half = (gui.GetLayoutWidth() - Gap - 8f) / 2f;
            gui.BeginHorizontal();

            gui.BeginVertical(half);
            gui.Text("CREATE LOBBY", Gold);
            Rules(gui, vm.LobbyRules);
            if (vm.HasLobbyBrowser)
            {
                bool listed = vm.LobbyPublic;
                if (gui.Checkbox(ref listed, "PUBLIC (IN THE LOBBY BROWSER)")) vm.LobbyPublic = listed;
            }
            gui.AddSpacing(6f);
            Command(gui, "OPEN LOBBY", vm.OpenLobbyCommand, new ImSize(half, 44f));
            gui.EndVertical();

            gui.AddSpacing(Gap);

            gui.BeginVertical(half);
            gui.Text("JOIN LOBBY", Gold);
            gui.Text("Paste the code a friend sent you (WKR-XXXX-XXXX).", true);
            if (TextBox(gui, ref _joinCode, new ImSize(half, 36f), 40)) vm.JoinCode = _joinCode;
            gui.AddSpacing(6f);
            Command(gui, "JOIN", vm.JoinLobbyCommand, new ImSize(half, 44f));
            if (vm.HasLobbyBrowser)
            {
                gui.AddSpacing(14f);
                gui.Text("LOBBY BROWSER", Gold);
                gui.Text("The public lobbies of players on your version.", true);
                if (gui.Button("BROWSE LOBBIES", new ImSize(half, 44f))) Show(Page.Browse);
            }
            gui.EndVertical();

            gui.EndHorizontal();
            if (!string.IsNullOrEmpty(vm.LobbyText)) gui.Text(vm.LobbyText, Gold, true);
        }

        private void LobbyRoom(ImGui gui, MainViewModel vm)
        {
            Title(gui, vm.LobbyTitle, vm.LobbyRulesText);
            gui.BeginHorizontal();
            gui.Text("CODE  " + vm.LobbyCode, Gold);
            gui.AddSpacing(Gap);
            Command(gui, "COPY CODE", vm.CopyCodeCommand, new ImSize(140f, 30f));
            gui.EndHorizontal();
            gui.AddSpacing(6f);

            gui.BeginList(new ImSize(gui.GetLayoutWidth(), 150f));
            foreach (LobbyMemberRow member in vm.LobbyMembers) gui.Text(member.Name + "    " + member.Detail, ToColor(member.Brush));
            gui.EndList();

            gui.Text("The lobby talks in the chat's Match room: press " + vm.KeyChatText + " to open the chat.", Dim, true);

            if (!string.IsNullOrEmpty(vm.LobbyText)) gui.Text(vm.LobbyText, Gold, true);
            gui.AddSpacing(8f);
            gui.BeginHorizontal();
            if (vm.IsLobbyHost)
            {
                Command(gui, vm.StartText, vm.StartLobbyCommand, new ImSize(200f, 40f));
                gui.AddSpacing(Gap);
            }
            Command(gui, "LEAVE", vm.CancelLobbyCommand, new ImSize(140f, 40f));
            gui.EndHorizontal();
        }

        private string _seed = "";

        private void Unranked(ImGui gui, MainViewModel vm)
        {
            Title(gui, "UNRANKED", "Solo, with your own modifiers and seed. Nothing is uploaded.");
            gui.Text("SOLO UNRANKED", Gold);
            gui.BeginHorizontal();
            gui.Text("SEED ");
            if (TextBox(gui, ref _seed, new ImSize(220f, 36f), 11)) vm.PracticeSeed = _seed;
            gui.AddSpacing(Gap);
            Command(gui, "START", vm.PracticeCommand, new ImSize(160f, 36f));
            gui.EndHorizontal();
            gui.Text("With a seed typed in, restarting in the game restarts that same seed. With no seed, every restart is a new random seed.", Dim, true);
            gui.AddSpacing(6f);
            Rules(gui, vm.PracticeRules);
            gui.AddSpacing(10f);
            if (gui.Button("SAVED REPLAYS", new ImSize(220f, 36f))) Show(Page.Replays);
        }

        // ---- segments ---------------------------------------------------------------------------------------------

        private string _segmentSeed = "";

        // The segments to run, and the modifiers. With the leaderboard's modifiers, no seed of the player's own and a
        // connection the run counts for the segment's leaderboard; anything else is an unranked run, and says so.
        private void SegmentRuns(ImGui gui, MainViewModel vm, bool offline)
        {
            Title(gui, "SEGMENTS", "A solo run of one part of the climb: the clock stops where the segment ends. Every segment has a leaderboard of its own (LEADERBOARDS, SEGMENTS).");
            bool asRanked = true;
            foreach (WKRace.Protocol.SegmentInfo segment in WKRace.Protocol.Segments.All)
            {
                bool counts = vm.SegmentCounts(segment.Id);
                asRanked &= vm.SegmentAsRanked(segment.Id);
                gui.Text(segment.Name, Gold);
                gui.Text(segment.About, true);
                gui.BeginHorizontal();
                gui.BeginReadOnly(!vm.CanStartSegment);
                bool pressed = gui.Button("START", new ImSize(200f, 44f));
                gui.EndReadOnly();
                if (pressed && vm.CanStartSegment) vm.StartSegment(segment.Id);
                gui.AddSpacing(Gap);
                gui.Text(counts ? "COUNTS FOR THE LEADERBOARD" : "UNRANKED", counts ? Gold : Dim);
                gui.EndHorizontal();
                gui.AddSpacing(10f);
            }

            gui.Text("MODIFIERS", Gold);
            Rules(gui, vm.SegmentRules, false);
            gui.AddSpacing(8f);
            gui.BeginHorizontal();
            gui.Text("SEED ");
            if (TextBox(gui, ref _segmentSeed, new ImSize(220f, 36f), 11)) vm.SegmentSeed = _segmentSeed;
            gui.AddSpacing(Gap);
            gui.Text("Blank: a random seed.", Dim);
            gui.EndHorizontal();
            gui.AddSpacing(8f);

            if (vm.SteamMode)
                gui.Text("Segment runs are unranked in Steam lobbies (there are no leaderboards): nothing is uploaded.", Dim, true);
            else if (offline)
                gui.Text("Offline: segment runs are unranked and nothing is uploaded. CONNECT on the first page for runs that count for the leaderboards.", Dim, true);
            else if (!asRanked)
            {
                gui.Text("NOT FOR THE LEADERBOARD: with changed modifiers or a seed of your own the run is unranked, and its time is not uploaded.", Red, true);
                if (gui.Button("BACK TO LEADERBOARD SETTINGS", new ImSize(330f, 34f)))
                {
                    vm.ResetSegmentRules();
                    _segmentSeed = "";
                }
            }
            else if (!vm.ServerConnected)
                gui.Text("Not connected to the match server right now: a run started now is unranked.", Dim, true);
            else
            {
                gui.Text("Leaderboard runs use these modifiers. Every run, and every restart in the game, is on a new seed, and your fastest time goes on the segment's leaderboard.", Dim, true);
                if (vm.SteamAvailable && !vm.SteamLinked) gui.Text("They need your Steam account. It links by itself when the game runs through Steam. " + vm.SteamStatus, Gold, true);
            }
        }

        // ---- the lobby browser -------------------------------------------------------------------------------------

        private static void Browse(ImGui gui, MainViewModel vm)
        {
            Title(gui, "LOBBY BROWSER", "Public lobbies you can join. Private lobbies are not listed: they are joined with their code.");
            float width = gui.GetLayoutWidth();
            BrowseLine(gui, "HOST", "MODE", "PLAYERS", "", Dim, width, true);
            gui.BeginList(new ImSize(width, 300f));
            foreach (BrowseRowModel row in vm.BrowseRows)
            {
                gui.BeginHorizontal();
                BrowseLine(gui, row.Host, row.Mode, row.Players, row.State, ToColor(row.Brush), width, false);
                Command(gui, "JOIN", row.JoinCommand, new ImSize(80f, gui.GetRowHeight()));
                gui.EndHorizontal();
                gui.Text(row.Rules ?? "", Dim, true);
                gui.AddSpacing(4f);
            }
            gui.EndList();
            if (!string.IsNullOrEmpty(vm.BrowseStatus)) gui.Text(vm.BrowseStatus, Gold, true);
            if (!string.IsNullOrEmpty(vm.LobbyText)) gui.Text(vm.LobbyText, Gold, true);
            Command(gui, "REFRESH", vm.RefreshBrowseCommand, new ImSize(160f, 36f));
        }

        private static void BrowseLine(ImGui gui, string host, string mode, string players, string state, Color32 color, float width, bool own)
        {
            float h = gui.GetRowHeight();
            if (own) gui.BeginHorizontal();
            gui.Text(host ?? "", color, gui.AddLayoutRect(width * 0.38f, h));
            gui.Text(mode ?? "", color, gui.AddLayoutRect(100f, h));
            gui.Text(players ?? "", color, gui.AddLayoutRect(100f, h));
            gui.Text(state ?? "", color, gui.AddLayoutRect(120f, h));
            if (own) gui.EndHorizontal();
        }

        // ---- saved replays ----------------------------------------------------------------------------------------

        private static void Replays(ImGui gui, MainViewModel vm)
        {
            Title(gui, "SAVED REPLAYS", "The runs you saved with SAVE REPLAY. They stay on this PC. RUN AGAINST starts an unranked run on the same seed, with the same modifiers and bans, against the replay as a ghost.");
            gui.BeginList(new ImSize(gui.GetLayoutWidth(), 300f));
            foreach (ReplayRowModel row in vm.ReplayRows)
            {
                float h = gui.GetRowHeight();
                gui.BeginHorizontal();
                gui.Text(row.When ?? "", gui.AddLayoutRect(165f, h));
                gui.Text(row.Seed ?? "", Gold, gui.AddLayoutRect(155f, h));
                gui.Text(row.Result ?? "", gui.AddLayoutRect(150f, h));
                Command(gui, "RUN AGAINST", row.PlayCommand, new ImSize(125f, h));
                gui.AddSpacing(6f);
                Command(gui, "DELETE", row.DeleteCommand, new ImSize(80f, h));
                gui.EndHorizontal();
                gui.Text(row.Detail ?? "", Dim, true);
                gui.AddSpacing(4f);
            }
            gui.EndList();
            gui.Text(vm.ReplayRangeText, Dim);
            if (!string.IsNullOrEmpty(vm.ReplayStatus)) gui.Text(vm.ReplayStatus, Gold, true);
            gui.BeginHorizontal();
            Command(gui, "UP", vm.ReplaysUpCommand, new ImSize(90f, 34f));
            gui.AddSpacing(8f);
            Command(gui, "DOWN", vm.ReplaysDownCommand, new ImSize(90f, 34f));
            gui.AddSpacing(8f);
            Command(gui, "OPEN FOLDER", vm.ReplaysFolderCommand, new ImSize(160f, 34f));
            gui.EndHorizontal();
        }

        // The modifier picker (CREATE LOBBY with the mode, SOLO UNRANKED without). withBans: the artifact bans can be
        // picked (not for the segments, which so far all end before the artifact room).
        private static void Rules(ImGui gui, RulesModel rules, bool withBans = true)
        {
            if (rules.ShowMode)
            {
                gui.BeginHorizontal();
                bool duel = rules.IsDuel, party = rules.IsParty;
                if (gui.Checkbox(ref duel, "1V1") && duel) rules.IsDuel = true;
                gui.AddSpacing(Gap);
                if (gui.Checkbox(ref party, "PARTY (UP TO 15)") && party) rules.IsParty = true;
                gui.EndHorizontal();
            }
            if (withBans)
            {
                bool bans = rules.Bans;
                gui.BeginReadOnly(!rules.BansAvailable);
                if (gui.Checkbox(ref bans, "ARTIFACT BANS (1V1 ONLY)")) rules.Bans = bans;
                gui.EndReadOnly();
            }
            bool curated = rules.Curated;
            if (gui.Checkbox(ref curated, "CURATED SEEDS")) rules.Curated = curated;
            gui.BeginIndent();
            bool food = rules.Food, perks = rules.Perks, items = rules.Items;
            if (gui.Checkbox(ref food, "DEEP STORAGE FOOD")) rules.Food = food;
            if (gui.Checkbox(ref perks, "PERKS")) rules.Perks = perks;
            if (gui.Checkbox(ref items, "ITEMS")) rules.Items = items;
            gui.EndIndent();
        }

        // The match or run in progress, or its result.
        private static void Match(ImGui gui, MainViewModel vm)
        {
            gui.BeginHorizontal();
            gui.Text(vm.OpponentText, Gold);
            gui.AddSpacing(24f);
            gui.Text(vm.TimerText, Gold);
            gui.EndHorizontal();
            gui.Text(vm.SeedText, Dim, true);
            if (!string.IsNullOrEmpty(vm.BansText)) gui.Text(vm.BansText, Dim, true);
            gui.Text(vm.MatchState, Red);
            if (vm.HasResult)
            {
                gui.Text(vm.ResultText, ToColor(vm.ResultBrush), true);
                if (vm.HasBoardNote) gui.Text(vm.BoardNoteText, Gold, true);
            }
            if (vm.ShowReady) Command(gui, "READY", vm.ReadyCommand, new ImSize(200f, 44f));

            gui.BeginList(new ImSize(gui.GetLayoutWidth(), 230f));
            if (vm.IsPartyMatch)
            {
                foreach (StandingRow row in vm.StandingsRows) gui.Text(row.Place + "   " + row.Name + "     " + row.Detail, ToColor(row.Brush));
            }
            else
            {
                foreach (SplitRow row in vm.Splits) gui.Text(row.Index.ToString().PadLeft(2) + "  " + (row.Level ?? "").PadRight(34) + row.You.PadLeft(10) + row.Opp.PadLeft(12) + "  " + row.Delta);
            }
            gui.EndList();

            if (vm.ShowSaveRow)
            {
                gui.BeginHorizontal();
                gui.Text(vm.SavePromptText, Dim);
                if (vm.ShowSavePrompt)
                {
                    gui.AddSpacing(Gap);
                    Command(gui, "SAVE REPLAY", vm.SaveReplayCommand, new ImSize(160f, 30f));
                }
                gui.EndHorizontal();
            }
            gui.AddSpacing(6f);
            gui.BeginHorizontal();
            if (vm.ForfeitCommand.CanExecute(null)) { Command(gui, vm.ForfeitText, vm.ForfeitCommand, new ImSize(150f, 40f)); gui.AddSpacing(Gap); }
            if (vm.PracticeSeedCommand.CanExecute(null)) { Command(gui, "RUN THIS SEED", vm.PracticeSeedCommand, new ImSize(190f, 40f)); gui.AddSpacing(Gap); }
            if (vm.DismissCommand.CanExecute(null)) Command(gui, "BACK", vm.DismissCommand, new ImSize(140f, 40f));
            gui.EndHorizontal();
        }

        // A button for one of WKSR's commands: greyed out while the command cannot run.
        private static void Command(ImGui gui, string label, ICommand command, ImSize size)
        {
            bool can = command != null && command.CanExecute(null);
            gui.BeginReadOnly(!can);
            bool pressed = gui.Button(label, size);
            gui.EndReadOnly();
            if (pressed && can) command.Execute(null);
        }

        private static Color32 ToColor(System.Windows.Media.Brush brush)
        {
            return brush == null ? (Color32)Color.white : new Color32(brush.Color.R, brush.Color.G, brush.Color.B, 255);
        }

        private static void Title(ImGui gui, string title, string note)
        {
            gui.Text(title, Gold);
            if (!string.IsNullOrEmpty(note)) gui.Text(note, true);
            gui.AddSpacing(12f);
        }
    }
}
