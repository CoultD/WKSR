using System;
using System.Collections.Generic;
using System.Windows.Input;
using UnityEngine;
using WKRace.Client;

namespace WKRace.Mod
{
    // Testing only (Debug/OpenMenu in the config): walks through the WKSR window by itself, keeps a picture of each
    // page, then plays out Debug/MenuAction (a ranked solo run, a lobby match or a ranked 1v1 against a test player).
    // Nothing here runs for players.
    internal static class MenuWalk
    {
        private static readonly Queue<Func<bool>> Steps = new Queue<Func<bool>>();
        private static bool _started;
        private static float _mark;

        private static MainViewModel Vm { get { return EmbeddedClient.Vm; } }

        public static void Start()
        {
            if (_started) return;
            _started = true;
            _mark = Time.unscaledTime;

            Wait(3.2f);
            Do(MenuUi.Open);
            Wait(1.5f);
            Shot("menu-0-offline");
            string action = RaceConfig.DebugMenuAction.Value.Trim();
#if DEBUG
            if (action.StartsWith("dump-events:", StringComparison.Ordinal))
            {
                Do(() => ReconDump.DumpEvents(action.Substring(12)));
                Do(() => Plugin.Log.LogInfo("WKSR menu test: done."));
                return;
            }
#endif
            if (action.StartsWith("steam", StringComparison.Ordinal))
            {
                // STEAM LOBBIES ("steam-lobby": a lobby the test player joins).
                Do(() => Vm.GoSteam());
                Wait(1f);
                SteamWalk(action);
                return;
            }
            if (action == "offline-segment")
            {
                // Without CONNECT: a segment run is unranked.
                Page("menu-0-offline-segments", WksrWindow.Page.Segments, 1.5f);
                Do(() => { Plugin.Log.LogInfo("WKSR menu test: " + action + " pressed."); Vm.StartSegment(WKRace.Protocol.Segments.Silos); });
                SegmentToItsEnd();
                Do(() => Plugin.Log.LogInfo("WKSR menu test: done."));
                return;
            }
            if (action == "offline-solo")
            {
                // Without CONNECT: an unranked solo run, and nothing reaches a server.
                Page("menu-0-offline-unranked", WksrWindow.Page.Unranked, 1.5f);
                Do(() => Press(Vm.PracticeCommand, action));
                Wait(30f);
                Shot("offline-run");
                Do(() => Plugin.Log.LogInfo("WKSR menu test: done."));
                return;
            }
            Do(() => Vm.GoOnline());
            Wait(3f);
            Shot("menu-1-main");
            Page("menu-2-ranked", WksrWindow.Page.Ranked, 1.5f);
            Page("menu-3-lobby", WksrWindow.Page.Lobby, 1.5f);
            Page("menu-4-unranked", WksrWindow.Page.Unranked, 1.5f);
            Page("menu-4-segments", WksrWindow.Page.Segments, 1.5f);
            Page("menu-5-leaderboards", WksrWindow.Page.Boards, 2.5f);
            Page("menu-6-options", WksrWindow.Page.Options, 1.5f);
            Page("menu-7-replays", WksrWindow.Page.Replays, 1.5f);
            Do(() => MenuUi.Window.Show(WksrWindow.Page.Main));

            switch (action)
            {
                case "":
                    // The chat: a line in Global, the open panel, the list of rooms, and the one line when it is closed.
                    Do(() => RaceController.Current.Chat.OpenPanel());
                    Wait(0.8f);
                    Do(() => Vm.Say("hello from the game"));
                    Wait(0.5f);
                    Do(() => Vm.Say("a long line to see how it wraps: " + string.Join(" ", System.Linq.Enumerable.Repeat("the quick brown fox jumps over the lazy dog", 9))));
                    Wait(1.2f);
                    Shot("chat-1-open");
                    Do(() => RaceController.Current.Chat.ShowRooms = true);
                    Wait(0.8f);
                    Shot("chat-2-rooms");
                    Do(() => RaceController.Current.Chat.Close());
                    Wait(0.8f);
                    Shot("chat-3-closed");
                    // Five lines at once: the fifth is sent, then the silence and its countdown on the input line.
                    Wait(5.5f);
                    Do(() => RaceController.Current.Chat.OpenPanel());
                    Do(() => { for (int i = 1; i <= 6; i++) Vm.Say("fast line " + i); });
                    Wait(1.6f);
                    Shot("chat-5-silenced");
                    Do(() => RaceController.Current.Chat.Close());
                    Do(() => Vm.GoOffline());
                    Wait(1.5f);
                    Shot("menu-9-disconnected");
                    break;
                case "ranked-solo":
                    Do(() => Press(Vm.RankedSoloCommand, action));
                    Wait(2.5f);
                    Shot("solo-1-bans");
                    Wait(0.5f);
                    break;
                case "unranked-solo": Do(() => Press(Vm.PracticeCommand, action)); break;
                case "segment":
                    // A segment run for the leaderboard, played to its end, and then its board.
                    Do(() => MenuUi.Window.Show(WksrWindow.Page.Segments));
                    Do(() => { Plugin.Log.LogInfo("WKSR menu test: " + action + " pressed."); Vm.StartSegment(WKRace.Protocol.Segments.Silos); });
                    SegmentToItsEnd();
                    Do(() => { CL_GameManager.gMan.Pause(); MenuUi.PressPauseButton(); });
                    Wait(1.5f);
                    Shot("segment-3-result-wksr");
                    Do(() => Press(Vm.DismissCommand, "back"));
                    Do(() => MenuUi.Window.Show(WksrWindow.Page.Boards));
                    Wait(1f);
                    Do(() => Vm.ShowSegmentBoard(WKRace.Protocol.Segments.Silos));
                    Wait(2f);
                    Shot("segment-4-board");
                    break;
                case "search":
                    Do(() => Press(Vm.FindCardCommand, action));
                    MatchAgainstTestPlayer(200f);
                    break;
                case "lobby":
                    // Without bans, so the match starts as soon as the test player joins.
                    Do(() => { Vm.LobbyRules.Bans = false; Vm.LobbyPublic = true; Press(Vm.OpenLobbyCommand, action); });
                    Until("the lobby opening", 10f, () => Vm.ShowLobbyRoom && !string.IsNullOrEmpty(Vm.LobbyCode));
                    // The lobby browser lists it (when the server has the browser).
                    Wait(2f);
                    Do(() => Vm.RefreshBrowseCommand.Execute(null));
                    WaitFor(5f, () => !Vm.BrowseStatus.StartsWith("Looking", StringComparison.Ordinal) && Vm.BrowseStatus.Length > 0);
                    Do(() => Plugin.Log.LogInfo("WKSR menu test: browser while hosting: " + Vm.BrowseStatus + " "
                        + string.Join(" | ", System.Linq.Enumerable.Select(Vm.BrowseRows, r => r.Host + " " + r.Mode + " " + r.Players + " " + r.State))));
                    Do(() =>
                    {
                        Plugin.Log.LogInfo("WKSR menu test: lobby code " + Vm.LobbyCode);
                        Vm.SelectRoom(WKRace.Protocol.ChatRooms.Match);
                        Vm.Say("hello from the game");
                    });
                    Wait(1.5f);
                    Shot("menu-8-lobby-room");
                    Do(() => RaceController.Current.Chat.OpenPanel());
                    Wait(1f);
                    Shot("chat-4-lobby");
                    Do(() => RaceController.Current.Chat.Close());
                    Do(() => Plugin.Log.LogInfo("WKSR menu test: lobby ready for the second player"));
                    MatchAgainstTestPlayer(90f);
                    break;
            }
            Do(() => Plugin.Log.LogInfo("WKSR menu test: done."));
        }

