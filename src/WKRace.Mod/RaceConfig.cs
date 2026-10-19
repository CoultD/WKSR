using BepInEx.Configuration;
using UnityEngine;

namespace WKRace.Mod
{
    internal static class RaceConfig
    {
        public static ConfigEntry<KeyCode> JoinKey;
        public static ConfigEntry<KeyCode> ForfeitKey;
        public static ConfigEntry<KeyCode> SplitsKey;
        public static ConfigEntry<KeyCode> GhostKey;
        public static ConfigEntry<KeyCode> ChatKey;
        public static ConfigEntry<float> HudScale;
        public static ConfigEntry<bool> ShowSplits;
        public static ConfigEntry<int> SplitRows;
        public static ConfigEntry<int> UpcomingSplits;
        public static ConfigEntry<bool> ShowGhost;
        public static ConfigEntry<float> GhostOpacity;
        public static ConfigEntry<string> GhostColor;
        public static ConfigEntry<bool> AutoStartFromMenu;
        public static ConfigEntry<bool> VerboseLog;
        public static ConfigEntry<bool> AutoScreenshots;
        public static ConfigEntry<bool> DebugOpenMenu;
        public static ConfigEntry<string> DebugMenuAction;
        public static ConfigEntry<string> TestServer;
        public static ConfigEntry<int> HostTcpPort;
        public static ConfigEntry<string> Profile;
        public static ConfigEntry<bool> ProfileLog;
#if DEBUG
        public static ConfigEntry<bool> DebugAutoAdvance;
        public static ConfigEntry<int> DebugAutoAdvanceLevels;
        public static ConfigEntry<bool> DebugSeedLevelContents;
        public static ConfigEntry<bool> DebugDumpLevelContents;
        public static ConfigEntry<int> DebugPerturbRandom;
        public static ConfigEntry<bool> DebugAllowCheats;
        public static ConfigEntry<bool> DebugNoclip;
        public static ConfigEntry<bool> DebugReconDump;
        public static ConfigEntry<bool> DebugGuaranteeEarly;
        public static ConfigEntry<float> DebugAutoAdvanceSeconds;
#endif

        // Debug builds can switch off cheat blocking and disqualification for testing; release builds never can.
        public static bool CheatsAllowed
        {
#if DEBUG
            get { return DebugAllowCheats != null && DebugAllowCheats.Value; }
#else
            get { return false; }
#endif
        }

        public static bool Verbose { get { return VerboseLog != null && VerboseLog.Value; } }

