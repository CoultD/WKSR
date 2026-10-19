using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace WKRace.Mod
{
    // Notices when the player's character fails to start. An exception inside ENT_Player.Awake (in practice another
    // mod's patch that no longer fits the game) leaves the character half set up: it cannot move, and the game only
    // says so in its log. The HUD then tells the player what to try.
    internal static class PlayerCheck
    {
        // Null while all is well, else the line for the HUD.
        public static string Fault { get; private set; }

        public static void Apply(Harmony harmony)
        {
            MethodBase target = AccessTools.Method(typeof(ENT_Player), "Awake");
            if (target == null)
            {
                Plugin.Log.LogError("Patch target missing for the character check. The game may have been updated.");
                return;
            }
            harmony.Patch(target, finalizer: new HarmonyMethod(typeof(PlayerCheck), nameof(AfterAwake)));
        }

        // Runs after Awake and every patch on it, whether or not they threw; the exception itself goes on as before.
        private static void AfterAwake(MethodBase __originalMethod, Exception __exception)
        {
            if (__exception == null)
            {
                Fault = null;
                return;
            }
            try
            {
                List<string> others = OtherPatchers(__originalMethod);
                bool otherMods = ModList.Loaded().Count > 0;
                Fault = Describe(others, otherMods);
                Plugin.Log.LogError("The player's character did not start: " + __exception.GetType().Name + ": " + __exception.Message +
                                    (others.Count > 0 ? " (mods that change this part of the game: " + string.Join(", ", others) + ")" : ""));
            }
            catch (Exception e)
            {
                Fault = Describe(new List<string>(), true);
                Plugin.Log.LogError("Character check failed: " + e);
            }
        }

        internal static string Describe(List<string> suspects, bool otherMods)
        {
            if (suspects.Count > 0)
                return "Another mod does not fit this version of the game (" + string.Join(", ", suspects) + "). Try again without it.";
            if (otherMods) return "Another mod may not fit this version of the game. Try again without other mods.";
            return "The game hit an error while starting your character. Restart the game.";
        }

        // The other mods with a patch on the method, by name where the patch owner is a loaded plugin.
        private static List<string> OtherPatchers(MethodBase method)
        {
            var names = new List<string>();
            HarmonyLib.Patches info = method != null ? Harmony.GetPatchInfo(method) : null;
            if (info == null) return names;
            foreach (string owner in info.Owners)
            {
                if (string.IsNullOrEmpty(owner) || string.Equals(owner, PluginInfo.Guid, StringComparison.OrdinalIgnoreCase)) continue;
                BepInEx.PluginInfo plugin;
                string name = Chainloader.PluginInfos.TryGetValue(owner, out plugin) && plugin != null && plugin.Metadata != null && !string.IsNullOrEmpty(plugin.Metadata.Name)
                    ? plugin.Metadata.Name : owner;
                if (!names.Contains(name)) names.Add(name);
            }
            return names;
        }
    }
}