        // STEAM LOBBIES: the pages, then a lobby this game hosts. "steam-lobby": a 1v1 lobby that the test player (the
        // DevTools bot, on Debug/HostTcpPort) joins and plays; the lobby is also looked for in Steam's lobby search.
        private static void SteamWalk(string action)
        {
            Shot("steam-0-main");
            Page("steam-1-lobby", WksrWindow.Page.Lobby, 1.5f);
            Page("steam-2-browse", WksrWindow.Page.Browse, 4f);
            Do(() => Plugin.Log.LogInfo("WKSR menu test: browser before hosting: " + Vm.BrowseStatus));
            Page("steam-3-unranked", WksrWindow.Page.Unranked, 1.5f);
            Page("steam-4-segments", WksrWindow.Page.Segments, 1.5f);
            Page("steam-5-options", WksrWindow.Page.Options, 1.5f);
            Do(() => MenuUi.Window.Show(WksrWindow.Page.Main));
            if (action != "steam-lobby")
            {
                Do(() => Plugin.Log.LogInfo("WKSR menu test: done."));
                return;
            }
            Do(() => { Vm.LobbyRules.IsDuel = true; Vm.LobbyRules.Bans = false; Vm.LobbyPublic = true; Press(Vm.OpenLobbyCommand, action); });
            Until("the lobby opening", 15f, () => Vm.ShowLobbyRoom && !string.IsNullOrEmpty(Vm.LobbyCode));
            Until("the Steam lobby", 20f, () => Vm.SteamLobbyStatus == "HOSTING A LOBBY" && Vm.LastEvent.Contains("lobby browser"));
            Wait(1.5f);
            Shot("steam-6-lobby-room");
            // Steam's lobby search finds the new lobby (the same search the browser does).
            Do(() => Vm.RefreshBrowseCommand.Execute(null));
            WaitFor(10f, () => Vm.BrowseStatus.EndsWith(".", StringComparison.Ordinal) && !Vm.BrowseStatus.StartsWith("Looking", StringComparison.Ordinal));
            Do(() =>
            {
                string rows = string.Join(" | ", System.Linq.Enumerable.Select(Vm.BrowseRows, r => r.Host + " " + r.Mode + " " + r.Players + " " + r.State + " (" + r.Rules + ")"));
                Plugin.Log.LogInfo("WKSR menu test: browser while hosting: " + Vm.BrowseStatus + " " + rows);
                Plugin.Log.LogInfo("WKSR menu test: lobby code " + Vm.LobbyCode);
                Vm.SelectRoom(WKRace.Protocol.ChatRooms.Match);
                Vm.Say("hello from the host");
            });
            Do(() => Plugin.Log.LogInfo("WKSR menu test: lobby ready for the second player"));
            MatchAgainstTestPlayer(90f);
            Do(() => { MenuUi.Window.isOpen = false; CL_GameManager.gMan.UnPause(); });
            Do(() => Press(Vm.CancelLobbyCommand, "leave"));
            Wait(2f);
            Do(() => Plugin.Log.LogInfo("WKSR menu test: after leaving: " + Vm.SteamLobbyStatus + " / " + Vm.LobbyText));
            Do(() => Vm.LeaveSteam());
            Wait(1f);
            Shot("steam-7-left");
            Do(() => Plugin.Log.LogInfo("WKSR menu test: done."));
        }

