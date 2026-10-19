using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using WKRace.Protocol;

namespace WKRace.Mod
{
    // The chat. Closed, the bottom left corner shows the last line of the selected room. The chat key (F8) opens the
    // panel and closes it again, and nothing else does: the rooms as tabs (x leaves a room, + lists the rooms to join
    // or leave), the selected room's lines of the last 24 hours, and the input line. Enter sends to the selected
    // room. Tabs are picked with the mouse (in the menus and while paused). Closing only puts the panel away: the
    // rooms stay joined. While it is open the game gets no input (ChatInputBlock) and the menus do not react to Enter.
    // The rooms and their lines are WKSR's (EmbeddedClient.Vm); the HUD draws the panel and says where its buttons are.
    internal sealed class ChatBox
    {
        public struct Hit
        {
            public Rect Area;
            public Action Do;
        }

        // The panel is open: the WKSR window stands back for it.
        public static bool AnyOpen { get; private set; }

        public bool Open { get; private set; }
        public bool Typing { get { return Open; } }
        public string Draft { get; private set; } = "";
        // How many lines the history is scrolled back.
        public int Scroll;
        // The list of rooms (the + button) shows in place of the lines.
        public bool ShowRooms;
        // The panel's buttons as the HUD drew them last, in its virtual coordinates.
        public readonly List<Hit> Hits = new List<Hit>();
        private int _openedFrame;

        public void OpenPanel()
        {
            if (Open) return;
            Open = true;
            AnyOpen = true;
            Draft = "";
            Scroll = 0;
            ShowRooms = false;
            _openedFrame = Time.frameCount;
            ChatInputBlock.Begin();
        }

        public void Close()
        {
            if (!Open) return;
            Open = false;
            AnyOpen = false;
            Draft = "";
            ShowRooms = false;
            Hits.Clear();
            ChatInputBlock.End();
        }

        // Reads this frame's keys and clicks. Returns a line to send when Enter was pressed, else null.
        public string Update(float virtualWidth, float virtualHeight)
        {
            if (!Open) return null;
            // (Not in the frame it was opened: that is the same press.)
            if (Time.frameCount == _openedFrame) return null;
            if (Input.GetKeyDown(RaceConfig.ChatKey.Value))
            {
                Close();
                return null;
            }

            float wheel = Input.mouseScrollDelta.y;
            if (wheel > 0f) Scroll += 3;
            else if (wheel < 0f) Scroll = Math.Max(0, Scroll - 3);
            if (Input.GetKeyDown(KeyCode.PageUp)) Scroll += 10;
            if (Input.GetKeyDown(KeyCode.PageDown)) Scroll = Math.Max(0, Scroll - 10);

            if (Input.GetMouseButtonDown(0) && Screen.width > 0 && Screen.height > 0)
            {
                Vector3 mouse = Input.mousePosition;
                var at = new Vector2(mouse.x * virtualWidth / Screen.width, (Screen.height - mouse.y) * virtualHeight / Screen.height);
                for (int i = Hits.Count - 1; i >= 0; i--)
                {
                    if (!Hits[i].Area.Contains(at)) continue;
                    Hits[i].Do();
                    break;
                }
            }

            bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
            var draft = new StringBuilder(Draft);
            foreach (char c in Input.inputString)
            {
                if (c == '\b')
                {
                    if (draft.Length > 0) draft.Length--;
                }
                else if (c == '\n' || c == '\r')
                {
                    enter = true;
                }
                else if (!char.IsControl(c) && draft.Length < ProtocolConstants.MaxChatLength)
                {
                    draft.Append(c);
                }
            }
            Draft = draft.ToString();
            if (!enter) return null;
            // Silenced for sending too fast: the line stays in the box (the input line counts the seconds down) and
            // can be sent once the silence is over.
            WKRace.Client.MainViewModel vm = EmbeddedClient.Vm;
            if (vm != null && vm.ChatSilencedSeconds > 0) return null;
            string line = Draft.Trim();
            Draft = "";
            Scroll = 0;
            return line.Length > 0 ? line : null;
        }
    }