        public static void Bind(ConfigFile config)
        {
            AutoStartFromMenu = config.Bind("Race", "AutoStartFromMenu", true,
                "Start the seeded run automatically when a match is found while you are on the main menu.");

            JoinKey = config.Bind("Hotkeys", "StartOrRestartRun", KeyCode.F9,
                "Start your run (from the menu), or restart it. Resets cost match time.");
            ForfeitKey = config.Bind("Hotkeys", "Forfeit", KeyCode.F10,
                "Press twice within three seconds to forfeit the current match.");
            SplitsKey = config.Bind("Hotkeys", "ToggleSplitList", KeyCode.F7, "Show or hide the split list.");
            GhostKey = config.Bind("Hotkeys", "ToggleGhost", KeyCode.F6, "Show or hide the opponent ghost.");
            ChatKey = config.Bind("Hotkeys", "Chat", KeyCode.F8, "Open and close the chat (Enter sends a line). It cannot be a key that types a character.");
            // Before 0.9.5 this was T, which opened a single line; the chat is now opened and closed with one key.
            if (WKRace.Protocol.KeyBindings.TypesText((int)ChatKey.Value)) ChatKey.Value = KeyCode.F8;

            HudScale = config.Bind("Hud", "Scale", 1.0f, new ConfigDescription("Overlay size multiplier.", new AcceptableValueRange<float>(0.5f, 2.5f)));
            ShowSplits = config.Bind("Hud", "ShowSplitList", true, "Show the split list while racing.");
            SplitRows = config.Bind("Hud", "SplitRows", 6, new ConfigDescription("How many completed splits to list.", new AcceptableValueRange<int>(1, 20)));
            UpcomingSplits = config.Bind("Hud", "UpcomingSplits", 1, new ConfigDescription("How many splits after the current one to list.", new AcceptableValueRange<int>(0, 5)));

            ShowGhost = config.Bind("Ghost", "Enabled", true, "Draw the opponent as a ghost in the world.");
            GhostOpacity = config.Bind("Ghost", "Opacity", 0.45f, new ConfigDescription("Ghost opacity (WKSR's ghost opacity slider sets it).", new AcceptableValueRange<float>(0f, 1f)));
            GhostColor = config.Bind("Ghost", "ColorHex", "4DB8FF", "Ghost colour as RRGGBB.");

            // The Debug section is for testing and troubleshooting: it is in the config file, but not in the in-game
            // options window (WKLib leaves out entries tagged "Hidden").
            VerboseLog = config.Bind("Debug", "VerboseLog", false, Hidden("Log extra detail about race events."));
            TestServer = config.Bind("Debug", "TestServer", "", Hidden("A test match server to use instead of the official one, as host:port (no TLS). Leave empty."));
            HostTcpPort = config.Bind("Debug", "HostTcpPort", 0, Hidden("Tests only: a Steam lobby this game hosts also takes players on this local TCP port (the DevTools bot). 0: off."));
            Profile = config.Bind("Debug", "Profile", "", Hidden("Use another player file (client.<name>.json) instead of your own (for testing). Leave empty."));
            DebugMenuAction = config.Bind("Debug", "MenuAction", "", Hidden("With OpenMenu: press this in the WKSR window by itself afterwards (ranked-solo, unranked-solo, search, lobby, offline-solo). Leave empty."));
            DebugOpenMenu = config.Bind("Debug", "OpenMenu", false, Hidden("Open the WKSR window by itself when the main menu loads (for testing the menu)."));
            AutoScreenshots = config.Bind("Debug", "AutoScreenshots", false, Hidden("Save a screenshot at key race moments to BepInEx/WKRace-screenshots (for troubleshooting)."));
            ProfileLog = config.Bind("Debug", "ProfileLog", false, Hidden("Write the mod's cost to the log every 5 seconds: frame times, overlay and level seeding time, garbage collections (for troubleshooting a low framerate)."));
#if DEBUG
            DebugAutoAdvance = config.Bind("Debug", "AutoAdvance", false, "Debug builds only: teleport to the next level every few seconds while racing.");
            DebugAutoAdvanceLevels = config.Bind("Debug", "AutoAdvanceLevels", 6, "Debug builds only: how many levels AutoAdvance climbs.");
            DebugSeedLevelContents = config.Bind("Debug", "SeedLevelContents", true, "Debug builds only: roll level contents from the race seed. Turn off to compare against the unmodified game.");
            DebugDumpLevelContents = config.Bind("Debug", "DumpLevelContents", false, "Debug builds only: write each entered level's items, creatures and spawn rolls to BepInEx/WKRace-dumps.");
            DebugPerturbRandom = config.Bind("Debug", "PerturbRandom", 0, "Debug builds only: draw this many random numbers every frame, to imitate a player whose game ran differently.");
            DebugAllowCheats = config.Bind("Debug", "AllowCheats", false, "Debug builds only: leave console cheats enabled during races and do not disqualify runs that use them.");
            DebugReconDump = config.Bind("Debug", "ReconDump", false, "Debug builds only: write per-level spawn chances, spawners, vending stock, computers and the perk table to BepInEx/WKRace-recon.");
            DebugGuaranteeEarly = config.Bind("Debug", "GuaranteeEarly", false, "Debug builds only: vending guarantees always use the first eligible level (to test the swap).");
            DebugAutoAdvanceSeconds = config.Bind("Debug", "AutoAdvanceSeconds", 7f, "Debug builds only: seconds between AutoAdvance teleports.");
            DebugNoclip = config.Bind("Debug", "Noclip", false, "Debug builds only: turn on noclip whenever a race attempt starts (does not count as cheating).");
#endif
        }

        private static ConfigDescription Hidden(string text)
        {
            return new ConfigDescription(text, null, "Hidden");
        }

        public static WKRace.Protocol.KeyBindings CurrentKeys()
        {
            return new WKRace.Protocol.KeyBindings
            {
                Ready = (int)JoinKey.Value,
                Forfeit = (int)ForfeitKey.Value,
                Splits = (int)SplitsKey.Value,
                Ghost = (int)GhostKey.Value,
                Chat = (int)ChatKey.Value,
            };
        }

        // Hotkeys picked in WKSR's options window. They are kept in this config file like any other setting.
        public static void ApplyKeys(WKRace.Protocol.KeyBindings keys)
        {
            SetKey(JoinKey, keys.Ready);
            SetKey(ForfeitKey, keys.Forfeit);
            SetKey(SplitsKey, keys.Splits);
            SetKey(GhostKey, keys.Ghost);
            if (!WKRace.Protocol.KeyBindings.TypesText(keys.Chat)) SetKey(ChatKey, keys.Chat);
        }

        private static void SetKey(ConfigEntry<KeyCode> entry, int code)
        {
            if (code <= 0 || !System.Enum.IsDefined(typeof(KeyCode), code)) return;
            var key = (KeyCode)code;
            if (entry.Value != key) entry.Value = key;
        }

        public static Color ParseGhostColor()
        {
            Color c;
            if (ColorUtility.TryParseHtmlString("#" + GhostColor.Value.TrimStart('#'), out c)) return c;
            return new Color(0.3f, 0.72f, 1f);
        }
    }
}