        // From the match being found to its result: ready, a look at the run, the pause menu and its WKSR button.
        private static void MatchAgainstTestPlayer(float readyTimeout)
        {
            Until("a match", 90f, () => Vm.HasMatch);
            Wait(1.5f);
            Shot("match-1-found");
            Until("the ready check", readyTimeout, () => Vm.ShowReady && Vm.ReadyCommand.CanExecute(null));
            Do(() => Press(Vm.ReadyCommand, "ready"));
            Wait(16f);
            Shot("match-2-run");
            Do(() => CL_GameManager.gMan.Pause());
            Wait(1f);
            Shot("match-3-pause-menu");
            Do(() => Plugin.Log.LogInfo("WKSR menu test: pause menu button " + (MenuUi.PressPauseButton() ? "pressed." : "NOT FOUND.")));
            Wait(1.5f);
            Shot("match-4-pause-wksr");
            Do(() => { MenuUi.Window.isOpen = false; CL_GameManager.gMan.UnPause(); });
            // The test player finishes after a while; if that does not end the match for this one, give up the match.
            WaitFor(45f, () => Vm.HasResult);
            Do(() => { if (!Vm.HasResult) Press(Vm.ForfeitCommand, "forfeit"); });
            Until("the result", 20f, () => Vm.HasResult);
            Wait(2.5f);
            Shot("match-5-result");
            Do(() => { CL_GameManager.gMan.Pause(); MenuUi.PressPauseButton(); });
            Wait(1.5f);
            Shot("match-6-result-wksr");
            Do(() => RaceController.Current.Chat.OpenPanel());
            Wait(1f);
            Shot("match-7-chat");
            Do(() => RaceController.Current.Chat.Close());
        }