    // While the chat or the WKSR window is up, the game's menus must not react to the keyboard: Enter would press
    // whichever menu button is highlighted.
    internal static class MenuQuiet
    {
        private static bool _quiet;

        public static void Update(bool want)
        {
            UnityEngine.EventSystems.EventSystem events = UnityEngine.EventSystems.EventSystem.current;
            if (events == null) return;
            if (want)
            {
                if (events.currentSelectedGameObject != null) events.SetSelectedGameObject(null);
                if (events.sendNavigationEvents) events.sendNavigationEvents = false;
                _quiet = true;
            }
            else if (_quiet)
            {
                events.sendNavigationEvents = true;
                _quiet = false;
            }
        }
    }

    // Keeps the game's own input quiet while the chat line is open: InputManager computes every button and stick each
    // frame, and this resets them right after (buttons held when the chat opened stay held, so a grip is not lost).
    // The console's key binds are skipped too, so typing cannot trigger them.
    internal static class ChatInputBlock
    {
        private static int _endFrame = -10;
        private static bool _typing;
        private static readonly Dictionary<InputManager.ButtonInput, bool> Held = new Dictionary<InputManager.ButtonInput, bool>();
        private static AccessTools.FieldRef<InputManager, List<InputManager.ButtonInput>> _buttons;
        private static AccessTools.FieldRef<InputManager, List<InputManager.VectorInput>> _vectors;

        // Typing, or the frame just after (the key that closed the chat must not reach the game either).
        public static bool Active { get { return _typing || Time.frameCount <= _endFrame + 1; } }

        public static void Apply(Harmony harmony)
        {
            _buttons = AccessTools.FieldRefAccess<InputManager, List<InputManager.ButtonInput>>("buttonInputs");
            _vectors = AccessTools.FieldRefAccess<InputManager, List<InputManager.VectorInput>>("vectorInputs");
            PatchOne(harmony, AccessTools.Method(typeof(InputManager), "UpdateInputs"), null, nameof(AfterUpdateInputs));
            // The console reads its key binds in LateUpdate.
            PatchOne(harmony, AccessTools.Method(typeof(CommandConsole), "LateUpdate"), nameof(BeforeConsoleUpdate), null);
        }

        private static void PatchOne(Harmony harmony, System.Reflection.MethodBase target, string prefix, string postfix)
        {
            if (target == null)
            {
                Plugin.Log.LogError("Chat input block: patch target missing for " + (prefix ?? postfix) + ". The game may have been updated.");
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(ChatInputBlock), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(ChatInputBlock), postfix));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Chat input block: could not patch " + target.Name + ": " + e.Message);
            }
        }

        public static void Begin()
        {
            _typing = true;
            Held.Clear();
            InputManager im = InputManager.instance;
            List<InputManager.ButtonInput> buttons = im != null && _buttons != null ? _buttons(im) : null;
            if (buttons != null) foreach (InputManager.ButtonInput b in buttons) if (b != null) Held[b] = b.Pressed;
        }

        public static void End()
        {
            _typing = false;
            _endFrame = Time.frameCount;
        }

        private static void AfterUpdateInputs(InputManager __instance)
        {
            if (!Active) return;
            try
            {
                List<InputManager.ButtonInput> buttons = _buttons(__instance);
                if (buttons != null)
                {
                    foreach (InputManager.ButtonInput b in buttons)
                    {
                        if (b == null) continue;
                        bool held;
                        b.Pressed = Held.TryGetValue(b, out held) && held;
                        b.Down = false;
                        b.Up = false;
                    }
                }
                List<InputManager.VectorInput> vectors = _vectors(__instance);
                if (vectors != null)
                {
                    foreach (InputManager.VectorInput v in vectors)
                    {
                        if (v == null) continue;
                        v.vector = Vector2.zero;
                        v.axis = 0f;
                        v.Pressed = false;
                        v.Down = false;
                        v.Up = false;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Chat input block failed: " + e.Message);
                _typing = false;
            }
        }

        private static bool BeforeConsoleUpdate()
        {
            return !Active;
        }
    }
}