        // A segment run from its start to its end: the run begins, the player is carried up to the segment's last
        // level (the debug build's AutoAdvance), and the segment's button is pressed as a click would.
        private static void SegmentToItsEnd()
        {
            WKRace.Protocol.SegmentInfo segment = WKRace.Protocol.Segments.Find(WKRace.Protocol.Segments.Silos);
            Until("the run", 30f, () => Vm.HasMatch);
            Wait(12f);
            Shot("segment-1-run");
            CL_Button end = null;
            Until("the segment's last level and its button", 150f, () =>
            {
                // (The levels are all built when the world loads: wait until the player stands in the last one.)
                bool there = Vm.Splits.Count >= 11;
                foreach (SplitRow row in Vm.Splits) there |= (row.Level ?? "").IndexOf("Pipeworks", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!there) return false;
                foreach (CL_Button button in UnityEngine.Object.FindObjectsOfType<CL_Button>())
                    if (SegmentEnd.Is(button, segment)) { end = button; return true; }
                return false;
            });
            Wait(3f);
            Do(() => { Plugin.Log.LogInfo("WKSR menu test: pressing " + end.name + "."); end.Interact(); });
            Until("the result", 15f, () => Vm.HasResult);
            Wait(2f);
            Shot("segment-2-result");
        }

        // Called every frame by the controller.
        public static void Update()
        {
            if (Steps.Count == 0) return;
            try
            {
                if (!Steps.Peek()() || Steps.Count == 0) return;
                Steps.Dequeue();
                _mark = Time.unscaledTime;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("WKSR menu test: FAILED: " + e);
                Steps.Clear();
            }
        }

        private static void Do(Action action)
        {
            Steps.Enqueue(() => { action(); return true; });
        }

        private static void Wait(float seconds)
        {
            Steps.Enqueue(() => Time.unscaledTime - _mark >= seconds);
        }

        // Goes on when the condition holds, or after the time at the latest.
        private static void WaitFor(float seconds, Func<bool> condition)
        {
            Steps.Enqueue(() => condition() || Time.unscaledTime - _mark >= seconds);
        }

        // Goes on when the condition holds; the test ends when it does not in time.
        private static void Until(string what, float timeout, Func<bool> condition)
        {
            Steps.Enqueue(() =>
            {
                if (condition()) return true;
                if (Time.unscaledTime - _mark < timeout) return false;
                Plugin.Log.LogError("WKSR menu test: FAILED: no " + what + " within " + timeout + " s.");
                Steps.Clear();
                return false;
            });
        }

        private static void Page(string tag, WksrWindow.Page page, float seconds)
        {
            Do(() => MenuUi.Window.Show(page));
            Wait(seconds);
            Shot(tag);
            Wait(0.7f);
        }

        private static void Press(ICommand command, string what)
        {
            bool can = command != null && command.CanExecute(null);
            Plugin.Log.LogInfo("WKSR menu test: " + what + (can ? " pressed." : " cannot be pressed now."));
            if (can) command.Execute(null);
        }

        private static void Shot(string tag)
        {
            Do(() =>
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "WKRace-screenshots");
                System.IO.Directory.CreateDirectory(dir);
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, DateTime.Now.ToString("HHmmss") + "-" + tag + ".png"));
            });
        }
    }
}
